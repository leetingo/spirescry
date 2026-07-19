namespace Spirescry.State;

// The engine reports faults inside its own fire-and-forget task chains as
// Error-level log lines, not as exceptions any tracked task surfaces —
// these tokens are how those faults enter the revision stream and the
// follow response's errors array. Pure string logic, engine-free, so the
// unit layer can pin the format.
public static class ErrorEvents
{
    public const string EnginePrefix = ProtocolVocabulary.FaultEvents.EngineError;
    public const string AsyncPrefix = ProtocolVocabulary.FaultEvents.AsyncFault;
    // Known-benign engine error lines: kept in the event stream for
    // forensics, excluded from the errors array — they must not read as
    // pollution.
    public const string NotePrefix = ProtocolVocabulary.FaultEvents.EngineNote;

    public static string FromLogLine(string text, bool combatInProgress) =>
        $"{(IsKnownBenignLogLine(text, combatInProgress) ? NotePrefix : EnginePrefix)}{Condense(text)}";

    // The one error line the engine emits on a healthy path: the victory
    // stale-pop (EndPlayerTurnAction popped after combat teardown already
    // drained it). Mirrors SettlementModule's VictorySettled gate — same
    // message pattern (shared predicate) and the same "combat is over"
    // requirement: the identical text logged mid-combat is queue
    // corruption and stays a real error. The caller supplies the combat
    // flag; when it can't be read, pass true so unknown context degrades
    // toward reporting, never toward hiding.
    private static bool IsKnownBenignLogLine(string text, bool combatInProgress) =>
        !combatInProgress
        && text.Contains("InvalidOperationException", StringComparison.Ordinal)
        && SettlementModule.IsStalePopMessage(text);

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
