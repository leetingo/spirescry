using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Spirescry.State;

// One schema owns every wire key consumed by settlement/replay and every key
// emitted by their canonical projection. C# typed accessors below reference
// these constants; protocol.json publishes the grouped field definitions so
// non-C# consumers can generate the same projection without copying strings.
internal static class SnapshotConsumerSchema
{
    internal static class Top
    {
        internal const string RevisionWire = "rev";
        internal const string RunIdWire = "runId";
        internal const string PhaseWire = "phase";
        internal const string PhaseOutput = "phase";
        internal const string IdWire = "id";
        internal const string IdOutput = "id";
        internal const string ActWire = "act";
        internal const string ActOutput = "act";
        internal const string CurrentWire = "current";
        internal const string CurrentOutput = "current";
        internal const string TurnWire = "turn";
        internal const string TurnOutput = "turn";
        internal const string OutcomeWire = "outcome";
        internal const string OutcomeOutput = "outcome";
        internal const string HpWire = "hp";
        internal const string HpOutput = "hp";
        internal const string GoldWire = "gold";
        internal const string GoldOutput = "gold";
        internal const string SemanticStateWire = "semanticState";
        internal const string SemanticStateOutput = "semanticState";
        internal const string SelectedWire = "selected";
        internal const string SelectedOutput = "selected";
        internal const string SideWire = "side";
        internal const string SideOutput = "side";
        internal const string ActionsDisabledWire = "actionsDisabled";
        internal const string ActionsDisabledOutput = "actionsDisabled";
        internal const string AvailableWire = "available";
        internal const string AvailableOutput = "available";
        internal const string ProceedAvailableWire = "proceedAvailable";
        internal const string ProceedAvailableOutput = "proceedAvailable";
        internal const string ChestOpenedWire = "chestOpened";
        internal const string ChestOpenedOutput = "chestOpened";
        internal const string ConfirmableWire = "confirmable";
        internal const string ConfirmableOutput = "confirmable";
        internal const string CancelableWire = "cancelable";
        internal const string CancelableOutput = "cancelable";
        internal const string HasTopLevelPotionsOutput = "hasTopLevelPotions";
        internal const string NextWire = "next";
        internal const string NextOutput = "next";
        internal const string HandWire = "hand";
        internal const string HandOutput = "hand";
        internal const string PotionsWire = "potions";
        internal const string PotionsOutput = "potions";
        internal const string OptionsWire = "options";
        internal const string OptionsOutput = "options";
        internal const string CardsWire = "cards";
        internal const string CardsOutput = "cards";
        internal const string ColorlessWire = "colorless";
        internal const string ColorlessOutput = "colorless";
        internal const string RelicsWire = "relics";
        internal const string RelicsOutput = "relics";
        internal const string RewardsWire = "rewards";
        internal const string RewardsOutput = "rewards";
        internal const string AlternativesWire = "alternatives";
        internal const string AlternativesOutput = "alternatives";
        internal const string BundlesWire = "bundles";
        internal const string BundlesOutput = "bundles";
        internal const string CellsWire = "cells";
        internal const string CellsOutput = "cells";
        internal const string YouWire = "you";
        internal const string YouOutput = "you";
        internal const string EnemiesWire = "enemies";
        internal const string EnemiesOutput = "enemies";
        internal const string PlayerWire = "player";
        internal const string PlayerOutput = "player";
        internal const string CardRemovalWire = "cardRemoval";
        internal const string CardRemovalOutput = "cardRemoval";
        internal const string LegalWire = "legal";
        internal const string LegalOutput = "legal";

        internal static IReadOnlyList<ConsumerProjectionField> Fields { get; } =
            Array.AsReadOnly(
            [
                Field("phase", PhaseWire, PhaseOutput, ConsumerProjectionFieldKind.RequiredString),
                Field("id", IdWire, IdOutput, ConsumerProjectionFieldKind.OptionalString),
                Field("act", ActWire, ActOutput, ConsumerProjectionFieldKind.OptionalNumber),
                Field("current", CurrentWire, CurrentOutput, ConsumerProjectionFieldKind.OptionalIntArray),
                Field("turn", TurnWire, TurnOutput, ConsumerProjectionFieldKind.OptionalNumber),
                Field("outcome", OutcomeWire, OutcomeOutput, ConsumerProjectionFieldKind.OptionalString),
                Field("hp", HpWire, HpOutput, ConsumerProjectionFieldKind.OptionalIntArray),
                Field("gold", GoldWire, GoldOutput, ConsumerProjectionFieldKind.OptionalNumber),
                Field("semanticState", SemanticStateWire, SemanticStateOutput, ConsumerProjectionFieldKind.OptionalStringArray),
                Field("selected", SelectedWire, SelectedOutput, ConsumerProjectionFieldKind.OptionalStringArray),
                Field("side", SideWire, SideOutput, ConsumerProjectionFieldKind.OptionalString),
                Field("actionsDisabled", ActionsDisabledWire, ActionsDisabledOutput, ConsumerProjectionFieldKind.OptionalBoolean),
                Field("available", AvailableWire, AvailableOutput, ConsumerProjectionFieldKind.OptionalBoolean),
                Field("proceedAvailable", ProceedAvailableWire, ProceedAvailableOutput, ConsumerProjectionFieldKind.OptionalBoolean),
                Field("chestOpened", ChestOpenedWire, ChestOpenedOutput, ConsumerProjectionFieldKind.OptionalBoolean),
                Field("confirmable", ConfirmableWire, ConfirmableOutput, ConsumerProjectionFieldKind.OptionalBoolean),
                Field("cancelable", CancelableWire, CancelableOutput, ConsumerProjectionFieldKind.OptionalBoolean),
                Field("hasTopLevelPotions", PotionsWire, HasTopLevelPotionsOutput, ConsumerProjectionFieldKind.PresenceBoolean),
                Field("next", NextWire, NextOutput, ConsumerProjectionFieldKind.ItemArray),
                Field("hand", HandWire, HandOutput, ConsumerProjectionFieldKind.ItemArray),
                Field("potions", PotionsWire, PotionsOutput, ConsumerProjectionFieldKind.ItemArray),
                Field("options", OptionsWire, OptionsOutput, ConsumerProjectionFieldKind.ItemArray),
                Field("cards", CardsWire, CardsOutput, ConsumerProjectionFieldKind.ItemArray),
                Field("colorless", ColorlessWire, ColorlessOutput, ConsumerProjectionFieldKind.ItemArray),
                Field("relics", RelicsWire, RelicsOutput, ConsumerProjectionFieldKind.ItemArray),
                Field("rewards", RewardsWire, RewardsOutput, ConsumerProjectionFieldKind.ItemArray),
                Field("alternatives", AlternativesWire, AlternativesOutput, ConsumerProjectionFieldKind.ItemArray),
                Field("bundles", BundlesWire, BundlesOutput, ConsumerProjectionFieldKind.ItemArray),
                Field("cells", CellsWire, CellsOutput, ConsumerProjectionFieldKind.ItemArray),
                Field("you", YouWire, YouOutput, ConsumerProjectionFieldKind.OptionalCombatant),
                Field("enemies", EnemiesWire, EnemiesOutput, ConsumerProjectionFieldKind.EnemyArray),
                Field("player", PlayerWire, PlayerOutput, ConsumerProjectionFieldKind.OptionalPlayer),
                Field("cardRemoval", CardRemovalWire, CardRemovalOutput, ConsumerProjectionFieldKind.OptionalItem),
                Field("legal", LegalWire, LegalOutput, ConsumerProjectionFieldKind.RequiredStringArray),
            ]);
    }

    internal static class Item
    {
        internal const string IndexWire = "idx";
        internal const string IndexOutput = "index";
        internal const string IdWire = "id";
        internal const string IdOutput = "id";
        internal const string ModelWire = "model";
        internal const string ModelOutput = "model";
        internal const string SelectorWire = "selector";
        internal const string SelectorOutput = "selector";
        internal const string SlotWire = "slot";
        internal const string SlotOutput = "slot";
        internal const string TargetWire = "target";
        internal const string TargetOutput = "target";
        internal const string ColWire = "col";
        internal const string ColOutput = "col";
        internal const string RowWire = "row";
        internal const string RowOutput = "row";
        internal const string TypeWire = "type";
        internal const string TypeOutput = "type";
        internal const string SemanticStateWire = "semanticState";
        internal const string SemanticStateOutput = "semanticState";
        internal const string SelectedWire = "selected";
        internal const string SelectedOutput = "selected";
        internal const string PlayableWire = "playable";
        internal const string PlayableOutput = "playable";
        internal const string LockedWire = "locked";
        internal const string LockedOutput = "locked";
        internal const string ChosenWire = "chosen";
        internal const string ChosenOutput = "chosen";
        internal const string EnabledWire = "enabled";
        internal const string EnabledOutput = "enabled";
        internal const string PurchasableWire = "purchasable";
        internal const string PurchasableOutput = "purchasable";
        internal const string HiddenWire = "hidden";
        internal const string HiddenOutput = "hidden";
        internal const string CardsWire = "cards";
        internal const string CardsOutput = "cards";

        internal static IReadOnlyList<ConsumerProjectionField> Fields { get; } =
            Array.AsReadOnly(
            [
                Field("index", IndexWire, IndexOutput, ConsumerProjectionFieldKind.OptionalNumber),
                Field("id", IdWire, IdOutput, ConsumerProjectionFieldKind.OptionalString),
                Field("model", ModelWire, ModelOutput, ConsumerProjectionFieldKind.OptionalString),
                Field("selector", SelectorWire, SelectorOutput, ConsumerProjectionFieldKind.OptionalString),
                Field("slot", SlotWire, SlotOutput, ConsumerProjectionFieldKind.OptionalNumber),
                Field("target", TargetWire, TargetOutput, ConsumerProjectionFieldKind.OptionalString),
                Field("col", ColWire, ColOutput, ConsumerProjectionFieldKind.OptionalNumber),
                Field("row", RowWire, RowOutput, ConsumerProjectionFieldKind.OptionalNumber),
                Field("type", TypeWire, TypeOutput, ConsumerProjectionFieldKind.OptionalString),
                Field("semanticState", SemanticStateWire, SemanticStateOutput, ConsumerProjectionFieldKind.OptionalStringArray),
                Field("selected", SelectedWire, SelectedOutput, ConsumerProjectionFieldKind.OptionalBoolean),
                Field("playable", PlayableWire, PlayableOutput, ConsumerProjectionFieldKind.OptionalBoolean),
                Field("locked", LockedWire, LockedOutput, ConsumerProjectionFieldKind.OptionalBoolean),
                Field("chosen", ChosenWire, ChosenOutput, ConsumerProjectionFieldKind.OptionalBoolean),
                Field("enabled", EnabledWire, EnabledOutput, ConsumerProjectionFieldKind.OptionalBoolean),
                Field("purchasable", PurchasableWire, PurchasableOutput, ConsumerProjectionFieldKind.OptionalBoolean),
                Field("hidden", HiddenWire, HiddenOutput, ConsumerProjectionFieldKind.OptionalBoolean),
                Field("cards", CardsWire, CardsOutput, ConsumerProjectionFieldKind.ItemArray),
            ]);
    }

    internal static class Combatant
    {
        internal const string HpWire = "hp";
        internal const string HpOutput = "hp";
        internal const string BlockWire = "block";
        internal const string BlockOutput = "block";
        internal const string EnergyWire = "energy";
        internal const string EnergyOutput = "energy";
        internal const string StarsWire = "stars";
        internal const string StarsOutput = "stars";
        internal const string SemanticStateWire = "semanticState";
        internal const string SemanticStateOutput = "semanticState";

        internal static IReadOnlyList<ConsumerProjectionField> Fields { get; } =
            Array.AsReadOnly(
            [
                Field("hp", HpWire, HpOutput, ConsumerProjectionFieldKind.RequiredIntArray),
                Field("block", BlockWire, BlockOutput, ConsumerProjectionFieldKind.OptionalNumber),
                Field("energy", EnergyWire, EnergyOutput, ConsumerProjectionFieldKind.RequiredIntArray),
                Field("stars", StarsWire, StarsOutput, ConsumerProjectionFieldKind.OptionalNumber),
                Field("semanticState", SemanticStateWire, SemanticStateOutput, ConsumerProjectionFieldKind.OptionalStringArray),
            ]);
    }

    internal static class Enemy
    {
        internal const string IdWire = "id";
        internal const string IdOutput = "id";
        internal const string ModelWire = "model";
        internal const string ModelOutput = "model";
        internal const string HpWire = "hp";
        internal const string HpOutput = "hp";
        internal const string BlockWire = "block";
        internal const string BlockOutput = "block";
        internal const string AliveWire = "alive";
        internal const string AliveOutput = "alive";
        internal const string SemanticStateWire = "semanticState";
        internal const string SemanticStateOutput = "semanticState";

        internal static IReadOnlyList<ConsumerProjectionField> Fields { get; } =
            Array.AsReadOnly(
            [
                Field("id", IdWire, IdOutput, ConsumerProjectionFieldKind.OptionalNumber),
                Field("model", ModelWire, ModelOutput, ConsumerProjectionFieldKind.OptionalString),
                Field("hp", HpWire, HpOutput, ConsumerProjectionFieldKind.RequiredIntArray),
                Field("block", BlockWire, BlockOutput, ConsumerProjectionFieldKind.OptionalNumber),
                Field("alive", AliveWire, AliveOutput, ConsumerProjectionFieldKind.OptionalBoolean),
                Field("semanticState", SemanticStateWire, SemanticStateOutput, ConsumerProjectionFieldKind.OptionalStringArray),
            ]);
    }

    internal static class Player
    {
        internal const string HpWire = "hp";
        internal const string HpOutput = "hp";
        internal const string GoldWire = "gold";
        internal const string GoldOutput = "gold";
        internal const string SemanticStateWire = "semanticState";
        internal const string SemanticStateOutput = "semanticState";
        internal const string PotionsWire = "potions";
        internal const string PotionsOutput = "potions";

        internal static IReadOnlyList<ConsumerProjectionField> Fields { get; } =
            Array.AsReadOnly(
            [
                Field("hp", HpWire, HpOutput, ConsumerProjectionFieldKind.OptionalIntArray),
                Field("gold", GoldWire, GoldOutput, ConsumerProjectionFieldKind.OptionalNumber),
                Field("semanticState", SemanticStateWire, SemanticStateOutput, ConsumerProjectionFieldKind.OptionalStringArray),
                Field("potions", PotionsWire, PotionsOutput, ConsumerProjectionFieldKind.ItemArray),
            ]);
    }

    internal static ConsumerProjectionSchema Artifact { get; } = new(
        Top.Fields, Item.Fields, Combatant.Fields, Enemy.Fields, Player.Fields);

    private static ConsumerProjectionField Field(
        string symbol,
        string wire,
        string output,
        ConsumerProjectionFieldKind kind) => new(symbol, wire, output, kind);
}

// The typed seam between snapshot production and the consumers that derive
// decisions, attribute run-log outcomes, and decide follow stability. The
// backing JSON retains every phase-specific field verbatim; consumed keys are
// exposed only here so producers, consumers, and fixtures compile against one
// vocabulary instead of repeating stringly-typed shapes.
internal sealed class SnapshotContract
{
    private static readonly JsonSerializerOptions ProjectionJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly HashSet<string> ConsumedKeys = new(
        SnapshotConsumerSchema.Top.Fields.Select(field => field.Wire)
            .Append(SnapshotConsumerSchema.Top.RevisionWire)
            .Append(SnapshotConsumerSchema.Top.RunIdWire),
        StringComparer.Ordinal);
    private readonly JsonObject _wire;

    internal SnapshotContract(Phase phase)
    {
        Phase = phase;
        _wire = new JsonObject
        {
            [SnapshotConsumerSchema.Top.PhaseWire] = ProtocolVocabulary.Phases.Name(phase),
        };
    }

    public Phase Phase { get; }

    public string PhaseName => ProtocolVocabulary.Phases.Name(Phase);

    public long? Revision
    {
        get => Scalar<long>(SnapshotConsumerSchema.Top.RevisionWire);
        set => SetScalar(SnapshotConsumerSchema.Top.RevisionWire, value);
    }

    public string? RunId
    {
        get => String(SnapshotConsumerSchema.Top.RunIdWire);
        set => SetString(SnapshotConsumerSchema.Top.RunIdWire, value);
    }

    public string? Id
    {
        get => String(SnapshotConsumerSchema.Top.IdWire);
        set => SetString(SnapshotConsumerSchema.Top.IdWire, value);
    }

    public int? Act
    {
        get => Scalar<int>(SnapshotConsumerSchema.Top.ActWire);
        set => SetScalar(SnapshotConsumerSchema.Top.ActWire, value);
    }

    public int[]? Current
    {
        get => _wire[SnapshotConsumerSchema.Top.CurrentWire] is JsonArray
            ? IntArray(SnapshotConsumerSchema.Top.CurrentWire)
            : null;
        set
        {
            if (value is null) _wire[SnapshotConsumerSchema.Top.CurrentWire] = null;
            else SetIntArray(SnapshotConsumerSchema.Top.CurrentWire, value);
        }
    }

    public int? Gold
    {
        get => Scalar<int>(SnapshotConsumerSchema.Top.GoldWire);
        set => SetScalar(SnapshotConsumerSchema.Top.GoldWire, value);
    }

    public string[]? SemanticState
    {
        get => StringArrayOrNull(SnapshotConsumerSchema.Top.SemanticStateWire);
        set => SetStringArrayOrNull(SnapshotConsumerSchema.Top.SemanticStateWire, value);
    }

    public string[]? Selected
    {
        get => StringArrayOrNull(SnapshotConsumerSchema.Top.SelectedWire);
        set => SetStringArrayOrNull(SnapshotConsumerSchema.Top.SelectedWire, value);
    }

    public int? Turn
    {
        get => Scalar<int>(SnapshotConsumerSchema.Top.TurnWire);
        set => SetScalar(SnapshotConsumerSchema.Top.TurnWire, value);
    }

    public string? Outcome
    {
        get => String(SnapshotConsumerSchema.Top.OutcomeWire);
        set => SetString(SnapshotConsumerSchema.Top.OutcomeWire, value);
    }

    public int[]? Hp
    {
        get => _wire[SnapshotConsumerSchema.Top.HpWire] is JsonArray
            ? IntArray(SnapshotConsumerSchema.Top.HpWire)
            : null;
        set
        {
            if (value is null) _wire[SnapshotConsumerSchema.Top.HpWire] = null;
            else SetIntArray(SnapshotConsumerSchema.Top.HpWire, value);
        }
    }

    public string? Side
    {
        get => String(SnapshotConsumerSchema.Top.SideWire);
        set => SetString(SnapshotConsumerSchema.Top.SideWire, value);
    }

    public bool? ActionsDisabled
    {
        get => Scalar<bool>(SnapshotConsumerSchema.Top.ActionsDisabledWire);
        set => SetScalar(SnapshotConsumerSchema.Top.ActionsDisabledWire, value);
    }

    public bool? Available
    {
        get => Scalar<bool>(SnapshotConsumerSchema.Top.AvailableWire);
        set => SetScalar(SnapshotConsumerSchema.Top.AvailableWire, value);
    }

    public bool? ProceedAvailable
    {
        get => Scalar<bool>(SnapshotConsumerSchema.Top.ProceedAvailableWire);
        set => SetScalar(SnapshotConsumerSchema.Top.ProceedAvailableWire, value);
    }

    public bool? ChestOpened
    {
        get => Scalar<bool>(SnapshotConsumerSchema.Top.ChestOpenedWire);
        set => SetScalar(SnapshotConsumerSchema.Top.ChestOpenedWire, value);
    }

    public bool? Confirmable
    {
        get => Scalar<bool>(SnapshotConsumerSchema.Top.ConfirmableWire);
        set => SetScalar(SnapshotConsumerSchema.Top.ConfirmableWire, value);
    }

    public bool? Cancelable
    {
        get => Scalar<bool>(SnapshotConsumerSchema.Top.CancelableWire);
        set => SetScalar(SnapshotConsumerSchema.Top.CancelableWire, value);
    }

    public SnapshotItemContract[] Next
    {
        get => Items(SnapshotConsumerSchema.Top.NextWire);
        set => SetItems(SnapshotConsumerSchema.Top.NextWire, value);
    }

    public SnapshotItemContract[] Hand
    {
        get => Items(SnapshotConsumerSchema.Top.HandWire);
        set => SetItems(SnapshotConsumerSchema.Top.HandWire, value);
    }

    public SnapshotItemContract[] Potions
    {
        get => Items(SnapshotConsumerSchema.Top.PotionsWire);
        set => SetItems(SnapshotConsumerSchema.Top.PotionsWire, value);
    }

    public bool HasTopLevelPotions =>
        _wire[SnapshotConsumerSchema.Top.PotionsWire] is JsonArray;

    public SnapshotItemContract[] Options
    {
        get => Items(SnapshotConsumerSchema.Top.OptionsWire);
        set => SetItems(SnapshotConsumerSchema.Top.OptionsWire, value);
    }

    public SnapshotItemContract[] Cards
    {
        get => Items(SnapshotConsumerSchema.Top.CardsWire);
        set => SetItems(SnapshotConsumerSchema.Top.CardsWire, value);
    }

    public SnapshotItemContract[] Colorless
    {
        get => Items(SnapshotConsumerSchema.Top.ColorlessWire);
        set => SetItems(SnapshotConsumerSchema.Top.ColorlessWire, value);
    }

    public SnapshotItemContract[] Relics
    {
        get => Items(SnapshotConsumerSchema.Top.RelicsWire);
        set => SetItems(SnapshotConsumerSchema.Top.RelicsWire, value);
    }

    public SnapshotItemContract[] Rewards
    {
        get => Items(SnapshotConsumerSchema.Top.RewardsWire);
        set => SetItems(SnapshotConsumerSchema.Top.RewardsWire, value);
    }

    public SnapshotItemContract[] Alternatives
    {
        get => Items(SnapshotConsumerSchema.Top.AlternativesWire);
        set => SetItems(SnapshotConsumerSchema.Top.AlternativesWire, value);
    }

    public SnapshotItemContract[] Bundles
    {
        get => Items(SnapshotConsumerSchema.Top.BundlesWire);
        set => SetItems(SnapshotConsumerSchema.Top.BundlesWire, value);
    }

    public SnapshotItemContract[] Cells
    {
        get => Items(SnapshotConsumerSchema.Top.CellsWire);
        set => SetItems(SnapshotConsumerSchema.Top.CellsWire, value);
    }

    public SnapshotEnemyContract[] Enemies
    {
        get => _wire[SnapshotConsumerSchema.Top.EnemiesWire] is JsonArray enemies
            ? enemies.OfType<JsonObject>()
                .Select(enemy => new SnapshotEnemyContract(enemy)).ToArray()
            : [];
        set => _wire[SnapshotConsumerSchema.Top.EnemiesWire] = new JsonArray(
            value.Select(enemy => (JsonNode)enemy.ToJsonObject()).ToArray());
    }

    public SnapshotCombatantContract? You
    {
        get => _wire[SnapshotConsumerSchema.Top.YouWire] is JsonObject combatant
            ? new SnapshotCombatantContract(combatant)
            : null;
        set => _wire[SnapshotConsumerSchema.Top.YouWire] = value?.ToJsonObject();
    }

    public SnapshotPlayerContract? Player
    {
        get => _wire[SnapshotConsumerSchema.Top.PlayerWire] is JsonObject player
            ? new SnapshotPlayerContract(player)
            : null;
        set => _wire[SnapshotConsumerSchema.Top.PlayerWire] = value?.ToJsonObject();
    }

    public SnapshotItemContract? CardRemoval
    {
        get => _wire[SnapshotConsumerSchema.Top.CardRemovalWire] is JsonObject removal
            ? new SnapshotItemContract(removal)
            : null;
        set => _wire[SnapshotConsumerSchema.Top.CardRemovalWire] = value?.ToJsonObject();
    }

    public string[] Legal
    {
        get => _wire[SnapshotConsumerSchema.Top.LegalWire] is JsonArray legal
            ? legal.Select(item => item?.GetValue<string>())
                .Where(item => item is not null)
                .Cast<string>()
                .ToArray()
            : [];
        set => _wire[SnapshotConsumerSchema.Top.LegalWire] = new JsonArray(
            value.Select(item => (JsonNode)item).ToArray());
    }

    // Phase-specific fields that no shared consumer reads still ride the
    // original wire. Consumed keys are rejected here: Snapshotter must set
    // those through the typed properties above, so a contract rename breaks
    // producer and consumers together at compile time.
    internal void AddExtensions(object extension)
    {
        var fields = JsonSerializer.SerializeToNode(extension)?.AsObject()
            ?? throw new InvalidOperationException("snapshot extension returned null");
        foreach (var (property, value) in fields)
        {
            if (ConsumedKeys.Contains(property))
                throw new InvalidOperationException(
                    $"snapshot producer must set consumed '{property}' through SnapshotContract");
            _wire[property] = value?.DeepClone();
        }
    }

    internal JsonObject ToJsonObject() => (JsonObject)_wire.DeepClone();

    // The expanded semantic projection is an internal settlement/replay
    // input, not part of the normal decision payload. Keep it available for
    // explicit diagnostics while bounding default responses independently of
    // deck and pile size.
    internal JsonObject ToAgentJsonObject(bool includeSemanticState = false)
    {
        var result = ToJsonObject();
        if (!includeSemanticState)
            StripSemanticState(result);
        return result;
    }

    private static void StripSemanticState(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove(SnapshotConsumerSchema.Top.SemanticStateWire);
            foreach (var value in obj.Select(pair => pair.Value).OfType<JsonNode>())
                StripSemanticState(value);
            return;
        }
        if (node is JsonArray array)
            foreach (var value in array.OfType<JsonNode>())
                StripSemanticState(value);
    }

    // The two consumers that need a stable representation (GUI settlement
    // and replay attribution) deliberately share this named projection.
    // Extension data is presentation-only: adding or renaming it cannot
    // silently redefine settlement or replay semantics. Every projected
    // value is reached through a typed property, so contract refactors make
    // this seam fail at compile time instead of changing a hash by accident.
    internal JsonObject ConsumerProjection() =>
        JsonSerializer.SerializeToNode(new SnapshotConsumerProjection(
            PhaseName,
            Id,
            Act,
            Current,
            Turn,
            Outcome,
            Hp,
            Gold,
            SemanticState,
            Selected,
            Side,
            ActionsDisabled,
            Available,
            ProceedAvailable,
            ChestOpened,
            Confirmable,
            Cancelable,
            HasTopLevelPotions,
            Next.Select(item => item.ConsumerProjection()).ToArray(),
            Hand.Select(item => item.ConsumerProjection()).ToArray(),
            Potions.Select(item => item.ConsumerProjection()).ToArray(),
            Options.Select(item => item.ConsumerProjection()).ToArray(),
            Cards.Select(item => item.ConsumerProjection()).ToArray(),
            Colorless.Select(item => item.ConsumerProjection()).ToArray(),
            Relics.Select(item => item.ConsumerProjection()).ToArray(),
            Rewards.Select(item => item.ConsumerProjection()).ToArray(),
            Alternatives.Select(item => item.ConsumerProjection()).ToArray(),
            Bundles.Select(item => item.ConsumerProjection()).ToArray(),
            Cells.Select(item => item.ConsumerProjection()).ToArray(),
            You?.ConsumerProjection(),
            Enemies.Select(enemy => enemy.ConsumerProjection()).ToArray(),
            Player?.ConsumerProjection(),
            CardRemoval?.ConsumerProjection(),
            Legal), ProjectionJson)!.AsObject();

    internal string ConsumerStateKey() => ConsumerProjection().ToJsonString();

    internal string ConsumerFingerprint()
    {
        var canonical = Canonicalize(ConsumerProjection())!;
        var bytes = Encoding.UTF8.GetBytes(canonical.ToJsonString(CanonicalJson));
        ulong hash = 14695981039346656037;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= 1099511628211;
        }
        return hash.ToString("x16");
    }

    private static JsonNode? Canonicalize(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var sorted = new JsonObject();
            foreach (var pair in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                sorted[pair.Key] = Canonicalize(pair.Value);
            return sorted;
        }
        if (node is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var value in array) result.Add(Canonicalize(value));
            return result;
        }
        return node is null ? null : JsonNode.Parse(node.ToJsonString());
    }

    private string? String(string property) =>
        _wire[property] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;

    private T? Scalar<T>(string property) where T : struct =>
        _wire[property] is JsonValue value && value.TryGetValue<T>(out var result)
            ? result
            : null;

    private int[] IntArray(string property) =>
        _wire[property] is JsonArray values
            ? values.Select(value => value?.GetValue<int>() ?? 0).ToArray()
            : [];

    private string[]? StringArrayOrNull(string property) =>
        _wire[property] is JsonArray values
            ? values.Select(value => value?.GetValue<string>())
                .Where(value => value is not null)
                .Cast<string>()
                .ToArray()
            : null;

    private void SetString(string property, string? value)
    {
        if (value is null) _wire.Remove(property);
        else _wire[property] = value;
    }

    private void SetScalar<T>(string property, T? value) where T : struct
    {
        if (value is null) _wire.Remove(property);
        else _wire[property] = JsonValue.Create(value.Value);
    }

    private void SetIntArray(string property, IEnumerable<int> values) =>
        _wire[property] = new JsonArray(
            values.Select(value => (JsonNode)value).ToArray());

    private void SetStringArrayOrNull(string property, IEnumerable<string>? values)
    {
        if (values is null) _wire.Remove(property);
        else _wire[property] = new JsonArray(
            values.Select(value => (JsonNode)value).ToArray());
    }

    private SnapshotItemContract[] Items(string property) =>
        _wire[property] is JsonArray items
            ? items.OfType<JsonObject>().Select(item => new SnapshotItemContract(item)).ToArray()
            : [];

    private void SetItems(string property, IEnumerable<SnapshotItemContract> items) =>
        _wire[property] = new JsonArray(
            items.Select(item => (JsonNode)item.ToJsonObject()).ToArray());
}

internal sealed record SnapshotConsumerProjection(
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.PhaseOutput)]
    string Phase,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.IdOutput)]
    string? Id,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.ActOutput)]
    int? Act,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.CurrentOutput)]
    int[]? Current,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.TurnOutput)]
    int? Turn,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.OutcomeOutput)]
    string? Outcome,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.HpOutput)]
    int[]? Hp,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.GoldOutput)]
    int? Gold,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.SemanticStateOutput)]
    string[]? SemanticState,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.SelectedOutput)]
    string[]? Selected,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.SideOutput)]
    string? Side,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.ActionsDisabledOutput)]
    bool? ActionsDisabled,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.AvailableOutput)]
    bool? Available,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.ProceedAvailableOutput)]
    bool? ProceedAvailable,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.ChestOpenedOutput)]
    bool? ChestOpened,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.ConfirmableOutput)]
    bool? Confirmable,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.CancelableOutput)]
    bool? Cancelable,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.HasTopLevelPotionsOutput)]
    bool HasTopLevelPotions,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.NextOutput)]
    SnapshotItemConsumerProjection[] Next,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.HandOutput)]
    SnapshotItemConsumerProjection[] Hand,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.PotionsOutput)]
    SnapshotItemConsumerProjection[] Potions,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.OptionsOutput)]
    SnapshotItemConsumerProjection[] Options,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.CardsOutput)]
    SnapshotItemConsumerProjection[] Cards,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.ColorlessOutput)]
    SnapshotItemConsumerProjection[] Colorless,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.RelicsOutput)]
    SnapshotItemConsumerProjection[] Relics,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.RewardsOutput)]
    SnapshotItemConsumerProjection[] Rewards,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.AlternativesOutput)]
    SnapshotItemConsumerProjection[] Alternatives,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.BundlesOutput)]
    SnapshotItemConsumerProjection[] Bundles,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.CellsOutput)]
    SnapshotItemConsumerProjection[] Cells,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.YouOutput)]
    SnapshotCombatantConsumerProjection? You,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.EnemiesOutput)]
    SnapshotEnemyConsumerProjection[] Enemies,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.PlayerOutput)]
    SnapshotPlayerConsumerProjection? Player,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.CardRemovalOutput)]
    SnapshotItemConsumerProjection? CardRemoval,
    [property: JsonPropertyName(SnapshotConsumerSchema.Top.LegalOutput)]
    string[] Legal);

internal sealed record SnapshotItemConsumerProjection(
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.IndexOutput)]
    int? Index,
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.IdOutput)]
    string? Id,
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.ModelOutput)]
    string? Model,
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.SelectorOutput)]
    string? Selector,
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.SlotOutput)]
    int? Slot,
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.TargetOutput)]
    string? Target,
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.ColOutput)]
    int? Col,
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.RowOutput)]
    int? Row,
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.TypeOutput)]
    string? Type,
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.SemanticStateOutput)]
    string[]? SemanticState,
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.SelectedOutput)]
    bool? Selected,
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.PlayableOutput)]
    bool? Playable,
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.LockedOutput)]
    bool? Locked,
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.ChosenOutput)]
    bool? Chosen,
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.EnabledOutput)]
    bool? Enabled,
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.PurchasableOutput)]
    bool? Purchasable,
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.HiddenOutput)]
    bool? Hidden,
    [property: JsonPropertyName(SnapshotConsumerSchema.Item.CardsOutput)]
    SnapshotItemConsumerProjection[] Cards);

internal sealed record SnapshotPlayerConsumerProjection(
    [property: JsonPropertyName(SnapshotConsumerSchema.Player.HpOutput)]
    int[]? Hp,
    [property: JsonPropertyName(SnapshotConsumerSchema.Player.GoldOutput)]
    int? Gold,
    [property: JsonPropertyName(SnapshotConsumerSchema.Player.SemanticStateOutput)]
    string[]? SemanticState,
    [property: JsonPropertyName(SnapshotConsumerSchema.Player.PotionsOutput)]
    SnapshotItemConsumerProjection[] Potions);

internal sealed record SnapshotEnemyConsumerProjection(
    [property: JsonPropertyName(SnapshotConsumerSchema.Enemy.IdOutput)]
    uint? Id,
    [property: JsonPropertyName(SnapshotConsumerSchema.Enemy.ModelOutput)]
    string? Model,
    [property: JsonPropertyName(SnapshotConsumerSchema.Enemy.HpOutput)]
    int[] Hp,
    [property: JsonPropertyName(SnapshotConsumerSchema.Enemy.BlockOutput)]
    int? Block,
    [property: JsonPropertyName(SnapshotConsumerSchema.Enemy.AliveOutput)]
    bool? Alive,
    [property: JsonPropertyName(SnapshotConsumerSchema.Enemy.SemanticStateOutput)]
    string[]? SemanticState);

internal sealed record SnapshotCombatantConsumerProjection(
    [property: JsonPropertyName(SnapshotConsumerSchema.Combatant.HpOutput)]
    int[] Hp,
    [property: JsonPropertyName(SnapshotConsumerSchema.Combatant.BlockOutput)]
    int? Block,
    [property: JsonPropertyName(SnapshotConsumerSchema.Combatant.EnergyOutput)]
    int[] Energy,
    [property: JsonPropertyName(SnapshotConsumerSchema.Combatant.StarsOutput)]
    int? Stars,
    [property: JsonPropertyName(SnapshotConsumerSchema.Combatant.SemanticStateOutput)]
    string[]? SemanticState);

internal sealed class SnapshotCombatantContract
{
    private static readonly HashSet<string> ConsumedKeys = new(
        SnapshotConsumerSchema.Combatant.Fields.Select(field => field.Wire),
        StringComparer.Ordinal);
    private readonly JsonObject _wire;

    public SnapshotCombatantContract()
        : this(new JsonObject())
    {
    }

    internal SnapshotCombatantContract(JsonObject wire)
    {
        _wire = wire;
    }

    public int[] Hp
    {
        get => IntArray(SnapshotConsumerSchema.Combatant.HpWire);
        set => SetIntArray(SnapshotConsumerSchema.Combatant.HpWire, value);
    }

    public int? Block
    {
        get => Scalar<int>(SnapshotConsumerSchema.Combatant.BlockWire);
        set => SetScalar(SnapshotConsumerSchema.Combatant.BlockWire, value);
    }

    public int[] Energy
    {
        get => IntArray(SnapshotConsumerSchema.Combatant.EnergyWire);
        set => SetIntArray(SnapshotConsumerSchema.Combatant.EnergyWire, value);
    }

    public int? Stars
    {
        get => Scalar<int>(SnapshotConsumerSchema.Combatant.StarsWire);
        set => SetScalar(SnapshotConsumerSchema.Combatant.StarsWire, value);
    }

    public string[]? SemanticState
    {
        get => StringArrayOrNull(SnapshotConsumerSchema.Combatant.SemanticStateWire);
        set => SetStringArrayOrNull(SnapshotConsumerSchema.Combatant.SemanticStateWire, value);
    }

    internal void AddExtensions(object extension)
    {
        var fields = JsonSerializer.SerializeToNode(extension)?.AsObject()
            ?? throw new InvalidOperationException("snapshot combatant extension returned null");
        foreach (var (property, value) in fields)
        {
            if (ConsumedKeys.Contains(property))
                throw new InvalidOperationException(
                    $"snapshot producer must set consumed combatant '{property}' through SnapshotCombatantContract");
            _wire[property] = value?.DeepClone();
        }
    }

    internal JsonObject ToJsonObject() => (JsonObject)_wire.DeepClone();

    internal SnapshotCombatantConsumerProjection ConsumerProjection() =>
        new(Hp, Block, Energy, Stars, SemanticState);

    private int[] IntArray(string property) =>
        _wire[property] is JsonArray values
            ? values.Select(value => value?.GetValue<int>() ?? 0).ToArray()
            : [];

    private T? Scalar<T>(string property) where T : struct =>
        _wire[property] is JsonValue value && value.TryGetValue<T>(out var result)
            ? result
            : null;

    private string[]? StringArrayOrNull(string property) =>
        _wire[property] is JsonArray values
            ? values.Select(value => value?.GetValue<string>())
                .Where(value => value is not null)
                .Cast<string>()
                .ToArray()
            : null;

    private void SetIntArray(string property, IEnumerable<int> values) =>
        _wire[property] = new JsonArray(
            values.Select(value => (JsonNode)value).ToArray());

    private void SetStringArrayOrNull(string property, IEnumerable<string>? values)
    {
        if (values is null) _wire.Remove(property);
        else _wire[property] = new JsonArray(
            values.Select(value => (JsonNode)value).ToArray());
    }

    private void SetScalar<T>(string property, T? value) where T : struct
    {
        if (value is null) _wire.Remove(property);
        else _wire[property] = JsonValue.Create(value.Value);
    }
}

internal sealed class SnapshotEnemyContract
{
    private static readonly HashSet<string> ConsumedKeys = new(
        SnapshotConsumerSchema.Enemy.Fields.Select(field => field.Wire),
        StringComparer.Ordinal);
    private readonly JsonObject _wire;

    public SnapshotEnemyContract()
        : this(new JsonObject())
    {
    }

    internal SnapshotEnemyContract(JsonObject wire)
    {
        _wire = wire;
    }

    public uint? Id
    {
        get => Scalar<uint>(SnapshotConsumerSchema.Enemy.IdWire);
        set => SetScalar(SnapshotConsumerSchema.Enemy.IdWire, value);
    }

    public string? Model
    {
        get => String(SnapshotConsumerSchema.Enemy.ModelWire);
        set => SetString(SnapshotConsumerSchema.Enemy.ModelWire, value);
    }

    public int[] Hp
    {
        get => _wire[SnapshotConsumerSchema.Enemy.HpWire] is JsonArray hp
            ? hp.Select(value => value?.GetValue<int>() ?? 0).ToArray()
            : [];
        set => _wire[SnapshotConsumerSchema.Enemy.HpWire] = new JsonArray(
            value.Select(item => (JsonNode)item).ToArray());
    }

    public bool? Alive
    {
        get => Scalar<bool>(SnapshotConsumerSchema.Enemy.AliveWire);
        set => SetScalar(SnapshotConsumerSchema.Enemy.AliveWire, value);
    }

    public int? Block
    {
        get => Scalar<int>(SnapshotConsumerSchema.Enemy.BlockWire);
        set => SetScalar(SnapshotConsumerSchema.Enemy.BlockWire, value);
    }

    public string[]? SemanticState
    {
        get => StringArrayOrNull(SnapshotConsumerSchema.Enemy.SemanticStateWire);
        set => SetStringArrayOrNull(SnapshotConsumerSchema.Enemy.SemanticStateWire, value);
    }

    internal void AddExtensions(object extension)
    {
        var fields = JsonSerializer.SerializeToNode(extension)?.AsObject()
            ?? throw new InvalidOperationException("snapshot enemy extension returned null");
        foreach (var (property, value) in fields)
        {
            if (ConsumedKeys.Contains(property))
                throw new InvalidOperationException(
                    $"snapshot producer must set consumed enemy '{property}' through SnapshotEnemyContract");
            _wire[property] = value?.DeepClone();
        }
    }

    internal JsonObject ToJsonObject() => (JsonObject)_wire.DeepClone();

    internal SnapshotEnemyConsumerProjection ConsumerProjection() =>
        new(Id, Model, Hp, Block, Alive, SemanticState);

    private string? String(string property) =>
        _wire[property] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;

    private T? Scalar<T>(string property) where T : struct =>
        _wire[property] is JsonValue value && value.TryGetValue<T>(out var result)
            ? result
            : null;

    private string[]? StringArrayOrNull(string property) =>
        _wire[property] is JsonArray values
            ? values.Select(value => value?.GetValue<string>())
                .Where(value => value is not null)
                .Cast<string>()
                .ToArray()
            : null;

    private void SetString(string property, string? value)
    {
        if (value is null) _wire.Remove(property);
        else _wire[property] = value;
    }

    private void SetStringArrayOrNull(string property, IEnumerable<string>? values)
    {
        if (values is null) _wire.Remove(property);
        else _wire[property] = new JsonArray(
            values.Select(value => (JsonNode)value).ToArray());
    }

    private void SetScalar<T>(string property, T? value) where T : struct
    {
        if (value is null) _wire.Remove(property);
        else _wire[property] = JsonValue.Create(value.Value);
    }
}

internal sealed class SnapshotItemContract
{
    private static readonly HashSet<string> ConsumedKeys = new(
        SnapshotConsumerSchema.Item.Fields.Select(field => field.Wire),
        StringComparer.Ordinal);
    private readonly JsonObject _wire;

    public SnapshotItemContract()
        : this(new JsonObject())
    {
    }

    internal SnapshotItemContract(JsonObject wire)
    {
        _wire = wire;
    }

    internal void AddExtensions(object extension)
    {
        var fields = JsonSerializer.SerializeToNode(extension)?.AsObject()
            ?? throw new InvalidOperationException("snapshot item extension returned null");
        foreach (var (property, value) in fields)
        {
            if (ConsumedKeys.Contains(property))
                throw new InvalidOperationException(
                    $"snapshot producer must set consumed item '{property}' through SnapshotItemContract");
            _wire[property] = value?.DeepClone();
        }
    }

    public int? Index
    {
        get => Scalar<int>(SnapshotConsumerSchema.Item.IndexWire);
        set => SetScalar(SnapshotConsumerSchema.Item.IndexWire, value);
    }

    public string? Id
    {
        get => String(SnapshotConsumerSchema.Item.IdWire);
        set => SetString(SnapshotConsumerSchema.Item.IdWire, value);
    }

    public string? Model
    {
        get => String(SnapshotConsumerSchema.Item.ModelWire);
        set => SetString(SnapshotConsumerSchema.Item.ModelWire, value);
    }

    public string? Selector
    {
        get => String(SnapshotConsumerSchema.Item.SelectorWire);
        set => SetString(SnapshotConsumerSchema.Item.SelectorWire, value);
    }

    public int? Slot
    {
        get => Scalar<int>(SnapshotConsumerSchema.Item.SlotWire);
        set => SetScalar(SnapshotConsumerSchema.Item.SlotWire, value);
    }

    public string? Target
    {
        get => String(SnapshotConsumerSchema.Item.TargetWire);
        set => SetString(SnapshotConsumerSchema.Item.TargetWire, value);
    }

    public int? Col
    {
        get => Scalar<int>(SnapshotConsumerSchema.Item.ColWire);
        set => SetScalar(SnapshotConsumerSchema.Item.ColWire, value);
    }

    public int? Row
    {
        get => Scalar<int>(SnapshotConsumerSchema.Item.RowWire);
        set => SetScalar(SnapshotConsumerSchema.Item.RowWire, value);
    }

    public string? Type
    {
        get => String(SnapshotConsumerSchema.Item.TypeWire);
        set => SetString(SnapshotConsumerSchema.Item.TypeWire, value);
    }

    public string[]? SemanticState
    {
        get => StringArrayOrNull(SnapshotConsumerSchema.Item.SemanticStateWire);
        set => SetStringArrayOrNull(SnapshotConsumerSchema.Item.SemanticStateWire, value);
    }

    public bool? Selected
    {
        get => Scalar<bool>(SnapshotConsumerSchema.Item.SelectedWire);
        set => SetScalar(SnapshotConsumerSchema.Item.SelectedWire, value);
    }

    public bool? Playable
    {
        get => Scalar<bool>(SnapshotConsumerSchema.Item.PlayableWire);
        set => SetScalar(SnapshotConsumerSchema.Item.PlayableWire, value);
    }

    public bool? Locked
    {
        get => Scalar<bool>(SnapshotConsumerSchema.Item.LockedWire);
        set => SetScalar(SnapshotConsumerSchema.Item.LockedWire, value);
    }

    public bool? Chosen
    {
        get => Scalar<bool>(SnapshotConsumerSchema.Item.ChosenWire);
        set => SetScalar(SnapshotConsumerSchema.Item.ChosenWire, value);
    }

    public bool? Enabled
    {
        get => Scalar<bool>(SnapshotConsumerSchema.Item.EnabledWire);
        set => SetScalar(SnapshotConsumerSchema.Item.EnabledWire, value);
    }

    public bool? Purchasable
    {
        get => Scalar<bool>(SnapshotConsumerSchema.Item.PurchasableWire);
        set => SetScalar(SnapshotConsumerSchema.Item.PurchasableWire, value);
    }

    public bool? Hidden
    {
        get => Scalar<bool>(SnapshotConsumerSchema.Item.HiddenWire);
        set => SetScalar(SnapshotConsumerSchema.Item.HiddenWire, value);
    }

    public SnapshotItemContract[] Cards
    {
        get => _wire[SnapshotConsumerSchema.Item.CardsWire] is JsonArray cards
            ? cards.OfType<JsonObject>()
                .Select(item => new SnapshotItemContract(item)).ToArray()
            : [];
        set => _wire[SnapshotConsumerSchema.Item.CardsWire] = new JsonArray(
            value.Select(item => (JsonNode)item.ToJsonObject()).ToArray());
    }

    internal JsonObject ToJsonObject() => (JsonObject)_wire.DeepClone();

    internal SnapshotItemConsumerProjection ConsumerProjection() => new(
        Index, Id, Model, Selector, Slot, Target, Col, Row, Type,
        SemanticState, Selected,
        Playable, Locked, Chosen, Enabled, Purchasable, Hidden,
        Cards.Select(item => item.ConsumerProjection()).ToArray());

    private string? String(string property) =>
        _wire[property] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;

    private T? Scalar<T>(string property) where T : struct =>
        _wire[property] is JsonValue value && value.TryGetValue<T>(out var result)
            ? result
            : null;

    private string[]? StringArrayOrNull(string property) =>
        _wire[property] is JsonArray values
            ? values.Select(value => value?.GetValue<string>())
                .Where(value => value is not null)
                .Cast<string>()
                .ToArray()
            : null;

    private void SetScalar<T>(string property, T? value) where T : struct
    {
        if (value is null) _wire.Remove(property);
        else _wire[property] = JsonValue.Create(value.Value);
    }

    private void SetString(string property, string? value)
    {
        if (value is null) _wire.Remove(property);
        else _wire[property] = value;
    }

    private void SetStringArrayOrNull(string property, IEnumerable<string>? values)
    {
        if (values is null) _wire.Remove(property);
        else _wire[property] = new JsonArray(
            values.Select(value => (JsonNode)value).ToArray());
    }
}

internal sealed class SnapshotPlayerContract
{
    private static readonly HashSet<string> ConsumedKeys = new(
        SnapshotConsumerSchema.Player.Fields.Select(field => field.Wire),
        StringComparer.Ordinal);
    private readonly JsonObject _wire;

    public SnapshotPlayerContract()
        : this(new JsonObject())
    {
    }

    internal SnapshotPlayerContract(JsonObject wire)
    {
        _wire = wire;
    }

    internal void AddExtensions(object extension)
    {
        var fields = JsonSerializer.SerializeToNode(extension)?.AsObject()
            ?? throw new InvalidOperationException("snapshot player extension returned null");
        foreach (var (property, value) in fields)
        {
            if (ConsumedKeys.Contains(property))
                throw new InvalidOperationException(
                    $"snapshot producer must set consumed player '{property}' through SnapshotPlayerContract");
            _wire[property] = value?.DeepClone();
        }
    }

    public int[]? Hp
    {
        get => _wire[SnapshotConsumerSchema.Player.HpWire] is JsonArray hp
            ? hp.Select(value => value?.GetValue<int>() ?? 0).ToArray()
            : null;
        set => _wire[SnapshotConsumerSchema.Player.HpWire] = value is null
            ? null
            : new JsonArray(value.Select(item => (JsonNode)item).ToArray());
    }

    public int? Gold
    {
        get => Scalar<int>(SnapshotConsumerSchema.Player.GoldWire);
        set => SetScalar(SnapshotConsumerSchema.Player.GoldWire, value);
    }

    public string[]? SemanticState
    {
        get => StringArrayOrNull(SnapshotConsumerSchema.Player.SemanticStateWire);
        set => SetStringArrayOrNull(SnapshotConsumerSchema.Player.SemanticStateWire, value);
    }

    public SnapshotItemContract[] Potions
    {
        get => _wire[SnapshotConsumerSchema.Player.PotionsWire] is JsonArray potions
            ? potions.OfType<JsonObject>()
                .Select(item => new SnapshotItemContract(item)).ToArray()
            : [];
        set => _wire[SnapshotConsumerSchema.Player.PotionsWire] = new JsonArray(
            value.Select(item => (JsonNode)item.ToJsonObject()).ToArray());
    }

    internal JsonObject ToJsonObject() => (JsonObject)_wire.DeepClone();

    internal SnapshotPlayerConsumerProjection ConsumerProjection() => new(
        Hp,
        Gold,
        SemanticState,
        Potions.Select(item => item.ConsumerProjection()).ToArray());

    private T? Scalar<T>(string property) where T : struct =>
        _wire[property] is JsonValue value && value.TryGetValue<T>(out var result)
            ? result
            : null;

    private string[]? StringArrayOrNull(string property) =>
        _wire[property] is JsonArray values
            ? values.Select(value => value?.GetValue<string>())
                .Where(value => value is not null)
                .Cast<string>()
                .ToArray()
            : null;

    private void SetScalar<T>(string property, T? value) where T : struct
    {
        if (value is null) _wire.Remove(property);
        else _wire[property] = JsonValue.Create(value.Value);
    }

    private void SetStringArrayOrNull(string property, IEnumerable<string>? values)
    {
        if (values is null) _wire.Remove(property);
        else _wire[property] = new JsonArray(
            values.Select(value => (JsonNode)value).ToArray());
    }
}
