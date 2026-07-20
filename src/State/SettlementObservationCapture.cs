namespace Spirescry.State;

internal sealed record SettlementObservationCaptureResult(
    SnapshotContract Observation,
    bool ObservationAvailable,
    IReadOnlyList<string> Errors);

// Snapshot construction can straddle an engine lifecycle edge. If the
// published phase changed while the old phase-owned state was being read,
// retry exactly once against the resulting phase. A stable-phase failure is
// a real diagnostic fault and must never be coerced to an empty projection.
internal static class SettlementObservationCapture
{
    private static int _forcedFailures;

    // Dev/verification seam used by the observation-fault cheat. It arms the
    // next followed capture without mutating the run a second time.
    internal static void ForceNextFailure() =>
        Interlocked.Exchange(ref _forcedFailures, 1);

    internal static SettlementObservationCaptureResult Capture(
        Func<Phase> phase,
        Func<SnapshotContract> snapshot,
        Func<long> revision,
        Func<string> runId)
    {
        var attemptedPhase = ReadPhase(phase);
        try
        {
            if (Interlocked.Exchange(ref _forcedFailures, 0) != 0)
                throw new InvalidOperationException(
                    "forced post-acceptance observation failure");
            return Available(snapshot(), revision, runId);
        }
        catch (Exception first)
        {
            var resultingPhase = ReadPhase(phase);
            if (resultingPhase != attemptedPhase)
            {
                try
                {
                    return Available(snapshot(), revision, runId);
                }
                catch (Exception retry)
                {
                    return Unavailable(
                        resultingPhase, revision, runId, retry);
                }
            }
            return Unavailable(attemptedPhase, revision, runId, first);
        }
    }

    private static SettlementObservationCaptureResult Available(
        SnapshotContract observation,
        Func<long> revision,
        Func<string> runId)
    {
        observation.Revision = revision();
        observation.RunId = runId();
        return new SettlementObservationCaptureResult(
            observation, ObservationAvailable: true, Errors: []);
    }

    private static SettlementObservationCaptureResult Unavailable(
        Phase phase,
        Func<long> revision,
        Func<string> runId,
        Exception exception)
    {
        var observation = new SnapshotContract(phase)
        {
            Revision = ReadOr(revision, 0L),
            RunId = ReadOr(runId, "none"),
            Legal = [],
        };
        return new SettlementObservationCaptureResult(
            observation,
            ObservationAvailable: false,
            Errors:
            [
                ErrorEvents.FromAsyncFault(
                    "observation", exception.GetType().Name, exception.Message),
            ]);
    }

    private static Phase ReadPhase(Func<Phase> phase) =>
        ReadOr(phase, Phase.Unknown);

    private static T ReadOr<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }
}
