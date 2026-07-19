using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace Spirescry.State;

// Event-driven waiting. Every state change bumps a monotonic revision:
// point signals come from the engine's own C# events (action executor,
// combat manager, overlay stack — no patches), and a per-tick phase diff
// is the safety net for anything without one. Agents park on
// GET /obs?since=<rev>&wait=<ms> instead of sleep-polling; the response
// carries the new revision plus the named events behind it.
public static class Signals
{
    private const int LogCap = 64;
    // Errors are rarer and heavier than events; a run that produces more
    // than this is already unplayable, so dropping the oldest is fine.
    private const int ErrorCap = 256;

    private static readonly object Gate = new();
    private static readonly List<(long rev, string type)> Log = new();
    private static readonly List<(long rev, string type)> Errors = new();
    private static readonly List<TaskCompletionSource<bool>> Waiters = new();
    private static readonly List<TaskCompletionSource<bool>> TickWaiters = new();
    private static readonly HashSet<Task> PendingAsync = new();
    private static long _revision;
    private static long _tickCount;
    private static string _lastPhase = "";
    private static object? _runStateRef;
    private static string _runId = "none";
    private static bool _logHooked;

    // Milliseconds the executor has been stuck on the SAME action. The
    // serial executor never recovers from a parked action on its own —
    // past the threshold the contract is: abandon and start over.
    public static long ExecutorStuckMs => Settlement.Current.ExecutorStuckMs;

    private static ActionExecutor? _exec;
    private static MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSet? _queueSet;
    private static CombatManager? _combat;
    private static NOverlayStack? _stack;

    public static long Revision { get { lock (Gate) return _revision; } }

    public static long TickCount { get { lock (Gate) return _tickCount; } }

    public static string RunId { get { lock (Gate) return _runId; } }

    public static int PendingAsyncCount { get { lock (Gate) return PendingAsync.Count; } }

    // Dispatcher fire-and-forget tasks are part of action settlement too.
    // Tracking them closes the gap where GUI work had left the pump job but
    // had not yet enqueued an engine action or changed phase.
    public static void TrackAsync(Task task, string label)
    {
        lock (Gate) PendingAsync.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (Gate) PendingAsync.Remove(task);
            Bump(AsyncCompletionEvent(completed, label));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string AsyncCompletionEvent(Task task, string label)
    {
        if (task.Exception is not { } aggregate) return $"async:{label}";
        var cause = aggregate.Flatten().InnerExceptions.FirstOrDefault() ?? aggregate;
        return ErrorEvents.FromAsyncFault(label, cause.GetType().Name, cause.Message);
    }

    // The engine catches exceptions from fire-and-forget task chains
    // (TaskHelper.RunSafely and friends) and only logs them — the fault
    // never reaches a task the mod tracks, so a half-executed effect
    // still settles quietly. Every Logger chains its callback to this
    // global event, so folding Error-level lines into the revision
    // stream makes those swallowed failures visible to follow/obs.
    private static void OnEngineLog(
        MegaCrit.Sts2.Core.Logging.LogLevel level, string text, int _)
    {
        if (level != MegaCrit.Sts2.Core.Logging.LogLevel.Error) return;
        // Benign-line triage needs to know whether combat is live; the
        // callback can fire on any thread, so read defensively and let
        // an unreadable state count as in-combat — unknown context must
        // degrade toward reporting an error, never toward hiding one.
        bool combatInProgress;
        try { combatInProgress = CombatManager.Instance is { IsInProgress: true }; }
        catch { combatInProgress = true; }
        Bump(ErrorEvents.FromLogLine(text, combatInProgress));
    }

    // Error events accepted-to-settlement: the follow response surfaces
    // them so a verb whose effects half-executed cannot read as a clean
    // "settled". Kept in their own journal: the shared event ring holds
    // LogCap entries and one busy resolution can push more than that
    // between acceptance and settlement — an error evicted from the ring
    // must still reach the follow response and the runlog.
    public static string[] ErrorsSince(long since)
    {
        lock (Gate)
            return Errors.Where(e => e.rev > since)
                .Select(e => e.type)
                .ToArray();
    }

    // Call from a main-thread pump job immediately before reading state or
    // dispatching a verb. Tick also calls it, but Tick is only a notification
    // path: optimistic guards must compare against the actual RunState in the
    // same serialized job as dispatch, never against a prior frame's cache.
    public static string RefreshRunIdentity()
    {
        // RunState identity exists before the local player seat is mounted.
        // Using the player-gated context here would transiently publish
        // run:none for the same RunState, then mint a second token once the
        // player appeared.
        var runState = LocalRunContext.StateOnly?.State;
        string? changedTo = null;
        lock (Gate)
        {
            if (ReferenceEquals(runState, _runStateRef)) return _runId;
            _runStateRef = runState;
            _runId = runState is null ? "none" : Guid.NewGuid().ToString("N")[..8];
            changedTo = _runId;
        }
        Bump($"run:{changedTo}");
        return changedTo;
    }

    public static void Bump(string type)
    {
        List<TaskCompletionSource<bool>>? wake = null;
        lock (Gate)
        {
            _revision++;
            Log.Add((_revision, type));
            if (Log.Count > LogCap) Log.RemoveAt(0);
            if (ErrorEvents.IsError(type))
            {
                Errors.Add((_revision, type));
                if (Errors.Count > ErrorCap) Errors.RemoveAt(0);
            }
            if (Waiters.Count > 0)
            {
                wake = new List<TaskCompletionSource<bool>>(Waiters);
                Waiters.Clear();
            }
        }
        if (wake is null) return;
        foreach (var w in wake) w.TrySetResult(true);
    }

    // Runs on the pump every tick (GUI: each frame; host: after each
    // handler + a slow timer for background continuations): re-hook engine
    // events across instance swaps, and fold phase changes into the
    // revision stream.
    public static void Tick()
    {
        EnsureSubscribed();
        WatchSettlement();
        RefreshRunIdentity();
        var phase = PhaseDetector.Current().AsString();
        if (phase != _lastPhase)
        {
            var from = _lastPhase;
            _lastPhase = phase;
            Bump($"phase:{from}->{phase}");
        }
        AdvanceTick();
    }

    // True when the revision moved past `since`; false on timeout.
    public static async Task<bool> WaitForChange(long since, int timeoutMs)
    {
        TaskCompletionSource<bool> tcs;
        lock (Gate)
        {
            if (_revision > since) return true;
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Waiters.Add(tcs);
        }
        try { return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)); }
        catch (TimeoutException) { return false; }
        finally { lock (Gate) Waiters.Remove(tcs); }
    }

    // GUI callbacks do not all expose a Task or an engine event. Follow
    // therefore also observes distinct process frames before declaring a
    // quiet GUI action settled. Headless Tick calls remain useful to tests,
    // but headless follow resolves synchronously and does not wait on them.
    public static async Task<bool> WaitForTick(long after, int timeoutMs)
    {
        TaskCompletionSource<bool> tcs;
        lock (Gate)
        {
            if (_tickCount > after) return true;
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TickWaiters.Add(tcs);
        }
        try { return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)); }
        catch (TimeoutException) { return false; }
        finally { lock (Gate) TickWaiters.Remove(tcs); }
    }

    private static void AdvanceTick()
    {
        List<TaskCompletionSource<bool>>? wake = null;
        lock (Gate)
        {
            _tickCount++;
            if (TickWaiters.Count > 0)
            {
                wake = new List<TaskCompletionSource<bool>>(TickWaiters);
                TickWaiters.Clear();
            }
        }
        if (wake is null) return;
        foreach (var waiter in wake) waiter.TrySetResult(true);
    }

    public static object[] EventsSince(long since)
    {
        lock (Gate)
            return Log.Where(e => e.rev > since)
                .Select(e => (object)new { rev = e.rev, type = e.type })
                .ToArray();
    }

    private static void WatchSettlement()
    {
        var run = RunManager.Instance;
        var running = run?.ActionExecutor?.CurrentlyRunningAction;
        var combat = CombatManager.Instance;
        var queuesEmpty = run is not null
            && EngineQueues.All(run).All(queue => queue.depth == 0);
        var result = Settlement.Current.ObserveWatchdogs(new SettlementWatchdogProbe(
            running,
            running?.GetType().Name,
            DecisionSurface.Current.DeferredCardChoiceActive,
            combat is { IsInProgress: true },
            combat is { IsEnding: true },
            queuesEmpty,
            combat is not null && AllEnemiesDead(combat)));
        foreach (var settlementEvent in result.Events)
            Bump(settlementEvent);
    }

    private static bool AllEnemiesDead(CombatManager combat)
    {
        var enemies = combat.DebugOnlyGetState()?.Enemies;
        if (enemies is null) return false;
        var any = false;
        foreach (var e in enemies)
        {
            if (e is null) continue;
            any = true;
            if (e.IsAlive) return false;
        }
        return any;
    }

    // New runs / scene loads construct fresh engine singletons; cheap
    // reference compares keep the subscriptions on the live instances.
    private static void EnsureSubscribed()
    {
        if (!_logHooked)
        {
            _logHooked = true;
            MegaCrit.Sts2.Core.Logging.Log.LogCallback += OnEngineLog;
        }

        var exec = RunManager.Instance?.ActionExecutor;
        if (!ReferenceEquals(exec, _exec))
        {
            if (_exec is not null) _exec.AfterActionExecuted -= OnAction;
            _exec = exec;
            if (exec is not null) exec.AfterActionExecuted += OnAction;
        }

        var queues = RunManager.Instance?.ActionQueueSet;
        if (!ReferenceEquals(queues, _queueSet))
        {
            if (_queueSet is not null) _queueSet.ActionEnqueued -= OnEnqueued;
            _queueSet = queues;
            if (queues is not null) queues.ActionEnqueued += OnEnqueued;
        }

        var combat = CombatManager.Instance;
        if (!ReferenceEquals(combat, _combat))
        {
            if (_combat is not null)
            {
                _combat.TurnStarted -= OnTurn;
                _combat.TurnEnded -= OnTurn;
                _combat.CombatSetUp -= OnTurn;
                _combat.CombatEnded -= OnCombatRoom;
                _combat.PlayerActionsDisabledChanged -= OnTurn;
            }
            _combat = combat;
            if (combat is not null)
            {
                combat.TurnStarted += OnTurn;
                combat.TurnEnded += OnTurn;
                combat.CombatSetUp += OnTurn;
                combat.CombatEnded += OnCombatRoom;
                combat.PlayerActionsDisabledChanged += OnTurn;
            }
        }

        var stack = NOverlayStack.Instance;
        if (!ReferenceEquals(stack, _stack))
        {
            if (_stack is not null) _stack.Changed -= OnOverlay;
            _stack = stack;
            if (stack is not null) stack.Changed += OnOverlay;
        }
    }

    private static void OnAction(GameAction a) => Bump($"action:{a.GetType().Name}");
    private static void OnEnqueued(GameAction a) => Bump($"enqueued:{a.GetType().Name}");
    private static void OnTurn(CombatState _) => Bump("combat");
    private static void OnCombatRoom(MegaCrit.Sts2.Core.Rooms.CombatRoom _) => Bump("combat_ended");
    private static void OnOverlay() => Bump("overlay");
}
