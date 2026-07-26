namespace Spirescry.State;

// Run ownership for ordinary fire-and-forget work — dispatcher verbs and
// the engine-log correlation windows. Signals owns the lock around every
// call; this class owns the rule for which run a completion still belongs
// to. Engine-free on purpose: the rule is stated over a run token and a
// Task, so the unit tests CI does run can cover it.
//
// Ownership is reference identity on the RunState, the same token Signals
// mints run ids from. Work registered while run X was active answers to X
// and to nothing else: once X stops being the active run, its completions
// must not reach the revision stream, the error journal, the engine-log
// correlation, or the follow result of whatever run comes next.
//
// Retired work leaves the pending ledger at the rotation, not at its
// completion. The follow probe counts pending fire-and-forget work as work
// the run still owes, and an abandoned run's task can be parked on a scene
// that no longer exists — one zombie would otherwise hold every later run
// busy until the host restarts.
internal sealed class FireAndForgetTracker
{
    // Task -> the run that owned it when it was tracked. A retired task is
    // simply absent, so nothing accumulates for work that never completes.
    private readonly Dictionary<Task, object?> _pending = new();
    private object? _run;

    public int PendingCount => _pending.Count;

    public void ChangeRun(object? runState)
    {
        if (ReferenceEquals(runState, _run)) return;
        _run = runState;
        foreach (var (task, owner) in _pending.ToArray())
            if (!Owns(owner, runState))
                _pending.Remove(task);
    }

    public void Track(Task task) => _pending[task] = _run;

    // True means the completion still belongs to the run that started the
    // work and may publish. False means a rotation already retired it — the
    // ledger dropped it then, so the completion must change nothing at all.
    public bool Complete(Task task) => _pending.Remove(task);

    // Work registered while NO run was active is never retired: `new-run`
    // tracks the very task that mints the next RunState, and retiring it
    // would hide the failure of the verb that started the run. Menu work
    // therefore errs toward reporting a fault rather than hiding one — the
    // same trade the retired-correlation window makes.
    private static bool Owns(object? owner, object? runState) =>
        owner is null || ReferenceEquals(owner, runState);
}
