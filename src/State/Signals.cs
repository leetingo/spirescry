using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
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
    private sealed class WaitClock
    {
        public long Revision { get; private set; }
        public List<TaskCompletionSource<bool>> Waiters { get; } = new();

        public void Advance() => Revision++;
    }

    private const int LogCap = 64;
    private const int RetiredLogCorrelationMs = 50;
    // Errors are rarer and heavier than events; a run that produces more
    // than this is already unplayable, so dropping the oldest is fine.
    private const int ErrorCap = 256;

    private static readonly object Gate = new();
    private static readonly RevisionJournal EventJournal = new(LogCap);
    private static readonly RevisionJournal ErrorJournal = new(ErrorCap);
    private static readonly EventOptionTracker EventOptions = new();
    private static readonly EngineLogCorrelation EngineLogs = new();
    private static readonly WaitClock PublicClock = new();
    private static readonly WaitClock SettlementClock = new();
    private static readonly List<TaskCompletionSource<bool>> TickWaiters = new();
    private static readonly HashSet<Task> PendingFireAndForget = new();
    private static long _tickCount;
    private static string _lastPhase = "";
    private static object? _runStateRef;
    private static string _runId = "none";
    private static GameAction? _watchedAction;
    private static DateTime _watchedSinceUtc;
    private static bool _wedgeAnnounced;
    private static DateTime? _deadBoardSinceUtc;
    private static bool _deadBoardAnnounced;
    private static bool _logHooked;

    // Milliseconds the executor has been stuck on the SAME action. The
    // serial executor never recovers from a parked action on its own —
    // past the threshold the contract is: abandon and start over.
    public static long ExecutorStuckMs { get; private set; }

    private static ActionExecutor? _exec;
    private static MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSet? _queueSet;
    private static CombatManager? _combat;
    private static NOverlayStack? _stack;

    public static long Revision { get { lock (Gate) return PublicClock.Revision; } }

    public static long WorkRevision
    {
        get { lock (Gate) return SettlementClock.Revision; }
    }

    public static long TickCount { get { lock (Gate) return _tickCount; } }

    public static string RunId { get { lock (Gate) return _runId; } }

    // The follow probe must read votes through the same authenticated
    // owner as the task sweep. RunManager retains an old synchronizer
    // after State is cleared, so reading it directly can resurrect a
    // dead run's unresolved vote window.
    public static bool SharedVotePending()
    {
        lock (Gate) return EventOptions.SharedVotePending();
    }

    // Option tasks can enter the synchronizer's list outside any
    // dispatch: a shared choice appends one RunSafely(Chosen()) task per
    // player, and a multiplayer client's vote lands later via a network
    // message. Sweep the list on EVERY tick — not just event frames: a
    // delivered Chosen() runs synchronously to its first await and can
    // open a picker or a combat before the next tick, and the task must
    // still be found. Both boots run Tick, so GUI clients get the same
    // coverage; the membership gate in TrackAsync makes re-scans
    // idempotent. Main thread only, like every other engine read here.
    private static void SweepPendingEventOptions()
    {
        var rm = RunManager.Instance;
        var runState = rm?.DebugOnlyGetState();
        var sync = runState is null ? null : rm?.EventSynchronizer;
        if (sync is null) return;
        // Tick refreshed the owner immediately before this sweep. Refuse
        // a stale synchronizer even if RunManager keeps it after State is
        // cleared during teardown.
        lock (Gate)
            if (!EventOptions.MatchesOwner(runState, sync)) return;
        if (EventSync.PendingTasks(sync) is not { } pending) return;
        foreach (var task in pending)
            TrackEventOption(task);
    }

    // An abandoned run's option task can be parked on a combat or dialog
    // that no longer exists — it will never complete, and one zombie
    // would hold the follow probe's busy flag for every later run. Drop
    // the tracker's busy membership and advance its generation. Orphaned
    // continuations are then forbidden from publishing into a later run's
    // revision/error window.
    public static void DropEventOptionTracking()
    {
        lock (Gate) EventOptions.Drop();
    }

    // How a tracked task participates in the follow probe's busy logic.
    // Fire-and-forget dispatcher work blocks settlement until done; an
    // event option's task legitimately outlives one follow window (it
    // awaits embedded combats and parked pickers), so its kind gets the
    // three-state treatment in the probe instead of a flat "busy".
    private enum TrackedKind { FireAndForget, EventOption }

    // One atomic read of both counters — the probe must not reconstruct
    // internal state from two independently-locked reads.
    public readonly record struct PendingWork(
        int FireAndForget, int EventOptions);

    public static PendingWork PendingSnapshot()
    {
        lock (Gate)
            return new PendingWork(
                PendingFireAndForget.Count,
                EventOptions.PendingCount);
    }

    // Dispatcher fire-and-forget tasks are part of action settlement too.
    // Tracking them closes the gap where GUI work had left the pump job but
    // had not yet enqueued an engine action or changed phase.
    public static void TrackAsync(Task task, string label) =>
        TrackAsync(task, label, TrackedKind.FireAndForget);

    private static void TrackAsync(
        Task task, string label, TrackedKind kind)
    {
        var isEventOption = kind == TrackedKind.EventOption;
        var eventGeneration = 0L;
        lock (Gate)
        {
            if (isEventOption)
            {
                if (!EventOptions.TryTrack(task, out eventGeneration)) return;
            }
            else
                PendingFireAndForget.Add(task);
        }
        _ = task.ContinueWith(completed =>
        {
            var type = AsyncCompletionEvent(completed, label);
            List<TaskCompletionSource<bool>>? wake;
            lock (Gate)
            {
                var currentOwner = true;
                if (isEventOption)
                {
                    currentOwner = EventOptions.Complete(task, eventGeneration);
                }
                else
                    PendingFireAndForget.Remove(task);
                var resolvedLog = EngineLogs.ResolveForTask(
                    completed,
                    ManagedThreadId.Current,
                    currentOwner
                        ? EngineLogDisposition.Publish
                        : EngineLogDisposition.Suppress);
                if (!currentOwner)
                {
                    if (resolvedLog)
                        EventOptions.MarkRetiredFaultLogResolved(task);
                    return;
                }
                wake = RecordBumpLocked(type);
            }
            Wake(wake);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public static void TrackEventOption(Task task) =>
        TrackAsync(task, "event-option", TrackedKind.EventOption);

    private static string AsyncCompletionEvent(Task task, string label)
    {
        if (TaskFault.From(task) is not { } fault) return $"async:{label}";
        return ErrorEvents.FromAsyncFault(label, fault.TypeName, fault.Message);
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
        // TaskHelper logs just before its returned task transitions to
        // Faulted. When dead-owner tasks exist, give the concrete task's
        // same-thread completion continuation a short identity-correlation
        // window. Text alone never suppresses a line. The window itself is
        // tracked as pending work, so a live follow cannot settle before we
        // either suppress that exact retired task or publish a genuine
        // current-run engine error.
        PendingEngineLog? pending = null;
        lock (Gate)
        {
            if (EventOptions.HasRetired)
                pending = EngineLogs.Register(
                    text,
                    combatInProgress,
                    ManagedThreadId.Current);
        }
        if (pending is not null)
        {
            HoldAsyncSilently(
                PublishEngineLogAfterRetiredCorrelation(pending));
            return;
        }
        Bump(ErrorEvents.FromLogLine(text, combatInProgress));
    }

    // Correlation is real pending work — follow must not settle while the
    // log's owner is undecided — but resolving it is not itself an engine
    // event. Wake waiters without incrementing revision or emitting a
    // synthetic completion into the response.
    private static void HoldAsyncSilently(Task task)
    {
        lock (Gate) PendingFireAndForget.Add(task);
        _ = task.ContinueWith(_ =>
        {
            List<TaskCompletionSource<bool>>? wake = null;
            lock (Gate)
            {
                PendingFireAndForget.Remove(task);
                SettlementClock.Advance();
                wake = DrainWaitersLocked(SettlementClock.Waiters);
            }
            Wake(wake);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task PublishEngineLogAfterRetiredCorrelation(
        PendingEngineLog pending)
    {
        try
        {
            await pending.Resolution.Task.WaitAsync(
                TimeSpan.FromMilliseconds(RetiredLogCorrelationMs))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (Gate) EngineLogs.Expire(pending);
        }
        if (await pending.Resolution.Task.ConfigureAwait(false)
            == EngineLogDisposition.Publish)
            Bump(ErrorEvents.FromLogLine(
                pending.Text, pending.CombatInProgress));
    }

    // Error events accepted-to-settlement: the follow response surfaces
    // them so a verb whose effects half-executed cannot read as a clean
    // "settled". Kept in their own journal: the shared event ring holds
    // LogCap entries and one busy resolution can push more than that
    // between acceptance and settlement — an error evicted from the ring
    // must still reach the follow response and the runlog.
    public static string[] ErrorsSince(long since)
    {
        lock (Gate) return ErrorJournal.TypesSince(since);
    }

    // Call from a main-thread pump job immediately before reading state or
    // dispatching a verb. Tick also calls it, but Tick is only a notification
    // path: optimistic guards must compare against the actual RunState in the
    // same serialized job as dispatch, never against a prior frame's cache.
    public static string RefreshRunIdentity()
    {
        var rm = RunManager.Instance;
        var runState = rm?.DebugOnlyGetState();
        var eventSync = runState is null ? null : rm?.EventSynchronizer;
        string? changedTo = null;
        var ownerChangedWithoutRun = false;
        lock (Gate)
        {
            var runChanged = !ReferenceEquals(runState, _runStateRef);
            var ownerChanged = EventOptions.ChangeOwner(runState, eventSync);
            ownerChangedWithoutRun = ownerChanged && !runChanged;
            if (runChanged)
            {
                _runStateRef = runState;
                _runId = runState is null
                    ? "none"
                    : Guid.NewGuid().ToString("N")[..8];
                changedTo = _runId;
            }
        }
        if (changedTo is not null) Bump($"run:{changedTo}");
        else if (ownerChangedWithoutRun) Bump("event-option:owner-changed");
        return RunId;
    }

    public static void Bump(string type)
    {
        List<TaskCompletionSource<bool>>? wake;
        lock (Gate) wake = RecordBumpLocked(type);
        Wake(wake);
    }

    private static List<TaskCompletionSource<bool>>? RecordBumpLocked(string type)
    {
        PublicClock.Advance();
        SettlementClock.Advance();
        EventJournal.Add(PublicClock.Revision, type);
        if (ErrorEvents.IsError(type))
            ErrorJournal.Add(PublicClock.Revision, type);
        return DrainWaitersLocked(
            PublicClock.Waiters, SettlementClock.Waiters);
    }

    private static void Wake(List<TaskCompletionSource<bool>>? waiters)
    {
        if (waiters is null) return;
        foreach (var waiter in waiters) waiter.TrySetResult(true);
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
        SweepPendingEventOptions();
        if (phase != _lastPhase)
        {
            var from = _lastPhase;
            _lastPhase = phase;
            Bump($"phase:{from}->{phase}");
        }
        AdvanceTick();
    }

    // True when the revision moved past `since`; false on timeout.
    public static Task<bool> WaitForChange(long since, int timeoutMs) =>
        WaitForCounterChange(since, timeoutMs, PublicClock);

    // Follow observes both public revision changes and silent changes to
    // tracked-work membership. Keeping this clock separate preserves the
    // /obs?since contract: a correlation-only wake never claims the public
    // state revision moved.
    public static Task<bool> WaitForWorkChange(long since, int timeoutMs) =>
        WaitForCounterChange(since, timeoutMs, SettlementClock);

    private static async Task<bool> WaitForCounterChange(
        long since, int timeoutMs, WaitClock clock)
    {
        TaskCompletionSource<bool> tcs;
        lock (Gate)
        {
            if (clock.Revision > since) return true;
            tcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            clock.Waiters.Add(tcs);
        }
        try { return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)); }
        catch (TimeoutException) { return false; }
        finally { lock (Gate) clock.Waiters.Remove(tcs); }
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
            wake = DrainWaitersLocked(TickWaiters);
        }
        Wake(wake);
    }

    private static List<TaskCompletionSource<bool>>? DrainWaitersLocked(
        List<TaskCompletionSource<bool>> first,
        List<TaskCompletionSource<bool>>? second = null)
    {
        var count = first.Count + (second?.Count ?? 0);
        if (count == 0) return null;
        var wake = new List<TaskCompletionSource<bool>>(count);
        wake.AddRange(first);
        first.Clear();
        if (second is not null)
        {
            wake.AddRange(second);
            second.Clear();
        }
        return wake;
    }

    public static object[] EventsSince(long since)
    {
        lock (Gate)
            return EventJournal.Since(since)
                .Select(e => (object)new { rev = e.Revision, type = e.Type })
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
