namespace Spirescry.State;

// The engine reports faults inside its own fire-and-forget task chains as
// Error-level log lines, not as exceptions any tracked task surfaces —
// these tokens are how those faults enter the revision stream and the
// follow response's errors array. Pure string logic, engine-free, so the
// unit layer can pin the format.
public static class ErrorEvents
{
    public const string EnginePrefix = "engine_error:";
    public const string AsyncPrefix = "async_fault:";

    public static string FromLogLine(string text) =>
        $"{EnginePrefix}{Condense(text)}";

    public static string FromAsyncFault(
        string label, string exceptionType, string message) =>
        $"{AsyncPrefix}{label}:{exceptionType}:{Condense(message)}";

    public static bool IsError(string eventType) =>
        eventType.StartsWith(EnginePrefix, StringComparison.Ordinal)
        || eventType.StartsWith(AsyncPrefix, StringComparison.Ordinal);

    // Multi-line exception dumps become one bounded token: the event log
    // is a ring buffer of short entries, and the full text stays in the
    // host log.
    private static string Condense(string text)
    {
        var message = string.Join(' ', text.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return message.Length > 160 ? message[..160] : message;
    }
}
