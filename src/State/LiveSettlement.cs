using Spirescry.Threading;

namespace Spirescry.State;

internal static class Settlement
{
    public static SettlementModule Current { get; } = new(
        new LiveSettlementTickSource(),
        SystemSettlementClock.Instance);
}

// The engine adapter for the pure settlement module. Captures are serialized
// on the main-thread pump; waits use Signals' revision/tick notifications.
internal sealed class LiveSettlementTickSource : ISettlementTickSource
{
    public async Task<SettlementProbe> Capture(long startedRevision) =>
        await MainThreadPump.Instance!.Run(() =>
        {
            var runId = Signals.RefreshRunIdentity();
            var snapshot = Snapshotter.ForCurrentPhase(
                compact: false, decision: true, knownCardTexts: []);
            var revision = Signals.Revision;
            snapshot.Revision = revision;
            snapshot.RunId = runId;
            snapshot.Legal = DecisionProjection.LegalVerbs(
                snapshot, runId != "none");

            // Settlement can begin during lifecycle edges before the local
            // player is attached. The explicit state-only view preserves the
            // manager's executor/queues without inventing a partial seat.
            var run = LocalRunContext.StateOnly;
            var activity = new SettlementActivity(
                Signals.PendingAsyncCount,
                run?.Manager.ActionExecutor?.CurrentlyRunningAction is not null,
                EngineQueues.All(run?.Manager).Sum(queue => queue.depth));
            var hasDecision = snapshot.Legal.Any(
                verb => verb is not ("abandon" or "potion-discard"));
            return new SettlementProbe(
                revision,
                Signals.TickCount,
                runId,
                snapshot.Phase,
                DecisionSurface.Current.RequiresSettlementFrameStability,
                activity,
                hasDecision,
                snapshot.ToJsonString(),
                snapshot,
                Signals.ErrorsSince(startedRevision));
        });

    public async Task WaitForChange(long afterRevision, int timeoutMs) =>
        _ = await Signals.WaitForChange(afterRevision, timeoutMs);

    public async Task WaitForTick(long afterTick, int timeoutMs) =>
        _ = await Signals.WaitForTick(afterTick, timeoutMs);
}
