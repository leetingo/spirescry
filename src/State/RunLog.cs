using System.Collections;
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
    // Both recipe decisions are value rules; they are stated in RunLogRules so
    // CI can verify them without the game's dlls. The entries are projected
    // through a live view rather than copied, so a rule's cheap identity
    // guards still settle the common case without walking the history.
    private static readonly IReadOnlyList<RunLogVerbFacts> Facts =
        new VerbFactsView(Verbs);
    private static string _runId = RunLogRules.NoRun;
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
            if (action == RunLogRules.OpeningAction)
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
            // The observation behind an owner change was captured from
            // another run, or from the menu the accepted run was retired to.
            // Nothing read off that board — the run it names, the phase it
            // parked in, its fingerprint — describes this verb, so the entry
            // keeps only what it owns: the outcome, plus the fault tokens
            // SettlementModule.Follow had already observed while the accepted
            // run was still the board on screen.
            var ownsObservation = outcome.OwnsObservation();
            var observedRunId = observation.RunId;
            if (ownsObservation
                && entry.Action == RunLogRules.OpeningAction
                && observedRunId is not (null or RunLogRules.NoRun)
                && CanAdopt(observedRunId))
                AdoptRun(observedRunId, captureMetadata: false);
            entry.Outcome = outcome;
            if (ownsObservation)
                entry.PhaseAfter = observation.PhaseName;
            // Engine faults during this verb's window: preserved in the
            // diagnostic recipe so a polluted run stays attributable even
            // after the host log rotates away.
            if (errors is { Length: > 0 })
                entry.Errors = errors.ToArray();
            entry.Fingerprint = ownsObservation && outcome.IsReplayable()
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
            if (_runId == liveRunId
                && liveRunId != RunLogRules.NoRun
                && _seed is null)
                CaptureMetadata();
            var verbs = Verbs
                .Select(verb => verb.ToJson())
                .ToArray();
            return new
            {
                kind = "diagnostic_reconstruction_recipe",
                runId = _runId,
                liveRunId,
                seed = _seed,
                character = _character,
                ascension = _ascension,
                complete = RunLogRules.IsComplete(_runId, Facts),
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
        RunLogRules.CanAdopt(_runId, runId, Facts);

    private static void AdoptRun(string runId, bool captureMetadata)
    {
        _runId = runId;
        foreach (var verb in Verbs) verb.RunId = runId;
        if (captureMetadata) CaptureMetadata();
    }

    // The entries read as the plain values RunLogRules judges. Reads through
    // to the live list, so it stays true as verbs are appended, settled and
    // relabelled — and costs nothing until a rule actually walks it.
    private sealed class VerbFactsView(List<RunLogEntry> verbs)
        : IReadOnlyList<RunLogVerbFacts>
    {
        public int Count => verbs.Count;
        public RunLogVerbFacts this[int index] => verbs[index].Facts;

        public IEnumerator<RunLogVerbFacts> GetEnumerator() =>
            verbs.Select(verb => verb.Facts).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
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

        public RunLogVerbFacts Facts =>
            new(RunId, Action, Outcome, Fingerprint);

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
