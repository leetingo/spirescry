namespace Spirescry.State;

internal readonly record struct TaskFault(Exception Cause)
{
    public string TypeName => Cause.GetType().Name;
    public string Message => Cause.Message;

    public bool AppearsIn(string text) =>
        text.Contains(TypeName, StringComparison.Ordinal)
        && text.Contains(Message, StringComparison.Ordinal);

    public static TaskFault? From(Task task) =>
        task.Exception is not { } aggregate
            ? null
            : new TaskFault(
                aggregate.Flatten().InnerExceptions.FirstOrDefault()
                ?? aggregate);
}
