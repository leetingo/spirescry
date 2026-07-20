using System.Text.Json;
using System.Text.Json.Nodes;
using Spirescry.Actions;
using Spirescry.State;
using Spirescry.Threading;

namespace Spirescry.Bridge;

// Each handler touches game state through exactly one pump job, and the
// pump runs jobs one at a time on the main thread — so a snapshot never
// interleaves with an action dispatch.
public static class Handlers
{
    public static async Task<Response> Health()
    {
        var snapshot = await MainThreadPump.Instance!.Run(() =>
        {
            var runId = Signals.RefreshRunIdentity();
            var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
            var exec = rm?.ActionExecutor;
            var running = exec?.CurrentlyRunningAction;
            var pending = Signals.PendingSnapshot();
            var queues = new List<object>();
            foreach (var (owner, depth, paused) in EngineQueues.All(rm))
                queues.Add(new { owner, depth, paused });
            return new
            {
                phase = PhaseDetector.Current().AsString(),
                rev = Signals.Revision,
                runId,
                executor = running is null
                    ? null
                    : $"{running.GetType().Name}:{running.State}",
                executorStuckMs = Signals.ExecutorStuckMs,
                // The two counters behind the follow probe's busy flag —
                // a follow that times out with an idle executor is almost
                // always one of these stuck above zero.
                pendingAsync = pending.FireAndForget + pending.EventOptions,
                pendingEventOptions = pending.EventOptions,
                queues,
            };
        });
        return Response.Json(new
        {
            ok = true,
            mod = Mod.Id,
            version = Mod.Version,
            buildHash = Mod.BuildHash,
            protocolVersion = Mod.ProtocolVersion,
            capabilities = ProtocolCapabilities.Create(Dispatcher.Verbs),
            snapshot.phase,
            snapshot.rev,
            snapshot.runId,
            snapshot.executor,
            snapshot.executorStuckMs,
            snapshot.pendingAsync,
            snapshot.pendingEventOptions,
            snapshot.queues,
        });
    }

    // ?since=<rev>&wait=<ms>: park until the revision moves past `since`
    // (engine events / phase changes bump it) or the wait expires — the
    // event-driven replacement for sleep-polling. The response carries the
    // current revision and, when `since` was given, the events behind it.
    public static async Task<Response> Obs(
        string? sinceStr,
        string? waitStr,
        string? compactStr = null,
        string? decisionStr = null,
        string[]? knownCards = null,
        string? semanticStateStr = null)
    {
        var since = long.TryParse(sinceStr, out var s) ? s : -1;
        var wait = int.TryParse(waitStr, out var w) ? Math.Clamp(w, 0, 60_000) : 0;
        var compact = compactStr is "1" or "true";
        var decision = decisionStr is "1" or "true";
        var includeSemanticState = semanticStateStr is "1" or "true";
        if (since >= 0 && wait > 0)
            await Signals.WaitForChange(since, wait);

        return await MainThreadPump.Instance!.Run(() =>
        {
            var runId = Signals.RefreshRunIdentity();
            var snapshot = Snapshotter.ForCurrentPhase(compact, decision, knownCards ?? []);
            var revision = Signals.Revision;
            snapshot.Revision = revision;
            snapshot.RunId = runId;
            if (decision)
                snapshot.Legal = DecisionProjection.LegalVerbs(snapshot, runId != "none");
            var node = snapshot.ToAgentJsonObject(includeSemanticState);
            if (since >= 0)
            {
                node["changed"] = revision > since;
                node["events"] = JsonSerializer.SerializeToNode(Signals.EventsSince(since));
            }
            return new Response { Body = node.ToJsonString() };
        });
    }

    public static async Task<Response> GetRunLog() =>
        await MainThreadPump.Instance!.Run(() =>
        {
            var liveRunId = Signals.RefreshRunIdentity();
            return Response.Json(RunLog.Snapshot(liveRunId));
        });

    public static async Task<Response> Step(string body)
    {
        string action;
        JsonElement args;
        long? ifRev = null;
        string? ifRun = null;
        int? followMs = null;
        var includeSemanticState = false;
        string? parseError = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                action = "";
                args = default;
                parseError = "request body must be an object";
            }
            else if (!doc.RootElement.TryGetProperty("action", out var actionEl)
                || actionEl.ValueKind != JsonValueKind.String)
            {
                action = "";
                args = default;
                parseError = "missing 'action'";
            }
            else
            {
                action = actionEl.GetString()!;
                args = doc.RootElement.TryGetProperty("args", out var argsEl)
                    ? argsEl.Clone()
                    : default;
                if (doc.RootElement.TryGetProperty("ifRev", out var revEl))
                {
                    if (revEl.ValueKind != JsonValueKind.Number
                        || !revEl.TryGetInt64(out var parsedRev)
                        || parsedRev < 0)
                        parseError = "'ifRev' must be a non-negative integer";
                    else
                        ifRev = parsedRev;
                }
                if (doc.RootElement.TryGetProperty("ifRun", out var runEl))
                {
                    if (runEl.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(runEl.GetString()))
                        parseError ??= "'ifRun' must be a non-empty string";
                    else
                        ifRun = runEl.GetString();
                }
                if (doc.RootElement.TryGetProperty("follow", out var followEl))
                {
                    if (followEl.ValueKind != JsonValueKind.Number
                        || !followEl.TryGetInt32(out var parsedFollow)
                        || parsedFollow < 0 || parsedFollow > 60_000)
                        parseError ??= "'follow' must be an integer in [0,60000]";
                    else
                        followMs = parsedFollow;
                }
                if (doc.RootElement.TryGetProperty(
                    "includeSemanticState", out var semanticEl))
                {
                    if (semanticEl.ValueKind is not (
                        JsonValueKind.True or JsonValueKind.False))
                        parseError ??= "'includeSemanticState' must be a boolean";
                    else
                        includeSemanticState = semanticEl.GetBoolean();
                }
            }
        }
        catch (JsonException ex)
        {
            action = "";
            args = default;
            parseError = $"invalid json body: {ex.Message}";
        }

        if (parseError is not null)
            return await MainThreadPump.Instance!.Run(() =>
            {
                var runId = Signals.RefreshRunIdentity();
                return Response.Error(RejectionCodes.BadRequest, parseError, runId: runId);
            });

        var result = await MainThreadPump.Instance!.Run(() =>
        {
            var runId = Signals.RefreshRunIdentity();
            var phaseBefore = PhaseDetector.Current();
            var tickBefore = Signals.TickCount;
            if (ifRun is not null && ifRun != runId)
                return (dispatch: DispatchResult.Reject(RejectionCodes.ExternalChange,
                    $"run {ifRun} is gone — the live run is {runId}"),
                    rev: Signals.Revision, runId, phaseBefore,
                    startedRev: Signals.Revision, startedTick: tickBefore,
                    acceptedRunId: runId,
                    logEntryId: (long?)null);
            if (ifRev is { } expectedRev && Signals.Revision != expectedRev)
                return (dispatch: DispatchResult.Reject(RejectionCodes.StaleState,
                    $"rev moved {expectedRev}->{Signals.Revision} since you scried — rescry and decide again"),
                    rev: Signals.Revision, runId, phaseBefore,
                    startedRev: Signals.Revision, startedTick: tickBefore,
                    acceptedRunId: runId,
                    logEntryId: (long?)null);
            var runIdBefore = runId;
            var before = Signals.Revision;
            var r = Dispatcher.Dispatch(action, args);
            // Verbs that resolve inline within one phase (host-mode Neow
            // claims, shop buys, reward gold, …) ride no engine event and
            // no phase diff, so nothing else bumps the revision. Every
            // accepted step must be visible to --since waiters.
            if (r.Ok && Signals.Revision == before)
                Signals.Bump($"step:{action}");
            runId = Signals.RefreshRunIdentity();
            long? logEntryId = null;
            var acceptedRunId = action == "new-run" ? runId : runIdBefore;
            if (r.Ok)
            {
                logEntryId = RunLog.RecordAccepted(
                    action, args, acceptedRunId, phaseBefore, before, Signals.Revision);
            }
            return (dispatch: r, rev: Signals.Revision, runId, phaseBefore,
                startedRev: before, startedTick: tickBefore,
                acceptedRunId, logEntryId);
        });
        if (!result.dispatch.Ok)
            return Response.Error(
                result.dispatch.Err!, result.dispatch.Msg ?? "",
                result.dispatch.Status, result.runId);

        if (followMs is { } follow)
            return await Follow(
                action, result.startedRev, result.rev, result.phaseBefore,
                result.startedTick, follow, result.acceptedRunId, result.logEntryId,
                includeSemanticState);

        // A success Msg is a note (e.g. "settled with victory cleanup").
        return result.dispatch.Msg is null
            ? Response.Json(new
                { ok = true, enqueued = action, rev = result.rev, runId = result.runId })
            : Response.Json(new
                { ok = true, enqueued = action, rev = result.rev,
                    runId = result.runId, note = result.dispatch.Msg });
    }

    private static async Task<Response> Follow(
        string action,
        long startedRev,
        long acceptedRev,
        Phase phaseBefore,
        long acceptedTick,
        int timeoutMs,
        string acceptedRunId,
        long? logEntryId,
        bool includeSemanticState)
    {
        var result = await Settlement.Current.Follow(new SettlementRequest(
            phaseBefore,
            startedRev,
            acceptedRev,
            acceptedTick,
            timeoutMs,
            acceptedRunId));
        return FollowResponse(
            action, startedRev, acceptedRev, acceptedRunId,
            result, logEntryId, includeSemanticState);
    }

    private static Response FollowResponse(
        string action,
        long startedRev,
        long acceptedRev,
        string acceptedRunId,
        SettlementResult result,
        long? logEntryId,
        bool includeSemanticState)
    {
        var outcome = result.Outcome;
        var probe = result.Probe;
        // Engine-side faults between acceptance and settlement: an outcome
        // says what the one settlement module observed; retain the detailed
        // tokens so callers and the run log can attribute the engine failure.
        var errors = probe.Errors.ToArray();
        try
        {
            var observation = probe.ObservationAvailable
                ? probe.Observation.ToAgentJsonObject(includeSemanticState)
                : null;
            var events = Signals.EventsSince(startedRev);
            if (logEntryId is { } id)
                RunLog.RecordOutcome(id, outcome, probe.Observation, errors);
            var node = new JsonObject
            {
                ["ok"] = true,
                ["action"] = action,
                ["enqueued"] = action,
                ["startedRev"] = startedRev,
                ["acceptedRev"] = acceptedRev,
                ["acceptedRunId"] = acceptedRunId,
                ["rev"] = probe.Revision,
                ["runId"] = probe.RunId,
                ["settled"] = outcome.ReachedBoundary(),
                ["outcome"] = outcome.WireName(),
                ["observationAvailable"] = probe.ObservationAvailable,
                ["errors"] = JsonSerializer.SerializeToNode(errors),
                ["events"] = JsonSerializer.SerializeToNode(events),
                ["obs"] = observation,
            };
            return new Response { Body = node.ToJsonString() };
        }
        catch (Exception exception)
        {
            var responseErrors = errors
                .Append(ErrorEvents.FromAsyncFault(
                    "response", exception.GetType().Name, exception.Message))
                .ToArray();
            var fallback = new SnapshotContract(probe.Observation.Phase)
            {
                Revision = acceptedRev,
                RunId = acceptedRunId,
                Legal = [],
            };
            if (logEntryId is { } id)
            {
                try
                {
                    RunLog.RecordOutcome(
                        id, SettlementOutcome.Fault, fallback, responseErrors);
                }
                catch { }
            }
            return Response.Json(new
            {
                ok = true,
                action,
                enqueued = action,
                startedRev,
                acceptedRev,
                acceptedRunId,
                rev = acceptedRev,
                runId = acceptedRunId,
                settled = true,
                outcome = SettlementOutcome.Fault.WireName(),
                observationAvailable = false,
                errors = responseErrors,
                events = Array.Empty<object>(),
                obs = (object?)null,
            });
        }
    }

    // The registry the cheats validate against, enumerable — sweeps drive
    // every card/potion/encounter from here instead of hardcoded lists.
    public static async Task<Response> Models(string? kind)
    {
        var entries = await MainThreadPump.Instance!.Run<object?>(() => kind switch
        {
            "card" => MegaCrit.Sts2.Core.Models.ModelDb.AllCards
                .OrderBy(c => c.Id.Entry)
                .Select(c => (object)new
                {
                    model = c.Id.Entry,
                    type = c.Type.ToString().ToLowerInvariant(),
                    rarity = c.Rarity.ToString().ToLowerInvariant(),
                    pool = c.Pool.Title,
                }).ToArray(),
            "relic" => MegaCrit.Sts2.Core.Models.ModelDb.AllRelics
                .OrderBy(r => r.Id.Entry)
                .Select(r => (object)new { model = r.Id.Entry }).ToArray(),
            "potion" => MegaCrit.Sts2.Core.Models.ModelDb.AllPotions
                .OrderBy(p => p.Id.Entry)
                .Select(p => (object)new { model = p.Id.Entry }).ToArray(),
            "event" => MegaCrit.Sts2.Core.Models.ModelDb.AllEvents
                .OrderBy(e => e.Id.Entry)
                .Select(e => (object)new { model = e.Id.Entry }).ToArray(),
            "encounter" => MegaCrit.Sts2.Core.Models.ModelDb.AllEncounters
                .OrderBy(e => e.Id.Entry)
                .Select(e => (object)new { model = e.Id.Entry }).ToArray(),
            "character" => MegaCrit.Sts2.Core.Models.ModelDb.AllCharacters
                .OrderBy(c => c.Id.Entry)
                .Select(c => (object)new { model = c.Id.Entry }).ToArray(),
            _ => null,
        });
        return entries is null
            ? Response.Error(RejectionCodes.BadRequest,
                "kind must be card|relic|potion|event|encounter|character")
            : Response.Json(new { ok = true, kind, entries });
    }
}
