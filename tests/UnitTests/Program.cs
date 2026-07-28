using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Spirescry;
using Spirescry.Bridge;
using Spirescry.Host;
using Spirescry.State;
// Only the one stub type is pulled in by name: a blanket `using Godot` would
// shadow Array, Dictionary and Object across the whole file.
using Color = Godot.Color;

// Every public static parameterless method on Tests is a test — discovered
// here by reflection so a new test can't be silently left unregistered.
var tests = typeof(Tests)
    .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
    .Where(m => m.GetParameters().Length == 0 && !m.IsGenericMethod)
    .ToArray();

if (tests.Length == 0)
{
    Console.Error.WriteLine("not ok - no tests discovered");
    return 1;
}

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Invoke(null, null);
        Console.WriteLine($"ok - {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
        Console.Error.WriteLine($"not ok - {test.Name}: {cause.Message}");
    }
}

return failures == 0 ? 0 : 1;

internal static class Tests
{
    public static void RequiredHostPatchFailureStopsBootWithMethodAndCause()
    {
        var cause = new InvalidOperationException("Harmony JIT exploded");
        var result = new HostPatchBatchResult(
            MatchedCount: 1,
            PatchedCount: 0,
            Failures:
            [
                new HostPatchFailure(
                    "Spirescry.Host.PatchIdentityProbe.Apply(System.Int32,System.String)",
                    cause),
            ]);
        var reports = new List<(string message, Exception cause)>();

        var thrown = Capture<InvalidOperationException>(() => result.Enforce(
            "combat queue shim",
            HostPatchFailurePolicy.Required,
            (message, error) => reports.Add((message, error))));

        True(thrown.Message.Contains("combat queue shim", StringComparison.Ordinal));
        True(thrown.Message.Contains("PatchIdentityProbe.Apply", StringComparison.Ordinal));
        Equal(1, reports.Count);
        True(reports[0].message.Contains(
            "Spirescry.Host.PatchIdentityProbe.Apply(System.Int32,System.String)",
            StringComparison.Ordinal));
        Equal(cause, reports[0].cause);
    }

    public static void PresentationHostPatchFailureIsReportedAndContinues()
    {
        var cause = new InvalidOperationException("presentation method would not JIT");
        var result = new HostPatchBatchResult(
            MatchedCount: 1,
            PatchedCount: 0,
            Failures: [new HostPatchFailure("Example.Vfx.Play()", cause)]);
        var reports = new List<(string message, Exception cause)>();

        result.Enforce(
            "VFX finalizers",
            HostPatchFailurePolicy.PresentationOnly,
            (message, error) => reports.Add((message, error)));

        Equal(1, reports.Count);
        True(reports[0].message.Contains("Example.Vfx.Play()", StringComparison.Ordinal));
        Equal(cause, reports[0].cause);
    }

    public static void RequiredHostPatchSetCannotSilentlyMatchNothing()
    {
        var result = new HostPatchBatchResult(
            MatchedCount: 0,
            PatchedCount: 0,
            Failures: []);
        var reports = new List<(string message, Exception cause)>();

        var thrown = Capture<InvalidOperationException>(() => result.Enforce(
            "custom reward completion",
            HostPatchFailurePolicy.Required,
            (message, error) => reports.Add((message, error))));

        True(thrown.Message.Contains("matched no methods", StringComparison.Ordinal));
        Equal(1, reports.Count);
        True(reports[0].message.Contains("matched no methods", StringComparison.Ordinal));
        True(reports[0].cause is MissingMethodException);
    }

    public static void HostPatchFailureIdentityIncludesTheOverloadSignature()
    {
        var method = typeof(PatchIdentityProbe).GetMethod(
            nameof(PatchIdentityProbe.Apply),
            [typeof(int), typeof(string)])!;

        var failure = HostPatchFailure.From(
            method, new InvalidOperationException("failure"));

        Equal(
            "PatchIdentityProbe.Apply(System.Int32,System.String)",
            failure.MethodIdentity);
    }

    public static void ProtocolVocabularyExposesTheCompleteWireContract()
    {
        var artifact = JsonNode.Parse(ProtocolVocabulary.CreateArtifactJson())!.AsObject();

        Equal(ProtocolVocabulary.ProtocolVersion,
            artifact["protocolVersion"]!.GetValue<int>());
        Equal(ProtocolVocabulary.Rejections.All.Count,
            artifact["rejectionCodes"]!.AsArray().Count);
        Equal(ProtocolVocabulary.Phases.All.Count,
            artifact["phases"]!.AsArray().Count);
        Equal(ProtocolVocabulary.SettlementOutcomes.All.Count,
            artifact["settlementOutcomes"]!.AsArray().Count);
        Equal(ProtocolVocabulary.FaultEvents.All.Count,
            artifact["faultEventTokens"]!.AsObject().Count);
        Equal(ProtocolVocabulary.Cheats.All.Count,
            artifact["cheatArgumentShapes"]!.AsArray().Count);
    }

    public static void ProtocolVersionCoversTheMandatoryOkEnvelope()
    {
        // v6 makes `ok` mandatory on every body, snapshots included. Every
        // earlier host answered /obs without it, and a v6 CLI calls a body
        // with no boolean `ok` malformed, so the pairing has to be rejected
        // as an incompatible host before any route is read.
        Equal(6, ProtocolVocabulary.ProtocolVersion);
    }

    public static void ResponseEnvelopeStampsOkFromTheHttpStatus()
    {
        // The rule every route now answers through: 2xx is a result, any
        // other status is a rejection, and the flag says which.
        Equal(true, ResponseEnvelope.OkFor(200));
        Equal(false, ResponseEnvelope.OkFor(400));
        Equal(false, ResponseEnvelope.OkFor(404));
        Equal(false, ResponseEnvelope.OkFor(500));

        var accepted = ResponseEnvelope.Stamp(
            new JsonObject { ["enqueued"] = "play" }, 200);
        var rejected = ResponseEnvelope.Stamp(
            new JsonObject { ["err"] = "bad_state" }, 400);

        Equal(true, accepted[ResponseEnvelope.OkField]!.GetValue<bool>());
        Equal("play", accepted["enqueued"]!.GetValue<string>());
        Equal(false, rejected[ResponseEnvelope.OkField]!.GetValue<bool>());
    }

    public static void ResponseEnvelopeOverwritesAnOkTheBodyCarriedItself()
    {
        // A body that disagrees with its own status is exactly what the CLI
        // rejects as malformed, so the status wins here rather than being
        // passed on to be discovered downstream.
        var claimsSuccess = ResponseEnvelope.Stamp(
            new JsonObject { ["ok"] = true, ["err"] = "internal" }, 500);
        var claimsFailure = ResponseEnvelope.Stamp(
            new JsonObject { ["ok"] = false, ["phase"] = "combat" }, 200);

        Equal(false, claimsSuccess[ResponseEnvelope.OkField]!.GetValue<bool>());
        Equal(true, claimsFailure[ResponseEnvelope.OkField]!.GetValue<bool>());
    }

    public static void ProtocolVersionCoversTheOwnerChangeOutcome()
    {
        // v5 added owner_changed. A v4 CLI cannot decode it: the outcome would
        // read as absent, so an unowned follow would look like a response
        // with no verdict rather than "your run is gone". v4 itself made
        // expanded semanticState opt-in while replay kept hashing it — a v3
        // CLI would calculate a narrower fingerprint. Every such skew must be
        // rejected at /health before a verb is fired, so the outcome has to
        // stay in the published vocabulary and the constant has to stay ahead
        // of the version that introduced it (now 6, see above).
        Equal(true, ProtocolVocabulary.SettlementOutcomes.All.Contains("owner_changed"));
        Equal(true, ProtocolVocabulary.ProtocolVersion >= 5);
    }

    public static void ProtocolArtifactPublishesConsumerProjectionSchema()
    {
        var artifact = JsonNode.Parse(ProtocolVocabulary.CreateArtifactJson())!.AsObject();
        var projection = artifact["consumerProjection"]!.AsObject();

        JsonObject Field(string group, string symbol) => projection[group]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(field => field["symbol"]!.GetValue<string>() == symbol);

        Equal("phase", Field("top", "phase")["wire"]!.GetValue<string>());
        Equal("phase", Field("top", "phase")["output"]!.GetValue<string>());
        Equal("requiredString", Field("top", "phase")["kind"]!.GetValue<string>());
        Equal("potions", Field("top", "hasTopLevelPotions")["wire"]!.GetValue<string>());
        Equal("hasTopLevelPotions",
            Field("top", "hasTopLevelPotions")["output"]!.GetValue<string>());
        Equal("presenceBoolean",
            Field("top", "hasTopLevelPotions")["kind"]!.GetValue<string>());
        Equal("idx", Field("item", "index")["wire"]!.GetValue<string>());
        Equal("index", Field("item", "index")["output"]!.GetValue<string>());
        Equal("model", Field("enemy", "model")["wire"]!.GetValue<string>());
        Equal("energy", Field("combatant", "energy")["wire"]!.GetValue<string>());
        Equal("gold", Field("player", "gold")["wire"]!.GetValue<string>());
    }

    public static void ConsumerProjectionOutputPropertiesUseThePublishedSchema()
    {
        var groups = new (Type Type, IReadOnlyList<ConsumerProjectionField> Fields)[]
        {
            (typeof(SnapshotConsumerProjection), SnapshotConsumerSchema.Top.Fields),
            (typeof(SnapshotItemConsumerProjection), SnapshotConsumerSchema.Item.Fields),
            (typeof(SnapshotCombatantConsumerProjection), SnapshotConsumerSchema.Combatant.Fields),
            (typeof(SnapshotEnemyConsumerProjection), SnapshotConsumerSchema.Enemy.Fields),
            (typeof(SnapshotPlayerConsumerProjection), SnapshotConsumerSchema.Player.Fields),
        };

        foreach (var (type, fields) in groups)
        {
            var properties = type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Equal(fields.Count, properties.Length);
            foreach (var field in fields)
            {
                var propertyName = char.ToUpperInvariant(field.Symbol[0]) + field.Symbol[1..];
                var property = type.GetProperty(propertyName)
                    ?? throw new Exception($"{type.Name} is missing {propertyName}");
                var attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>()
                    ?? throw new Exception($"{type.Name}.{propertyName} has no JsonPropertyName");
                Equal(field.Output, attribute.Name);
            }
        }
    }

    public static void ProtocolVocabularyMapsEveryPhaseAndUnknownValues()
    {
        var mapped = Enum.GetValues<Phase>()
            .Select(ProtocolVocabulary.Phases.Name).ToArray();

        True(mapped.SequenceEqual(ProtocolVocabulary.Phases.All));
        Equal(ProtocolVocabulary.Phases.Unknown,
            ProtocolVocabulary.Phases.Name((Phase)999));
    }

    public static void ProtocolVocabularyMapsEverySettlementOutcome()
    {
        var mapped = Enum.GetValues<SettlementOutcome>()
            .Select(ProtocolVocabulary.SettlementOutcomes.Name).ToArray();

        True(mapped.SequenceEqual(ProtocolVocabulary.SettlementOutcomes.All));
        Equal("settled", SettlementOutcome.Settled.WireName());
        Equal("next_decision", SettlementOutcome.NextDecision.WireName());
        Equal("fault", SettlementOutcome.Fault.WireName());
        Equal("timeout", SettlementOutcome.Timeout.WireName());
        Equal("owner_changed", SettlementOutcome.OwnerChanged.WireName());
    }

    public static void CollectionSnapshotMaterializesALiveSourceOnlyOnce()
    {
        var live = new OneShotEnumerable<int>([1, 2, 3]);

        var snapshot = CollectionSnapshot.Once(live.Select(value => value * 10));

        Equal(1, live.EnumerationCount);
        Equal(3, snapshot.Length);
        Equal(60, snapshot.Sum());
        Equal(60, snapshot.Sum());
    }

    public static void CollectionSnapshotRetriesATransientLiveMutation()
    {
        var attempts = 0;

        var snapshot = CollectionSnapshot.ReadStable(
            "power semantic state",
            () =>
            {
                attempts++;
                if (attempts == 1) throw CollectionMutation();
                return new[] { "STRENGTH" };
            });

        Equal(2, attempts);
        Equal("STRENGTH", snapshot.Single());
    }

    public static void CollectionSnapshotPropagatesPersistentLiveMutation()
    {
        var attempts = 0;

        var error = Capture<InvalidOperationException>(() =>
            CollectionSnapshot.ReadStable<int[]>(
                "intent semantic state",
                () =>
                {
                    attempts++;
                    throw CollectionMutation();
                }));

        Equal(3, attempts);
        True(error.Message.Contains("intent semantic state"));
        True(error.InnerException is InvalidOperationException);
    }

    public static void CollectionSnapshotDoesNotRetryOtherFailures()
    {
        var attempts = 0;

        var error = Capture<InvalidOperationException>(() =>
            CollectionSnapshot.ReadStable<int[]>(
                "card dynamic vars",
                () =>
                {
                    attempts++;
                    throw new ArgumentException("broken model");
                }));

        Equal(1, attempts);
        True(error.Message.Contains("card dynamic vars"));
        True(error.InnerException is ArgumentException);
    }

    public static void CollectionSnapshotPreservesAValidEmptyRead()
    {
        var snapshot = CollectionSnapshot.ReadStable(
            "power semantic state", Array.Empty<string>);

        Equal(0, snapshot.Length);
    }

    public static void ConsumerCardPlayableDistinguishesFalseFromReadFailure()
    {
        var playable = ConsumerSemanticRead.CardPlayable(() => false);

        False(playable);
        var error = Capture<InvalidOperationException>(() =>
            ConsumerSemanticRead.CardPlayable(
                () => throw new ArgumentException("broken CanPlay")));
        True(error.Message.Contains("card playable semantic state"));
        True(error.InnerException is ArgumentException);
    }

    public static void ConsumerMapMarkersDistinguishEmptyFromReadFailure()
    {
        var markers = ConsumerSemanticRead.MapMarkerIdentities(
            "map marker semantic state at 2,3", Array.Empty<string>);

        Equal(0, markers.Length);
        var error = Capture<InvalidOperationException>(() =>
            ConsumerSemanticRead.MapMarkerIdentities(
                "map marker semantic state at 2,3",
                () => throw new ArgumentException("broken quest collection")));
        True(error.Message.Contains("map marker semantic state at 2,3"));
        True(error.InnerException is ArgumentException);
    }

    public static void SettlementReturnsImmediateQuietBoundary()
    {
        var clock = new FakeSettlementClock();
        var ticks = new FakeSettlementTicks(clock,
            Probe(revision: 4, tick: 2, busy: false));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(timeoutMs: 100)).GetAwaiter().GetResult();

        Equal(SettlementOutcome.Settled, result.Outcome);
        Equal(1, ticks.Captures);
        Equal(0, ticks.ChangeWaits + ticks.TickWaits);
    }

    public static void SettlementReportsAnOwnerChangeWhenAnotherRunTakesOver()
    {
        // #144: a follow window is scoped to the run that accepted the verb.
        // A concurrent new-run parks the next probe on a quiet, decision-free
        // board that belongs to somebody else — classifying it would report
        // this action Settled (and replayable) against a run it never touched.
        var clock = new FakeSettlementClock();
        var ticks = new FakeSettlementTicks(clock,
            Probe(revision: 5, busy: true, hasDecision: false),
            Probe(revision: 6, tick: 3, busy: false, runId: "other-run"));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(timeoutMs: 100))
            .GetAwaiter().GetResult();

        Equal(SettlementOutcome.OwnerChanged, result.Outcome);
        Equal("other-run", result.Probe.RunId);
        // Conclusive the moment it is seen: nothing later can make the
        // accepted run observable again.
        Equal(2, ticks.Captures);
        Equal(1, ticks.ChangeWaits);
    }

    public static void SettlementReportsAnOwnerChangeWhenTheRunIsAbandoned()
    {
        // The other half of the concurrency shape: the run is retired to the
        // main menu while a tracked option effect is still mid-flight. The
        // ownership check has to outrank the wait, or the verb spins to its
        // deadline and then reports a timeout against run:none.
        var clock = new FakeSettlementClock();
        var executing = new SettlementActivity(
            FireAndForgetCount: 0, EventOptionExecuting: true,
            ExecutorRunning: false, QueuedActionCount: 0);
        var ticks = new FakeSettlementTicks(clock,
            Probe(revision: 5, phase: Phase.Event, activity: executing),
            Probe(revision: 6, tick: 3, phase: Phase.MainMenu,
                activity: executing, runId: "none"));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(timeoutMs: 100))
            .GetAwaiter().GetResult();

        Equal(SettlementOutcome.OwnerChanged, result.Outcome);
        Equal("none", result.Probe.RunId);
        Equal(2, ticks.Captures);
    }

    public static void SettlementReportsAnOwnerChangeOnTheMenuUnderTheSameRunId()
    {
        // #144, the shape no headless boot can produce: HeadlessDecisionSurface
        // .AbandonRun nulls RunManager.State, so identity flips with the menu
        // — but the GUI does not. It keeps the retired RunState loaded behind
        // ReturnToMainMenuAfterRun, which is why PhaseDetector lets a visible
        // main menu win over RunManager's terminal flags and why new-run's
        // run_exists rejection says to abandon first. A foreign abandon there
        // leaves the accepted identity live under a quiet, decision-free menu:
        // identity alone would read that as Settled, and Settled is replayable,
        // so the run log would fingerprint the main menu as this verb's result.
        var clock = new FakeSettlementClock();
        var ticks = new FakeSettlementTicks(clock,
            Probe(revision: 5, busy: true),
            Probe(revision: 6, tick: 3, phase: Phase.MainMenu, busy: false,
                runId: "run"));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(timeoutMs: 100))
            .GetAwaiter().GetResult();

        Equal(SettlementOutcome.OwnerChanged, result.Outcome);
        // The identity never moved — the board did.
        Equal("run", result.Probe.RunId);
    }

    public static void SettlementKeepsOnlyTheFaultsSeenWhileTheRunWasOwned()
    {
        // Errors are read cumulatively from a revision (Signals.ErrorsSince),
        // so the capture that discovers the owner change also carries whatever
        // the new owner's abandon or launch logged. Attributing those to this
        // verb would decorate its run-log entry with a foreign run's faults.
        var clock = new FakeSettlementClock();
        var executing = new SettlementActivity(
            FireAndForgetCount: 0, EventOptionExecuting: true,
            ExecutorRunning: false, QueuedActionCount: 0);
        var ticks = new FakeSettlementTicks(clock,
            Probe(revision: 5, phase: Phase.Event, activity: executing,
                errors: ["fault:ours"]),
            Probe(revision: 6, tick: 3, phase: Phase.MainMenu, busy: false,
                runId: "none", errors: ["fault:ours", "fault:theirs"]));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(timeoutMs: 100))
            .GetAwaiter().GetResult();

        Equal(SettlementOutcome.OwnerChanged, result.Outcome);
        Equal(1, result.Probe.Errors.Count);
        Equal("fault:ours", result.Probe.Errors[0]);
    }

    public static void SettlementLetsAbandonSettleOnTheMenuItAskedFor()
    {
        // abandon owns the transition it requested: the menu is its boundary,
        // not a stolen observation — whether the engine has already dropped
        // the run identity or is still holding the retired state (GUI).
        foreach (var runId in new[] { "none", "run" })
        {
            var clock = new FakeSettlementClock();
            var ticks = new FakeSettlementTicks(clock,
                Probe(revision: 6, phase: Phase.MainMenu, busy: false,
                    runId: runId));
            var module = new SettlementModule(ticks, clock);

            var result = module.Follow(Request(
                timeoutMs: 100, ownership: RunOwnership.EndsRun))
                .GetAwaiter().GetResult();

            Equal(SettlementOutcome.Settled, result.Outcome);
        }
    }

    public static void SettlementLetsNewRunAdoptTheRunItMints()
    {
        // RunState is published a beat after acceptance, so new-run is
        // routinely accepted from the menu while the identity is still `none`,
        // and settles on the run it just created.
        var clock = new FakeSettlementClock();
        var ticks = new FakeSettlementTicks(clock,
            Probe(revision: 6, phase: Phase.Event, busy: false,
                runId: "fresh-run"));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(
            timeoutMs: 100,
            phaseBefore: Phase.MainMenu,
            acceptedRunId: RunOwnershipRules.NoRun,
            ownership: RunOwnership.StartsRun))
            .GetAwaiter().GetResult();

        Equal(SettlementOutcome.Settled, result.Outcome);
        Equal("fresh-run", result.Probe.RunId);
    }

    public static void SettlementDeniesNewRunAMenuBoundaryBeforeItsRunIsUp()
    {
        // #144, the last shape the ownership check alone cannot see: new-run
        // is accepted while identity is still `none`, and the launch stalls
        // (or a foreign abandon lands) with the window open. The next probe
        // reads a quiet main menu under run:none — the accepted identity, so
        // no owner change — and quiet is Settled, which is replayable. That
        // would fingerprint the main menu as the result of starting a run.
        // A launch that never leaves the menu is a timeout, not a boundary.
        var clock = new FakeSettlementClock();
        var ticks = new FakeSettlementTicks(clock, 5,
            Probe(revision: 6, phase: Phase.MainMenu, busy: false,
                runId: RunOwnershipRules.NoRun));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(
            timeoutMs: 5,
            phaseBefore: Phase.MainMenu,
            acceptedRunId: RunOwnershipRules.NoRun,
            ownership: RunOwnership.StartsRun))
            .GetAwaiter().GetResult();

        Equal(SettlementOutcome.Timeout, result.Outcome);
        False(result.Outcome.IsReplayable());
    }

    public static void SettlementStillReportsALaunchFaultOnTheMenu()
    {
        // Waiting for the board out is not a reason to sit on a fault: it
        // names the action's own outcome and is never replayable.
        var clock = new FakeSettlementClock();
        var ticks = new FakeSettlementTicks(clock, 5,
            Probe(revision: 6, phase: Phase.MainMenu, busy: false,
                runId: RunOwnershipRules.NoRun, errors: ["fault:launch"]));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(
            timeoutMs: 100,
            phaseBefore: Phase.MainMenu,
            acceptedRunId: RunOwnershipRules.NoRun,
            ownership: RunOwnership.StartsRun))
            .GetAwaiter().GetResult();

        Equal(SettlementOutcome.Fault, result.Outcome);
        Equal(1, ticks.Captures);
    }

    public static void RunOwnershipMakesALaunchWaitForItsOwnBoard()
    {
        // Only new-run, only the menu, only before its board has been seen.
        True(RunOwnershipRules.AwaitingOwnBoard(
            RunOwnership.StartsRun, Phase.MainMenu, runSeenInPlay: false));
        False(RunOwnershipRules.AwaitingOwnBoard(
            RunOwnership.StartsRun, Phase.MainMenu, runSeenInPlay: true));
        False(RunOwnershipRules.AwaitingOwnBoard(
            RunOwnership.StartsRun, Phase.Map, runSeenInPlay: false));
        False(RunOwnershipRules.AwaitingOwnBoard(
            RunOwnership.EndsRun, Phase.MainMenu, runSeenInPlay: false));
        False(RunOwnershipRules.AwaitingOwnBoard(
            RunOwnership.Bound, Phase.MainMenu, runSeenInPlay: false));
    }

    public static void SettlementDeniesNewRunAMenuBoundaryOnceItsRunIsUp()
    {
        // A launch reads main_menu under a concrete run id while the local
        // seat mounts — RunState identity exists before the seat does, which
        // is why Signals.RefreshRunIdentity reads StateOnly and PhaseDetector
        // does not. So new-run may sit on the menu holding its own id, but
        // once its board has been seen, a return to the menu is somebody
        // else's abandon: settling there would fingerprint the main menu as
        // the result of starting a run.
        var clock = new FakeSettlementClock();
        var ticks = new FakeSettlementTicks(clock,
            Probe(revision: 5, phase: Phase.MainMenu, busy: true,
                runId: "minted-run"),
            Probe(revision: 6, tick: 3, phase: Phase.Map, busy: true,
                runId: "minted-run"),
            Probe(revision: 7, tick: 4, phase: Phase.MainMenu, busy: false,
                runId: "none"));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(
            timeoutMs: 100,
            phaseBefore: Phase.MainMenu,
            acceptedRunId: "minted-run",
            ownership: RunOwnership.StartsRun))
            .GetAwaiter().GetResult();

        Equal(SettlementOutcome.OwnerChanged, result.Outcome);
        // The launch window itself was not mistaken for an owner change.
        Equal(3, ticks.Captures);
    }

    public static void RunOwnershipScopesEachVerbToTheRunThatAcceptedIt()
    {
        Equal(RunOwnership.StartsRun, RunOwnershipRules.For("new-run"));
        Equal(RunOwnership.EndsRun, RunOwnershipRules.For("abandon"));
        Equal(RunOwnership.Bound, RunOwnershipRules.For("play"));

        // Same run, still on its own board: never an owner change, whatever
        // the verb does. A run that ends naturally keeps its RunState through
        // game_over, so a bound verb settles there under its own identity.
        False(OwnerChange(RunOwnership.Bound, "a", "a"));
        False(OwnerChange(RunOwnership.Bound, "a", "a", Phase.GameOver));
        False(OwnerChange(RunOwnership.EndsRun, "a", "a"));
        False(OwnerChange(RunOwnership.StartsRun, "a", "a"));

        // A bound verb owns exactly one identity.
        True(OwnerChange(RunOwnership.Bound, "a", "b"));
        True(OwnerChange(RunOwnership.Bound, "a", "none"));
        True(OwnerChange(RunOwnership.Bound, "none", "b"));

        // ... and identity alone is not ownership: the engine can keep the
        // retired run loaded behind a visible main menu, so a verb that was
        // acting inside a run is unowned there under its own id.
        True(OwnerChange(RunOwnership.Bound, "a", "a", Phase.MainMenu));

        // The lifecycle verbs own their own transition, and only that one.
        False(OwnerChange(RunOwnership.EndsRun, "a", "none"));
        False(OwnerChange(RunOwnership.EndsRun, "a", "a", Phase.MainMenu));
        False(OwnerChange(RunOwnership.EndsRun, "a", "none", Phase.MainMenu));
        True(OwnerChange(RunOwnership.EndsRun, "a", "b"));
        False(OwnerChange(RunOwnership.StartsRun, "none", "b"));
        True(OwnerChange(RunOwnership.StartsRun, "a", "b"));
        True(OwnerChange(RunOwnership.StartsRun, "a", "none"));

        // new-run's launch window legitimately reads main_menu under the id
        // it just minted, until its board has actually been seen.
        False(OwnerChange(
            RunOwnership.StartsRun, "a", "a", Phase.MainMenu,
            runSeenInPlay: false));
        True(OwnerChange(RunOwnership.StartsRun, "a", "a", Phase.MainMenu));

        // Only a live identity outside the menu proves the board was seen.
        True(RunOwnershipRules.SeenInPlay("a", Phase.Map));
        False(RunOwnershipRules.SeenInPlay("a", Phase.MainMenu));
        False(RunOwnershipRules.SeenInPlay("none", Phase.Map));
    }

    public static void OwnerChangeIsNeitherABoundaryNorAnOwnedObservation()
    {
        False(SettlementOutcome.OwnerChanged.ReachedBoundary());
        False(SettlementOutcome.OwnerChanged.IsReplayable());
        False(SettlementOutcome.OwnerChanged.OwnsObservation());

        // Every same-run outcome still attributes its own observation: the
        // run log keeps fingerprinting settled boundaries and keeps recording
        // the phase a fault or a timeout left behind.
        True(SettlementOutcome.Settled.OwnsObservation());
        True(SettlementOutcome.NextDecision.OwnsObservation());
        True(SettlementOutcome.Fault.OwnsObservation());
        True(SettlementOutcome.Timeout.OwnsObservation());
        True(SettlementOutcome.Settled.ReachedBoundary());
        True(SettlementOutcome.Fault.ReachedBoundary());
        False(SettlementOutcome.Timeout.ReachedBoundary());
    }

    public static void SettlementBusyAccountingIncludesEveryWorkChannel()
    {
        var activities = new[]
        {
            new SettlementActivity(
                FireAndForgetCount: 1, EventOptionExecuting: false,
                ExecutorRunning: false, QueuedActionCount: 0),
            new SettlementActivity(
                FireAndForgetCount: 0, EventOptionExecuting: true,
                ExecutorRunning: false, QueuedActionCount: 0),
            new SettlementActivity(
                FireAndForgetCount: 0, EventOptionExecuting: false,
                ExecutorRunning: true, QueuedActionCount: 0),
            new SettlementActivity(
                FireAndForgetCount: 0, EventOptionExecuting: false,
                ExecutorRunning: false, QueuedActionCount: 1),
        };

        foreach (var activity in activities)
        {
            var clock = new FakeSettlementClock();
            var ticks = new FakeSettlementTicks(clock, 5,
                Probe(activity: activity, hasDecision: false));
            var module = new SettlementModule(ticks, clock);

            var result = module.Follow(Request(timeoutMs: 5))
                .GetAwaiter().GetResult();

            Equal(SettlementOutcome.Timeout, result.Outcome);
            Equal(1, ticks.ChangeWaits);
        }
    }

    public static void SettlementWaitsOutAnExecutingEventOptionEffect()
    {
        // An executing option effect must hold settlement open even though
        // a decision is on screen and the phase moved — the transient page
        // is not the boundary; the task must park or complete first.
        var clock = new FakeSettlementClock();
        var executing = new SettlementActivity(
            FireAndForgetCount: 0, EventOptionExecuting: true,
            ExecutorRunning: false, QueuedActionCount: 0);
        var ticks = new FakeSettlementTicks(clock,
            Probe(phase: Phase.Event, hasDecision: true, activity: executing),
            Probe(revision: 6, tick: 3, busy: false));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(timeoutMs: 100))
            .GetAwaiter().GetResult();

        Equal(SettlementOutcome.Settled, result.Outcome);
        Equal(2, ticks.Captures);
        Equal(1, ticks.ChangeWaits);
    }

    public static void SettlementPreservesSamePhaseEventDecisionSemantics()
    {
        var clock = new FakeSettlementClock();
        var ticks = new FakeSettlementTicks(clock,
            Probe(revision: 11, tick: 2, phase: Phase.Event,
                busy: true, hasDecision: true));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(
            phaseBefore: Phase.Event, acceptedRevision: 10, timeoutMs: 100))
            .GetAwaiter().GetResult();

        Equal(SettlementOutcome.NextDecision, result.Outcome);
    }

    public static void SettlementRequiresThreeStableDistinctFramesWhenRequested()
    {
        var clock = new FakeSettlementClock();
        var ticks = new FakeSettlementTicks(clock, 1,
            Probe(revision: 4, tick: 1, busy: false,
                requiresFrameStability: true, stateKey: "same"),
            Probe(revision: 4, tick: 2, busy: false,
                requiresFrameStability: true, stateKey: "same"),
            Probe(revision: 4, tick: 3, busy: false,
                requiresFrameStability: true, stateKey: "same"));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(timeoutMs: 100)).GetAwaiter().GetResult();

        Equal(SettlementOutcome.Settled, result.Outcome);
        Equal(3, ticks.Captures);
        Equal(2, ticks.TickWaits);
        Equal(0, ticks.ChangeWaits);
    }

    public static void SettlementTimesOutThroughInjectedClock()
    {
        var clock = new FakeSettlementClock();
        var ticks = new FakeSettlementTicks(clock, 5,
            Probe(revision: 4, tick: 1, busy: true, hasDecision: false));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(timeoutMs: 5)).GetAwaiter().GetResult();

        Equal(SettlementOutcome.Timeout, result.Outcome);
        Equal(1, ticks.ChangeWaits);
    }

    public static void SettlementClassifiesObservedErrorAsFaultWithoutFrameDelay()
    {
        var clock = new FakeSettlementClock();
        var ticks = new FakeSettlementTicks(clock,
            Probe(revision: 5, tick: 1, busy: true,
                requiresFrameStability: true,
                errors: ["async_fault:test:TestException:kaboom"]));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(timeoutMs: 100)).GetAwaiter().GetResult();

        Equal(SettlementOutcome.Fault, result.Outcome);
        True(result.Outcome.ReachedBoundary());
        False(result.Outcome.IsReplayable());
        Equal(0, ticks.TickWaits + ticks.ChangeWaits);
    }

    public static void GameOverOutcomeTrustsTheVictoryRoomOverTheRunClock()
    {
        // The V1 regression: WinTime is the run duration in whole seconds, so a
        // cheat-driven clear finishing inside one second leaves it 0. Deriving
        // the outcome from WinTime alone reported that real victory as a defeat.
        Equal("victory", RunOutcomeRules.GameOverOutcome(
            isAbandoned: false, endedInVictoryRoom: true, winTime: 0));
        Equal("defeat", RunOutcomeRules.GameOverOutcome(
            isAbandoned: false, endedInVictoryRoom: false, winTime: 0));
    }

    public static void GameOverOutcomeStillAcceptsTheRunClockAsCorroboration()
    {
        // WinTime was the previous sole test; keeping it can only add victories,
        // never remove one, so a won run that took measurable time still reads
        // as a victory even if the room signal is unavailable.
        Equal("victory", RunOutcomeRules.GameOverOutcome(
            isAbandoned: false, endedInVictoryRoom: false, winTime: 12));
    }

    public static void GameOverOutcomeReportsAbandonBeforeAnythingElse()
    {
        // Walking away is not a loss on the board, and it outranks both other
        // signals — an abandon from the final act must not read as a win.
        Equal("abandoned", RunOutcomeRules.GameOverOutcome(
            isAbandoned: true, endedInVictoryRoom: true, winTime: 30));
        Equal("abandoned", RunOutcomeRules.GameOverOutcome(
            isAbandoned: true, endedInVictoryRoom: false, winTime: 0));
    }

    public static void RunLogIsCompleteOnlyForAFullyFollowedSingleRunHistory()
    {
        // The recipe replay trusts: opened by new-run, one RunId throughout,
        // and every verb followed to a fingerprinted replayable boundary.
        True(RunLogRules.IsComplete("run-7",
        [
            Verb("run-7", "new-run", SettlementOutcome.Settled, "a1b2c3d4e5f60718"),
            Verb("run-7", "proceed", SettlementOutcome.NextDecision, "18f6e5d4c3b2a100"),
        ]));
    }

    public static void RunLogIsIncompleteWhenAnyVerbWasNotFollowed()
    {
        // A partial follow history: the accepted verbs are still diagnostic
        // truth, but replay would report success after checking only a prefix
        // of the fingerprints, so the recipe is not complete.
        False(RunLogRules.IsComplete("run-7",
        [
            Verb("run-7", "new-run", SettlementOutcome.Settled, "a1b2c3d4e5f60718"),
            Verb("run-7", "proceed", outcome: null, fingerprint: null),
        ]));

        // Not even the opening verb was followed — the P11 shape where a
        // new-run without --follow must never read as replayable.
        False(RunLogRules.IsComplete("run-7",
            [Verb("run-7", "new-run", outcome: null, fingerprint: null)]));
    }

    public static void RunLogIsIncompleteWhenAFingerprintIsMissing()
    {
        // Settled but unfingerprinted: replay has nothing to compare against,
        // so it could not stop at the first divergence.
        False(RunLogRules.IsComplete("run-7",
        [
            Verb("run-7", "new-run", SettlementOutcome.Settled, fingerprint: null),
        ]));
        False(RunLogRules.IsComplete("run-7",
        [
            Verb("run-7", "new-run", SettlementOutcome.Settled, "   "),
        ]));
    }

    public static void RunLogIsIncompleteForNonReplayableOutcomes()
    {
        // Fault and timeout leave the engine somewhere replay cannot check.
        foreach (var outcome in new[]
                 { SettlementOutcome.Fault, SettlementOutcome.Timeout })
            False(RunLogRules.IsComplete("run-7",
            [
                Verb("run-7", "new-run", SettlementOutcome.Settled, "a1b2c3d4e5f60718"),
                Verb("run-7", "play", outcome, "18f6e5d4c3b2a100"),
            ]));
    }

    public static void RunLogIsIncompleteForCrossRunOrHeadlessHistories()
    {
        var followed = new[]
        {
            Verb("run-7", "new-run", SettlementOutcome.Settled, "a1b2c3d4e5f60718"),
            Verb("run-8", "proceed", SettlementOutcome.Settled, "18f6e5d4c3b2a100"),
        };

        // Entries from two runs: replaying them would compound divergence.
        False(RunLogRules.IsComplete("run-7", followed));
        // A history that never opened its run cannot be replayed from a menu.
        False(RunLogRules.IsComplete("run-7",
            [Verb("run-7", "proceed", SettlementOutcome.Settled, "a1b2c3d4e5f60718")]));
        // No run identity at all, and no history at all.
        False(RunLogRules.IsComplete("none",
            [Verb("none", "new-run", SettlementOutcome.Settled, "a1b2c3d4e5f60718")]));
        False(RunLogRules.IsComplete("run-7", []));
    }

    public static void RunLogAdoptsAnUnassignedHistoryOnceTheEngineNamesTheRun()
    {
        // new-run is accepted before the engine has assigned a RunId, so the
        // opening verbs are recorded as "none" and relabelled afterwards.
        True(RunLogRules.CanAdopt("none", "run-7",
        [
            Verb("none", "new-run", SettlementOutcome.Settled, "a1b2c3d4e5f60718"),
            Verb("none", "proceed", SettlementOutcome.Settled, "18f6e5d4c3b2a100"),
        ]));
    }

    public static void RunLogRefusesToAdoptMixedMissingOrOwnedHistories()
    {
        var unassigned = new[]
        {
            Verb("none", "new-run", SettlementOutcome.Settled, "a1b2c3d4e5f60718"),
        };

        // Missing: there is no run to adopt, and nothing to attribute.
        False(RunLogRules.CanAdopt("none", "none", unassigned));
        False(RunLogRules.CanAdopt("none", "run-7", []));
        // Already owned: relabelling a bound log would forge attribution.
        False(RunLogRules.CanAdopt("run-7", "run-8", unassigned));
        // Mixed: a verb already names a run, so the log is not wholly unassigned.
        False(RunLogRules.CanAdopt("none", "run-8",
        [
            Verb("none", "new-run", SettlementOutcome.Settled, "a1b2c3d4e5f60718"),
            Verb("run-7", "proceed", SettlementOutcome.Settled, "18f6e5d4c3b2a100"),
        ]));
        // Truncated: the history does not open the run it would claim.
        False(RunLogRules.CanAdopt("none", "run-7",
            [Verb("none", "proceed", SettlementOutcome.Settled, "a1b2c3d4e5f60718")]));
    }

    public static void SelectionProjectionMarksOnlyThePickedInstance()
    {
        // The #147 regression: a hand routinely holds several copies of one
        // model. Matching a selection by model lights up every copy, so the
        // caller cannot tell which row is still free and re-picks the first
        // row forever — toggling it on and off instead of completing.
        var first = new SelectableCard("DEFEND_SILENT");
        var second = new SelectableCard("DEFEND_SILENT");
        var picked = SelectionProjection.Picked(
            new List<SelectableCard> { first });

        True(SelectionProjection.IsSelected(first, picked));
        False(SelectionProjection.IsSelected(second, picked));
    }

    public static void SelectionProjectionAgreesWithTheSelectedCollection()
    {
        // Per-row flags and the top-level selected list are two views of one
        // decision: every candidate the list names reads selected, every
        // other candidate reads unselected, after each pick and each toggle.
        var candidates = new[]
        {
            new SelectableCard("STRIKE_SILENT"),
            new SelectableCard("DEFEND_SILENT"),
            new SelectableCard("STRIKE_SILENT"),
        };
        var picked = new List<SelectableCard>();

        Equal("---", Flags(candidates, picked));
        picked.Add(candidates[2]);
        Equal("--x", Flags(candidates, picked));
        picked.Add(candidates[0]);
        Equal("x-x", Flags(candidates, picked));
        picked.Remove(candidates[2]);            // toggled back off
        Equal("x--", Flags(candidates, picked));
    }

    public static void SelectionProjectionTreatsAnEmptyHolderAsUnselected()
    {
        // GUI hand rows can be holders with no card node; they are reported
        // as a card-less slot and must never claim to be picked.
        Equal(false, SelectionProjection.IsSelected(
            (SelectableCard?)null,
            SelectionProjection.Picked(
                new[] { new SelectableCard("STRIKE_SILENT") })));
        Equal(false, SelectionProjection.IsSelected(
            new SelectableCard("STRIKE_SILENT"),
            SelectionProjection.Picked((IEnumerable<SelectableCard>?)null)));
    }

    public static void SelectionProjectionReadsThePickedListOncePerSnapshot()
    {
        // The picker hands out the live list its pick verb mutates, so the
        // rows of one snapshot are answered from a reading taken once —
        // never from a collection that can change between rows.
        var card = new SelectableCard("STRIKE_SILENT");
        var selected = new List<SelectableCard>();
        var picked = SelectionProjection.Picked(selected);

        selected.Add(card);

        False(SelectionProjection.IsSelected(card, picked));
        True(SelectionProjection.IsSelected(
            card, SelectionProjection.Picked(selected)));
    }

    public static void SeaGlassIsBoundToItsOwningCharacter()
    {
        // OROBAS stamps the character whose card pool Sea Glass reads before
        // the pick. Granted bare, AfterObtained logs "obtained without a
        // character ID assigned" — an engine error the sweeps read as a
        // product fault, and a silent fall back to Ironclad besides.
        var context = ContextBoundContent.For("SEA_GLASS");

        Equal(1, context.Count);
        Equal("CharacterId", context[0].Property);
        Equal(ConstructionValue.OwnerCharacterId, context[0].Value);
        True(ContextBoundContent.IsContextBound("SEA_GLASS"));
    }

    public static void MadScienceIsBoundToBothPropertiesItsEventAssigns()
    {
        // TINKER_TIME.RiderChosen assigns the card type and the rider in one
        // statement block before the card is added to the deck, so both are
        // construction context. Neither type default is reachable: CardType
        // .None makes OnPlay throw ArgumentOutOfRangeException, and
        // RiderEffect.None skips the rider half and renders the card's
        // description as "???".
        var context = ContextBoundContent.For("MAD_SCIENCE");

        Equal(2, context.Count);
        Equal("TinkerTimeType", context[0].Property);
        Equal(ConstructionValue.EnumMember, context[0].Value);
        Equal("Attack", context[0].Member);
        Equal("TinkerTimeRider", context[1].Property);
        Equal(ConstructionValue.EnumMember, context[1].Value);
        Equal("Sapping", context[1].Member);
    }

    public static void EveryStampedContextValueNamesAMember()
    {
        // EnumMember is the only kind that carries one, and the cheat parses
        // it against the property's own enum — a null there would reject the
        // injection at runtime, where only the deep sweeps would notice.
        foreach (var entry in new[] { "SEA_GLASS", "MAD_SCIENCE" })
            foreach (var context in ContextBoundContent.For(entry))
                Equal(context.Value == ConstructionValue.EnumMember,
                    !string.IsNullOrEmpty(context.Member));
    }

    public static void DirectlyExecutableContentDeclaresNoConstructionContext()
    {
        // The distinction the sweeps ride on: ordinary content is injected as
        // is, and /models must not advertise a context that would make a sweep
        // demand one.
        Equal(0, ContextBoundContent.For("STRIKE_IRONCLAD").Count);
        False(ContextBoundContent.IsContextBound("STRIKE_IRONCLAD"));
        Equal(null, ContextBoundContent.PublishedContext("STRIKE_IRONCLAD"));
    }

    public static void PublishedContextNamesEveryStampedProperty()
    {
        // The wire form /models hands the sweeps: property names only, in
        // table order, so a fixture that stops applying is visible from the
        // registry alone.
        Equal("CharacterId",
            string.Join(",", ContextBoundContent.PublishedContext("SEA_GLASS")!));
        Equal("TinkerTimeType,TinkerTimeRider",
            string.Join(",", ContextBoundContent.PublishedContext("MAD_SCIENCE")!));
    }

    public static void ConstructionContextIsKeyedByTheNormalizedModelEntry()
    {
        // The cheats upper-case args.id before every registry lookup, so the
        // table is keyed that way too. A lower-case probe must miss rather
        // than half-match and stamp nothing.
        False(ContextBoundContent.IsContextBound("sea_glass"));
        True(ContextBoundContent.IsContextBound("SEA_GLASS"));
    }

    public static void EventOptionTrackerOutlivesAbandonThenNewRunRotations()
    {
        // The P16 shape, and the regression this replaced rotation counting
        // for: a task parked before abandon only faults after the next run has
        // started. Abandon and new-run are two owner rotations back to back
        // with no elapsed time, so any rotation-counted lifetime expired
        // before the task could fault — and its stale TaskHelper line then
        // leaked into the new run.
        var clock = new FakeSettlementClock();
        var parked = new TaskCompletionSource();
        var tracker = RetiredTracker(clock, parked.Task);

        tracker.ChangeOwner(null, null);                       // abandon
        tracker.ChangeOwner(                                   // new-run
            new object(), new MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer());

        False(parked.Task.IsCompleted);
        True(tracker.HasRetired);
    }

    public static void EventOptionTrackerExpiresZombieOnElapsedTime()
    {
        // #125: a never-completing zombie must leave the ledger on bounded
        // time, so HasRetired returns to false without a host restart even if
        // no further run ever rotates the owner.
        var clock = new FakeSettlementClock();
        var parked = new TaskCompletionSource();
        var tracker = RetiredTracker(clock, parked.Task);

        True(tracker.HasRetired);
        clock.Advance(29_000);
        True(tracker.HasRetired);       // still inside the correlation window
        clock.Advance(2_000);
        False(tracker.HasRetired);      // expired on elapsed time alone
        False(parked.Task.IsCompleted);
    }

    public static void EventOptionTrackerDropsRetiredQuietCompletions()
    {
        // A stale success logged nothing, so it has nothing to suppress and
        // must not hold the correlation window open.
        var clock = new FakeSettlementClock();
        var finished = new TaskCompletionSource();
        var tracker = RetiredTracker(clock, finished.Task);

        True(tracker.HasRetired);
        finished.SetResult();
        False(tracker.HasRetired);
    }

    public static void SettlementWaitsOutATrackedOptionEffectBeforeReportingItsFault()
    {
        // A fault names the outcome, not the boundary. An option effect that
        // is still mid-continuation owns real state: the Amalgamator removes
        // the chosen cards behind one delay and grants their replacement
        // behind the next, so returning on the fault alone publishes a
        // half-applied deck. ErrorsSince is a window query, so the fault is
        // still there for the probe that finally parks.
        var clock = new FakeSettlementClock();
        var executing = new SettlementActivity(
            FireAndForgetCount: 0, EventOptionExecuting: true,
            ExecutorRunning: false, QueuedActionCount: 0);
        var fault = new[] { "async_fault:event-option:TestException:kaboom" };
        var ticks = new FakeSettlementTicks(clock,
            Probe(revision: 5, phase: Phase.Event, activity: executing,
                errors: fault),
            Probe(revision: 6, tick: 3, busy: false, errors: fault));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(timeoutMs: 100))
            .GetAwaiter().GetResult();

        Equal(SettlementOutcome.Fault, result.Outcome);
        // The settled board, not the torn one the fault interrupted.
        Equal(6L, result.Probe.Revision);
        Equal(2, ticks.Captures);
        Equal(1, ticks.ChangeWaits);
    }

    public static void SettlementKeepsAFaultObservedWhileTheEffectWasExecuting()
    {
        // Waiting out a tracked effect must not lose the fault that started the
        // wait. Signals.ErrorsSince reads a BOUNDED journal (RevisionJournal
        // evicts past its cap), and SettlementObservationCapture's own errors
        // are per-capture, so a later probe can legitimately come back clean.
        // Reporting Settled there would call a faulted action successful and
        // mark it replayable.
        var clock = new FakeSettlementClock();
        var executing = new SettlementActivity(
            FireAndForgetCount: 0, EventOptionExecuting: true,
            ExecutorRunning: false, QueuedActionCount: 0);
        var ticks = new FakeSettlementTicks(clock,
            Probe(revision: 5, phase: Phase.Event, activity: executing,
                errors: ["async_fault:event-option:TestException:kaboom"]),
            Probe(revision: 6, tick: 3, busy: false));   // journal dropped it
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(timeoutMs: 100))
            .GetAwaiter().GetResult();

        Equal(SettlementOutcome.Fault, result.Outcome);
        False(result.Outcome.IsReplayable());
        // The response attributes the errors it reports, so the token has to
        // ride along even though the final probe no longer carried it.
        Equal("async_fault:event-option:TestException:kaboom",
            result.Probe.Errors.Single());
    }

    public static void SettlementKeepsAnEarlierFaultWhenTheWaitTimesOut()
    {
        // Same loss on the deadline path: the last probe is the one inspected,
        // so an evicted error would downgrade a seen fault to a timeout and
        // flip the response's `settled` flag via ReachedBoundary.
        var clock = new FakeSettlementClock();
        var executing = new SettlementActivity(
            FireAndForgetCount: 0, EventOptionExecuting: true,
            ExecutorRunning: false, QueuedActionCount: 0);
        var ticks = new FakeSettlementTicks(clock, 5,
            Probe(revision: 5, phase: Phase.Event, activity: executing,
                errors: ["async_fault:event-option:TestException:kaboom"]),
            Probe(revision: 6, phase: Phase.Event, activity: executing));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(timeoutMs: 5))
            .GetAwaiter().GetResult();

        Equal(SettlementOutcome.Fault, result.Outcome);
        True(result.Outcome.ReachedBoundary());
        Equal("async_fault:event-option:TestException:kaboom",
            result.Probe.Errors.Single());
    }

    public static void SettlementReportsEachObservedErrorTokenOnce()
    {
        // A token repeated across captures is one failure, not several: the
        // window query re-reports what it still holds.
        var clock = new FakeSettlementClock();
        var executing = new SettlementActivity(
            FireAndForgetCount: 0, EventOptionExecuting: true,
            ExecutorRunning: false, QueuedActionCount: 0);
        var first = "async_fault:event-option:TestException:kaboom";
        var second = "engine_error:System.InvalidOperationException: later";
        var ticks = new FakeSettlementTicks(clock,
            Probe(revision: 5, phase: Phase.Event, activity: executing,
                errors: [first]),
            Probe(revision: 6, phase: Phase.Event, activity: executing,
                errors: [first, second]),
            Probe(revision: 7, tick: 4, busy: false, errors: [second]));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(timeoutMs: 100))
            .GetAwaiter().GetResult();

        Equal(SettlementOutcome.Fault, result.Outcome);
        Equal(2, result.Probe.Errors.Count);
        Equal(first, result.Probe.Errors[0]);
        Equal(second, result.Probe.Errors[1]);
    }

    public static void SettlementFaultStillOutrunsOpaqueFireAndForgetWork()
    {
        // The counterpart: fire-and-forget work is exactly the opaque channel
        // the bridge cannot follow, so it must never hold a fault open.
        var clock = new FakeSettlementClock();
        var opaque = new SettlementActivity(
            FireAndForgetCount: 1, EventOptionExecuting: false,
            ExecutorRunning: false, QueuedActionCount: 0);
        var ticks = new FakeSettlementTicks(clock,
            Probe(revision: 5, activity: opaque,
                errors: ["async_fault:test:TestException:kaboom"]));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(timeoutMs: 100))
            .GetAwaiter().GetResult();

        Equal(SettlementOutcome.Fault, result.Outcome);
        Equal(1, ticks.Captures);
        Equal(0, ticks.TickWaits + ticks.ChangeWaits);
    }

    public static void SettlementReportsAFaultWhenTrackedOptionWorkNeverParks()
    {
        // Waiting for the effect must not downgrade an observed fault to a
        // timeout: ReachedBoundary drives the response's `settled` flag, and
        // a fault we actually saw is conclusive about the action.
        var clock = new FakeSettlementClock();
        var executing = new SettlementActivity(
            FireAndForgetCount: 0, EventOptionExecuting: true,
            ExecutorRunning: false, QueuedActionCount: 0);
        var ticks = new FakeSettlementTicks(clock, 5,
            Probe(revision: 5, phase: Phase.Event, activity: executing,
                errors: ["async_fault:event-option:TestException:kaboom"]));
        var module = new SettlementModule(ticks, clock);

        var result = module.Follow(Request(timeoutMs: 5))
            .GetAwaiter().GetResult();

        Equal(SettlementOutcome.Fault, result.Outcome);
        True(result.Outcome.ReachedBoundary());
        False(result.Outcome.IsReplayable());
    }

    public static void SettlementTurnsCaptureFailuresIntoTypedFaults()
    {
        var clock = new FakeSettlementClock();
        var module = new SettlementModule(
            new ThrowingSettlementTicks(
                new InvalidOperationException("forced observation failure")),
            clock);

        var result = module.Follow(Request(
            acceptedRevision: 17,
            acceptedRunId: "accepted-run"))
            .GetAwaiter().GetResult();

        Equal(SettlementOutcome.Fault, result.Outcome);
        False(result.Probe.ObservationAvailable);
        Equal(17L, result.Probe.Revision);
        Equal("accepted-run", result.Probe.RunId);
        True(result.Probe.Errors.Single().StartsWith(
            "async_fault:observation:InvalidOperationException:",
            StringComparison.Ordinal));
    }

    public static void ObservationCaptureRetriesTheResultingPhaseAtCombatTeardown()
    {
        var phases = new Queue<Phase>([Phase.Combat, Phase.Rewards]);
        var attempts = 0;

        var captured = SettlementObservationCapture.Capture(
            () => phases.Count > 1 ? phases.Dequeue() : phases.Peek(),
            () =>
            {
                attempts++;
                if (attempts == 1)
                    throw new InvalidOperationException(
                        "failed to read power semantic state after 1 attempt");
                return new SnapshotContract(Phase.Rewards);
            },
            revision: () => 23,
            runId: () => "run-after-combat");

        Equal(2, attempts);
        True(captured.ObservationAvailable);
        Equal(Phase.Rewards, captured.Observation.Phase);
        Equal(23L, captured.Observation.Revision);
        Equal("run-after-combat", captured.Observation.RunId);
        Equal(0, captured.Errors.Count);
    }

    public static void ObservationCaptureKeepsStableSemanticReadFailuresVisible()
    {
        var captured = SettlementObservationCapture.Capture(
            () => Phase.Combat,
            () => throw new InvalidOperationException(
                "failed to read power semantic state after 1 attempt"),
            revision: () => 29,
            runId: () => "broken-run");

        False(captured.ObservationAvailable);
        Equal(Phase.Combat, captured.Observation.Phase);
        Equal(29L, captured.Observation.Revision);
        Equal("broken-run", captured.Observation.RunId);
        True(captured.Errors.Single().Contains(
            "failed to read power semantic state", StringComparison.Ordinal));
    }

    public static void SettlementExecutorWatchdogUsesInjectedClockAndFiresOnce()
    {
        var clock = new FakeSettlementClock();
        var module = new SettlementModule(
            new FakeSettlementTicks(clock, Probe()), clock);
        var action = new object();
        var probe = Watchdog(action, "PlayCardAction");

        Equal(0, module.ObserveWatchdogs(probe).Events.Count);
        clock.Advance(SettlementModule.WedgeTimeoutMs);
        Equal(0, module.ObserveWatchdogs(probe).Events.Count);
        clock.Advance(1);
        var wedged = module.ObserveWatchdogs(probe);
        Equal("wedge:PlayCardAction", string.Join(',', wedged.Events));
        Equal(SettlementModule.WedgeTimeoutMs + 1L, wedged.ExecutorStuckMs);
        clock.Advance(1000);
        Equal(0, module.ObserveWatchdogs(probe).Events.Count);
    }

    public static void SettlementDeadBoardWatchdogUsesInjectedClockAndFiresOnce()
    {
        var clock = new FakeSettlementClock();
        var module = new SettlementModule(
            new FakeSettlementTicks(clock, Probe()), clock);
        var deadBoard = Watchdog(
            action: null,
            actionName: null,
            combatInProgress: true,
            queuesEmpty: true,
            allEnemiesDead: true);

        Equal(0, module.ObserveWatchdogs(deadBoard).Events.Count);
        clock.Advance(SettlementModule.WedgeTimeoutMs + 1);
        Equal("wedge:DeadBoard", string.Join(',',
            module.ObserveWatchdogs(deadBoard).Events));
        clock.Advance(1000);
        Equal(0, module.ObserveWatchdogs(deadBoard).Events.Count);
    }

    public static void HealthCapabilitiesAdvertiseCheatArgumentShapes()
    {
        var capabilities = JsonSerializer.SerializeToNode(
            ProtocolCapabilities.Create([]))!.AsObject();
        var names = capabilities["cheats"]!.AsArray()
            .Select(item => item!.GetValue<string>());
        var shapes = capabilities["cheatArgumentShapes"]!.AsArray();
        var artifactShapes = JsonNode.Parse(
            ProtocolVocabulary.CreateArtifactJson())!["cheatArgumentShapes"];

        Equal(string.Join(',', ProtocolVocabulary.Cheats.All.Select(shape => shape.Name)),
            string.Join(',', names));
        True(JsonNode.DeepEquals(artifactShapes, shapes));
    }

    public static void RejectionCodesExposeTheCompleteDispatcherGrammar()
    {
        Equal(ProtocolVocabulary.Rejections.BadRequest, RejectionCodes.BadRequest);
        True(RejectionCodes.All.SequenceEqual(ProtocolVocabulary.Rejections.All));
    }

    public static void FieldValueFindsPrivateFieldsDeclaredOnBaseTypes()
    {
        var target = new DerivedProbe();

        Equal("base-secret", Reflect.FieldValue(target, "_secret"));
    }

    public static void PropertyValueInvokesPrivateGettersDeclaredOnBaseTypes()
    {
        var target = new DerivedProbe();

        Equal("computed-value", Reflect.PropertyValue(target, "Computed"));
    }

    public static void SetPropertyInvokesPrivateSetters()
    {
        var target = new DerivedProbe();

        True(Reflect.SetProperty(target, "Mutable", "changed"));

        Equal("changed", target.ReadMutable());
    }

    public static void SetPropertyOrBackingFieldSetsGetOnlyAutoProperties()
    {
        var target = new DerivedProbe();

        True(Reflect.SetPropertyOrBackingField(target, "GetOnly", "patched"));

        Equal("patched", target.GetOnly);
    }

    public static void InvokeFindsPrivateMethodsDeclaredOnBaseTypes()
    {
        var target = new DerivedProbe();

        Equal("left:right", Reflect.Invoke(target, "Join", "left", "right"));
    }

    public static void InvokeReportsMissingMethods()
    {
        var target = new DerivedProbe();

        Throws<MissingMethodException>(() => Reflect.Invoke(target, "Missing"));
    }

    public static void NormalizeIconsCollapsesEnergyIconsToOneToken()
    {
        Equal("Gain 2[energy].", RichText.NormalizeIcons(
            "Gain 2[img]res://images/packed/sprite_fonts/ironclad_energy_icon.png[/img]."));
    }

    public static void NormalizeIconsTokenizesEveryIconByBasename()
    {
        Equal("[star] beats [block]", RichText.NormalizeIcons(
            "[img]res://a/star_icon.png[/img] beats [img]res://b/block_icon.png[/img]"));
    }

    public static void NormalizeIconsToleratesImgAttributes()
    {
        Equal("[energy]", RichText.NormalizeIcons(
            "[img width=24]res://x/silent_energy_icon.png[/img]"));
    }

    public static void NormalizeIconsLeavesPlainRichTextAlone()
    {
        Equal("Deal [green]9[/green] damage.",
            RichText.NormalizeIcons("Deal [green]9[/green] damage."));
    }

    public static void FirstChanceFilterRecognizesOnlyKnownStubMisses()
    {
        var knownType = new TypeLoadException(
            "Could not load type 'MethodName' from assembly 'GodotSharp, "
            + "Version=4.5.1.0, Culture=neutral, PublicKeyToken=null'.");
        var knownReflection = new ReflectionTypeLoadException(
            Type.EmptyTypes, [knownType]);
        var knownMethod = new MissingMethodException(
            "Method not found: 'System.Collections.Generic.IEnumerator`1<!0> "
            + "Godot.Collections.Array`1.GetEnumerator()'.");

        True(FirstChanceFilter.IsKnownGodotStubMiss(knownReflection));
        True(FirstChanceFilter.IsKnownGodotStubMiss(knownMethod));
    }

    public static void FirstChanceFilterLeavesNewGodotApiMissesVisible()
    {
        var newType = new TypeLoadException(
            "Could not load type 'FatalNewApi' from assembly 'GodotSharp, "
            + "Version=4.5.1.0, Culture=neutral, PublicKeyToken=null'.");
        var mixedReflection = new ReflectionTypeLoadException(
            Type.EmptyTypes,
            [
                new TypeLoadException(
                    "Could not load type 'MethodName' from assembly 'GodotSharp, "
                    + "Version=4.5.1.0, Culture=neutral, PublicKeyToken=null'."),
                newType,
            ]);
        var newMethod = new MissingMethodException(
            "Method not found: 'Void Godot.Node.FatalNewApi()'.");

        True(!FirstChanceFilter.IsKnownGodotStubMiss(newType));
        True(!FirstChanceFilter.IsKnownGodotStubMiss(mixedReflection));
        True(!FirstChanceFilter.IsKnownGodotStubMiss(newMethod));
    }

    public static void MissingQueuePopIsSettledOnlyAfterCombatTeardown()
    {
        var clock = new FakeSettlementClock();
        var settlement = new SettlementModule(
            new FakeSettlementTicks(clock, Probe()), clock);
        var pop = new InvalidOperationException(
            "Tried to pop action EndPlayerTurnAction, but we didn't find it in any queue!");

        Equal(InlineFaultKind.VictorySettled, settlement.ClassifyInlineFault(
            pop, "EndPlayerTurnAction", combatInProgress: false, revisionChanged: true));
        Equal(InlineFaultKind.Partial, settlement.ClassifyInlineFault(
            pop, "EndPlayerTurnAction", combatInProgress: true, revisionChanged: true));
        Equal(InlineFaultKind.Partial, settlement.ClassifyInlineFault(
            pop, "PlayCardAction", combatInProgress: false, revisionChanged: true));
        Equal(InlineFaultKind.Failed, settlement.ClassifyInlineFault(
            new InvalidOperationException("some other queue failure"),
            "EndPlayerTurnAction", combatInProgress: false, revisionChanged: false));

        Equal(InlineFaultKind.VictorySettled, settlement.ClassifyInlineFault(
            new AggregateException(pop),
            "EndPlayerTurnAction", combatInProgress: false, revisionChanged: false));
        Equal(InlineFaultKind.Failed, settlement.ClassifyInlineFault(
            new AggregateException(new InvalidOperationException("some other queue failure")),
            "EndPlayerTurnAction", combatInProgress: false, revisionChanged: false));
    }

    public static void InlineFaultClassificationDistinguishesPartialFromFailed()
    {
        var clock = new FakeSettlementClock();
        var settlement = new SettlementModule(
            new FakeSettlementTicks(clock, Probe()), clock);
        var fault = new MissingMethodException("missing Godot API");

        Equal(InlineFaultKind.Partial, settlement.ClassifyInlineFault(
            fault, "PlayCardAction", combatInProgress: true, revisionChanged: true));
        Equal(InlineFaultKind.Failed, settlement.ClassifyInlineFault(
            fault, "PlayCardAction", combatInProgress: true, revisionChanged: false));
    }

    public static void EndingCombatIsNotADeadBoardWedge()
    {
        var clock = new FakeSettlementClock();
        var module = new SettlementModule(
            new FakeSettlementTicks(clock, Probe()), clock);
        var ending = Watchdog(null, null, combatInProgress: true,
            combatIsEnding: true, queuesEmpty: true, allEnemiesDead: true);
        var queued = Watchdog(null, null, combatInProgress: true,
            queuesEmpty: false, allEnemiesDead: true);

        Equal(0, module.ObserveWatchdogs(ending).Events.Count);
        clock.Advance(SettlementModule.WedgeTimeoutMs + 1);
        Equal(0, module.ObserveWatchdogs(ending).Events.Count);
        Equal(0, module.ObserveWatchdogs(queued).Events.Count);
    }

    public static void CardIdentityGrammarProducesBareSelectorAndTextKeyTogether()
    {
        var identity = CardSpecifier.Encode("BASH", false, 0, null, null);

        Equal("BASH", identity.Selector);
        Equal("BASH+0", identity.TextKey);
    }

    public static void CardIdentityGrammarSharesModifierOrderAcrossBothFormats()
    {
        var identity = CardSpecifier.Encode(
            "BASH", true, 2, "SELF_HELP", "CURSED");

        Equal("BASH+@SELF_HELP!CURSED", identity.Selector);
        Equal("BASH+2@SELF_HELP!CURSED", identity.TextKey);
    }

    public static void CardIdentityGrammarPreservesModifiersOnBareCopies()
    {
        var identity = CardSpecifier.Encode(
            "BASH", false, 0, "SELF_HELP", "CURSED");

        Equal("BASH@SELF_HELP!CURSED", identity.Selector);
        Equal("BASH+0@SELF_HELP!CURSED", identity.TextKey);
    }

    public static void CardIdentityGrammarPreservesDistinctEngineUpgradeSignals()
    {
        var identity = CardSpecifier.Encode("BASH", true, 0, null, null);

        Equal("BASH+", identity.Selector);
        Equal("BASH+0", identity.TextKey);
    }

    public static void DecisionLegalVerbsComeFromVisibleTargetsAndGates()
    {
        var snapshot = new SnapshotContract(Phase.CardSelect)
        {
            Cards = [new SnapshotItemContract { Index = 0 }],
            Confirmable = false,
            Cancelable = true,
            Player = new SnapshotPlayerContract { Potions = [] },
        };

        var legal = DecisionProjection.LegalVerbs(snapshot, runActive: true);

        Equal("pick-card,skip,abandon", string.Join(',', legal));
    }

    public static void QueenBoundDrawsExposeHookBlockAndKeepLegalPlayInAgreement()
    {
        SnapshotItemContract Card(
            string selector, CardPlayabilityState playability)
        {
            var card = new SnapshotItemContract
            {
                Model = selector.Split('!')[0],
                Selector = selector,
            };
            CardCombatObservation.ApplyPlayability(card, playability);
            return card;
        }

        var chains = CardPlayabilityState.Blocked(
            "BlockedByHook", "CHAINS_OF_BINDING_POWER");
        var bound = new[]
        {
            Card("STRIKE_R!BOUND", chains),
            Card("DEFEND_R!BOUND", chains),
            Card("BASH!BOUND", chains),
        };
        var unbound = Card("STRIKE_R", CardPlayabilityState.PlayableCard);
        var snapshot = new SnapshotContract(Phase.Combat)
        {
            Side = "player",
            ActionsDisabled = false,
            Hand = [.. bound, unbound],
        };

        foreach (var card in snapshot.Hand.Take(3))
        {
            False(card.Playable ?? true);
            var wire = card.ToJsonObject();
            Equal("BlockedByHook",
                wire["unplayableReason"]!.GetValue<string>());
            Equal("CHAINS_OF_BINDING_POWER",
                wire["unplayablePreventer"]!.GetValue<string>());
        }
        True(snapshot.Hand[3].Playable ?? false);
        Equal(RejectionCodes.NotPlayable, chains.RejectionCode);
        Equal("play,end-turn", string.Join(',',
            DecisionProjection.LegalVerbs(snapshot, runActive: false)));

        snapshot.Hand = bound;
        Equal("end-turn", string.Join(',',
            DecisionProjection.LegalVerbs(snapshot, runActive: false)));
    }

    public static void ErrorEventsCondenseLogLinesIntoBoundedTokens()
    {
        // Multi-line exception dumps become one whitespace-collapsed token.
        Equal("engine_error:TestException: kaboom at Some.Frame()",
            ErrorEvents.FromLogLine(
                "TestException: kaboom\n   at Some.Frame()", combatInProgress: false));
        Equal("async_fault:option:NullReferenceException:object was null",
            ErrorEvents.FromAsyncFault(
                "option", "NullReferenceException", "object  was\nnull"));

        // Journal entries stay bounded no matter how long the dump is.
        var flooded = ErrorEvents.FromLogLine(new string('x', 500), combatInProgress: false);
        Equal("engine_error:".Length + 160, flooded.Length);
    }

    public static void ErrorEventsRecognizeExactlyTheTwoFaultStreams()
    {
        True(ErrorEvents.IsError("engine_error:TestException: kaboom"));
        True(ErrorEvents.IsError("async_fault:option:TestException:kaboom"));
        False(ErrorEvents.IsError("async:option"));
        False(ErrorEvents.IsError("phase:map->combat"));
        False(ErrorEvents.IsError("wedge:DeadBoard"));
        False(ErrorEvents.IsError("engine_note:System.InvalidOperationException: benign"));
    }

    public static void ErrorEventsDowngradeTheVictoryStalePopToANote()
    {
        // The exact line the engine logs on the healthy victory path
        // (exception ToString: type, message, then stack) — a note, not
        // an error, or every clean victory could read as polluted.
        var victoryLine =
            "System.InvalidOperationException: Tried to pop action "
            + "EndPlayerTurnAction, but we didn't find it in any queue!\n"
            + "   at MegaCrit.Sts2.Core.GameActions.ActionQueue.Pop()";
        var evt = ErrorEvents.FromLogLine(victoryLine, combatInProgress: false);
        True(evt.StartsWith("engine_note:", StringComparison.Ordinal));
        False(ErrorEvents.IsError(evt));

        // The identical text mid-combat is queue corruption, not victory
        // cleanup — same context requirement as VictorySettled.
        True(ErrorEvents.IsError(
            ErrorEvents.FromLogLine(victoryLine, combatInProgress: true)));

        // A different action or exception type merely mentioning queues
        // stays a real error.
        True(ErrorEvents.IsError(ErrorEvents.FromLogLine(
            "System.InvalidOperationException: Tried to pop action "
            + "PlayCardAction, but we didn't find it in any queue!",
            combatInProgress: false)));
        True(ErrorEvents.IsError(ErrorEvents.FromLogLine(
            "System.NullReferenceException: Tried to pop action "
            + "EndPlayerTurnAction, but we didn't find it in any queue!",
            combatInProgress: false)));
    }

    public static void ErrorEventsDowngradeOnlyExactHeadlessCompletionNoise()
    {
        var headlessCompletionNoise = new[]
        {
            "Act 4 is not yet implemented.",
            "EpochModel was not found :(",
            "System.InvalidOperationException: Tried to set event options after event was finished!\n"
                + "   at MegaCrit.Sts2.Core.Models.EventModel.SetEventState(LocString description, IEnumerable`1 eventOptions)\n"
                + "   at MegaCrit.Sts2.Core.Models.EventModel.SetEventFinished(LocString description)",
        };

        foreach (var line in headlessCompletionNoise)
        {
            var note = ErrorEvents.FromLogLine(
                line, combatInProgress: true, headlessHost: true);
            True(note.StartsWith("engine_note:", StringComparison.Ordinal));
            False(ErrorEvents.IsError(note));

            // The same engine error in the GUI remains actionable.
            True(ErrorEvents.IsError(ErrorEvents.FromLogLine(
                line, combatInProgress: true, headlessHost: false)));
        }

        // Exact messages from another call path remain actionable even in
        // the host: only the known completion-tail stack is presentation
        // noise.
        True(ErrorEvents.IsError(ErrorEvents.FromLogLine(
            "EpochModel was not found :(\n   at Some.Other.Frame()",
            combatInProgress: false, headlessHost: true)));
        // Soul Nexus is repaired at host composition so its death hook can
        // finish. If that NRE ever reaches the error channel, keep failing:
        // downgrading it here would leave PlayCardAction permanently busy.
        True(ErrorEvents.IsError(ErrorEvents.FromLogLine(
            "System.NullReferenceException: Object reference not set to an instance of an object.\n"
                + "   at MegaCrit.Sts2.Core.Models.Monsters.SoulNexus.AfterDeath(Creature _)",
            combatInProgress: false, headlessHost: true)));
    }

    public static void EngineLogsRequireTaskIdentityBeforeRetiredSuppression()
    {
        const string duplicate =
            "System.InvalidOperationException: duplicate failure";
        var correlation = new EngineLogCorrelation();
        var directCurrentLog = correlation.Register(
            duplicate, combatInProgress: false,
            headlessHost: false, thread: new ManagedThreadId(7));

        // A current Error line with the same type/message as some retired
        // task has no completing-task identity. It must time out to Publish,
        // never be consumed merely because its text happens to collide.
        True(correlation.Expire(directCurrentLog));
        Equal(EngineLogDisposition.Publish,
            directCurrentLog.Resolution.Task.GetAwaiter().GetResult());

        var retiredLog = correlation.Register(
            duplicate, combatInProgress: false,
            headlessHost: false, thread: new ManagedThreadId(7));
        var retiredTask = Task.FromException(
            new InvalidOperationException("duplicate failure"));
        True(correlation.ResolveForTask(
            retiredTask, new ManagedThreadId(7), EngineLogDisposition.Suppress));
        Equal(EngineLogDisposition.Suppress,
            retiredLog.Resolution.Task.GetAwaiter().GetResult());

        var currentTaskLog = correlation.Register(
            duplicate, combatInProgress: false,
            headlessHost: false, thread: new ManagedThreadId(7));
        var currentTask = Task.FromException(
            new InvalidOperationException("duplicate failure"));
        True(correlation.ResolveForTask(
            currentTask, new ManagedThreadId(7), EngineLogDisposition.Publish));
        Equal(EngineLogDisposition.Publish,
            currentTaskLog.Resolution.Task.GetAwaiter().GetResult());

        // The real collision order is current direct log first, then the
        // retired TaskHelper line immediately before its task completes.
        // Resolve the most recent matching same-thread line, leaving the
        // current marker to expire conservatively to Publish.
        var collidingCurrent = correlation.Register(
            duplicate + " [current marker]", false, false, new ManagedThreadId(11));
        var collidingRetired = correlation.Register(
            duplicate, false, false, new ManagedThreadId(11));
        True(correlation.ResolveForTask(
            retiredTask, new ManagedThreadId(11),
            EngineLogDisposition.Suppress));
        Equal(EngineLogDisposition.Suppress,
            collidingRetired.Resolution.Task.GetAwaiter().GetResult());
        True(correlation.Expire(collidingCurrent));
        Equal(EngineLogDisposition.Publish,
            collidingCurrent.Resolution.Task.GetAwaiter().GetResult());
    }

    public static void EventOptionTrackerRegistersEachTaskOncePerOwner()
    {
        var tracker = new EventOptionTracker();
        var run = new object();
        var synchronizer = new MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer();
        var task = new TaskCompletionSource().Task;

        True(tracker.ChangeOwner(run, synchronizer));
        True(tracker.TryTrack(task, out var generation));
        False(tracker.TryTrack(task, out _));
        Equal(1, tracker.PendingCount);
        True(tracker.Complete(task, generation));
        Equal(0, tracker.PendingCount);
    }

    public static void EventOptionTrackerRotatesPendingWorkIntoTheRetiredWindow()
    {
        var tracker = new EventOptionTracker();
        var task = new TaskCompletionSource().Task;

        tracker.ChangeOwner(
            new object(), new MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer());
        True(tracker.TryTrack(task, out var oldGeneration));
        True(tracker.ChangeOwner(
            new object(), new MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer()));

        Equal(0, tracker.PendingCount);
        True(tracker.HasRetired);
        False(tracker.Complete(task, oldGeneration));
    }

    public static void EventOptionTrackerDropInvalidatesPendingWorkWithoutRetrackingIt()
    {
        var tracker = new EventOptionTracker();
        var run = new object();
        var synchronizer = new MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer();
        var task = new TaskCompletionSource().Task;

        tracker.ChangeOwner(run, synchronizer);
        True(tracker.TryTrack(task, out _));
        tracker.Drop();

        Equal(0, tracker.PendingCount);
        True(tracker.HasRetired);
        False(tracker.TryTrack(task, out _));
    }

    public static void EventOptionTrackerExpiresZombieOnceItsWindowElapses()
    {
        // Owner rotations do not age the window out — abandon-then-new-run is
        // two of them with no elapsed time, and the parked task can still
        // fault after both. Elapsed time is what closes it.
        var clock = new FakeSettlementClock();
        var task = new TaskCompletionSource().Task;
        var tracker = RetiredTracker(clock, task);

        tracker.ChangeOwner(null, null);
        True(tracker.HasRetired);
        tracker.ChangeOwner(
            new object(), new MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer());
        True(tracker.HasRetired);

        clock.Advance(31_000);
        False(tracker.HasRetired);
    }

    public static void EventOptionTrackerExpiresZombieAcrossAReusedSynchronizer()
    {
        // RunManager can hand the next run the same EventSynchronizer. The
        // correlation window must still close — one abandoned option task
        // would otherwise route every later engine error through it for the
        // rest of the process — while the re-tracking block survives for as
        // long as that synchronizer keeps owning the run, because the
        // engine's own list can still offer the parked task back.
        var clock = new FakeSettlementClock();
        var tracker = new EventOptionTracker(clock);
        var shared = new MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer();
        var zombie = new TaskCompletionSource().Task;

        tracker.ChangeOwner(new object(), shared);
        True(tracker.TryTrack(zombie, out _));
        tracker.Drop();
        tracker.ChangeOwner(null, null);
        tracker.ChangeOwner(new object(), shared);
        clock.Advance(31_000);

        False(tracker.HasRetired);
        False(tracker.TryTrack(zombie, out _));
        Equal(0, tracker.PendingCount);
    }

    public static void EventOptionTrackerKeepsFaultSuppressionWithinTheRetiredWindow()
    {
        var tracker = new EventOptionTracker();
        var source = new TaskCompletionSource();

        tracker.ChangeOwner(
            new object(), new MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer());
        True(tracker.TryTrack(source.Task, out var generation));
        tracker.Drop();
        tracker.ChangeOwner(
            new object(), new MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer());
        source.SetException(new InvalidOperationException("retired failure"));

        False(tracker.Complete(source.Task, generation));
        True(tracker.HasRetired);
        tracker.MarkRetiredFaultLogResolved(source.Task);
        False(tracker.HasRetired);
    }

    public static void FireAndForgetTrackerPublishesWorkOwnedByTheCurrentRun()
    {
        // The baseline the suppression must not eat: a verb's own async work
        // faulting inside the run that dispatched it is exactly the fault
        // follow and the runlog exist to report.
        var tracker = new FireAndForgetTracker();
        var run = new object();
        var task = new TaskCompletionSource().Task;
        var owner = new AsyncWorkOwner();

        tracker.ChangeRun(run);
        tracker.Track(task, owner);
        Equal(1, tracker.PendingCount);

        True(tracker.Complete(task));
        Equal(0, tracker.PendingCount);
        // Its engine log lines are the live run's errors too.
        False(tracker.WrittenByRetiredWork(owner));
    }

    public static void FireAndForgetTrackerRetiresWorkWhenItsRunGoesAway()
    {
        // #145: a task parked by an abandoned run must stop being work the
        // board owes the moment its owner is gone — the ledger drops it at
        // the rotation, so a zombie cannot hold the follow probe busy for
        // every later run — and its late completion must publish nothing,
        // whether it lands at the menu or inside the next run.
        var tracker = new FireAndForgetTracker();
        var abandoned = new object();
        var parked = new TaskCompletionSource().Task;
        var owner = new AsyncWorkOwner();

        tracker.ChangeRun(abandoned);
        tracker.Track(parked, owner);
        tracker.ChangeRun(null);                 // back to the main menu

        Equal(0, tracker.PendingCount);
        False(tracker.Complete(parked));
        True(tracker.WrittenByRetiredWork(owner));

        // The same task released later, once a new run is live, stays retired.
        tracker.ChangeRun(abandoned);
        tracker.Track(parked, owner);
        tracker.ChangeRun(new object());
        Equal(0, tracker.PendingCount);
        False(tracker.Complete(parked));
        True(tracker.WrittenByRetiredWork(owner));
    }

    public static void FireAndForgetTrackerReportsWorkThatEndsItsOwnRun()
    {
        // #145's third criterion, for the one verb whose work IS the
        // rotation: `abandon` tracks the task that clears RunState while that
        // run is still active, so the retirement rule would silently
        // downgrade the teardown's own fault and withhold every engine Error
        // line it writes afterwards — `abandon --follow` would report a clean
        // settle for a teardown that failed. The exemption is spent on that
        // one rotation, so a task that never completes cannot pin the ledger.
        var tracker = new FireAndForgetTracker();
        var leaving = new object();
        var teardown = new TaskCompletionSource().Task;
        var owner = new AsyncWorkOwner();

        tracker.ChangeRun(leaving);
        tracker.Track(teardown, owner, endsRun: true);
        tracker.ChangeRun(null);                 // the teardown's own rotation

        Equal(1, tracker.PendingCount);
        True(tracker.Complete(teardown));
        False(tracker.WrittenByRetiredWork(owner));

        // Ordinary work the same run parked is still retired by that
        // rotation — the exemption is scoped to the teardown's own task.
        var parked = new TaskCompletionSource().Task;
        var parkedOwner = new AsyncWorkOwner();
        tracker.ChangeRun(leaving);
        tracker.Track(parked, parkedOwner);
        tracker.ChangeRun(null);
        Equal(0, tracker.PendingCount);
        False(tracker.Complete(parked));
        True(tracker.WrittenByRetiredWork(parkedOwner));

        // And the flag does not make the teardown immortal: released to the
        // menu it is adopted by the next run, then retired like anything else.
        var lingering = new TaskCompletionSource().Task;
        var lingeringOwner = new AsyncWorkOwner();
        tracker.ChangeRun(leaving);
        tracker.Track(lingering, lingeringOwner, endsRun: true);
        tracker.ChangeRun(null);
        tracker.ChangeRun(new object());
        Equal(1, tracker.PendingCount);
        tracker.ChangeRun(null);
        Equal(0, tracker.PendingCount);
        False(tracker.Complete(lingering));
        True(tracker.WrittenByRetiredWork(lingeringOwner));
    }

    public static void FireAndForgetTrackerSuppressesEngineLogsFromRetiredWork()
    {
        // #145: the engine catches exceptions from fire-and-forget chains and
        // only logs them, so an abandoned run's work can reach the live run's
        // error journal without ever faulting a task — and it usually
        // completes successfully afterwards, leaving nothing to correlate an
        // identity against. The owner stamped on its async flow is the answer.
        var tracker = new FireAndForgetTracker();
        var abandoned = new object();
        var retired = new AsyncWorkOwner();
        var live = new AsyncWorkOwner();

        tracker.ChangeRun(abandoned);
        tracker.Track(new TaskCompletionSource().Task, retired);
        tracker.ChangeRun(new object());
        tracker.Track(new TaskCompletionSource().Task, live);

        True(tracker.WrittenByRetiredWork(retired));
        False(tracker.WrittenByRetiredWork(live));
        // The engine's own threads and the boot path carry no stamp, and a
        // job that tracked nothing never bound one: unknown context must
        // degrade toward reporting an error, never toward hiding one.
        False(tracker.WrittenByRetiredWork(null));
        False(tracker.WrittenByRetiredWork(new AsyncWorkOwner()));
    }

    public static void FireAndForgetTrackerAdoptsMenuWorkIntoTheRunItStarts()
    {
        // `new-run` tracks the very task that mints the next RunState, so a
        // rotation is the expected outcome of that work, not evidence it was
        // orphaned: retiring it there would hide the failure of the verb that
        // started the run. It answers to that run from then on, so the next
        // rotation retires it like anything else — otherwise a menu task that
        // never completes would pin the ledger, and with it every later run's
        // follow probe, for the rest of the process.
        var tracker = new FireAndForgetTracker();
        var starting = new TaskCompletionSource().Task;
        var owner = new AsyncWorkOwner();
        var run = new object();

        tracker.Track(starting, owner);          // tracked with no run active
        tracker.ChangeRun(run);

        Equal(1, tracker.PendingCount);
        False(tracker.WrittenByRetiredWork(owner));

        tracker.ChangeRun(null);
        Equal(0, tracker.PendingCount);
        False(tracker.Complete(starting));
        True(tracker.WrittenByRetiredWork(owner));
    }

    public static void FireAndForgetTrackerReportsMenuWorkThatNeverStartsARun()
    {
        // The other half of the same trade: a `new-run` that fails before any
        // RunState exists completes with the board still at the menu, and it
        // is the only report the agent will ever get.
        var tracker = new FireAndForgetTracker();
        var starting = new TaskCompletionSource().Task;
        var owner = new AsyncWorkOwner();

        tracker.Track(starting, owner);          // tracked with no run active

        Equal(1, tracker.PendingCount);
        True(tracker.Complete(starting));
        False(tracker.WrittenByRetiredWork(owner));
    }

    public static void FireAndForgetTrackerKeepsWorkAcrossARedundantRefresh()
    {
        // RefreshRunIdentity runs on every tick and every settlement probe.
        // Re-stating the same run must not rotate anything: in-flight work
        // would otherwise be retired between two frames of one run.
        var tracker = new FireAndForgetTracker();
        var run = new object();
        var task = new TaskCompletionSource().Task;
        var owner = new AsyncWorkOwner();

        tracker.ChangeRun(run);
        tracker.Track(task, owner);
        tracker.ChangeRun(run);
        tracker.ChangeRun(run);

        Equal(1, tracker.PendingCount);
        True(tracker.Complete(task));
        False(tracker.WrittenByRetiredWork(owner));
    }

    public static void AsyncWorkOwnerStampReachesWorkTheJobStarted()
    {
        // The stamp is only useful if it survives the awaits between the pump
        // job and the log line: an engine Error written by a continuation
        // scheduled minutes later must still resolve to the job's owner. Also
        // pins the restore — the pump thread's next job must not inherit it.
        var gate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        AsyncWorkOwner? stamped = null;
        AsyncWorkOwner? seenInsideJob = null;

        var work = AsyncWorkOwner.Stamp(() =>
        {
            seenInsideJob = AsyncWorkOwner.Current;
            return Observe();
        });

        stamped = seenInsideJob;
        True(stamped is not null);
        True(AsyncWorkOwner.Current is null);    // restored for the next job

        gate.SetResult();
        Equal(stamped, work.GetAwaiter().GetResult());

        async Task<AsyncWorkOwner?> Observe()
        {
            await gate.Task.ConfigureAwait(false);
            return AsyncWorkOwner.Current;
        }
    }

    public static void RevisionJournalsStayBoundedAndQueryByRevision()
    {
        var journal = new RevisionJournal(capacity: 2);
        journal.Add(4, "first");
        journal.Add(5, "second");
        journal.Add(6, "third");

        Equal("second,third", string.Join(',', journal.TypesSince(0)));
        Equal("third", string.Join(',', journal.TypesSince(5)));
        Equal("5:second,6:third", string.Join(',',
            journal.Since(0).Select(entry =>
                $"{entry.Revision}:{entry.Type}")));
    }

    public static void DecisionClosedChestAdvertisesTheOpeningPickRelic()
    {
        // Headless closed chest: relics empty, proceed always available.
        // pick-relic is the verb that opens the chest — omitting it left
        // "proceed" as the only advertised action and a legal-verbs-only
        // agent had to walk past every treasure room.
        var headless = new SnapshotContract(Phase.Treasure)
        {
            ChestOpened = false,
            ProceedAvailable = true,
            Relics = [],
            Player = new SnapshotPlayerContract { Potions = [] },
        };

        Equal("pick-relic,proceed,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(headless, runActive: true)));

        // GUI closed chest: the proceed button hides until the chest is
        // resolved, so opening is the only advertised move.
        var gui = new SnapshotContract(Phase.Treasure)
        {
            ChestOpened = false,
            ProceedAvailable = false,
            Relics = [],
            Player = new SnapshotPlayerContract { Potions = [] },
        };

        Equal("pick-relic,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(gui, runActive: true)));
    }

    public static void DecisionOpenChestOffersPickAndSkipThenOnlyProceed()
    {
        var offering = new SnapshotContract(Phase.Treasure)
        {
            ChestOpened = true,
            ProceedAvailable = true,
            Relics = [new SnapshotItemContract { Index = 0 }],
            Player = new SnapshotPlayerContract { Potions = [] },
        };

        Equal("pick-relic,skip,proceed,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(offering, runActive: true)));

        // Resolved offer: the chest stays open and empty — pick-relic must
        // not be advertised again (the dispatcher would reject it).
        var resolved = new SnapshotContract(Phase.Treasure)
        {
            ChestOpened = true,
            ProceedAvailable = true,
            Relics = [],
            Player = new SnapshotPlayerContract { Potions = [] },
        };

        Equal("proceed,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(resolved, runActive: true)));
    }

    public static void EventProceedIsWithheldUntilThePageOffersAWayOut()
    {
        static ProceedReadiness.EventOptionGate Option(
            bool proceed = false, bool locked = false, bool chosen = false) =>
            new(proceed, locked, chosen);

        // Neow: three live boons, no leave option, event unfinished. The GUI
        // renders no way past them, so neither may the bridge.
        var required = new[] { Option(), Option(), Option() };
        False(ProceedReadiness.EventReady(finished: false, required));

        // The engine's own two exits: a finished event (NEventRoom swaps the
        // page for a synthetic PROCEED) and a page carrying its own leave.
        True(ProceedReadiness.EventReady(finished: true, required));
        True(ProceedReadiness.EventReady(
            finished: false, [Option(), Option(proceed: true)]));

        // A locked leave is not an exit, and a spent or locked choice is not
        // a required decision — proceed and option stay complementary, so a
        // page whose options are all used up is never a dead end.
        False(ProceedReadiness.EventReady(
            finished: false, [Option(), Option(proceed: true, locked: true)]));
        True(ProceedReadiness.EventReady(
            finished: false, [Option(locked: true), Option(chosen: true)]));
        True(ProceedReadiness.EventReady(finished: false, []));
    }

    public static void DecisionEventProceedFollowsTheEventPageGate()
    {
        var pending = new SnapshotContract(Phase.Event)
        {
            Options =
            [
                new SnapshotItemContract { Index = 0 },
                new SnapshotItemContract { Index = 1 },
            ],
            ProceedAvailable = false,
            Player = new SnapshotPlayerContract { Potions = [] },
        };

        Equal("option,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(pending, runActive: true)));

        var resolved = new SnapshotContract(Phase.Event)
        {
            Options = [],
            ProceedAvailable = true,
            Player = new SnapshotPlayerContract { Potions = [] },
        };

        Equal("proceed,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(resolved, runActive: true)));
    }

    public static void RestSiteProceedWaitsForTheSeatToSpendItsChoice()
    {
        False(ProceedReadiness.RestSiteReady(
            optionCount: 2, optionSpent: false));
        True(ProceedReadiness.RestSiteReady(
            optionCount: 0, optionSpent: false));
        // A hook can leave the untaken options standing after one is taken;
        // the GUI enables its proceed button all the same.
        True(ProceedReadiness.RestSiteReady(
            optionCount: 1, optionSpent: true));

        var unchosen = new SnapshotContract(Phase.RestSite)
        {
            Options =
            [
                new SnapshotItemContract { Index = 0, Enabled = true },
                new SnapshotItemContract { Index = 1, Enabled = true },
            ],
            ProceedAvailable = false,
            Player = new SnapshotPlayerContract { Potions = [] },
        };

        Equal("option,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(unchosen, runActive: true)));
    }

    public static void RestSiteSeatOnlyCountsSelectionsThatConsumedSomething()
    {
        var room = new object();
        False(RestSiteSeat.HasSpentItsChoice(room));

        // SMITH, then cancelling its card grid: the synchronizer still
        // stamps a chosen index, but its own success flag says false and
        // nothing left the board — the seat still owes a decision.
        RestSiteSeat.RecordWhenSucceeded(Task.FromResult(false), room);
        False(RestSiteSeat.HasSpentItsChoice(room));

        // A selection that threw did not spend the rest either.
        var faulted = Task.FromException<bool>(
            new InvalidOperationException("boom"));
        RestSiteSeat.RecordWhenSucceeded(faulted, room);
        False(RestSiteSeat.HasSpentItsChoice(room));
        _ = faulted.Exception;  // observed for real by the dispatcher's Track

        RestSiteSeat.RecordWhenSucceeded(Task.FromResult(true), room);
        True(RestSiteSeat.HasSpentItsChoice(room));

        // Keyed to the room it happened in, so the next rest site starts
        // unspent without a room-entry hook.
        False(RestSiteSeat.HasSpentItsChoice(new object()));
        False(RestSiteSeat.HasSpentItsChoice(null));
    }

    public static void MerchantSeatMarksTheRemovalUsedOnlyWhenOneWasRemoved()
    {
        // `buy card_removal` opens a card picker; the entry is sold out only
        // once the picker resolved a removal. The headless seat owes this
        // marking because no NRun node is there to do it (#166).
        var marked = 0;

        // Cancelling the picker removes nothing and spends nothing, so the
        // stall is still stocked and the next `buy` must still be legal.
        MerchantSeat.MarkUsedWhenPurchased(Task.FromResult(false), () => marked++);
        Equal(0, marked);

        // A purchase that threw did not remove a card either.
        var faulted = Task.FromException<bool>(
            new InvalidOperationException("boom"));
        MerchantSeat.MarkUsedWhenPurchased(faulted, () => marked++);
        Equal(0, marked);
        _ = faulted.Exception;  // observed for real by the dispatcher's Track

        MerchantSeat.MarkUsedWhenPurchased(Task.FromResult(true), () => marked++);
        Equal(1, marked);

        // The continuation is synchronous: the marking has already landed by
        // the time the verb settles, so the very next obs reads `used: true`
        // and cannot advertise a second buy the dispatcher would reject.
        var pending = new TaskCompletionSource<bool>();
        MerchantSeat.MarkUsedWhenPurchased(pending.Task, () => marked++);
        Equal(1, marked);
        pending.SetResult(true);
        Equal(2, marked);
    }

    public static void DecisionUnavailableTransitionsDoNotAdvertiseActions()
    {
        foreach (var phase in new[] { Phase.Event, Phase.Rewards })
        {
            var snapshot = new SnapshotContract(phase)
            {
                Available = false,
                Options = [new SnapshotItemContract { Index = 0 }],
                Rewards = [new SnapshotItemContract { Index = 0 }],
            };

            var legal = DecisionProjection.LegalVerbs(snapshot, runActive: false);

            Equal("", string.Join(',', legal));
        }
    }

    public static void DecisionPotionDiscardReadsTheBeltNotMerchantStock()
    {
        // A shop's top-level `potions` is what the merchant sells; the belt
        // lives in the footer. Reading stock as a belt both hid a
        // discardable potion behind a sold-out shelf and, with stock on the
        // shelf and nothing in hand, advertised a discard of someone else's
        // potion. The dispatcher only ever discards from the belt.
        var belt = new SnapshotItemContract { Index = 0, Slot = 0 };
        var emptyShelf = new SnapshotContract(Phase.Shop)
        {
            Potions = [],
            Player = new SnapshotPlayerContract { Potions = [belt] },
        };
        var stockedShelf = new SnapshotContract(Phase.Shop)
        {
            Potions = [new SnapshotItemContract { Index = 0 }],
            Player = new SnapshotPlayerContract { Potions = [] },
        };
        var map = new SnapshotContract(Phase.Map)
        {
            Player = new SnapshotPlayerContract { Potions = [belt] },
        };
        // Combat publishes the belt at the top level and has no footer.
        var combat = new SnapshotContract(Phase.Combat)
        {
            Side = "player",
            ActionsDisabled = false,
            Hand = [],
            Potions = [belt],
        };

        Equal("leave,potion-discard,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(emptyShelf, runActive: true)));
        Equal("leave,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(stockedShelf, runActive: true)));
        Equal("potion-discard,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(map, runActive: true)));
        Equal("end-turn,potion-use,potion-discard,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(combat, runActive: true)));
    }

    public static void DecisionShopPotionUseFollowsTheRedeemableBeltEntry()
    {
        // Only a Foul Potion has a merchant interaction, so Snapshotter
        // marks exactly that belt entry playable. An ordinary potion in the
        // belt must not advertise a use the shop would reject.
        var ordinary = new SnapshotContract(Phase.Shop)
        {
            Potions = [],
            Player = new SnapshotPlayerContract
            {
                Potions =
                [
                    new SnapshotItemContract
                    {
                        Index = 0, Slot = 0,
                        Model = "ENERGY_POTION", Playable = false,
                    },
                ],
            },
        };
        var foul = new SnapshotContract(Phase.Shop)
        {
            Potions = [],
            Player = new SnapshotPlayerContract
            {
                Potions =
                [
                    new SnapshotItemContract
                    {
                        Index = 0, Slot = 0,
                        Model = "ENERGY_POTION", Playable = false,
                    },
                    new SnapshotItemContract
                    {
                        Index = 1, Slot = 1,
                        Model = "FOUL_POTION", Playable = true,
                    },
                ],
            },
        };

        Equal("leave,potion-discard,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(ordinary, runActive: true)));
        Equal("potion-use,leave,potion-discard,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(foul, runActive: true)));
    }

    public static void DecisionFakeMerchantAdvertisesBuyWhileAnEntryIsAffordable()
    {
        // The Fake Merchant sells relics from inside an event. Its stall
        // publishes the ordinary purchasable stock shape, so buy is
        // advertised exactly while the dispatcher would accept one.
        SnapshotContract Stall(params bool?[] purchasable) =>
            new(Phase.Event)
            {
                Options = [],
                // A stall page offering no unspent option is a page the
                // event itself lets the seat leave, so the readiness gate
                // (#146) publishes proceed here.
                ProceedAvailable = true,
                Relics = purchasable
                    .Select((flag, i) => new SnapshotItemContract
                    {
                        Index = i,
                        Model = "FAKE_ANCHOR",
                        Purchasable = flag,
                    }).ToArray(),
                Player = new SnapshotPlayerContract { Potions = [] },
            };

        Equal("buy,proceed,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(Stall(false, true), runActive: true)));
        // Sold out or short of gold: proceed (or the fight) is all that's left.
        Equal("proceed,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(Stall(false, false), runActive: true)));
        // An ordinary event stocks nothing and never advertises buy.
        Equal("proceed,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(Stall(), runActive: true)));

        // The two event gates compose: a stall page that still owes the
        // seat a decision sells its stock but withholds the way out.
        var owing = Stall(true);
        owing.Options = [new SnapshotItemContract { Index = 0 }];
        owing.ProceedAvailable = false;
        Equal("option,buy,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(owing, runActive: true)));
    }

    public static void MerchantBuyRejectsEveryIndexButThePublishedRemoval()
    {
        // A stall sells one card removal, so `buy card_removal` has exactly
        // one target — the idx obs.cardRemoval publishes. Indexed kinds are
        // bounded by the inventory instead, which only the dispatcher can
        // read, so the rule leaves their in-range indices alone.
        Equal(0, MerchantRules.CardRemovalIndex);
        Equal(null, MerchantRules.BuyIndexRejection(
            "card_removal", MerchantRules.CardRemovalIndex));

        foreach (var idx in new[] { 1, 2, 7 })
        {
            var removal = MerchantRules.BuyIndexRejection("card_removal", idx);
            Equal(RejectionCodes.BadIndex, removal?.Code);
            Equal($"card_removal has one entry, at idx 0; got {idx}",
                removal?.Message);
        }

        foreach (var kind in new[] { "card", "colorless", "relic", "potion" })
        {
            Equal(null, MerchantRules.BuyIndexRejection(kind, 0));
            Equal(null, MerchantRules.BuyIndexRejection(kind, 7));
            var negative = MerchantRules.BuyIndexRejection(kind, -1);
            Equal(RejectionCodes.BadIndex, negative?.Code);
            Equal($"{kind} idx -1 must be non-negative", negative?.Message);
        }
        Equal(RejectionCodes.BadIndex,
            MerchantRules.BuyIndexRejection("card_removal", -1)?.Code);
    }

    public static void MerchantFoulPotionRedemptionNeedsEveryGate()
    {
        True(MerchantRules.RedeemableAtMerchant(
            isFoulPotion: true, usableAnyTime: true, ownerAlive: true,
            canUseOrRemovePotions: true, interactionAvailable: () => true));
        False(MerchantRules.RedeemableAtMerchant(
            isFoulPotion: false, usableAnyTime: true, ownerAlive: true,
            canUseOrRemovePotions: true, interactionAvailable: () => true));
        False(MerchantRules.RedeemableAtMerchant(
            isFoulPotion: true, usableAnyTime: false, ownerAlive: true,
            canUseOrRemovePotions: true, interactionAvailable: () => true));
        False(MerchantRules.RedeemableAtMerchant(
            isFoulPotion: true, usableAnyTime: true, ownerAlive: false,
            canUseOrRemovePotions: true, interactionAvailable: () => true));
        False(MerchantRules.RedeemableAtMerchant(
            isFoulPotion: true, usableAnyTime: true, ownerAlive: true,
            canUseOrRemovePotions: false, interactionAvailable: () => true));
        False(MerchantRules.RedeemableAtMerchant(
            isFoulPotion: true, usableAnyTime: true, ownerAlive: true,
            canUseOrRemovePotions: true, interactionAvailable: () => false));

        // The last gate walks the run's current room in the live game, so a
        // belt full of ordinary potions must never reach it.
        var asked = 0;
        False(MerchantRules.RedeemableAtMerchant(
            isFoulPotion: false, usableAnyTime: true, ownerAlive: true,
            canUseOrRemovePotions: true,
            interactionAvailable: () => { asked++; return true; }));
        Equal(0, asked);
    }

    public static void SnapshotContractPreservesUnconsumedProducerFields()
    {
        var card = new SnapshotItemContract
        {
            Index = 0,
            Model = "STRIKE_R",
            Playable = true,
        };
        var player = new SnapshotPlayerContract { Hp = [40, 80], Potions = [] };
        var snapshot = new SnapshotContract(Phase.Combat)
        {
            Side = "player",
            ActionsDisabled = false,
            Hand = [card],
            Player = player,
        };

        snapshot.AddExtensions(new
        {
            phaseSpecific = new { value = 7, missing = (string?)null },
        });

        var wire = snapshot.ToJsonObject();
        Equal(Phase.Combat, snapshot.Phase);
        Equal("player", snapshot.Side);
        True(snapshot.Hand.Single().Playable == true);
        Equal("STRIKE_R", wire["hand"]![0]!["model"]!.GetValue<string>());
        Equal(7, wire["phaseSpecific"]!["value"]!.GetValue<int>());
        True(wire["phaseSpecific"]!["missing"] is null);
    }

    public static void AgentWireOmitsExpandedSemanticStateUnlessDiagnosing()
    {
        SnapshotContract Build(int pileCopies)
        {
            var snapshot = new SnapshotContract(Phase.Combat)
            {
                SemanticState = Enumerable.Range(0, pileCopies)
                    .Select(index => $"pile:draw:STRIKE_IRONCLAD:{index}")
                    .ToArray(),
                Hand =
                [
                    new SnapshotItemContract
                    {
                        Model = "STRIKE_IRONCLAD",
                        SemanticState = ["card:STRIKE_IRONCLAD:1"],
                    },
                ],
                Legal = ["play", "end-turn"],
            };
            return snapshot;
        }

        var small = Build(1);
        var large = Build(40);
        var smallWire = small.ToAgentJsonObject();
        var largeWire = large.ToAgentJsonObject();
        var diagnostic = large.ToAgentJsonObject(includeSemanticState: true);

        False(smallWire.ContainsKey("semanticState"));
        False(largeWire.ContainsKey("semanticState"));
        False(largeWire["hand"]![0]!.AsObject().ContainsKey("semanticState"));
        True(diagnostic["semanticState"]!.AsArray().Count == 40);
        True(diagnostic["hand"]![0]!["semanticState"] is JsonArray);
        True(largeWire.ToJsonString().Length
            <= smallWire.ToJsonString().Length + 8);
        False(small.ConsumerFingerprint() == large.ConsumerFingerprint());
    }

    public static void SnapshotContractOwnsFollowAndAttributionMetadata()
    {
        var snapshot = new SnapshotContract(Phase.Event)
        {
            Revision = 42,
            RunId = "run-7",
            Legal = ["option", "proceed"],
        };

        Equal(42L, snapshot.Revision);
        Equal("run-7", snapshot.RunId);
        Equal("option,proceed", string.Join(',', snapshot.Legal));
        var wire = snapshot.ToJsonObject();
        Equal("event", wire["phase"]!.GetValue<string>());
        Equal(42L, wire["rev"]!.GetValue<long>());
        Equal("run-7", wire["runId"]!.GetValue<string>());
    }

    public static void SnapshotConsumerFingerprintTracksCardIdentityNotExtensions()
    {
        SnapshotContract Build(string model, string decorativeFrame)
        {
            var card = new SnapshotItemContract
            {
                Index = 0,
                Model = model,
                Playable = true,
            };
            var snapshot = new SnapshotContract(Phase.Combat)
            {
                Revision = 7,
                RunId = "source",
                Side = "player",
                Hand = [card],
                Legal = ["play", "end-turn"],
            };
            snapshot.AddExtensions(new { decorativeFrame });
            return snapshot;
        }

        var original = Build("STRIKE_R", "ornate");
        var extensionChanged = Build("STRIKE_R", "plain");
        var cardChanged = Build("BASH", "ornate");

        Equal(original.ConsumerStateKey(), extensionChanged.ConsumerStateKey());
        False(original.ConsumerFingerprint() == cardChanged.ConsumerFingerprint());
        False(original.ConsumerProjection().ContainsKey("decorativeFrame"));
        Equal("STRIKE_R", original.ConsumerProjection()["hand"]![0]!["model"]!.GetValue<string>());
    }

    public static void SnapshotConsumerFingerprintTracksMapTargetCoordinates()
    {
        SnapshotContract Build(int col, int row, string type) => new(Phase.Map)
        {
            Next =
            [
                new SnapshotItemContract
                {
                    Index = 0,
                    Col = col,
                    Row = row,
                    Type = type,
                },
            ],
            Legal = ["map-move", "abandon"],
        };

        var original = Build(2, 3, "monster");

        False(original.ConsumerFingerprint() == Build(3, 3, "monster").ConsumerFingerprint());
        False(original.ConsumerFingerprint() == Build(2, 4, "monster").ConsumerFingerprint());
        False(original.ConsumerFingerprint() == Build(2, 3, "elite").ConsumerFingerprint());
    }

    public static void SnapshotConsumerFingerprintTracksEnemyIdentityAndHp()
    {
        SnapshotContract Build(uint id, string model, int hp) => new(Phase.Combat)
        {
            Enemies =
            [
                new SnapshotEnemyContract
                {
                    Id = id,
                    Model = model,
                    Hp = [hp, 40],
                    Alive = true,
                },
            ],
            Legal = ["play", "end-turn"],
        };

        var original = Build(7, "CULTIST", 30);

        False(original.ConsumerFingerprint() == Build(8, "CULTIST", 30).ConsumerFingerprint());
        False(original.ConsumerFingerprint() == Build(7, "LOUSE", 30).ConsumerFingerprint());
        False(original.ConsumerFingerprint() == Build(7, "CULTIST", 29).ConsumerFingerprint());
    }

    public static void SnapshotConsumerFingerprintTracksPlayerHpAndGold()
    {
        SnapshotContract Build(int hp, int gold)
        {
            var player = new SnapshotPlayerContract
            {
                Hp = [hp, 80],
                Gold = gold,
                Potions = [],
            };
            player.AddExtensions(new { description = $"{hp}/{gold}" });
            return new SnapshotContract(Phase.Map)
            {
                Player = player,
                Legal = ["map-move", "abandon"],
            };
        }

        var original = Build(60, 100);

        False(original.ConsumerFingerprint() == Build(59, 100).ConsumerFingerprint());
        False(original.ConsumerFingerprint() == Build(60, 101).ConsumerFingerprint());
    }

    public static void SnapshotConsumerFingerprintTracksCombatResources()
    {
        SnapshotContract Build(int hp, int block, int energy, int stars) =>
            new(Phase.Combat)
            {
                You = new SnapshotCombatantContract
                {
                    Hp = [hp, 80],
                    Block = block,
                    Energy = [energy, 3],
                    Stars = stars,
                },
                Legal = ["play", "end-turn"],
            };

        var original = Build(60, 5, 2, 1);

        False(original.ConsumerFingerprint() == Build(59, 5, 2, 1).ConsumerFingerprint());
        False(original.ConsumerFingerprint() == Build(60, 6, 2, 1).ConsumerFingerprint());
        False(original.ConsumerFingerprint() == Build(60, 5, 1, 1).ConsumerFingerprint());
        False(original.ConsumerFingerprint() == Build(60, 5, 2, 2).ConsumerFingerprint());
    }

    public static void SnapshotConsumerFingerprintTracksCurrentMapPosition()
    {
        SnapshotContract Build(int act, int col, int row) => new(Phase.Map)
        {
            Act = act,
            Current = [col, row],
            Legal = ["map-move", "abandon"],
        };

        var original = Build(0, 2, 3);

        False(original.ConsumerFingerprint() == Build(1, 2, 3).ConsumerFingerprint());
        False(original.ConsumerFingerprint() == Build(0, 3, 3).ConsumerFingerprint());
        False(original.ConsumerFingerprint() == Build(0, 2, 4).ConsumerFingerprint());
    }

    public static void SnapshotConsumerFingerprintTracksTopLevelShopGold()
    {
        SnapshotContract Build(int gold) => new(Phase.Shop)
        {
            Gold = gold,
            Legal = ["buy", "leave", "abandon"],
        };

        False(Build(100).ConsumerFingerprint() == Build(99).ConsumerFingerprint());
    }

    public static void SnapshotConsumerFingerprintTracksRelicIdentity()
    {
        SnapshotContract Build(string model) => new(Phase.RelicReward)
        {
            Relics = [new SnapshotItemContract { Index = 0, Model = model }],
            Legal = ["pick-relic", "skip", "abandon"],
        };

        False(Build("VAJRA").ConsumerFingerprint()
            == Build("ANCHOR").ConsumerFingerprint());
    }

    public static void SnapshotConsumerFingerprintHasCrossLanguageFixture()
    {
        var snapshot = new SnapshotContract(Phase.Combat)
        {
            Act = 1,
            Current = [2, 3],
            Gold = 100,
            SemanticState = ["pile:draw:STRIKE_R"],
            Selected = ["STRIKE_R"],
            Side = "player",
            Next =
            [
                new SnapshotItemContract
                {
                    Index = 0, Id = "PATH_A", Col = 3, Row = 4,
                    Type = "monster",
                },
            ],
            Hand =
            [
                new SnapshotItemContract
                {
                    Index = 0, Model = "STRIKE_R", Playable = true,
                    Selected = false, SemanticState = ["cost:1"],
                },
            ],
            Relics = [new SnapshotItemContract { Index = 0, Model = "VAJRA" }],
            You = new SnapshotCombatantContract
            {
                Hp = [60, 80], Block = 5, Energy = [2, 3], Stars = 1,
                SemanticState = ["power:STRENGTH:1"],
            },
            Enemies =
            [
                new SnapshotEnemyContract
                {
                    Id = 7, Model = "CULTIST", Hp = [30, 40], Block = 0,
                    Alive = true, SemanticState = ["intent:attack:6:1"],
                },
            ],
            Player = new SnapshotPlayerContract
            {
                Hp = [60, 80], Gold = 100, Potions = [],
                SemanticState = ["deck:STRIKE_R"],
            },
            Legal = ["play", "end-turn"],
        };

        Equal("d4c312db8769179e", snapshot.ConsumerFingerprint());
    }

    public static void SnapshotConsumerFingerprintTracksActionTargetGrammar()
    {
        SnapshotContract Build(
            int turn, string eventId, string selector, int slot, string target) =>
            new(Phase.Combat)
            {
                Turn = turn,
                Id = eventId,
                Hand =
                [
                    new SnapshotItemContract
                    {
                        Index = 0,
                        Model = "BASH",
                        Selector = selector,
                        Slot = slot,
                        Target = target,
                    },
                ],
                Legal = ["play", "end-turn"],
            };

        var original = Build(2, "BIG_FISH", "BASH+", 1, "anyenemy");

        False(original.ConsumerFingerprint()
            == Build(3, "BIG_FISH", "BASH+", 1, "anyenemy").ConsumerFingerprint());
        False(original.ConsumerFingerprint()
            == Build(2, "SCRAP_OOZE", "BASH+", 1, "anyenemy").ConsumerFingerprint());
        False(original.ConsumerFingerprint()
            == Build(2, "BIG_FISH", "BASH", 1, "anyenemy").ConsumerFingerprint());
        False(original.ConsumerFingerprint()
            == Build(2, "BIG_FISH", "BASH+", 2, "anyenemy").ConsumerFingerprint());
        False(original.ConsumerFingerprint()
            == Build(2, "BIG_FISH", "BASH+", 1, "self").ConsumerFingerprint());
    }

    public static void SnapshotConsumerFingerprintTracksGameOverOutcomeAndHp()
    {
        SnapshotContract Build(string outcome, int hp) => new(Phase.GameOver)
        {
            Outcome = outcome,
            Hp = [hp, 80],
            Gold = 100,
        };

        var original = Build("victory", 20);

        False(original.ConsumerFingerprint() == Build("defeat", 20).ConsumerFingerprint());
        False(original.ConsumerFingerprint() == Build("victory", 0).ConsumerFingerprint());
        Equal("c02643081d2c619c", original.ConsumerFingerprint());
    }

    public static void SnapshotConsumerFingerprintTracksTypedSemanticState()
    {
        SnapshotContract Build(string top, string item, string player,
            string you, string enemy, bool selected) => new(Phase.Combat)
        {
            SemanticState = [top],
            Selected = ["BASH+"],
            Hand =
            [
                new SnapshotItemContract
                {
                    Index = 0,
                    Model = "BASH",
                    Selector = "BASH+",
                    Selected = selected,
                    SemanticState = [item],
                },
            ],
            Player = new SnapshotPlayerContract
            {
                Hp = [60, 80],
                Gold = 100,
                Potions = [],
                SemanticState = [player],
            },
            You = new SnapshotCombatantContract
            {
                Hp = [60, 80],
                Energy = [2, 3],
                SemanticState = [you],
            },
            Enemies =
            [
                new SnapshotEnemyContract
                {
                    Id = 7,
                    Model = "CULTIST",
                    Hp = [30, 40],
                    SemanticState = [enemy],
                },
            ],
            Legal = ["play", "end-turn"],
        };

        var original = Build("pile:draw:BASH+", "cost:2", "deck:BASH+",
            "power:STRENGTH:1", "intent:attack:6:1", selected: false);

        False(original.ConsumerFingerprint() == Build("pile:draw:STRIKE_R",
            "cost:2", "deck:BASH+", "power:STRENGTH:1",
            "intent:attack:6:1", selected: false).ConsumerFingerprint());
        False(original.ConsumerFingerprint() == Build("pile:draw:BASH+",
            "cost:1", "deck:BASH+", "power:STRENGTH:1",
            "intent:attack:6:1", selected: false).ConsumerFingerprint());
        False(original.ConsumerFingerprint() == Build("pile:draw:BASH+",
            "cost:2", "deck:STRIKE_R", "power:STRENGTH:1",
            "intent:attack:6:1", selected: false).ConsumerFingerprint());
        False(original.ConsumerFingerprint() == Build("pile:draw:BASH+",
            "cost:2", "deck:BASH+", "power:WEAK:1",
            "intent:attack:6:1", selected: false).ConsumerFingerprint());
        False(original.ConsumerFingerprint() == Build("pile:draw:BASH+",
            "cost:2", "deck:BASH+", "power:STRENGTH:1",
            "intent:attack:12:1", selected: false).ConsumerFingerprint());
        False(original.ConsumerFingerprint() == Build("pile:draw:BASH+",
            "cost:2", "deck:BASH+", "power:STRENGTH:1",
            "intent:attack:6:1", selected: true).ConsumerFingerprint());
        False(original.ConsumerStateKey() == Build("pile:draw:STRIKE_R",
            "cost:2", "deck:BASH+", "power:STRENGTH:1",
            "intent:attack:6:1", selected: false).ConsumerStateKey());
    }

    public static void SnapshotPlayerHpPreservesNullWireShape()
    {
        var player = new SnapshotPlayerContract
        {
            Hp = null,
            Gold = 100,
            Potions = [],
        };

        var wire = player.ToJsonObject();

        True(wire.ContainsKey("hp"));
        True(wire["hp"] is null);
    }

    public static void SnapshotConsumerFingerprintIgnoresPresentationAtEveryTypedLayer()
    {
        SnapshotContract Build(string presentation)
        {
            var item = new SnapshotItemContract
            {
                Model = "BASH",
                SemanticState = ["cost:2"],
            };
            item.AddExtensions(new { title = presentation });
            var player = new SnapshotPlayerContract
            {
                Hp = [60, 80],
                Potions = [],
                SemanticState = ["deck:BASH"],
            };
            player.AddExtensions(new { deckDescription = presentation });
            var you = new SnapshotCombatantContract
            {
                Hp = [60, 80],
                Energy = [2, 3],
                SemanticState = ["power:STRENGTH:1"],
            };
            you.AddExtensions(new { powerDescription = presentation });
            var enemy = new SnapshotEnemyContract
            {
                Id = 7,
                Model = "CULTIST",
                Hp = [30, 40],
                SemanticState = ["intent:attack:6:1"],
            };
            enemy.AddExtensions(new { title = presentation });
            var snapshot = new SnapshotContract(Phase.Combat)
            {
                Hand = [item],
                Player = player,
                You = you,
                Enemies = [enemy],
                SemanticState = ["pile:draw:BASH"],
            };
            snapshot.AddExtensions(new { decorativeFrame = presentation });
            return snapshot;
        }

        Equal(Build("ornate").ConsumerFingerprint(),
            Build("plain").ConsumerFingerprint());
    }

    public static void OmittedObservationParametersKeepTheirDefaults()
    {
        foreach (var empty in new[] { "", "?" })
        {
            var query = ParsedQuery(empty);

            Equal(ObservationQuery.NoSince, query.Since);
            Equal(0, query.Wait);
            False(query.Compact);
            False(query.Decision);
            False(query.SemanticState);
            False(query.WantsChangeFeed);
            False(query.ShouldPark);
        }

        // Parameters this rule does not own — `known` above all, which the
        // server reads itself because it is repeatable — pass through
        // untouched instead of counting as malformed.
        False(ParsedQuery("?known=STRIKE_IRONCLAD%2B0&known=BASH%2B1").Compact);
    }

    public static void ValidObservationLongPollParametersSurviveParsing()
    {
        var query = ParsedQuery("?since=0&wait=1500");

        Equal(0L, query.Since);
        Equal(1500, query.Wait);
        True(query.WantsChangeFeed);
        True(query.ShouldPark);

        // since without wait still reports the change feed, it just does
        // not park; wait=0 is the same request spelled out.
        True(ParsedQuery("?since=42").WantsChangeFeed);
        False(ParsedQuery("?since=42").ShouldPark);
        False(ParsedQuery("?since=42&wait=0").ShouldPark);
        Equal(ObservationQuery.MaxWaitMs, ParsedQuery("?since=1&wait=60000").Wait);
        // The leading '?' is the server's to keep or drop.
        Equal(7L, ParsedQuery("since=7").Since);
    }

    public static void MalformedObservationSinceIsRejected()
    {
        foreach (var since in new[]
                 {
                     "", "abc", "-1", " 1", "%201", "1.0", "1e3", "0x1",
                     "9223372036854775808",
                 })
            Equal("'since' must be a non-negative integer",
                RejectedQuery($"?since={since}"));
        // Parameter names match the way the server's own parser matches
        // them, so a shouted one is rejected under its canonical spelling.
        Equal("'since' must be a non-negative integer", RejectedQuery("?SINCE=abc"));
    }

    public static void MalformedObservationWaitIsRejected()
    {
        // Out of range counts as malformed: silently clamping a 10-minute
        // wait to a minute is the same lie as silently defaulting it.
        foreach (var wait in new[] { "", "soon", "-1", "1500ms", "60001" })
            Equal("'wait' must be an integer in [0,60000]",
                RejectedQuery($"?since=0&wait={wait}"));
    }

    public static void ObservationFlagsAcceptOnlyTheDocumentedEncodings()
    {
        foreach (var yes in new[] { "1", "true", "TRUE" })
        {
            True(ParsedQuery($"?compact={yes}").Compact);
            True(ParsedQuery($"?decision={yes}").Decision);
            True(ParsedQuery($"?semanticState={yes}").SemanticState);
        }
        foreach (var no in new[] { "0", "false", "False" })
        {
            False(ParsedQuery($"?compact={no}").Compact);
            False(ParsedQuery($"?decision={no}").Decision);
            False(ParsedQuery($"?semanticState={no}").SemanticState);
        }
        foreach (var flag in new[] { "compact", "decision", "semanticState" })
            foreach (var bad in new[] { "", "yes", "2", "on", "compact" })
                Equal($"'{flag}' must be one of 1|true|0|false",
                    RejectedQuery($"?{flag}={bad}"));
    }

    public static void ValuelessObservationParametersAreRejected()
    {
        // `?compact` with no '=' at all is supplied, not omitted — and it
        // is the form the server's own parsed query collection cannot
        // report: .NET files a valueless segment under the null key, so
        // the indexer answers null exactly as it does for an omitted one.
        Equal("'since' must be a non-negative integer", RejectedQuery("?since"));
        Equal("'wait' must be an integer in [0,60000]", RejectedQuery("?wait"));
        foreach (var flag in new[] { "compact", "decision", "semanticState" })
            Equal($"'{flag}' must be one of 1|true|0|false",
                RejectedQuery($"?{flag}"));
        // Including mid-query, where it is easiest to overlook.
        Equal("'decision' must be one of 1|true|0|false",
            RejectedQuery("?since=0&decision&wait=10"));
    }

    public static void RepeatedObservationParametersAreRejected()
    {
        // Two spellings of one parameter: picking a winner silently is the
        // same lie as defaulting a malformed one, whichever end wins.
        Equal("'compact' was supplied more than once",
            RejectedQuery("?compact=1&compact=0"));
        Equal("'since' was supplied more than once",
            RejectedQuery("?since=1&SINCE=2"));
        Equal("'wait' was supplied more than once",
            RejectedQuery("?since=0&wait=10&wait=10"));

        // `known` is the one parameter that is meant to repeat, and this
        // rule does not own it.
        True(ParsedQuery("?known=A&known=B&compact=1").Compact);
    }

    private static ObservationQuery ParsedQuery(string rawQuery)
    {
        True(ObservationQuery.TryParse(rawQuery, out var query, out var error));
        Equal(null, error);
        return query;
    }

    private static string RejectedQuery(string rawQuery)
    {
        False(ObservationQuery.TryParse(rawQuery, out var query, out var error));
        Equal(default(ObservationQuery), query);
        return error ?? throw new InvalidOperationException(
            "a rejected query carried no message");
    }

    // The headless GodotSharp stub is loaded in place of the real one, so the
    // game assemblies bind to these signatures. A shape that is absent throws
    // MissingMethodException as soon as a game method referencing it is
    // jitted — even along a branch that is never taken — which is how the
    // missing copy-plus-alpha ctor killed potion use (LIQUID_MEMORIES and
    // GIGANTIFICATION_POTION both splash `new Color(Colors.Blue/Red)`).
    public static void ColorStubCarriesEveryGodotConstructorShape()
    {
        var present = typeof(Color)
            .GetConstructors()
            .Select(c => string.Join(",", c.GetParameters().Select(p => p.ParameterType.Name)))
            .ToHashSet();
        // CI has no game dlls, so this list is a hand-kept copy of the real
        // engine's set. Refresh it whenever the game's Godot version moves:
        //   ilspycmd -t Godot.Color lib/GodotSharp.dll
        string[] required =
        [
            "Single,Single,Single,Single",
            "Color,Single",
            "UInt32",
            "UInt64",
            "String",
            "String,Single",
        ];

        var missing = required.Where(shape => !present.Contains(shape)).ToArray();
        Equal("none", missing.Length == 0 ? "none" : string.Join(" ", missing));
    }

    // Named colours are the same ABI surface as the constructors — an absent
    // one faults the same way, and Colors.DarkRed is read by a method on
    // RelicModel, a Model class headless does construct. This list is every
    // accessor sts2.dll binds against; refresh it by scanning the game
    // assembly's MemberRef table for the Godot.Colors parent type.
    public static void ColorsStubCarriesEveryNamedColorTheGameBinds()
    {
        var present = typeof(Godot.Colors)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Select(p => p.Name)
            .ToHashSet();
        string[] required =
        [
            "Black", "Blue", "Cyan", "DarkGray", "DarkRed", "DimGray", "Gold",
            "Gray", "Green", "Magenta", "Purple", "Red", "Transparent", "White",
        ];

        var missing = required.Where(name => !present.Contains(name)).ToArray();
        Equal("none", missing.Length == 0 ? "none" : string.Join(" ", missing));
    }

    // Deleting the copy ctor from the stub breaks this method's *compilation*
    // rather than tripping the shape assertion above — if CS1503/CS7036 lands
    // here, the stub lost `Color(Color, float)`.
    public static void ColorCopyConstructorKeepsRgbAndReplacesAlpha()
    {
        var source = new Color(0.25f, 0.5f, 0.75f, 0.125f);

        var opaque = new Color(source);
        var faded = new Color(source, 0.4f);

        Equal(new Color(0.25f, 0.5f, 0.75f, 1f), opaque);
        Equal(new Color(0.25f, 0.5f, 0.75f, 0.4f), faded);
        // The source is a value — copying must not disturb it.
        Equal(0.125f, source.A);
    }

    public static void ColorPackedConstructorsUnpackChannelsHighToLow()
    {
        Equal(new Color(1f, 0f, 0f, 1f), new Color(0xFF0000FFu));
        Equal(new Color(0f, 0f, 1f, 0f), new Color(0x0000FF00u));
        Equal(new Color(1f, 0f, 0f, 1f), new Color(0xFFFF_0000_0000_FFFFul));
    }

    // One option task tracked under an owner, then retired by Drop — the
    // state every retired-correlation test starts from.
    private static EventOptionTracker RetiredTracker(
        FakeSettlementClock clock, Task parked)
    {
        var tracker = new EventOptionTracker(clock);
        tracker.ChangeOwner(
            new object(),
            new MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer());
        True(tracker.TryTrack(parked, out _));
        tracker.Drop();
        return tracker;
    }

    // Defaults describe the common case: a verb dispatched inside a run, whose
    // board this window has therefore already seen.
    private static bool OwnerChange(
        RunOwnership ownership,
        string acceptedRunId,
        string observedRunId,
        Phase observedPhase = Phase.Map,
        bool runSeenInPlay = true) =>
        RunOwnershipRules.IsOwnerChange(
            ownership, acceptedRunId, observedRunId, observedPhase,
            runSeenInPlay);

    private static SettlementRequest Request(
        Phase phaseBefore = Phase.Map,
        long startedRevision = 3,
        long acceptedRevision = 4,
        long acceptedTick = 0,
        int timeoutMs = 100,
        string acceptedRunId = "run",
        RunOwnership ownership = RunOwnership.Bound) => new(
            phaseBefore,
            startedRevision,
            acceptedRevision,
            acceptedTick,
            timeoutMs,
            acceptedRunId,
            ownership);

    private static SettlementProbe Probe(
        long revision = 4,
        long tick = 1,
        long workRevision = 0,
        Phase phase = Phase.Map,
        bool requiresFrameStability = false,
        bool busy = false,
        bool hasDecision = false,
        string stateKey = "state",
        string[]? errors = null,
        SettlementActivity? activity = null,
        string runId = "run") => new(
            tick,
            workRevision,
            requiresFrameStability,
            activity ?? new SettlementActivity(
                busy ? 1 : 0, EventOptionExecuting: false,
                ExecutorRunning: false, QueuedActionCount: 0),
            new SnapshotContract(phase)
            {
                Revision = revision,
                RunId = runId,
                Side = stateKey,
                Legal = hasDecision ? ["option"] : [],
            },
            errors ?? []);

    private static SettlementWatchdogProbe Watchdog(
        object? action,
        string? actionName,
        bool pickerActive = false,
        bool combatInProgress = false,
        bool combatIsEnding = false,
        bool queuesEmpty = false,
        bool allEnemiesDead = false) => new(
            action,
            actionName,
            pickerActive,
            combatInProgress,
            combatIsEnding,
            queuesEmpty,
            allEnemiesDead);

    private static RunLogVerbFacts Verb(
        string runId,
        string action,
        SettlementOutcome? outcome = null,
        string? fingerprint = null) =>
        new(runId, action, outcome, fingerprint);

    // A value-equal stand-in for CardModel: two copies of one model compare
    // equal, so a projection that leaned on Equals instead of identity would
    // pass this suite only by accident.
    private sealed record SelectableCard(string Model);

    // One snapshot's worth of rows, projected the way Snapshotter does it:
    // read the picked instances once, then answer every row from that.
    private static string Flags(
        IReadOnlyList<SelectableCard?> candidates,
        IReadOnlyCollection<SelectableCard> selected)
    {
        var picked = SelectionProjection.Picked(selected);
        return string.Concat(candidates.Select(card =>
            SelectionProjection.IsSelected(card, picked) ? "x" : "-"));
    }

    private static void Equal(object? expected, object? actual)
    {
        if (!Equals(expected, actual))
            throw new InvalidOperationException($"expected {expected ?? "<null>"}, got {actual ?? "<null>"}");
    }

    private static void True(bool actual)
    {
        if (!actual)
            throw new InvalidOperationException("expected true");
    }

    private static void False(bool actual)
    {
        if (actual)
            throw new InvalidOperationException("expected false");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"expected {typeof(T).Name}");
    }

    private static T Capture<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"expected {typeof(T).Name}");
    }
    private static InvalidOperationException CollectionMutation() => new(
        "Collection was modified; enumeration operation may not execute.");
}

internal static class PatchIdentityProbe
{
    public static void Apply(int value, string text) { }
}

internal class BaseProbe
{
    private readonly string _secret = "base-secret";

    private string Computed => "computed-value";

    public string GetOnly { get; } = "initial";

    private string Mutable { get; set; } = "initial";

    public string ReadSecret() => _secret;

    public string ReadMutable() => Mutable;

    private string Join(string first, string second) => $"{first}:{second}";
}

internal sealed class OneShotEnumerable<T>(IEnumerable<T> values) : IEnumerable<T>
{
    public int EnumerationCount { get; private set; }

    public IEnumerator<T> GetEnumerator()
    {
        EnumerationCount++;
        if (EnumerationCount > 1)
            throw new InvalidOperationException("live source was enumerated twice");
        return values.GetEnumerator();
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();
}

internal sealed class DerivedProbe : BaseProbe
{
}

internal sealed class FakeSettlementClock : ISettlementClock
{
    public DateTimeOffset UtcNow { get; private set; } =
        DateTimeOffset.UnixEpoch;

    public void Advance(int milliseconds) =>
        UtcNow = UtcNow.AddMilliseconds(milliseconds);
}

internal sealed class FakeSettlementTicks : ISettlementTickSource
{
    private readonly FakeSettlementClock _clock;
    private readonly int _waitAdvanceMs;
    private readonly SettlementProbe[] _probes;

    public FakeSettlementTicks(
        FakeSettlementClock clock, params SettlementProbe[] probes)
        : this(clock, 1, probes) { }

    public FakeSettlementTicks(
        FakeSettlementClock clock,
        int waitAdvanceMs,
        params SettlementProbe[] probes)
    {
        _clock = clock;
        _waitAdvanceMs = waitAdvanceMs;
        _probes = probes;
    }

    public int Captures { get; private set; }
    public int ChangeWaits { get; private set; }
    public int TickWaits { get; private set; }

    public Task<SettlementProbe> Capture(long startedRevision)
    {
        var probe = _probes[Math.Min(Captures, _probes.Length - 1)];
        Captures++;
        return Task.FromResult(probe);
    }

    public Task WaitForChange(long afterRevision, int timeoutMs)
    {
        ChangeWaits++;
        _clock.Advance(Math.Min(_waitAdvanceMs, timeoutMs));
        return Task.CompletedTask;
    }

    public Task WaitForTick(long afterTick, int timeoutMs)
    {
        TickWaits++;
        _clock.Advance(Math.Min(_waitAdvanceMs, timeoutMs));
        return Task.CompletedTask;
    }
}

internal sealed class ThrowingSettlementTicks(Exception exception) : ISettlementTickSource
{
    public Task<SettlementProbe> Capture(long startedRevision) =>
        Task.FromException<SettlementProbe>(exception);

    public Task WaitForChange(long afterRevision, int timeoutMs) =>
        Task.CompletedTask;

    public Task WaitForTick(long afterTick, int timeoutMs) =>
        Task.CompletedTask;
}
