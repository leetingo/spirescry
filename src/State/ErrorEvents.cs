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

    public static string FromLogLine(
        string text, bool combatInProgress, bool headlessHost = false) =>
        $"{(IsKnownBenignLogLine(text, combatInProgress, headlessHost) ? NotePrefix : EnginePrefix)}{Condense(text)}";

    // The one error line the engine emits on a healthy path: the victory
    // stale-pop (EndPlayerTurnAction popped after combat teardown already
    // drained it). Mirrors SettlementModule's VictorySettled gate — same
    // message pattern (shared predicate) and the same "combat is over"
    // requirement: the identical text logged mid-combat is queue
    // corruption and stays a real error. The caller supplies the combat
    // flag; when it can't be read, pass true so unknown context degrades
    // toward reporting, never toward hiding.
    private static bool IsKnownBenignLogLine(
        string text, bool combatInProgress, bool headlessHost) =>
        (!combatInProgress
            && text.Contains("InvalidOperationException", StringComparison.Ordinal)
            && SettlementModule.IsStalePopMessage(text))
        || (headlessHost && IsHeadlessCompletionNoise(text));

    // The pure host deliberately omits presentation and persistent-profile
    // infrastructure. These exact engine call paths log after their gameplay
    // effect has reached its completion boundary: combat rewards exist or an
    // event has already returned to the map.
    // Keep the full line as an engine_note for diagnostics, but do not turn a
    // successfully followed action into a fault. The progress logger exposes
    // only its exact message to subscribers; exception-backed event noise is
    // narrowed by both message and owning stack frames. Similar errors stay
    // fatal.
    private static bool IsHeadlessCompletionNoise(string text) =>
        text.Equals("Act 4 is not yet implemented.", StringComparison.Ordinal)
        || text.Equals("EpochModel was not found :(", StringComparison.Ordinal)
        || (text.StartsWith(
                "System.InvalidOperationException: Tried to set event options after event was finished!",
                StringComparison.Ordinal)
            && text.Contains(
                "at MegaCrit.Sts2.Core.Models.EventModel.SetEventState(",
                StringComparison.Ordinal)
            && text.Contains(
                "at MegaCrit.Sts2.Core.Models.EventModel.SetEventFinished(",
                StringComparison.Ordinal));

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
