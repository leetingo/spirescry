namespace Spirescry.State;

internal readonly record struct RevisionEntry(long Revision, string Type);

// Bounded append-only history. Signals supplies synchronization so both the
// public event trail and the dedicated error trail share one storage/query
// contract without paying for a second lock or reconstructing snapshots.
internal sealed class RevisionJournal(int capacity)
{
    private readonly List<RevisionEntry> _entries = new();

    public void Add(long revision, string type)
    {
        _entries.Add(new RevisionEntry(revision, type));
        if (_entries.Count > capacity) _entries.RemoveAt(0);
    }

    public RevisionEntry[] Since(long revision) =>
        _entries.Where(entry => entry.Revision > revision).ToArray();

    public string[] TypesSince(long revision) =>
        _entries.Where(entry => entry.Revision > revision)
            .Select(entry => entry.Type)
            .ToArray();
}
