namespace Spirescry.State;

// Whether the local seat has actually spent its rest.
//
// In the GUI the engine answers this itself: NRestSiteRoom disables its
// proceed button when the room is built and re-enables it only from
// ShowProceedButton, which AfterSelectingOptionAsync reaches — and
// NRestSiteButton calls that exclusively when ChooseLocalOption returned
// true. Headless has no such node, so the bridge keeps the same fact here.
//
// The engine's own bookkeeping cannot stand in for it. ChooseOption stamps
// `lastChosenOptionIndex` *before* it tests the option's success flag, so
// backing out of a sub-picker — take SMITH, then skip the card grid —
// leaves an index behind while nothing was consumed: no option left the
// board, no hp or upgrade was spent, and the GUI's button stayed disabled.
// Reading that index as "spent" let one `proceed` walk out of a rest site
// the seat had not used (#146).
internal static class RestSiteSeat
{
    // The room a selection last succeeded in. Keying on the room instance
    // is the reset: the next rest site is a different object, so it starts
    // unspent without the bridge needing a room-entry hook.
    private static object? _spentIn;

    public static bool HasSpentItsChoice(object? room) =>
        room is not null && ReferenceEquals(_spentIn, room);

    // Follows the selection the caller just started and records it only if
    // the engine reports it consumed something. The continuation is
    // synchronous, like Signals': headless resolves a parked picker's whole
    // awaiting chain inline, so the observation taken straight after the
    // picking verb already carries the new readiness.
    public static void RecordWhenSucceeded(Task<bool> choice, object? room) =>
        choice.ContinueWith(
            completed =>
            {
                if (completed.IsCompletedSuccessfully && completed.Result)
                    _spentIn = room;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
