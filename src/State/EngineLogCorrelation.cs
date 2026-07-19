namespace Spirescry.State;

internal enum EngineLogDisposition { Publish, Suppress }

internal sealed class PendingEngineLog(
    string text, bool combatInProgress, int threadId)
{
    public string Text { get; } = text;
    public bool CombatInProgress { get; } = combatInProgress;
    public int ThreadId { get; } = threadId;
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
        string text, bool combatInProgress, int threadId)
    {
        var pending = new PendingEngineLog(text, combatInProgress, threadId);
        _pending.Add(pending);
        return pending;
    }

    public bool ResolveForTask(
        Task task, int threadId, EngineLogDisposition disposition)
    {
        if (Cause(task) is not { } cause) return false;
        var pending = _pending.LastOrDefault(entry =>
            entry.ThreadId == threadId
            && entry.Text.Contains(cause.GetType().Name, StringComparison.Ordinal)
            && entry.Text.Contains(cause.Message, StringComparison.Ordinal));
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

    private static Exception? Cause(Task task) =>
        task.Exception is not { } aggregate
            ? null
            : aggregate.Flatten().InnerExceptions.FirstOrDefault() ?? aggregate;
}
