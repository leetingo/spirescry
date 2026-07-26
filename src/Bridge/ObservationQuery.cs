using System.Globalization;
using System.Net;

namespace Spirescry.Bridge;

// The /obs query string reduced to plain values. A parameter that is
// present but malformed is rejected rather than silently replaced by its
// default: `?wait=1s` quietly becoming a no-wait poll is indistinguishable
// from a long-poll that timed out, and `?compact=yes` quietly meaning
// "not compact" is indistinguishable from a compact snapshot of a small
// phase. Omitted parameters keep their existing defaults.
//
// The rule is stated over the raw query string and lives outside Handlers
// so it compiles — and gets tested — without the game's dlls, where the
// CI-run unit tests reach it.
internal readonly record struct ObservationQuery(
    long Since,
    int Wait,
    bool Compact,
    bool Decision,
    bool SemanticState)
{
    // The same bound /step's `follow` enforces: the bridge never parks a
    // request longer than a minute.
    internal const int MaxWaitMs = 60_000;

    // The documented boolean encodings, in the order the error message
    // lists them.
    private static readonly string[] TrueForms = ["1", "true"];
    private static readonly string[] FalseForms = ["0", "false"];

    // -1 is "no `since` given": no parking, and no `changed`/`events`
    // fields on the response.
    internal const long NoSince = -1;

    // The parameters this rule owns, spelled the way the error messages
    // name them. `known` is deliberately absent: it is legitimately
    // repeatable and each value is an opaque card key, so it stays on the
    // server's own multi-value collection.
    private static readonly string[] Names =
        ["since", "wait", "compact", "decision", "semanticState"];

    internal bool WantsChangeFeed => Since >= 0;

    internal bool ShouldPark => Since >= 0 && Wait > 0;

    // `rawQuery` is the wire query string, leading '?' optional. It is
    // split here rather than read out of the server's parsed collection
    // because that collection cannot tell `?compact` — supplied, with no
    // value — from an omitted `compact`: .NET files a valueless segment
    // under the null key, so the indexer answers null for both and the
    // silent default walks back in through the side door.
    internal static bool TryParse(
        string? rawQuery, out ObservationQuery query, out string? error)
    {
        query = default;
        var supplied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in (rawQuery ?? "").TrimStart('?').Split('&'))
        {
            if (segment.Length == 0) continue;
            var equals = segment.IndexOf('=');
            var name = Decode(equals < 0 ? segment : segment[..equals]);
            var canonical = Array.Find(
                Names,
                known => string.Equals(
                    known, name, StringComparison.OrdinalIgnoreCase));
            if (canonical is null) continue;
            // No '=' at all is a parameter supplied with an empty value,
            // not an omitted one — and empty is malformed for all five.
            var value = equals < 0 ? "" : Decode(segment[(equals + 1)..]);
            // Two spellings of one parameter have no defensible winner.
            if (!supplied.TryAdd(canonical, value))
            {
                error = $"'{canonical}' was supplied more than once";
                return false;
            }
        }

        string? Supplied(string name) =>
            supplied.TryGetValue(name, out var value) ? value : null;

        return TryParse(
            Supplied("since"), Supplied("wait"), Supplied("compact"),
            Supplied("decision"), Supplied("semanticState"),
            out query, out error);
    }

    // The decoding the server applies to a query it parses itself:
    // percent escapes resolved, '+' read as a space.
    private static string Decode(string raw) => WebUtility.UrlDecode(raw);

    private static bool TryParse(
        string? since,
        string? wait,
        string? compact,
        string? decision,
        string? semanticState,
        out ObservationQuery query,
        out string? error)
    {
        query = default;
        var parsedSince = NoSince;
        if (since is not null)
        {
            // NumberStyles.None: no sign, no whitespace, no separators —
            // "-1", " 1" and "1_000" are malformed, not clamped.
            if (!long.TryParse(
                    since, NumberStyles.None, CultureInfo.InvariantCulture,
                    out parsedSince))
            {
                error = "'since' must be a non-negative integer";
                return false;
            }
        }
        var parsedWait = 0;
        if (wait is not null)
        {
            if (!int.TryParse(
                    wait, NumberStyles.None, CultureInfo.InvariantCulture,
                    out parsedWait)
                || parsedWait > MaxWaitMs)
            {
                error = $"'wait' must be an integer in [0,{MaxWaitMs}]";
                return false;
            }
        }
        if (!TryFlag("compact", compact, out var parsedCompact, out error)
            || !TryFlag("decision", decision, out var parsedDecision, out error)
            || !TryFlag(
                "semanticState", semanticState, out var parsedSemantic, out error))
            return false;

        query = new ObservationQuery(
            parsedSince, parsedWait, parsedCompact, parsedDecision, parsedSemantic);
        error = null;
        return true;
    }

    private static bool TryFlag(
        string name, string? raw, out bool value, out string? error)
    {
        error = null;
        value = false;
        if (raw is null) return true;
        if (Matches(TrueForms, raw))
        {
            value = true;
            return true;
        }
        if (Matches(FalseForms, raw)) return true;
        error = $"'{name}' must be one of "
            + $"{string.Join("|", TrueForms.Concat(FalseForms))}";
        return false;
    }

    private static bool Matches(string[] forms, string raw) =>
        forms.Any(form => string.Equals(form, raw, StringComparison.OrdinalIgnoreCase));
}
