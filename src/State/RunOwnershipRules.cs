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

// Whether the run a follow window is watching is still the run that accepted
// the verb. Stated over plain identity strings so the unit tests CI does run
// cover it — the bridge is the only place that knows the live RunState.
internal static class RunOwnershipRules
{
    public const string NoRun = "none";

    public static RunOwnership For(string action) => action switch
    {
        "new-run" => RunOwnership.StartsRun,
        "abandon" => RunOwnership.EndsRun,
        _ => RunOwnership.Bound,
    };

    // A run ending naturally (defeat, victory, a trial's double-down) keeps
    // its RunState through game_over, so the identity a bound verb accepted
    // stays live to the end of its own follow window. Seeing any other
    // identity means someone else's abandon or new-run landed inside that
    // window, and the observation on hand describes a board this verb never
    // acted on.
    public static bool IsOwnerChange(
        RunOwnership ownership, string acceptedRunId, string observedRunId)
    {
        if (observedRunId == acceptedRunId) return false;
        return ownership switch
        {
            // new-run mints the run it is followed into. The identity can
            // still be `none` at acceptance (RunState is published a beat
            // later), so adopting the first real run is this verb's own
            // work — but landing back on the menu is not.
            RunOwnership.StartsRun =>
                acceptedRunId != NoRun || observedRunId == NoRun,
            // abandon retires its run: the menu is the boundary it asked
            // for. A different live run means another one started meanwhile.
            RunOwnership.EndsRun => observedRunId != NoRun,
            _ => true,
        };
    }
}
