namespace Spirescry.State;

// How a selection surface reports which of its offered rows are already
// picked. Stated over plain references so it compiles — and gets tested —
// without the game's dlls: the end-to-end suite cannot run in GitHub-hosted
// CI, so the rule lives here rather than inline in Snapshotter.
internal static class SelectionProjection
{
    // A row is selected when the decision's selected list holds THAT
    // instance. Identity, never value: a hand routinely holds several
    // copies of one model, and matching by model would light up every
    // copy the moment one of them is picked — the caller then has no way
    // to tell which row to pick next, and re-picks the first row forever.
    //
    // An empty holder (a hand slot with no card node) is never selected.
    internal static bool IsSelected<TCard>(
        TCard? card, IReadOnlyCollection<TCard>? selected)
        where TCard : class
    {
        if (card is null || selected is null) return false;
        foreach (var picked in selected)
            if (ReferenceEquals(picked, card)) return true;
        return false;
    }
}
