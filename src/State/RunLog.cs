using System.Text.Json;
using System.Text.Json.Nodes;

namespace Spirescry.State;

// A diagnostic reconstruction recipe, not an authoritative run history.
// Every accepted bridge verb is attributed to the RunId it acted on. Steps
// driven with follow also carry a stable outcome fingerprint so replay can
// stop at the first divergence instead of compounding it.
public static class RunLog
{
    private static readonly object Gate = new();
    private static readonly List<RunLogEntry> Verbs = new();
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
        Phase phaseBefore,
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
            var entry = new RunLogEntry(
                id,
                runId,
                action,
                args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? null
                    : JsonNode.Parse(args.GetRawText()),
                phaseBefore,
                startedRev,
                acceptedRev);
            Verbs.Add(entry);
            return id;
        }
    }

    internal static void RecordOutcome(
        long entryId,
        SettlementOutcome outcome,
        SnapshotContract observation,
        string[]? errors = null)
    {
        lock (Gate)
        {
            var entry = Verbs.FirstOrDefault(verb => verb.Id == entryId);
            if (entry is null) return;
            var observedRunId = observation.RunId;
            if (entry.Action == "new-run"
                && observedRunId is not (null or "none")
                && CanAdopt(observedRunId))
                AdoptRun(observedRunId, captureMetadata: false);
            entry.Outcome = outcome;
            entry.PhaseAfter = observation.PhaseName;
            // Engine faults during this verb's window: preserved in the
            // diagnostic recipe so a polluted run stays attributable even
            // after the host log rotates away.
            if (errors is { Length: > 0 })
                entry.Errors = errors.ToArray();
            entry.Fingerprint = outcome.IsReplayable()
                ? Fingerprint(observation)
                : null;
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
                .Select(verb => verb.ToJson())
                .ToArray();
            var coherent = _runId != "none"
                && verbs.Length > 0
                && Verbs[0].Action == "new-run"
                && Verbs.All(verb => verb.RunId == _runId);
            var verified = Verbs.All(verb =>
                verb.Outcome is { } outcome
                && outcome.IsReplayable()
                && !string.IsNullOrWhiteSpace(verb.Fingerprint));
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
        && Verbs[0].Action == "new-run"
        && Verbs.All(verb => verb.RunId == "none");

    private static void AdoptRun(string runId, bool captureMetadata)
    {
        _runId = runId;
        foreach (var verb in Verbs) verb.RunId = runId;
        if (captureMetadata) CaptureMetadata();
    }

    private sealed class RunLogEntry(
        long id,
        string runId,
        string action,
        JsonNode? arguments,
        Phase phaseBefore,
        long startedRevision,
        long acceptedRevision)
    {
        public long Id { get; } = id;
        public string RunId { get; set; } = runId;
        public string Action { get; } = action;
        public JsonNode? Arguments { get; } = arguments;
        public string PhaseBefore { get; } = ProtocolVocabulary.Phases.Name(phaseBefore);
        public long StartedRevision { get; } = startedRevision;
        public long AcceptedRevision { get; } = acceptedRevision;
        public SettlementOutcome? Outcome { get; set; }
        public string? PhaseAfter { get; set; }
        public string[]? Errors { get; set; }
        public string? Fingerprint { get; set; }

        public JsonObject ToJson()
        {
            var node = new JsonObject
            {
                ["id"] = Id,
                ["runId"] = RunId,
                ["action"] = Action,
                ["phaseBefore"] = PhaseBefore,
                ["startedRev"] = StartedRevision,
                ["acceptedRev"] = AcceptedRevision,
            };
            if (Arguments is not null)
                node["args"] = Arguments.DeepClone();
            if (Outcome is { } outcome)
                node["outcome"] = outcome.WireName();
            if (PhaseAfter is not null)
                node["phaseAfter"] = PhaseAfter;
            if (Errors is { Length: > 0 })
                node["errors"] = new JsonArray(
                    Errors.Select(error => (JsonNode)error).ToArray());
            if (Fingerprint is not null)
                node["fingerprint"] = Fingerprint;
            return node;
        }
    }

    // FNV-1a over the explicit typed consumer projection. Presentation-only
    // extension fields never redefine replay compatibility; revisions and
    // RunIds are absent because a reconstruction is a different run.
    internal static string Fingerprint(SnapshotContract observation)
        => observation.ConsumerFingerprint();
}
