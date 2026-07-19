namespace Spirescry.State;

// Engine collections can change as an action crosses phase boundaries.
// Materialize them once before deriving counts and semantic tokens so a
// snapshot never returns to the live enumerator for a second pass.
internal static class CollectionSnapshot
{
    internal static T[] Once<T>(IEnumerable<T> source) => source.ToArray();
}
