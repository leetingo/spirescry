namespace Spirescry.State;

internal enum InlineFaultKind
{
    VictorySettled,
    Partial,
    Failed,
}

internal static class SettlementOutcomeRules
{
    public static string WireName(this SettlementOutcome outcome) =>
        ProtocolVocabulary.SettlementOutcomes.Name(outcome);

    public static bool ReachedBoundary(this SettlementOutcome outcome) =>
        outcome is not SettlementOutcome.Timeout;

    public static bool IsReplayable(this SettlementOutcome outcome) =>
        outcome is SettlementOutcome.Settled or SettlementOutcome.NextDecision;
}

internal sealed record SettlementRequest(
    Phase PhaseBefore,
    long StartedRevision,
    long AcceptedRevision,
    long AcceptedTick,
    int TimeoutMs);

internal sealed record SettlementProbe(
    long Tick,
    long WorkRevision,
    bool RequiresFrameStability,
    SettlementActivity Activity,
    SnapshotContract Observation,
    IReadOnlyList<string> Errors)
{
    public bool OptionExecuting => Activity.EventOptionExecuting;

    public long Revision => Observation.Revision
        ?? throw new InvalidOperationException("settlement snapshot has no revision");

    public string RunId => Observation.RunId
        ?? throw new InvalidOperationException("settlement snapshot has no run identity");

    public Phase Phase => Observation.Phase;

    public bool HasDecision => Observation.Legal.Any(
        verb => verb is not ("abandon" or "potion-discard"));

    public string StateKey => Observation.ConsumerStateKey();
}

internal readonly record struct SettlementActivity(
    int FireAndForgetCount,
    bool EventOptionExecuting,
    bool ExecutorRunning,
    int QueuedActionCount)
{
    public bool IsBusy =>
        FireAndForgetCount > 0
        || EventOptionExecuting
        || ExecutorRunning
        || QueuedActionCount > 0;
}

internal sealed record SettlementResult(
    SettlementOutcome Outcome,
    SettlementProbe Probe);

internal interface ISettlementTickSource
{
    Task<SettlementProbe> Capture(long startedRevision);
    Task WaitForChange(long afterWorkRevision, int timeoutMs);
    Task WaitForTick(long afterTick, int timeoutMs);
}

internal interface ISettlementClock
{
    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemSettlementClock : ISettlementClock
{
    public static SystemSettlementClock Instance { get; } = new();

    private SystemSettlementClock() { }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

internal sealed record SettlementWatchdogProbe(
    object? RunningAction,
    string? RunningActionName,
    bool PickerActive,
    bool CombatInProgress,
    bool CombatIsEnding,
    bool QueuesEmpty,
    bool AllEnemiesDead);

internal sealed record SettlementWatchdogResult(
    long ExecutorStuckMs,
    IReadOnlyList<string> Events);

// The single owner of accepted-action settlement. Live code supplies the
// engine-backed tick source; tests supply deterministic probes and time. The
// same module also owns the two clocks that identify abandoned resolution and
// the exact inline-fault classification used by headless dispatch.
internal sealed class SettlementModule
{
    internal const int WedgeTimeoutMs = 8000;
    private const int RequiredStableFrames = 3;

    private readonly ISettlementTickSource _ticks;
    private readonly ISettlementClock _clock;
    private object? _watchedAction;
    private DateTimeOffset _watchedSince;
    private bool _executorWedgeAnnounced;
    private DateTimeOffset? _deadBoardSince;
    private bool _deadBoardAnnounced;

    public SettlementModule(ISettlementTickSource ticks, ISettlementClock clock)
    {
        _ticks = ticks;
        _clock = clock;
    }

    public long ExecutorStuckMs { get; private set; }

    public async Task<SettlementResult> Follow(SettlementRequest request)
    {
        var deadline = _clock.UtcNow.AddMilliseconds(request.TimeoutMs);
        SettlementOutcome? candidateOutcome = null;
        string? candidateState = null;
        var candidateTick = request.AcceptedTick;
        var stableFrames = 0;

        while (true)
        {
            var probe = await _ticks.Capture(request.StartedRevision);
            var outcome = CandidateOutcome(
                probe, request.PhaseBefore, request.AcceptedRevision);
            if (outcome is { } candidate)
            {
                // A fault token is conclusive even if opaque engine work is
                // still unwinding. Do not hide it behind GUI frame stability.
                if (candidate is SettlementOutcome.Fault
                    || !probe.RequiresFrameStability)
                    return new SettlementResult(candidate, probe);

                if (candidate == candidateOutcome
                    && probe.StateKey == candidateState
                    && probe.Tick > candidateTick)
                    stableFrames++;
                else
                    stableFrames = 1;
                candidateOutcome = candidate;
                candidateState = probe.StateKey;
                candidateTick = probe.Tick;

                // A GUI callback may enqueue its real work on a later frame
                // without returning a Task. Three distinct, identical frames
                // are the observable quiet boundary.
                if (stableFrames >= RequiredStableFrames)
                    return new SettlementResult(candidate, probe);
            }
            else
            {
                candidateOutcome = null;
                candidateState = null;
                stableFrames = 0;
            }

            var remaining = (int)Math.Max(
                0, (deadline - _clock.UtcNow).TotalMilliseconds);
            if (remaining == 0)
                return new SettlementResult(SettlementOutcome.Timeout, probe);

            if (probe.RequiresFrameStability && outcome is not null)
                await _ticks.WaitForTick(probe.Tick, remaining);
            else
                await _ticks.WaitForChange(probe.WorkRevision, remaining);
        }
    }

    public SettlementWatchdogResult ObserveWatchdogs(SettlementWatchdogProbe probe)
    {
        var events = new List<string>(1);
        WatchDeadBoard(probe, events);
        WatchExecutor(probe, events);
        return new SettlementWatchdogResult(ExecutorStuckMs, events);
    }

    internal static SettlementOutcome? CandidateOutcome(
        SettlementProbe probe, Phase phaseBefore, long acceptedRevision)
    {
        if (probe.Errors.Count > 0) return SettlementOutcome.Fault;
        if (!probe.Activity.IsBusy) return SettlementOutcome.Settled;
        // A mid-flight option effect can flip the phase back to event
        // before its continuation finishes (and before a late fault
        // logs) — the page on screen is transient, not a decision to
        // report. Wait for the task to park or complete.
        if (probe.OptionExecuting) return null;
        if (!probe.HasDecision) return null;
        if (probe.Phase != phaseBefore || IsNestedDecision(probe.Phase))
            return SettlementOutcome.NextDecision;
        // Event tasks intentionally remain parked while a page exposes its
        // next option inside the same coarse phase.
        return probe.Phase == Phase.Event
            && probe.Revision > acceptedRevision
                ? SettlementOutcome.NextDecision
                : null;
    }

    internal InlineFaultKind ClassifyInlineFault(
        Exception exception,
        string actionName,
        bool combatInProgress,
        bool revisionChanged)
    {
        // Task.Exception adds a singleton AggregateException transport
        // wrapper. Multiple failures are never the known benign stale pop.
        if (exception is AggregateException aggregate)
        {
            aggregate = aggregate.Flatten();
            if (aggregate.InnerExceptions.Count == 1)
                exception = aggregate.InnerExceptions[0];
        }

        if (!combatInProgress
            && actionName == "EndPlayerTurnAction"
            && exception is InvalidOperationException
            && IsStalePopMessage(exception.Message))
            return InlineFaultKind.VictorySettled;

        return revisionChanged ? InlineFaultKind.Partial : InlineFaultKind.Failed;
    }

    internal static bool IsStalePopMessage(string message) =>
        message.Contains(
            "Tried to pop action EndPlayerTurnAction", StringComparison.Ordinal)
        && message.Contains("didn't find it in any queue", StringComparison.Ordinal);

    internal static bool IsNestedDecision(Phase phase) => phase is
        Phase.CardSelect
        or Phase.HandSelect
        or Phase.BundleSelect
        or Phase.CardReward
        or Phase.RelicReward;

    private void WatchExecutor(
        SettlementWatchdogProbe probe, ICollection<string> events)
    {
        var now = _clock.UtcNow;
        if (probe.RunningAction is null
            || !ReferenceEquals(probe.RunningAction, _watchedAction))
        {
            _watchedAction = probe.RunningAction;
            _watchedSince = now;
            ExecutorStuckMs = 0;
            _executorWedgeAnnounced = false;
            return;
        }

        // A deferred picker is an intentional next decision, not a wedge.
        if (probe.PickerActive)
        {
            _watchedSince = now;
            ExecutorStuckMs = 0;
            return;
        }

        ExecutorStuckMs = (long)(now - _watchedSince).TotalMilliseconds;
        if (ExecutorStuckMs > WedgeTimeoutMs && !_executorWedgeAnnounced)
        {
            _executorWedgeAnnounced = true;
            events.Add($"wedge:{probe.RunningActionName ?? "UnknownAction"}");
        }
    }

    private void WatchDeadBoard(
        SettlementWatchdogProbe probe, ICollection<string> events)
    {
        var candidate = probe.RunningAction is null
            && !probe.PickerActive
            && probe.CombatInProgress
            && !probe.CombatIsEnding
            && probe.QueuesEmpty
            && probe.AllEnemiesDead;
        if (!candidate)
        {
            _deadBoardSince = null;
            _deadBoardAnnounced = false;
            return;
        }

        var now = _clock.UtcNow;
        _deadBoardSince ??= now;
        if (!_deadBoardAnnounced
            && (now - _deadBoardSince.Value).TotalMilliseconds > WedgeTimeoutMs)
        {
            _deadBoardAnnounced = true;
            events.Add("wedge:DeadBoard");
        }
    }
}
