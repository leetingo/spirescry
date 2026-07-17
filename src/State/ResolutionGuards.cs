namespace Spirescry.State;

internal enum InlineFaultKind
{
    VictorySettled,
    Partial,
    Failed,
}

// Pure predicates for the two headless resolution fault shapes. Keeping
// classification independent of engine singletons makes the boundary
// exhaustive and directly regression-testable.
internal static class ResolutionGuards
{
    public static InlineFaultKind ClassifyInlineFault(
        Exception ex,
        string actionName,
        bool combatInProgress,
        bool revisionChanged)
    {
        // Task.Exception wraps the executor's original throw in a singleton
        // AggregateException. Accept that transport wrapper, but no aggregate
        // with multiple failures: only one exact stale-pop is known benign.
        if (ex is AggregateException aggregate)
        {
            aggregate = aggregate.Flatten();
            if (aggregate.InnerExceptions.Count == 1)
                ex = aggregate.InnerExceptions[0];
        }

        // Only the observed EndPlayerTurnAction duplicate pop is known to
        // be idempotent. A different action, or an exception merely using
        // similar queue wording, still represents unknown corruption.
        if (!combatInProgress
            && actionName == "EndPlayerTurnAction"
            && ex is InvalidOperationException
            && ex.Message.Contains(
                "Tried to pop action EndPlayerTurnAction", StringComparison.Ordinal)
            && ex.Message.Contains("didn't find it in any queue", StringComparison.Ordinal))
            return InlineFaultKind.VictorySettled;

        return revisionChanged ? InlineFaultKind.Partial : InlineFaultKind.Failed;
    }

    public static bool IsDeadBoardCandidate(
        bool actionRunning,
        bool pickerActive,
        bool combatInProgress,
        bool combatIsEnding,
        bool queuesEmpty,
        bool allEnemiesDead) =>
        !actionRunning
        && !pickerActive
        && combatInProgress
        && !combatIsEnding
        && queuesEmpty
        && allEnemiesDead;
}
