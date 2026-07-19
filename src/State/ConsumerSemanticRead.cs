namespace Spirescry.State;

// Strict boundary for engine values that feed settlement/replay semantics.
// A real false or empty value is meaningful; a failed read must not be
// converted into one and mistaken for a stable observation.
internal static class ConsumerSemanticRead
{
    internal static bool CardPlayable(Func<bool> read) =>
        CollectionSnapshot.ReadStable("card playable semantic state", read);

    internal static string[] MapMarkerIdentities(
        string component, Func<IEnumerable<string>> read) =>
        CollectionSnapshot.ReadStable(
            component,
            () => read().OrderBy(identity => identity, StringComparer.Ordinal).ToArray());
}
