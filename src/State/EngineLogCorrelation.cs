namespace Spirescry.State;

internal enum EngineLogDisposition { Publish, Suppress }

internal readonly record struct ManagedThreadId(int Value)
{
    public static ManagedThreadId Current =>
        new(Environment.CurrentManagedThreadId);
}

internal readonly record struct EngineLogCorrelationKey(
    ManagedThreadId Thread, TaskFault Fault)
{
    public bool Matches(PendingEngineLog pending) =>
        pending.Thread == Thread && Fault.AppearsIn(pending.Text);
}

internal sealed class PendingEngineLog(
    string text, bool combatInProgress, bool headlessHost,
    ManagedThreadId thread)
{
    public string Text { get; } = text;
    public bool CombatInProgress { get; } = combatInProgress;
    public bool HeadlessHost { get; } = headlessHost;
    public ManagedThreadId Thread { get; } = thread;
    public TaskCompletionSource<EngineLogDisposition> Resolution { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// TaskHelper writes its Error line synchronously immediately before the
// returned task transitions to Faulted. An ExecuteSynchronously completion
// therefore supplies the missing identity: same execution thread, same
// exception, and the concrete task whose ownership Signals already knows.
// Unclaimed lines expire to Publish; text alone can never suppress a line.
internal sealed class EngineLogCorrelation
{
    private readonly List<PendingEngineLog> _pending = new();

    public PendingEngineLog Register(
        string text, bool combatInProgress, bool headlessHost,
        ManagedThreadId thread)
    {
        var pending = new PendingEngineLog(
            text, combatInProgress, headlessHost, thread);
        _pending.Add(pending);
        return pending;
    }

    public bool ResolveForTask(
        Task task, ManagedThreadId thread, EngineLogDisposition disposition)
    {
        if (TaskFault.From(task) is not { } fault) return false;
        var key = new EngineLogCorrelationKey(thread, fault);
        var pending = _pending.LastOrDefault(key.Matches);
        if (pending is null) return false;
        _pending.Remove(pending);
        pending.Resolution.TrySetResult(disposition);
        return true;
    }

    public bool Expire(PendingEngineLog pending)
    {
        if (!_pending.Remove(pending)) return false;
        pending.Resolution.TrySetResult(EngineLogDisposition.Publish);
        return true;
    }

}
