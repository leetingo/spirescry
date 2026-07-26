namespace Spirescry.State;

// When a room's decision is actually finished with the local seat, and so
// may be left. Both boots gate `proceed` on these rules, and they need no
// engine types to decide — the end-to-end suite that exercises the real
// rooms cannot run in GitHub-hosted CI, so the rule itself lives where the
// unit tests reach it.
internal static class ProceedReadiness
{
    // One event option reduced to the flags that decide whether it still
    // owes the player a decision.
    internal readonly record struct EventOptionGate(
        bool IsProceed, bool IsLocked, bool WasChosen);

    // An event page may be left exactly when the engine itself offers a way
    // out. NEventRoom renders CurrentOptions and only swaps the whole page
    // for a lone synthetic PROCEED option once the event IsFinished, so a
    // page that still has a pickable, non-leave option is a required choice
    // the GUI gives the player no way to walk past. `proceed` used to do it
    // anyway — one verb skipped Neow's blessing and every event body with
    // it (#146).
    //
    // "Pickable" is deliberately the same predicate DecisionProjection uses
    // to advertise `option`: unlocked and unchosen. That keeps the two verbs
    // complementary — an event page always advertises at least one of them,
    // so gating proceed can never wedge a caller on a page whose options are
    // all spent (several events only mark their choices Chosen) or all
    // locked.
    internal static bool EventReady(
        bool finished, IReadOnlyList<EventOptionGate> options) =>
        finished
        || options.Any(option => option.IsProceed && !option.IsLocked)
        || !options.Any(option => !option.IsLocked && !option.WasChosen);

    // A rest site may be left once the seat has nothing left to choose, or
    // has already spent its choice. NRestSiteRoom enables its proceed button
    // on exactly those two edges: no options when the screen activates, and
    // AfterSelectingOptionAsync. The second edge is load-bearing — a hook
    // can leave the remaining options standing after one is taken, and the
    // GUI still lets that player walk away.
    internal static bool RestSiteReady(int optionCount, bool optionChosen) =>
        optionCount == 0 || optionChosen;
}
