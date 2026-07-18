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

    private static readonly object Gate = new();
    private static readonly List<(long rev, string type)> Log = new();
    private static readonly List<TaskCompletionSource<bool>> Waiters = new();
    private static readonly List<TaskCompletionSource<bool>> TickWaiters = new();
    private static readonly HashSet<Task> PendingAsync = new();
    private static long _revision;
    private static long _tickCount;
    private static string _lastPhase = "";
    private static object? _runStateRef;
    private static string _runId = "none";
    private static GameAction? _watchedAction;
    private static DateTime _watchedSinceUtc;
    private static bool _wedgeAnnounced;
    private static DateTime? _deadBoardSinceUtc;
    private static bool _deadBoardAnnounced;

    // Milliseconds the executor has been stuck on the SAME action. The
    // serial executor never recovers from a parked action on its own —
    // past the threshold the contract is: abandon and start over.
    public static long ExecutorStuckMs { get; private set; }

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
        var message = string.Join(' ', cause.Message.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (message.Length > 160) message = message[..160];
        return $"async_fault:{label}:{cause.GetType().Name}:{message}";
    }

    // Call from a main-thread pump job immediately before reading state or
    // dispatching a verb. Tick also calls it, but Tick is only a notification
    // path: optimistic guards must compare against the actual RunState in the
    // same serialized job as dispatch, never against a prior frame's cache.
    public static string RefreshRunIdentity()
    {
        var runState = RunManager.Instance?.DebugOnlyGetState();
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
        WatchExecutor();
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

    private static void WatchExecutor()
    {
        var running = RunManager.Instance?.ActionExecutor?.CurrentlyRunningAction;
        WatchDeadBoard(running);
        if (running is null || !ReferenceEquals(running, _watchedAction))
        {
            _watchedAction = running;
            _watchedSinceUtc = DateTime.UtcNow;
            ExecutorStuckMs = 0;
            _wedgeAnnounced = false;
            return;
        }
        // A deferred pick parks the executing action on purpose — the
        // engine is waiting on the agent's pick-card/confirm, not stuck.
        // Hold the clock at zero so a slow decision can't fire a spurious
        // wedge (a potion pick pondered past 8s used to).
        if (HeadlessPicker.IsActive)
        {
            _watchedSinceUtc = DateTime.UtcNow;
            ExecutorStuckMs = 0;
            return;
        }
        ExecutorStuckMs = (long)(DateTime.UtcNow - _watchedSinceUtc).TotalMilliseconds;
        if (ExecutorStuckMs > 8000 && !_wedgeAnnounced)
        {
            _wedgeAnnounced = true;
            Bump($"wedge:{running.GetType().Name}");
        }
    }

    // The stuck-executor clock above can't see the other fatal shape: an
    // exception mid death-resolution aborts the chain, leaving nothing
    // running, nothing queued, every enemy dead — and the combat never
    // ending. Executor-idle resets the wedge clock, so a second clock
    // times the dead board itself and announces once past the same
    // threshold.
    private static void WatchDeadBoard(GameAction? running)
    {
        var combat = CombatManager.Instance;
        // IsEnding legitimately has an all-dead board while victory
        // actions, revives, and phase transitions are still settling.
        var queuesEmpty = RunManager.Instance is { } rm
            && EngineQueues.All(rm).All(q => q.depth == 0);
        var deadBoard = ResolutionGuards.IsDeadBoardCandidate(
            running is not null,
            HeadlessPicker.IsActive,
            combat is { IsInProgress: true },
            combat is { IsEnding: true },
            queuesEmpty,
            combat is not null && AllEnemiesDead(combat));
        if (!deadBoard)
        {
            _deadBoardSinceUtc = null;
            _deadBoardAnnounced = false;
            return;
        }
        _deadBoardSinceUtc ??= DateTime.UtcNow;
        if (!_deadBoardAnnounced
            && (DateTime.UtcNow - _deadBoardSinceUtc.Value).TotalMilliseconds > 8000)
        {
            _deadBoardAnnounced = true;
            Bump("wedge:DeadBoard");
        }
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
