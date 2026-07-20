using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace Spirescry.State;

// Run-scoped lifecycle for EventSynchronizer option work. Signals owns
// the lock around every call: this class owns the state-machine invariants
// (owner → seen → pending → complete, or owner → retired → suppressed)
// without introducing a second lock or split counter snapshot.
internal sealed class EventOptionTracker
{
    // Correlation state for a retired task survives one later tracking
    // epoch; by the second rotation its TaskHelper line can no longer be in
    // flight, so the entry expires even if that line never arrived. Expiry
    // can never resurrect the task into pending work — that is the
    // tombstone's job, and it keeps its own lifetime.
    private const int RetiredTrackingEpochLifetime = 2;

    private readonly HashSet<Task> _seen = new();
    private readonly HashSet<Task> _pending = new();
    // Retired work whose engine log may still need suppressing, keyed to the
    // epoch that retired it. HasRetired gates the identity-correlation window
    // in Signals, so this MUST expire: a task parked on a combat or dialog
    // that no longer exists never completes, and keeping its entry would
    // route every later engine error through that window — in every later
    // run — for the rest of the process.
    private readonly Dictionary<Task, long> _retired = new();
    // Tasks that must not re-enter pending work while the synchronizer that
    // retired them still owns the run: the engine keeps its own list, and the
    // per-tick sweep would otherwise re-track a task nothing will ever
    // complete, leaving every follow busy. Held apart from _retired so
    // correlation state can expire without lifting this block.
    private readonly Dictionary<Task, EventSynchronizer?> _tombstones = new();
    private object? _runState;
    private EventSynchronizer? _synchronizer;
    private long _generation;
    private long _trackingEpoch;

    public int PendingCount => _pending.Count;
    public bool HasRetired => _retired.Count > 0;

    public bool ChangeOwner(object? runState, EventSynchronizer? synchronizer)
    {
        if (ReferenceEquals(runState, _runState)
            && ReferenceEquals(synchronizer, _synchronizer))
            return false;
        ResetGeneration();
        _runState = runState;
        _synchronizer = synchronizer;
        _trackingEpoch++;
        PruneRetiredWork();
        return true;
    }

    public bool MatchesOwner(object? runState, EventSynchronizer? synchronizer) =>
        _runState is not null
        && ReferenceEquals(runState, _runState)
        && ReferenceEquals(synchronizer, _synchronizer);

    public bool SharedVotePending() =>
        _runState is not null && EventSync.SharedVotePending(_synchronizer);

    public void Drop() => ResetGeneration();

    public bool TryTrack(Task task, out long generation)
    {
        generation = _generation;
        if (_runState is null || _synchronizer is null)
            return false;
        // RunManager can expose the same synchronizer across a RunState
        // transition. Its old list is still readable, so remember which
        // synchronizer retired each task and never let that same owner
        // source re-register it under the new generation.
        if (_tombstones.TryGetValue(task, out var retiredBy)
            && ReferenceEquals(retiredBy, _synchronizer))
            return false;
        if (!_seen.Add(task))
            return false;
        _pending.Add(task);
        return true;
    }

    // True means this completion belongs to the current owner and should
    // publish. A stale success needs no correlation and leaves the ledger
    // immediately; a stale fault stays until its matching TaskHelper log is
    // consumed, or until its epoch expires.
    public bool Complete(Task task, long generation)
    {
        if (generation != _generation)
        {
            if (task.Exception is null) _retired.Remove(task);
            return false;
        }
        _pending.Remove(task);
        return true;
    }

    public void MarkRetiredFaultLogResolved(Task task) => _retired.Remove(task);

    private void ResetGeneration()
    {
        foreach (var task in _pending)
        {
            _retired[task] = _trackingEpoch;
            _tombstones[task] = _synchronizer;
        }
        _pending.Clear();
        _seen.Clear();
        _generation++;
    }

    private void PruneRetiredWork()
    {
        foreach (var (task, retiredAtEpoch) in _retired.ToArray())
            if (_trackingEpoch - retiredAtEpoch >= RetiredTrackingEpochLifetime
                || (task.IsCompleted && task.Exception is null))
                _retired.Remove(task);
        // A tombstone is only needed while its own synchronizer owns the run:
        // once another one does, the engine can no longer hand that task back
        // from the old list, and TryTrack's same-source guard would not fire
        // anyway.
        if (_synchronizer is null) return;
        foreach (var (task, retiredBy) in _tombstones.ToArray())
            if (!ReferenceEquals(retiredBy, _synchronizer))
                _tombstones.Remove(task);
    }
}
