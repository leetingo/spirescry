using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Spirescry.State;

// A diagnostic reconstruction recipe, not an authoritative run history.
// Every accepted bridge verb is attributed to the RunId it acted on. Steps
// driven with follow also carry a stable outcome fingerprint so replay can
// stop at the first divergence instead of compounding it.
public static class RunLog
{
    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        // serde_json writes Unicode and HTML characters directly. Matching
        // that byte representation is required because replay fingerprints
        // are recomputed by the Rust CLI, including under zhs localization.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly object Gate = new();
    private static readonly List<JsonObject> Verbs = new();
    private static string _runId = "none";
    private static string? _seed;
    private static string? _character;
    private static int? _ascension;
    private static long _nextEntryId;

    // Must run on the pump after a successful dispatch.
    public static long RecordAccepted(
        string action,
        JsonElement args,
        string runId,
        string phaseBefore,
        long startedRev,
        long acceptedRev)
    {
        lock (Gate)
        {
            if (action == "new-run")
            {
                Verbs.Clear();
                _runId = runId;
                CaptureMetadata();
            }
            else if (runId != _runId)
            {
                if (CanAdopt(runId)) AdoptRun(runId, captureMetadata: true);
                else
                {
                    Verbs.Clear();
                    _runId = runId;
                    CaptureMetadata();
                }
            }
            var id = ++_nextEntryId;
            var entry = new JsonObject
            {
                ["id"] = id,
                ["runId"] = runId,
                ["action"] = action,
                ["phaseBefore"] = phaseBefore,
                ["startedRev"] = startedRev,
                ["acceptedRev"] = acceptedRev,
            };
            if (args.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                entry["args"] = JsonNode.Parse(args.GetRawText());
            Verbs.Add(entry);
            return id;
        }
    }

    internal static void RecordOutcome(
        long entryId, string outcome, SnapshotContract observation, string[]? errors = null)
    {
        lock (Gate)
        {
            var entry = Verbs.FirstOrDefault(v => v["id"]?.GetValue<long>() == entryId);
            if (entry is null) return;
            var observedRunId = observation.RunId;
            if (entry["action"]?.GetValue<string>() == "new-run"
                && observedRunId is not (null or "none")
                && CanAdopt(observedRunId))
                AdoptRun(observedRunId, captureMetadata: false);
            entry["outcome"] = outcome;
            entry["phaseAfter"] = observation.Phase;
            // Engine faults during this verb's window: preserved in the
            // diagnostic recipe so a polluted run stays attributable even
            // after the host log rotates away.
            if (errors is { Length: > 0 })
                entry["errors"] = new JsonArray(
                    errors.Select(e => (JsonNode)e).ToArray());
            if (outcome != "timeout")
                entry["fingerprint"] = Fingerprint(observation);
        }
    }

    // Must run on the pump so live metadata is read consistently.
    public static object Snapshot(string liveRunId)
    {
        lock (Gate)
        {
            if (CanAdopt(liveRunId)) AdoptRun(liveRunId, captureMetadata: true);
            if (_runId == liveRunId && liveRunId != "none" && _seed is null)
                CaptureMetadata();
            var verbs = Verbs
                .Select(v => (JsonObject)JsonNode.Parse(v.ToJsonString())!)
                .ToArray();
            var coherent = _runId != "none"
                && verbs.Length > 0
                && verbs[0]["action"]?.GetValue<string>() == "new-run"
                && verbs.All(v => v["runId"]?.GetValue<string>() == _runId);
            var verified = verbs.All(v =>
                v["outcome"]?.GetValue<string>() is "settled" or "next_decision"
                && !string.IsNullOrWhiteSpace(v["fingerprint"]?.GetValue<string>()));
            return new
            {
                ok = true,
                kind = "diagnostic_reconstruction_recipe",
                runId = _runId,
                liveRunId,
                seed = _seed,
                character = _character,
                ascension = _ascension,
                // A recipe is replayable only when every accepted verb was
                // followed to a verified boundary. Merely sharing one RunId
                // is not enough: otherwise replay could report success after
                // checking zero (or only a prefix of) fingerprints.
                complete = coherent && verified,
                verbs,
            };
        }
    }

    private static void CaptureMetadata()
    {
        var run = LocalRunContext.Current;
        var state = run?.State;
        var player = run?.Player;
        _seed = state?.Rng?.StringSeed;
        _character = player?.Character?.Id.Entry;
        _ascension = state?.AscensionLevel;
    }

    private static bool CanAdopt(string runId) =>
        runId != "none"
        && _runId == "none"
        && Verbs.Count > 0
        && Verbs[0]["action"]?.GetValue<string>() == "new-run"
        && Verbs.All(v => v["runId"]?.GetValue<string>() == "none");

    private static void AdoptRun(string runId, bool captureMetadata)
    {
        _runId = runId;
        foreach (var verb in Verbs) verb["runId"] = runId;
        if (captureMetadata) CaptureMetadata();
    }

    // FNV-1a over the response's stable JSON ordering. Revisions and RunIds
    // are deliberately removed: a reconstruction is a different run.
    internal static string Fingerprint(SnapshotContract observation)
    {
        var canonical = Canonicalize(observation.ToJsonObject())!;
        var bytes = Encoding.UTF8.GetBytes(canonical.ToJsonString(CanonicalJson));
        ulong hash = 14695981039346656037;
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= 1099511628211;
        }
        return hash.ToString("x16");
    }

    private static JsonNode? Canonicalize(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var sorted = new JsonObject();
            foreach (var pair in obj
                .Where(pair => pair.Key is not ("rev" or "runId"))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal))
                sorted[pair.Key] = Canonicalize(pair.Value);
            return sorted;
        }
        if (node is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var value in array) result.Add(Canonicalize(value));
            return result;
        }
        return node is null ? null : JsonNode.Parse(node.ToJsonString());
    }
}
