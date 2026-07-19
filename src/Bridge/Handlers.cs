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
        string[]? knownCards = null)
    {
        var since = long.TryParse(sinceStr, out var s) ? s : -1;
        var wait = int.TryParse(waitStr, out var w) ? Math.Clamp(w, 0, 60_000) : 0;
        var compact = compactStr is "1" or "true";
        var decision = decisionStr is "1" or "true";
        if (since >= 0 && wait > 0)
            await Signals.WaitForChange(since, wait);

        return await MainThreadPump.Instance!.Run(() =>
        {
            var runId = Signals.RefreshRunIdentity();
            var snapshot = Snapshotter.ForCurrentPhase(compact, decision, knownCards ?? []);
            var node = JsonSerializer.SerializeToNode(snapshot)!.AsObject();
            var revision = Signals.Revision;
            node["rev"] = revision;
            node["runId"] = runId;
            if (decision)
                node["legal"] = JsonSerializer.SerializeToNode(
                    DecisionProjection.LegalVerbs(node, runId != "none"));
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
            var phaseBefore = PhaseDetector.Current().AsString();
            var tickBefore = Signals.TickCount;
            if (ifRun is not null && ifRun != runId)
                return (dispatch: DispatchResult.Reject(RejectionCodes.ExternalChange,
                    $"run {ifRun} is gone — the live run is {runId}"),
                    rev: Signals.Revision, runId, phaseBefore,
                    startedRev: Signals.Revision, startedTick: tickBefore,
                    logEntryId: (long?)null);
            if (ifRev is { } expectedRev && Signals.Revision != expectedRev)
                return (dispatch: DispatchResult.Reject(RejectionCodes.StaleState,
                    $"rev moved {expectedRev}->{Signals.Revision} since you scried — rescry and decide again"),
                    rev: Signals.Revision, runId, phaseBefore,
                    startedRev: Signals.Revision, startedTick: tickBefore,
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
            if (r.Ok)
            {
                var attributedRunId = action == "new-run" ? runId : runIdBefore;
                logEntryId = RunLog.RecordAccepted(
                    action, args, attributedRunId, phaseBefore, before, Signals.Revision);
            }
            return (dispatch: r, rev: Signals.Revision, runId, phaseBefore,
                startedRev: before, startedTick: tickBefore, logEntryId);
        });
        if (!result.dispatch.Ok)
            return Response.Error(
                result.dispatch.Err!, result.dispatch.Msg ?? "",
                result.dispatch.Status, result.runId);

        if (followMs is { } follow)
            return await Follow(
                action, result.startedRev, result.rev, result.phaseBefore,
                result.startedTick, follow, result.logEntryId);

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
        string phaseBefore,
        long acceptedTick,
        int timeoutMs,
        long? logEntryId)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var since = acceptedRev;
        string? candidateOutcome = null;
        string? candidateState = null;
        long candidateTick = acceptedTick;
        var stableFrames = 0;
        while (true)
        {
            var probe = await MainThreadPump.Instance!.Run(FollowProbe.Capture);
            var outcome = FollowProbe.CandidateOutcome(probe, phaseBefore, acceptedRev);
            if (outcome is not null)
            {
                if (!probe.RequiresFrameStability)
                    return FollowResponse(
                        action, startedRev, acceptedRev, outcome, probe, logEntryId);

                if (outcome == candidateOutcome
                    && probe.StateKey == candidateState
                    && probe.Tick > candidateTick)
                    stableFrames++;
                else
                    stableFrames = 1;
                candidateOutcome = outcome;
                candidateState = probe.StateKey;
                candidateTick = probe.Tick;

                // A GUI click or reflected callback may enqueue its real
                // work on a later frame without returning a Task. Require
                // the same quiet/decision state across three distinct
                // frames before claiming that follow reached a boundary.
                if (stableFrames >= 3)
                    return FollowResponse(
                        action, startedRev, acceptedRev, candidateOutcome, probe, logEntryId);
            }
            else
            {
                candidateOutcome = null;
                candidateState = null;
                stableFrames = 0;
            }

            var remaining = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
            if (remaining == 0)
                return FollowResponse(
                    action, startedRev, acceptedRev, "timeout", probe, logEntryId);
            if (probe.RequiresFrameStability && outcome is not null)
                await Signals.WaitForTick(probe.Tick, remaining);
            else
                await Signals.WaitForChange(since, remaining);
            since = Signals.Revision;
        }
    }

    private static Response FollowResponse(
        string action,
        long startedRev,
        long acceptedRev,
        string outcome,
        FollowProbe probe,
        long? logEntryId)
    {
        // Engine-side faults between acceptance and settlement: an outcome
        // of "settled" only says tracked work went quiet, and the engine
        // logs-and-swallows exceptions inside its own task chains — a verb
        // whose effect half-executed would otherwise read as clean.
        var errors = Signals.ErrorsSince(startedRev);
        if (logEntryId is { } id)
            RunLog.RecordOutcome(id, outcome, probe.Observation, errors);
        var node = new JsonObject
        {
            ["ok"] = true,
            ["enqueued"] = action,
            ["startedRev"] = startedRev,
            ["acceptedRev"] = acceptedRev,
            ["rev"] = probe.Rev,
            ["runId"] = probe.RunId,
            ["settled"] = outcome != "timeout",
            ["outcome"] = outcome,
            ["errors"] = JsonSerializer.SerializeToNode(errors),
            ["events"] = JsonSerializer.SerializeToNode(Signals.EventsSince(startedRev)),
            ["obs"] = probe.Observation,
        };
        return new Response { Body = node.ToJsonString() };
    }

    private sealed record FollowProbe(
        long Rev,
        long Tick,
        string RunId,
        string Phase,
        bool RequiresFrameStability,
        bool Busy,
        bool HasDecision,
        string StateKey,
        JsonObject Observation)
    {
        public static FollowProbe Capture()
        {
            var runId = Signals.RefreshRunIdentity();
            var snapshot = Snapshotter.ForCurrentPhase(
                compact: false, decision: true, knownCardTexts: []);
            var node = JsonSerializer.SerializeToNode(snapshot)!.AsObject();
            var rev = Signals.Revision;
            node["rev"] = rev;
            node["runId"] = runId;
            var legal = DecisionProjection.LegalVerbs(node, runId != "none");
            node["legal"] = JsonSerializer.SerializeToNode(legal);
            var stateKey = node.ToJsonString();

            var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
            var busy = Signals.PendingAsyncCount > 0
                || rm?.ActionExecutor?.CurrentlyRunningAction is not null
                || EngineQueues.All(rm).Any(queue => queue.depth > 0);
            var hasDecision = legal.Any(verb => verb is not ("abandon" or "potion-discard"));
            return new FollowProbe(
                rev, Signals.TickCount, runId,
                node["phase"]?.GetValue<string>() ?? "unknown",
                DecisionSurface.Current.RequiresSettlementFrameStability,
                busy, hasDecision, stateKey, node);
        }

        public static string? CandidateOutcome(
            FollowProbe probe, string phaseBefore, long acceptedRev)
        {
            if (!probe.Busy) return "settled";
            if (!probe.HasDecision) return null;
            if (probe.Phase != phaseBefore || IsNestedDecision(probe.Phase))
                return "next_decision";
            // Some events page within the same coarse phase while the
            // event Task remains parked on the next choice.
            return probe.Phase == "event" && probe.Rev > acceptedRev
                ? "next_decision"
                : null;
        }

        public static bool IsNestedDecision(string phase) => phase is
            "card_select" or "hand_select" or "bundle_select"
            or "card_reward" or "relic_reward";
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
