using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spirescry;
using Spirescry.Host;
using Spirescry.State;

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
    public static void ProtocolVocabularyExposesTheCompleteWireContract()
    {
        var artifact = JsonNode.Parse(ProtocolVocabulary.CreateArtifactJson())!.AsObject();

        Equal(ProtocolVocabulary.ProtocolVersion,
            artifact["protocolVersion"]!.GetValue<int>());
        Equal(ProtocolVocabulary.Rejections.All.Count,
            artifact["rejectionCodes"]!.AsArray().Count);
        Equal(ProtocolVocabulary.Phases.All.Count,
            artifact["phases"]!.AsArray().Count);
        Equal(ProtocolVocabulary.FaultEvents.All.Count,
            artifact["faultEventTokens"]!.AsObject().Count);
        Equal(ProtocolVocabulary.Cheats.All.Count,
            artifact["cheatArgumentShapes"]!.AsArray().Count);
    }

    public static void ProtocolVocabularyMapsEveryPhaseAndUnknownValues()
    {
        var mapped = Enum.GetValues<Phase>()
            .Select(ProtocolVocabulary.Phases.Name).ToArray();

        True(mapped.SequenceEqual(ProtocolVocabulary.Phases.All));
        Equal(ProtocolVocabulary.Phases.Unknown,
            ProtocolVocabulary.Phases.Name((Phase)999));
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
        var pop = new InvalidOperationException(
            "Tried to pop action EndPlayerTurnAction, but we didn't find it in any queue!");

        Equal(InlineFaultKind.VictorySettled, ResolutionGuards.ClassifyInlineFault(
            pop, "EndPlayerTurnAction", combatInProgress: false, revisionChanged: true));
        Equal(InlineFaultKind.Partial, ResolutionGuards.ClassifyInlineFault(
            pop, "EndPlayerTurnAction", combatInProgress: true, revisionChanged: true));
        Equal(InlineFaultKind.Partial, ResolutionGuards.ClassifyInlineFault(
            pop, "PlayCardAction", combatInProgress: false, revisionChanged: true));
        Equal(InlineFaultKind.Failed, ResolutionGuards.ClassifyInlineFault(
            new InvalidOperationException("some other queue failure"),
            "EndPlayerTurnAction", combatInProgress: false, revisionChanged: false));

        Equal(InlineFaultKind.VictorySettled, ResolutionGuards.ClassifyInlineFault(
            new AggregateException(pop),
            "EndPlayerTurnAction", combatInProgress: false, revisionChanged: false));
        Equal(InlineFaultKind.Failed, ResolutionGuards.ClassifyInlineFault(
            new AggregateException(new InvalidOperationException("some other queue failure")),
            "EndPlayerTurnAction", combatInProgress: false, revisionChanged: false));
    }

    public static void InlineFaultClassificationDistinguishesPartialFromFailed()
    {
        var fault = new MissingMethodException("missing Godot API");

        Equal(InlineFaultKind.Partial, ResolutionGuards.ClassifyInlineFault(
            fault, "PlayCardAction", combatInProgress: true, revisionChanged: true));
        Equal(InlineFaultKind.Failed, ResolutionGuards.ClassifyInlineFault(
            fault, "PlayCardAction", combatInProgress: true, revisionChanged: false));
    }

    public static void EndingCombatIsNotADeadBoardWedge()
    {
        False(ResolutionGuards.IsDeadBoardCandidate(
            actionRunning: false,
            pickerActive: false,
            combatInProgress: true,
            combatIsEnding: true,
            queuesEmpty: true,
            allEnemiesDead: true));
        False(ResolutionGuards.IsDeadBoardCandidate(
            actionRunning: false,
            pickerActive: false,
            combatInProgress: true,
            combatIsEnding: false,
            queuesEmpty: false,
            allEnemiesDead: true));
        True(ResolutionGuards.IsDeadBoardCandidate(
            actionRunning: false,
            pickerActive: false,
            combatInProgress: true,
            combatIsEnding: false,
            queuesEmpty: true,
            allEnemiesDead: true));
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
        var snapshot = new SnapshotContract("card_select")
        {
            Cards = [new SnapshotItemContract { Index = 0 }],
            Confirmable = false,
            Cancelable = true,
            Player = new SnapshotPlayerContract { Potions = [] },
        };

        var legal = DecisionProjection.LegalVerbs(snapshot, runActive: true);

        Equal("pick-card,skip,abandon", string.Join(',', legal));
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

    public static void DecisionClosedChestAdvertisesTheOpeningPickRelic()
    {
        // Headless closed chest: relics empty, proceed always available.
        // pick-relic is the verb that opens the chest — omitting it left
        // "proceed" as the only advertised action and a legal-verbs-only
        // agent had to walk past every treasure room.
        var headless = new SnapshotContract("treasure")
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
        var gui = new SnapshotContract("treasure")
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
        var offering = new SnapshotContract("treasure")
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
        var resolved = new SnapshotContract("treasure")
        {
            ChestOpened = true,
            ProceedAvailable = true,
            Relics = [],
            Player = new SnapshotPlayerContract { Potions = [] },
        };

        Equal("proceed,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(resolved, runActive: true)));
    }

    public static void DecisionUnavailableTransitionsDoNotAdvertiseActions()
    {
        foreach (var phase in new[] { "event", "rewards" })
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

    public static void DecisionPotionVisibilityPreservesTopLevelPrecedence()
    {
        var inventoryPotion = new SnapshotItemContract { Index = 0 };
        var shop = new SnapshotContract("shop")
        {
            Potions = [],
            Player = new SnapshotPlayerContract { Potions = [inventoryPotion] },
        };
        var map = new SnapshotContract("map")
        {
            Player = new SnapshotPlayerContract { Potions = [inventoryPotion] },
        };

        Equal("leave,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(shop, runActive: true)));
        Equal("potion-discard,abandon", string.Join(',',
            DecisionProjection.LegalVerbs(map, runActive: true)));
    }

    public static void SnapshotContractPreservesUnconsumedProducerFields()
    {
        var card = new SnapshotItemContract
        {
            Index = 0,
            Playable = true,
        };
        card.AddExtensions(new { model = "STRIKE_R" });
        var player = new SnapshotPlayerContract { Potions = [] };
        player.AddExtensions(new { hp = new[] { 40, 80 } });
        var snapshot = new SnapshotContract("combat")
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
        Equal("combat", snapshot.Phase);
        Equal("player", snapshot.Side);
        True(snapshot.Hand.Single().Playable == true);
        Equal("STRIKE_R", wire["hand"]![0]!["model"]!.GetValue<string>());
        Equal(7, wire["phaseSpecific"]!["value"]!.GetValue<int>());
        True(wire["phaseSpecific"]!["missing"] is null);
    }

    public static void SnapshotContractOwnsFollowAndAttributionMetadata()
    {
        var snapshot = new SnapshotContract("event")
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

internal sealed class DerivedProbe : BaseProbe
{
}
