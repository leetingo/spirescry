namespace Spirescry.State;

// Run ownership for ordinary fire-and-forget work — dispatcher verbs and
// the engine-log correlation windows. Signals owns the lock around every
// call; this class owns the rule for which run a completion still belongs
// to. Engine-free on purpose: the rule is stated over a run token, a Task
// and an AsyncWorkOwner, so the unit tests CI does run can cover it.
//
// Ownership is reference identity on the RunState, the same token Signals
// mints run ids from. Work registered while run X was active answers to X
// and to nothing else: once X stops being the active run, its completions
// must not reach the revision stream, the error journal, the engine-log
// correlation, or the follow result of whatever run comes next. Both
// channels a completion can take are covered — the task's own fault, via
// the ledger below, and any engine Error line the work writes, via the
// owner stamped on its async flow.
//
// Retired work leaves the pending ledger at the rotation, not at its
// completion. The follow probe counts pending fire-and-forget work as work
// the run still owes, and an abandoned run's task can be parked on a scene
// that no longer exists — one zombie would otherwise hold every later run
// busy until the host restarts.
internal sealed class FireAndForgetTracker
{
    // Task -> the owner record stamped on the work's async flow. A retired
    // task is simply absent, so nothing accumulates for work that never
    // completes.
    private readonly Dictionary<Task, AsyncWorkOwner> _pending = new();
    private object? _run;

    public int PendingCount => _pending.Count;

    public void ChangeRun(object? runState)
    {
        if (ReferenceEquals(runState, _run)) return;
        _run = runState;
        foreach (var (task, owner) in _pending.ToArray())
        {
            // Work tracked with no run active is adopted by the first run
            // that appears rather than retired: `new-run` tracks the very
            // task that mints the next RunState, and retiring it would hide
            // the failure of the verb that started the run. From then on it
            // answers to that run like anything else, so the following
            // rotation retires it — nothing here is immortal, and a menu
            // task that never completes cannot pin the ledger (and with it
            // every later run's follow probe) for the rest of the process.
            if (owner.Run is null && runState is not null) owner.Run = runState;
            else if (!ReferenceEquals(owner.Run, runState)) _pending.Remove(task);
        }
    }

    // `owner` is the stamp the pump put on this work's async flow, or null
    // for work started outside a pump job — the per-tick event-option sweep
    // and the engine-log correlation windows. Binding the run here rather
    // than at the stamp is deliberate: a job is stamped before it calls
    // RefreshRunIdentity, so the owning run is only settled once the verb is
    // actually dispatching.
    public void Track(Task task, AsyncWorkOwner? owner)
    {
        var record = owner ?? new AsyncWorkOwner();
        record.Run = _run;
        _pending[task] = record;
    }

    // True means the completion still belongs to the run that started the
    // work and may publish. False means a rotation already retired it — the
    // ledger dropped it then, so the completion must change nothing at all.
    public bool Complete(Task task) => _pending.Remove(task);

    // The engine-log half of the same rule: an Error line written by work a
    // previous run started belongs to that run's journal, not to the live
    // one's. Work that answers to no run — menu work, and anything started
    // outside a pump job — always publishes: unknown context must degrade
    // toward reporting an error, never toward hiding one.
    public bool WrittenByRetiredWork(AsyncWorkOwner? owner) =>
        owner is { Run: not null } work && !ReferenceEquals(work.Run, _run);
}
