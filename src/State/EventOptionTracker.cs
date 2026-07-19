using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace Spirescry.State;

// Run-scoped lifecycle for EventSynchronizer option work. Signals owns
// the lock around every call: this class owns the state-machine invariants
// (owner → seen → pending → complete, or owner → retired → suppressed)
// without introducing a second lock or split counter snapshot.
internal sealed class EventOptionTracker
{
    private sealed class RetiredTask(EventSynchronizer? source)
    {
        public EventSynchronizer? Source { get; } = source;
        public bool FaultLogResolved { get; set; }
    }

    private readonly HashSet<Task> _seen = new();
    private readonly HashSet<Task> _pending = new();
    private readonly Dictionary<Task, RetiredTask> _retired = new();
    private object? _runState;
    private EventSynchronizer? _synchronizer;
    private long _generation;

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
        PruneRetiredFromPreviousSynchronizers();
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
        if (_retired.TryGetValue(task, out var retired)
            && ReferenceEquals(retired.Source, _synchronizer))
            return false;
        if (!_seen.Add(task))
            return false;
        _pending.Add(task);
        return true;
    }

    // True means this completion belongs to the current owner and should
    // publish. A stale success can leave the retired ledger immediately;
    // a stale fault remains until its matching TaskHelper log is consumed.
    public bool Complete(Task task, long generation)
    {
        if (generation != _generation)
        {
            if (task.Exception is null
                && _retired.TryGetValue(task, out var retired)
                && CanDiscard(retired))
                _retired.Remove(task);
            return false;
        }
        _pending.Remove(task);
        return true;
    }

    public void MarkRetiredFaultLogResolved(Task task)
    {
        if (!_retired.TryGetValue(task, out var retired)) return;
        retired.FaultLogResolved = true;
        if (CanDiscard(retired)) _retired.Remove(task);
    }

    public static Exception? Cause(Task task) =>
        task.Exception is not { } aggregate
            ? null
            : aggregate.Flatten().InnerExceptions.FirstOrDefault() ?? aggregate;

    private void ResetGeneration()
    {
        foreach (var task in _pending)
            _retired[task] = new RetiredTask(_synchronizer);
        _pending.Clear();
        _seen.Clear();
        _generation++;
    }

    private bool CanDiscard(RetiredTask retired) =>
        _synchronizer is not null
        && !ReferenceEquals(retired.Source, _synchronizer);

    private void PruneRetiredFromPreviousSynchronizers()
    {
        if (_synchronizer is null) return;
        foreach (var (task, retired) in _retired.ToArray())
        {
            if (!CanDiscard(retired)) continue;
            var completedWithoutFault = task.IsCompleted && task.Exception is null;
            if (completedWithoutFault || retired.FaultLogResolved)
                _retired.Remove(task);
        }
    }
}
