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
            var captured = SettlementObservationCapture.Capture(
                PhaseDetector.Current,
                () => Snapshotter.ForCurrentPhase(
                    compact: false, decision: true, knownCardTexts: []),
                () => Signals.Revision,
                Signals.RefreshRunIdentity);
            var snapshot = captured.Observation;
            var revision = snapshot.Revision ?? Signals.Revision;
            var runId = snapshot.RunId ?? Signals.RunId;
            if (captured.ObservationAvailable)
                snapshot.Legal = DecisionProjection.LegalVerbs(
                    snapshot, runId != "none");

            // Settlement can begin during lifecycle edges before the local
            // player is attached. The explicit state-only view preserves the
            // manager's executor/queues without inventing a partial seat.
            var run = LocalRunContext.StateOnly;

            // An event option's task legitimately outlives a follow window
            // when it awaits an embedded combat or a parked picker — the
            // agent must act for it to ever complete, so those states must
            // not read as busy (combat plays would time out; picker parks
            // would never report next_decision). A pending option task
            // outside both states is an engine continuation still
            // executing (a delay between removing cards and granting the
            // reward, say): THAT blocks settlement, so the response can't
            // report a half-applied board with errors: []. Parked means a
            // headless stand-in holds the choice (HeadlessState owns that
            // list) or, in a GUI boot, a nested decision screen does.
            var parkedDecision = DecisionSurface.Current is HeadlessDecisionSurface
                ? HeadlessState.HasParkedDecision
                : SettlementModule.IsNestedDecision(snapshot.Phase)
                    || snapshot.Phase == Phase.CrystalSphere;
            var combatLive = MegaCrit.Sts2.Core.Combat.CombatManager.Instance
                is { IsInProgress: true };
            var pending = Signals.PendingSnapshot();
            // A shared-event vote on a multiplayer client owes option
            // work before any task exists — the resolution arrives by
            // network message, possibly seconds later. The pending vote
            // holds this verb's window open until the delivered tasks
            // take over (the Tick sweep picks them up), so a late fault
            // still lands in the response that caused it.
            var optionWorkOwed = pending.EventOptions > 0
                || Signals.SharedVotePending();
            var eventOptionExecuting = optionWorkOwed
                && !combatLive && !parkedDecision;
            var activity = new SettlementActivity(
                pending.FireAndForget,
                eventOptionExecuting,
                run?.Manager.ActionExecutor?.CurrentlyRunningAction is not null,
                EngineQueues.All(run?.Manager).Sum(queue => queue.depth));
            return new SettlementProbe(
                Signals.TickCount,
                Signals.WorkRevision,
                DecisionSurface.Current.RequiresSettlementFrameStability,
                activity,
                snapshot,
                [
                    .. Signals.ErrorsSince(startedRevision),
                    .. captured.Errors,
                ],
                captured.ObservationAvailable);
        });

    public async Task WaitForChange(long afterWorkRevision, int timeoutMs) =>
        _ = await Signals.WaitForWorkChange(afterWorkRevision, timeoutMs);

    public async Task WaitForTick(long afterTick, int timeoutMs) =>
        _ = await Signals.WaitForTick(afterTick, timeoutMs);
}
