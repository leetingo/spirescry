using System.Reflection;
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

    public static void DecisionCardTextKeySeparatesEveryTextChangingModifier()
    {
        Equal("BASH+0", DecisionProjection.CardTextKey("BASH", 0, null, null));
        Equal("BASH+1", DecisionProjection.CardTextKey("BASH", 1, null, null));
        Equal("BASH+1@SELF_HELP!CURSED",
            DecisionProjection.CardTextKey("BASH", 1, "SELF_HELP", "CURSED"));
    }

    public static void DecisionLegalVerbsComeFromVisibleTargetsAndGates()
    {
        var snapshot = System.Text.Json.Nodes.JsonNode.Parse("""
            {
              "phase":"card_select",
              "cards":[{"idx":0}],
              "confirmable":false,
              "cancelable":true,
              "player":{"potions":[]}
            }
            """)!.AsObject();

        var legal = DecisionProjection.LegalVerbs(snapshot, runActive: true);

        Equal("pick-card,skip,abandon", string.Join(',', legal));
    }

    public static void DecisionUnavailableTransitionsDoNotAdvertiseActions()
    {
        foreach (var phase in new[] { "event", "rewards" })
        {
            var snapshot = System.Text.Json.Nodes.JsonNode.Parse($$"""
                {
                  "phase":"{{phase}}",
                  "available":false,
                  "options":[{"idx":0}],
                  "rewards":[{"idx":0}]
                }
                """)!.AsObject();

            var legal = DecisionProjection.LegalVerbs(snapshot, runActive: false);

            Equal("", string.Join(',', legal));
        }
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
