using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace Spirescry.State;

// The one compatibility boundary onto EventSynchronizer's private option
// state. Everything that reads _pendingOptionTasks or _playerVotes goes
// through here — dispatcher suffix tracking, the Signals sweep, the
// probe's vote check, and the fault-injection cheats — so an upstream
// layout change is a one-file fix, not a three-site hunt.
internal static class EventSync
{
    // The synchronizer's live task list: one RunSafely(Chosen()) per
    // player per resolved option, drained by the engine at room exit.
    // Read-only from production code paths.
    public static List<Task>? PendingTasks(EventSynchronizer? sync) =>
        sync is null
            ? null
            : Reflect.FieldValue(sync, "_pendingOptionTasks") as List<Task>;

    // Identity snapshot for dispatcher attribution. A count cannot tell a
    // retained prefix from a list that was cleared and repopulated to the
    // same or a larger length during ChooseLocalOption.
    public static HashSet<Task> PendingTaskSnapshot(EventSynchronizer? sync) =>
        PendingTasks(sync)?.ToHashSet() ?? [];

    private static List<uint?>? PlayerVotes(EventSynchronizer? sync) =>
        sync is null
            ? null
            : Reflect.FieldValue(sync, "_playerVotes") as List<uint?>;

    // True from a shared-event vote until resolution clears the slots.
    // On a multiplayer client that spans the whole network round-trip —
    // and during that window it is the ONLY signal that option work is
    // still owed, because no task exists yet. Singleplayer resolves
    // inside the voting call, so the pending state is never observable
    // there.
    public static bool SharedVotePending(EventSynchronizer? sync) =>
        PlayerVotes(sync)?.Any(vote => vote.HasValue) is true;

    // ---- test hooks (cheat verbs only) --------------------------------

    // Mimic a network-delivered option task: appended to the real list
    // outside any dispatcher tracking.
    public static bool InjectTask(EventSynchronizer sync, Task task)
    {
        if (PendingTasks(sync) is not { } pending) return false;
        pending.Add(task);
        return true;
    }

    // Fabricate the client-side "voted, awaiting resolution" window.
    // Nothing in the engine polls the votes list outside the voting
    // calls, so a parked value only affects our own probe.
    public static bool InjectVote(EventSynchronizer sync)
    {
        if (PlayerVotes(sync) is not { } votes) return false;
        if (votes.Count == 0) votes.Add(0u);
        else votes[0] = 0u;
        return true;
    }

    public static void ClearVotes(EventSynchronizer sync)
    {
        if (PlayerVotes(sync) is { } votes)
            for (var i = 0; i < votes.Count; i++)
                votes[i] = null;
    }
}
