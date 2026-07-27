using System.Runtime.CompilerServices;

namespace Spirescry.State;

// How a selection surface reports which of its offered rows are already
// picked. Stated over plain references so it compiles — and gets tested —
// without the game's dlls: the end-to-end suite cannot run in GitHub-hosted
// CI, so the rule lives here rather than inline in Snapshotter.
internal static class SelectionProjection
{
    // The instances a decision has picked, read ONCE per snapshot. The
    // headless stand-in picker hands out its live picked list, so asking it
    // per row would read a collection the pick verb mutates as many times as
    // there are rows, and answer each row in a pass over every pick.
    internal static IReadOnlySet<TCard> Picked<TCard>(
        IEnumerable<TCard>? selected) where TCard : class =>
        selected is null
            ? new HashSet<TCard>(ByIdentity<TCard>.Instance)
            : new HashSet<TCard>(selected, ByIdentity<TCard>.Instance);

    // A row is selected when the decision picked THAT instance. Identity,
    // never value: a hand routinely holds several copies of one model, and
    // matching by model would light up every copy the moment one of them is
    // picked — the caller then has no way to tell which row to pick next,
    // and re-picks the first row forever.
    //
    // An empty holder (a hand slot with no card node) is never selected.
    internal static bool IsSelected<TCard>(
        TCard? card, IReadOnlySet<TCard> picked) where TCard : class =>
        card is not null && picked.Contains(card);

    // Card models inherit the default reference Equals, so a plain HashSet
    // would agree today — but nothing in the game's types promises that, and
    // a value-equal model would silently bring the repicking loop back.
    private sealed class ByIdentity<TCard> : IEqualityComparer<TCard>
        where TCard : class
    {
        internal static readonly ByIdentity<TCard> Instance = new();

        public bool Equals(TCard? left, TCard? right) =>
            ReferenceEquals(left, right);

        public int GetHashCode(TCard card) => RuntimeHelpers.GetHashCode(card);
    }
}
