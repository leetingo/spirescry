namespace Spirescry.State
{
    public enum Phase
    {
        MainMenu, Map, Combat, Event, Shop, RestSite, Treasure, Rewards,
        CardReward, RelicReward, CardSelect, HandSelect, BundleSelect,
        CrystalSphere, GameOver, Overlay, Unknown,
    }

    // The typed result of following one accepted bridge verb to a boundary.
    // Wire spellings live in ProtocolVocabulary.SettlementOutcomes below so
    // every generated consumer derives the same four-value vocabulary.
    public enum SettlementOutcome
    {
        Settled,
        NextDecision,
        Fault,
        Timeout,
    }
}

namespace Spirescry
{
using System.Text.Json;
using System.Text.Json.Serialization;
using State;

public enum ProtocolArgumentType
{
    Boolean,
    Integer,
    String,
}

public sealed record ProtocolArgument(
    string Name,
    ProtocolArgumentType Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool Optional = false);

public sealed record CheatArgumentShape(
    string Name,
    IReadOnlyList<ProtocolArgument> Arguments);

public enum ConsumerProjectionFieldKind
{
    RequiredString,
    OptionalString,
    OptionalNumber,
    RequiredIntArray,
    OptionalIntArray,
    OptionalStringArray,
    OptionalBoolean,
    PresenceBoolean,
    ItemArray,
    OptionalItem,
    OptionalCombatant,
    EnemyArray,
    OptionalPlayer,
    RequiredStringArray,
}

public sealed record ConsumerProjectionField(
    string Symbol,
    string Wire,
    string Output,
    ConsumerProjectionFieldKind Kind);

public sealed record ConsumerProjectionSchema(
    IReadOnlyList<ConsumerProjectionField> Top,
    IReadOnlyList<ConsumerProjectionField> Item,
    IReadOnlyList<ConsumerProjectionField> Combatant,
    IReadOnlyList<ConsumerProjectionField> Enemy,
    IReadOnlyList<ConsumerProjectionField> Player);

// The protocol's shared wire vocabulary. Compatibility facades keep the
// existing mod API stable; protocol.json is a deterministic projection of
// this module for consumers to adopt in the contract step.
public static class ProtocolVocabulary
{
    private sealed record FaultEventTokens(
        string EngineError,
        string AsyncFault,
        string EngineNote);

    private sealed record ArtifactDocument(
        int ProtocolVersion,
        IReadOnlyList<string> RejectionCodes,
        IReadOnlyList<string> Phases,
        IReadOnlyList<string> SettlementOutcomes,
        FaultEventTokens FaultEventTokens,
        IReadOnlyList<CheatArgumentShape> CheatArgumentShapes,
        ConsumerProjectionSchema ConsumerProjection);

    private static readonly JsonSerializerOptions ArtifactJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // v4 keeps the typed semanticState projection internal by default and
    // requires replay/diagnostic callers to request its expanded wire form.
    // A v3 CLI would hash the bounded response without those tokens and
    // report a false divergence, so compatibility must fail first.
    public const int ProtocolVersion = 4;

    public static string CreateArtifactJson() =>
        JsonSerializer.Serialize(new ArtifactDocument(
            ProtocolVersion,
            Rejections.All,
            Phases.All,
            SettlementOutcomes.All,
            new FaultEventTokens(
                FaultEvents.EngineError,
                FaultEvents.AsyncFault,
                FaultEvents.EngineNote),
            Cheats.All,
            SnapshotConsumerSchema.Artifact), ArtifactJsonOptions) + "\n";

    public static class Rejections
    {
        public const string BadRequest = "bad_request";
        public const string BadPhase = "bad_phase";
        public const string BadIndex = "bad_index";
        public const string BadTarget = "bad_target";
        public const string BadState = "bad_state";
        public const string NotReady = "not_ready";
        public const string NotPlayable = "not_playable";
        public const string NotEnoughGold = "not_enough_gold";
        public const string NotEnoughEnergy = "not_enough_energy";
        public const string NotEnoughStars = "not_enough_stars";
        public const string RunExists = "run_exists";
        public const string StaleState = "stale_state";
        public const string ExternalChange = "external_change";
        public const string ResolutionPartial = "resolution_partial";
        public const string ResolutionFailed = "resolution_failed";
        public const string NotFound = "not_found";
        public const string Internal = "internal";

        public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
        [
            BadRequest, BadPhase, BadIndex, BadTarget, BadState, NotReady,
            NotPlayable, NotEnoughGold, NotEnoughEnergy, NotEnoughStars,
            RunExists, StaleState, ExternalChange, ResolutionPartial,
            ResolutionFailed, NotFound, Internal,
        ]);
    }

    public static class Phases
    {
        public const string MainMenu = "main_menu";
        public const string Map = "map";
        public const string Combat = "combat";
        public const string Event = "event";
        public const string Shop = "shop";
        public const string RestSite = "rest_site";
        public const string Treasure = "treasure";
        public const string Rewards = "rewards";
        public const string CardReward = "card_reward";
        public const string RelicReward = "relic_reward";
        public const string CardSelect = "card_select";
        public const string HandSelect = "hand_select";
        public const string BundleSelect = "bundle_select";
        public const string CrystalSphere = "crystal_sphere";
        public const string GameOver = "game_over";
        public const string Overlay = "overlay";
        public const string Unknown = "unknown";

        public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
        [
            MainMenu, Map, Combat, Event, Shop, RestSite, Treasure, Rewards,
            CardReward, RelicReward, CardSelect, HandSelect, BundleSelect,
            CrystalSphere, GameOver, Overlay, Unknown,
        ]);

        public static string Name(Phase phase) => phase switch
        {
            Phase.MainMenu => MainMenu,
            Phase.Map => Map,
            Phase.Combat => Combat,
            Phase.Event => Event,
            Phase.Shop => Shop,
            Phase.RestSite => RestSite,
            Phase.Treasure => Treasure,
            Phase.Rewards => Rewards,
            Phase.CardReward => CardReward,
            Phase.RelicReward => RelicReward,
            Phase.CardSelect => CardSelect,
            Phase.HandSelect => HandSelect,
            Phase.BundleSelect => BundleSelect,
            Phase.CrystalSphere => CrystalSphere,
            Phase.GameOver => GameOver,
            Phase.Overlay => Overlay,
            _ => Unknown,
        };
    }

    public static class SettlementOutcomes
    {
        public const string Settled = "settled";
        public const string NextDecision = "next_decision";
        public const string Fault = "fault";
        public const string Timeout = "timeout";

        public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
            [Settled, NextDecision, Fault, Timeout]);

        public static string Name(SettlementOutcome outcome) => outcome switch
        {
            SettlementOutcome.Settled => Settled,
            SettlementOutcome.NextDecision => NextDecision,
            SettlementOutcome.Fault => Fault,
            SettlementOutcome.Timeout => Timeout,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };
    }

    public static class FaultEvents
    {
        public const string EngineError = "engine_error:";
        public const string AsyncFault = "async_fault:";
        public const string EngineNote = "engine_note:";

        public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
            [EngineError, AsyncFault, EngineNote]);
    }

    public static class Cheats
    {
        private static readonly IReadOnlyList<ProtocolArgument> NoArguments =
            Array.AsReadOnly<ProtocolArgument>([]);

        public static IReadOnlyList<CheatArgumentShape> All { get; } =
            Array.AsReadOnly(
            [
                Shape("goto", new ProtocolArgument("col", ProtocolArgumentType.Integer),
                    new ProtocolArgument("row", ProtocolArgumentType.Integer)),
                Shape("gold", new ProtocolArgument("value", ProtocolArgumentType.Integer)),
                Shape("heal"),
                Shape("hp", new ProtocolArgument("value", ProtocolArgumentType.Integer)),
                Shape("wound-enemies"),
                Shape("event", new ProtocolArgument("id", ProtocolArgumentType.String)),
                Shape("combat", new ProtocolArgument("id", ProtocolArgumentType.String)),
                Shape("card",
                    new ProtocolArgument("id", ProtocolArgumentType.String),
                    new ProtocolArgument(
                        "upgraded", ProtocolArgumentType.Boolean, Optional: true)),
                Shape("card-upgraded", new ProtocolArgument("id", ProtocolArgumentType.String)),
                Shape("relic", new ProtocolArgument("id", ProtocolArgumentType.String)),
                Shape("potion", new ProtocolArgument("id", ProtocolArgumentType.String)),
                Shape("stars", new ProtocolArgument("value", ProtocolArgumentType.Integer)),
                Shape("energy", new ProtocolArgument("value", ProtocolArgumentType.Integer)),
                Shape("async-fault"),
                Shape("engine-error"),
                Shape("engine-error-delayed"),
                Shape("observation-fault"),
                Shape("event-fault-delayed"),
                Shape("event-fault-late"),
                Shape("event-complete-late"),
                Shape("event-orphan"),
                Shape("event-orphan-fault"),
                Shape("event-orphan-collision"),
                Shape("event-owner-rotate"),
            ]);

        private static CheatArgumentShape Shape(
            string name, params ProtocolArgument[] arguments) =>
            new(name, arguments.Length == 0
                ? NoArguments
                : Array.AsReadOnly(arguments));
    }
}
}
