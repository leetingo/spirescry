namespace Spirescry.State;

// Run-report rules that need no engine types to decide. They live apart from
// Snapshotter so they compile — and get tested — without the game's dlls: the
// end-to-end suite cannot run in GitHub-hosted CI, so any rule that can be
// stated over plain values belongs here, where the unit tests reach it.
internal static class RunOutcomeRules
{
    // How a finished run is reported: won, lost, or walked away from.
    //
    // `winTime` is the run's duration in WHOLE SECONDS — RunManager.RunTime is
    // ToUnixTimeSeconds-based — so it is 0 for any run shorter than a second,
    // which is routine for a cheat-driven headless clear. It may corroborate a
    // victory but must never be the only test; using it alone reported real
    // wins as defeats. `endedInVictoryRoom` is the engine's own signal, the one
    // NGameOverScreen uses: `CurrentRoom?.IsVictoryRoom`, the final act's
    // Architect event.
    //
    // Abandoning outranks both: a run walked away from is not a loss on the
    // board, and the caller cannot tell the difference from position alone.
    internal static string GameOverOutcome(
        bool isAbandoned, bool endedInVictoryRoom, long winTime) =>
        isAbandoned
            ? "abandoned"
            : endedInVictoryRoom || winTime > 0
                ? "victory"
                : "defeat";
}
