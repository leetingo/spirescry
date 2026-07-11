namespace Spirescry.State;

// Pure predicates for the two headless resolution fault shapes. Keeping
// classification independent of engine singletons makes the boundary
// exhaustive and directly regression-testable.
internal static class ResolutionGuards
{
    public static bool IsVictoryTeardownPop(Exception ex, bool combatInProgress) =>
        !combatInProgress
        && ex is InvalidOperationException
        && ex.Message.Contains("didn't find it in any queue", StringComparison.Ordinal);

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
