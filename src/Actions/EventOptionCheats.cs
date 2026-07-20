using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using Spirescry.State;
using Spirescry.Threading;

namespace Spirescry.Actions;

// Integration fixtures for network-delivered event-option lifecycle
// shapes. Production dispatch stays declarative; the mutable timers and
// parked task used only by e2e regressions live together here.
internal static class EventOptionCheats
{
    private const int NetworkDeliveryDelayMs = 600;
    private const int OrphanFaultDelayMs = 200;
    private const int FollowWindowHoldMs = 600;
    private const string OrphanFaultMessage =
        "forced orphan event-option failure";
    private const string CurrentCollisionMarker =
        "current-run duplicate marker";
    private static TaskCompletionSource<bool>? _orphan;

    // Mimics a network delivery outside dispatcher tracking. Valid in any
    // phase because Chosen() can synchronously open a picker or combat
    // before Signals gets its next sweep.
    public static DispatchResult FaultDelayed()
    {
        if (RunManager.Instance?.EventSynchronizer is not { } sync)
            return NoSynchronizer();
        return EventSync.InjectTask(sync, TaskHelper.RunSafely(DelayedFault()))
            ? DispatchResult.Success()
            : MissingOptionState("pending list");
    }

    public static DispatchResult FaultLate() => StartLateDelivery(
        () => TaskHelper.RunSafely(DelayedFault()));

    public static DispatchResult CompleteLate() => StartLateDelivery(
        () => Task.CompletedTask);

    public static DispatchResult ParkOrphan()
    {
        if (RequireEvent() is { } err) return err;
        if (RunManager.Instance?.EventSynchronizer is not { } sync)
            return NoSynchronizer();
        if (_orphan is { Task.IsCompleted: false })
            return DispatchResult.Reject(
                RejectionCodes.BadState, "an orphan event option is already parked");
        _orphan = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return EventSync.InjectVote(sync)
            && EventSync.InjectTask(sync, TaskHelper.RunSafely(_orphan.Task))
            ? DispatchResult.Success()
            : MissingOptionState("option state");
    }

    public static DispatchResult FaultOrphan() => FaultOrphan(collision: false);

    public static DispatchResult FaultOrphanCollision() =>
        FaultOrphan(collision: true);

    private static DispatchResult FaultOrphan(bool collision)
    {
        if (_orphan is not { } orphan)
            return DispatchResult.Reject(
                RejectionCodes.BadState, "no orphan event option is parked");
        _orphan = null;
        _ = FaultDuringCurrentWindow(orphan, collision);
        Signals.TrackAsync(
            Task.Delay(FollowWindowHoldMs), "orphan-release-window");
        return DispatchResult.Success();
    }

    // Rotate the tracking generation while preserving the live engine
    // synchronizer. This is the same-source transition that can happen
    // while RunState changes ahead of EventSynchronizer replacement.
    public static DispatchResult RotateOwner()
    {
        if (RequireEvent() is { } err) return err;
        if (RunManager.Instance?.EventSynchronizer is not { } sync)
            return NoSynchronizer();
        EventSync.ClearVotes(sync);
        Signals.DropEventOptionTracking();
        return DispatchResult.Success();
    }

    private static DispatchResult StartLateDelivery(Func<Task> taskFactory)
    {
        if (RequireEvent() is { } err) return err;
        if (RunManager.Instance?.EventSynchronizer is not { } sync)
            return NoSynchronizer();
        if (!EventSync.InjectVote(sync)) return MissingOptionState("vote list");
        _ = DeliverLate(sync, taskFactory).ContinueWith(
            task =>
            {
                if (task.Exception is { } ex)
                    SafeLog.Error("event-option late delivery", ex.InnerException ?? ex);
            },
            TaskContinuationOptions.OnlyOnFaulted);
        return DispatchResult.Success();
    }

    private static async Task DeliverLate(
        EventSynchronizer sync, Func<Task> taskFactory)
    {
        await Task.Delay(NetworkDeliveryDelayMs).ConfigureAwait(false);
        await MainThreadPump.Instance!.Run(() =>
        {
            EventSync.ClearVotes(sync);
            EventSync.InjectTask(sync, taskFactory());
            return true;
        });
    }

    private static async Task FaultDuringCurrentWindow(
        TaskCompletionSource<bool> orphan, bool collision)
    {
        await Task.Delay(OrphanFaultDelayMs).ConfigureAwait(false);
        orphan.TrySetException(new InvalidOperationException(OrphanFaultMessage));
        if (collision)
            MegaCrit.Sts2.Core.Logging.Log.Error(
                $"System.InvalidOperationException: {OrphanFaultMessage} "
                + $"[{CurrentCollisionMarker}]");
    }

    private static async Task DelayedFault()
    {
        await Task.Delay(250).ConfigureAwait(false);
        throw new InvalidOperationException(
            "forced delayed event-option failure (cheat event-fault-delayed)");
    }

    private static DispatchResult? RequireEvent()
    {
        var current = PhaseDetector.Current();
        return current == Phase.Event
            ? null
            : DispatchResult.Reject(
                RejectionCodes.BadPhase,
                $"requires phase event, current is {current.AsString()}");
    }

    private static DispatchResult NoSynchronizer() =>
        DispatchResult.Reject(RejectionCodes.BadState, "no event synchronizer");

    private static DispatchResult MissingOptionState(string state) =>
        DispatchResult.Reject(
            RejectionCodes.Internal, $"event synchronizer {state} not found");
}
