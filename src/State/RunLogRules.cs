namespace Spirescry.State;

// One accepted verb reduced to the plain values the recipe rules judge: the
// run it was attributed to, whether it opened the log, and whether follow saw
// it through to a fingerprinted, replayable boundary.
internal readonly record struct RunLogVerbFacts(
    string RunId,
    string Action,
    SettlementOutcome? Outcome,
    string? Fingerprint);

// RunLog rules that need no engine types to decide. They live apart from
// RunLog so they compile — and get tested — without the game's dlls: the
// end-to-end suite cannot run in GitHub-hosted CI, so any rule that can be
// stated over plain values belongs here, where the unit tests reach it.
internal static class RunLogRules
{
    // The RunId a log carries before the engine has assigned one, and the one
    // the bridge reports when no run is live.
    internal const string NoRun = "none";

    // Every recipe opens with the verb that starts the run it reconstructs.
    internal const string OpeningAction = "new-run";

    // A recipe is replayable only when every accepted verb was followed to a
    // verified boundary. Merely sharing one RunId is not enough: otherwise
    // replay could report success after checking zero (or only a prefix of)
    // fingerprints.
    internal static bool IsComplete(
        string runId, IReadOnlyList<RunLogVerbFacts> verbs) =>
        IsCoherent(runId, verbs) && verbs.All(IsVerified);

    // A history belongs to exactly one identified run, and starts at its
    // beginning — a follow-on prefix of somebody else's run cannot be replayed
    // from a clean menu.
    private static bool IsCoherent(
        string runId, IReadOnlyList<RunLogVerbFacts> verbs) =>
        runId != NoRun
        && verbs.Count > 0
        && verbs[0].Action == OpeningAction
        && verbs.All(verb => verb.RunId == runId);

    // Only a settled or next-decision boundary yields state worth comparing
    // against; a faulted or timed-out verb left the engine somewhere replay
    // cannot check, so its fingerprint is deliberately absent.
    private static bool IsVerified(RunLogVerbFacts verb) =>
        verb.Outcome is { } outcome
        && outcome.IsReplayable()
        && !string.IsNullOrWhiteSpace(verb.Fingerprint);

    // Whether a history recorded before the engine assigned a RunId may be
    // relabelled with `candidateRunId` after the fact. Rejects a missing
    // candidate (nothing to adopt), a log already owned by a run (relabelling
    // it would forge attribution), and a mixed or headless-truncated history
    // (some verb already names a run, or the log does not open the run).
    internal static bool CanAdopt(
        string logRunId,
        string candidateRunId,
        IReadOnlyList<RunLogVerbFacts> verbs) =>
        candidateRunId != NoRun
        && logRunId == NoRun
        && verbs.Count > 0
        && verbs[0].Action == OpeningAction
        && verbs.All(verb => verb.RunId == NoRun);
}
