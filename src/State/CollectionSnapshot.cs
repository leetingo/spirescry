namespace Spirescry.State;

// Engine collections can change as an action crosses phase boundaries.
// Materialize them once before deriving counts and semantic tokens so a
// snapshot never returns to the live enumerator for a second pass.
internal static class CollectionSnapshot
{
    private const int MaxReadAttempts = 3;
    private const string CollectionModifiedPrefix = "Collection was modified;";

    internal static T[] Once<T>(IEnumerable<T> source) => source.ToArray();

    // The callback must finish the read (including materialization) before it
    // returns. Only the runtime's known live-enumerator mutation is transient;
    // every other failure is surfaced immediately with semantic component
    // context, and a persistently mutating collection fails after three tries.
    internal static TResult ReadStable<TResult>(
        string component, Func<TResult> read)
    {
        for (var attempt = 1; attempt <= MaxReadAttempts; attempt++)
        {
            try
            {
                return read();
            }
            catch (InvalidOperationException ex) when (
                IsCollectionMutation(ex) && attempt < MaxReadAttempts)
            {
                // The engine may be between two phase-boundary mutations.
                // Retry immediately on the main thread and take a fresh pass.
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"failed to read {component} after {attempt} "
                    + (attempt == 1 ? "attempt" : "attempts"),
                    ex);
            }
        }

        throw new System.Diagnostics.UnreachableException();
    }

    private static bool IsCollectionMutation(InvalidOperationException error) =>
        error.Message.StartsWith(CollectionModifiedPrefix, StringComparison.Ordinal);
}
