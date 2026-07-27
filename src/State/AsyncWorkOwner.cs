namespace Spirescry.State;

// Which run a piece of asynchronous work answers to, carried on the async
// control flow itself.
//
// Two channels can take a completion into the wrong run. One is the task's
// own fault, and FireAndForgetTracker's ledger closes it. The other is the
// engine log: the engine catches exceptions from fire-and-forget chains and
// only logs them, and OnEngineLog folds every Error line back into the live
// run's error journal — so an abandoned run's work can publish an error
// without ever faulting a task the mod holds. Identity correlation cannot
// close that one, because in that shape there is no exception to correlate
// on; the work often completes successfully after writing the line.
//
// The stamp has to go on before the work starts. An await captures the
// execution context when the async method first suspends, not when Signals
// gets around to tracking the task it returned, so the pump stamps every
// main-thread job — the one point guaranteed to run before anything a verb
// starts, however deep inside the verb the task is actually created.
//
// Run is mutable because ownership is settled after the stamp: the job is
// stamped before RefreshRunIdentity has run, work binds to its run when it
// is tracked, and ownerless work is adopted by the run it brings up (see
// FireAndForgetTracker). Signals holds its lock around every read and write.
internal sealed class AsyncWorkOwner
{
    private static readonly AsyncLocal<AsyncWorkOwner?> Ambient = new();

    // The run this work answers to, or null while it answers to none: menu
    // work, and jobs that never tracked anything at all.
    public object? Run { get; set; }

    // Set for work whose own job is to end the run that owns it — `abandon`
    // tears down the very RunState its task was bound to. Without this the
    // teardown's failure would be retired by the rotation the teardown
    // itself causes. FireAndForgetTracker spends the flag on that rotation.
    public bool EndsRun { get; set; }

    // The owner stamped on the caller's flow, or null when the caller is not
    // running inside a pump job — the engine's own threads and the boot path.
    public static AsyncWorkOwner? Current => Ambient.Value;

    // Runs `job` under a fresh owner. Restoring in the finally is what keeps
    // the stamp off the pump thread's next job; work `job` started already
    // captured a context of its own that holds this owner.
    public static T Stamp<T>(Func<T> job)
    {
        var previous = Ambient.Value;
        Ambient.Value = new AsyncWorkOwner();
        try { return job(); }
        finally { Ambient.Value = previous; }
    }
}
