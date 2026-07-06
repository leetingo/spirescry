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
            var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
            var exec = rm?.ActionExecutor;
            var running = exec?.CurrentlyRunningAction;
            var queues = new List<object>();
            if (rm?.ActionQueueSet is { } aqs
                && Reflect.FieldValue(aqs, "_actionQueues")
                    is System.Collections.IEnumerable qs)
                foreach (var q in qs)
                    queues.Add(new
                    {
                        owner = Reflect.FieldValue(q, "ownerId"),
                        depth = (Reflect.FieldValue(q, "actions")
                            as System.Collections.ICollection)?.Count ?? 0,
                        paused = Reflect.FieldValue(q, "isPaused") is true,
                    });
            return new
            {
                phase = PhaseDetector.Current().AsString(),
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
            snapshot.phase,
            rev = Signals.Revision,
            snapshot.executor,
            snapshot.executorStuckMs,
            snapshot.queues,
        });
    }

    // ?since=<rev>&wait=<ms>: park until the revision moves past `since`
    // (engine events / phase changes bump it) or the wait expires — the
    // event-driven replacement for sleep-polling. The response carries the
    // current revision and, when `since` was given, the events behind it.
    public static async Task<Response> Obs(string? sinceStr, string? waitStr)
    {
        var since = long.TryParse(sinceStr, out var s) ? s : -1;
        var wait = int.TryParse(waitStr, out var w) ? Math.Clamp(w, 0, 60_000) : 0;
        var changed = since < 0 || wait == 0 || await Signals.WaitForChange(since, wait);

        var snapshot = await MainThreadPump.Instance!.Run(Snapshotter.ForCurrentPhase);
        var node = JsonSerializer.SerializeToNode(snapshot)!.AsObject();
        node["rev"] = Signals.Revision;
        if (since >= 0)
        {
            node["changed"] = changed;
            node["events"] = JsonSerializer.SerializeToNode(Signals.EventsSince(since));
        }
        return new Response { Body = node.ToJsonString() };
    }

    public static async Task<Response> Step(string body)
    {
        string action;
        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("action", out var actionEl)
                || actionEl.ValueKind != JsonValueKind.String)
                return Response.Error("bad_request", "missing 'action'");
            action = actionEl.GetString()!;
            args = doc.RootElement.TryGetProperty("args", out var argsEl)
                ? argsEl.Clone()
                : default;
        }
        catch (JsonException ex)
        {
            return Response.Error("bad_request", $"invalid json body: {ex.Message}");
        }

        var result = await MainThreadPump.Instance!.Run(() => Dispatcher.Dispatch(action, args));
        return result.Ok
            // The action is enqueued on the engine's action queue and
            // resolves over the following frames — follow with
            // /obs?since=<rev>&wait=<ms> to wake on the outcome.
            ? Response.Json(new { ok = true, enqueued = action, rev = Signals.Revision })
            : Response.Error(result.Err!, result.Msg ?? "");
    }
}
