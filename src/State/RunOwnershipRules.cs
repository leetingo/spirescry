namespace Spirescry.State;

// What a followed verb is allowed to do to the run that accepted it. Only
// the two lifecycle verbs move their own run: everything else acts inside
// one run and must be observed there.
internal enum RunOwnership
{
    Bound,
    StartsRun,
    EndsRun,
}

// Whether the board a follow window is watching still belongs to the run
// that accepted the verb. Stated over plain values so the unit tests CI does
// run cover it — the bridge is the only place that knows the live RunState.
internal static class RunOwnershipRules
{
    public const string NoRun = "none";

    public static RunOwnership For(string action) => action switch
    {
        "new-run" => RunOwnership.StartsRun,
        "abandon" => RunOwnership.EndsRun,
        _ => RunOwnership.Bound,
    };

    // Identity alone is not ownership. A run ending naturally (defeat,
    // victory, a trial's double-down) keeps its RunState through game_over,
    // so the identity a bound verb accepted stays live to the end of its own
    // follow window — but the converse also holds, and it is the dangerous
    // half: the GUI keeps the retired RunState on RunManager after
    // ReturnToMainMenuAfterRun. That is exactly why PhaseDetector lets a
    // visible main menu win over RunManager's terminal flags, and why
    // new-run's `run_exists` rejection tells the caller to abandon first. In
    // that window a foreign abandon leaves the accepted identity live under a
    // quiet, decision-free menu — the exact shape the settlement loop reads
    // as Settled, and Settled is replayable. A board is therefore this verb's
    // only while the accepted identity is live AND the run is still on screen.
    //
    // `runSeenInPlay` says some observation in this window has already shown
    // a real run outside the menu, so a later menu means the run left the
    // board rather than that it has not arrived yet. new-run needs that
    // distinction: RunState identity exists before the local seat is mounted
    // (see Signals.RefreshRunIdentity and LocalRunContext.StateOnly, which
    // the player-gated PhaseDetector does not see), so a launch legitimately
    // reads main_menu under a concrete run id.
    public static bool IsOwnerChange(
        RunOwnership ownership,
        string acceptedRunId,
        string observedRunId,
        Phase observedPhase,
        bool runSeenInPlay)
    {
        // abandon retires its run: the menu is the boundary it asked for.
        // A different live run means another one started meanwhile.
        if (ownership == RunOwnership.EndsRun)
            return observedRunId != acceptedRunId && observedRunId != NoRun;

        // The run this verb was acting inside is off the board.
        if (observedPhase == Phase.MainMenu && runSeenInPlay) return true;

        if (observedRunId == acceptedRunId) return false;
        // new-run mints the run it is followed into, and the identity can
        // still be `none` at acceptance, so adopting the first real run is
        // this verb's own work. Every other identity change belongs to
        // someone else: a bound verb owns exactly one identity.
        return ownership != RunOwnership.StartsRun || acceptedRunId != NoRun;
    }

    // Has the run's own board been seen? A live identity outside the main
    // menu is the only observation that proves it — including the accepted
    // phase and run id of a verb that was already dispatched inside a run.
    public static bool SeenInPlay(string runId, Phase phase) =>
        runId != NoRun && phase != Phase.MainMenu;
}
