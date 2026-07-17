using System.Text.Json;
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
            capabilities = new { verbs = Dispatcher.Verbs, cheats = Dispatcher.Cheats },
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
        var changed = since < 0 || wait == 0 || await Signals.WaitForChange(since, wait);

        return await MainThreadPump.Instance!.Run(() =>
        {
            var runId = Signals.RefreshRunIdentity();
            var snapshot = Snapshotter.ForCurrentPhase(compact, decision, knownCards ?? []);
            var node = JsonSerializer.SerializeToNode(snapshot)!.AsObject();
            node["rev"] = Signals.Revision;
            node["runId"] = runId;
            if (decision)
                node["legal"] = JsonSerializer.SerializeToNode(
                    DecisionProjection.LegalVerbs(node, runId != "none"));
            if (since >= 0)
            {
                node["changed"] = changed;
                node["events"] = JsonSerializer.SerializeToNode(Signals.EventsSince(since));
            }
            return new Response { Body = node.ToJsonString() };
        });
    }

    public static async Task<Response> Step(string body)
    {
        string action;
        JsonElement args;
        long? ifRev = null;
        string? ifRun = null;
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
                return Response.Error("bad_request", parseError, runId: runId);
            });

        var result = await MainThreadPump.Instance!.Run(() =>
        {
            var runId = Signals.RefreshRunIdentity();
            if (ifRun is not null && ifRun != runId)
                return (dispatch: DispatchResult.Reject("external_change",
                    $"run {ifRun} is gone — the live run is {runId}"),
                    rev: Signals.Revision, runId);
            if (ifRev is { } expectedRev && Signals.Revision != expectedRev)
                return (dispatch: DispatchResult.Reject("stale_state",
                    $"rev moved {expectedRev}->{Signals.Revision} since you scried — rescry and decide again"),
                    rev: Signals.Revision, runId);
            var before = Signals.Revision;
            var r = Dispatcher.Dispatch(action, args);
            // Verbs that resolve inline within one phase (host-mode Neow
            // claims, shop buys, reward gold, …) ride no engine event and
            // no phase diff, so nothing else bumps the revision. Every
            // accepted step must be visible to --since waiters.
            if (r.Ok && Signals.Revision == before)
                Signals.Bump($"step:{action}");
            runId = Signals.RefreshRunIdentity();
            return (dispatch: r, rev: Signals.Revision, runId);
        });
        if (!result.dispatch.Ok)
            return Response.Error(
                result.dispatch.Err!, result.dispatch.Msg ?? "",
                result.dispatch.Status, result.runId);
        // The action is enqueued on the engine's action queue and
        // resolves over the following frames — follow with
        // /obs?since=<rev>&wait=<ms> to wake on the outcome. A success
        // Msg is a note (e.g. "settled with victory cleanup").
        return result.dispatch.Msg is null
            ? Response.Json(new
                { ok = true, enqueued = action, rev = result.rev, runId = result.runId })
            : Response.Json(new
                { ok = true, enqueued = action, rev = result.rev,
                    runId = result.runId, note = result.dispatch.Msg });
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
            ? Response.Error("bad_request",
                "kind must be card|relic|potion|event|encounter|character")
            : Response.Json(new { ok = true, kind, entries });
    }
}
