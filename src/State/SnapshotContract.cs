using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Spirescry.State;

// The typed seam between snapshot production and the consumers that derive
// decisions, attribute run-log outcomes, and decide follow stability. The
// backing JSON retains every phase-specific field verbatim; consumed keys are
// exposed only here so producers, consumers, and fixtures compile against one
// vocabulary instead of repeating stringly-typed shapes.
internal sealed class SnapshotContract
{
    private static readonly JsonSerializerOptions ProjectionJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly HashSet<string> ConsumedKeys = new(StringComparer.Ordinal)
    {
        "phase", "rev", "runId", "id", "act", "current", "turn", "outcome", "hp", "gold",
        "semanticState", "selected",
        "side", "actionsDisabled", "available",
        "proceedAvailable", "chestOpened", "confirmable", "cancelable",
        "next", "hand", "potions", "options", "cards", "colorless",
        "relics", "rewards", "alternatives", "bundles", "cells", "you", "enemies", "player",
        "cardRemoval", "legal",
    };
    private readonly JsonObject _wire;

    internal SnapshotContract(Phase phase)
    {
        Phase = phase;
        _wire = new JsonObject
        {
            ["phase"] = ProtocolVocabulary.Phases.Name(phase),
        };
    }

    public Phase Phase { get; }

    public string PhaseName => ProtocolVocabulary.Phases.Name(Phase);

    public long? Revision
    {
        get => Scalar<long>("rev");
        set => SetScalar("rev", value);
    }

    public string? RunId
    {
        get => String("runId");
        set => SetString("runId", value);
    }

    public string? Id
    {
        get => String("id");
        set => SetString("id", value);
    }

    public int? Act
    {
        get => Scalar<int>("act");
        set => SetScalar("act", value);
    }

    public int[]? Current
    {
        get => _wire["current"] is JsonArray ? IntArray("current") : null;
        set
        {
            if (value is null) _wire["current"] = null;
            else SetIntArray("current", value);
        }
    }

    public int? Gold
    {
        get => Scalar<int>("gold");
        set => SetScalar("gold", value);
    }

    public string[]? SemanticState
    {
        get => StringArrayOrNull("semanticState");
        set => SetStringArrayOrNull("semanticState", value);
    }

    public string[]? Selected
    {
        get => StringArrayOrNull("selected");
        set => SetStringArrayOrNull("selected", value);
    }

    public int? Turn
    {
        get => Scalar<int>("turn");
        set => SetScalar("turn", value);
    }

    public string? Outcome
    {
        get => String("outcome");
        set => SetString("outcome", value);
    }

    public int[]? Hp
    {
        get => _wire["hp"] is JsonArray ? IntArray("hp") : null;
        set
        {
            if (value is null) _wire["hp"] = null;
            else SetIntArray("hp", value);
        }
    }

    public string? Side
    {
        get => String("side");
        set => SetString("side", value);
    }

    public bool? ActionsDisabled
    {
        get => Scalar<bool>("actionsDisabled");
        set => SetScalar("actionsDisabled", value);
    }

    public bool? Available
    {
        get => Scalar<bool>("available");
        set => SetScalar("available", value);
    }

    public bool? ProceedAvailable
    {
        get => Scalar<bool>("proceedAvailable");
        set => SetScalar("proceedAvailable", value);
    }

    public bool? ChestOpened
    {
        get => Scalar<bool>("chestOpened");
        set => SetScalar("chestOpened", value);
    }

    public bool? Confirmable
    {
        get => Scalar<bool>("confirmable");
        set => SetScalar("confirmable", value);
    }

    public bool? Cancelable
    {
        get => Scalar<bool>("cancelable");
        set => SetScalar("cancelable", value);
    }

    public SnapshotItemContract[] Next
    {
        get => Items("next");
        set => SetItems("next", value);
    }

    public SnapshotItemContract[] Hand
    {
        get => Items("hand");
        set => SetItems("hand", value);
    }

    public SnapshotItemContract[] Potions
    {
        get => Items("potions");
        set => SetItems("potions", value);
    }

    public bool HasTopLevelPotions => _wire["potions"] is JsonArray;

    public SnapshotItemContract[] Options
    {
        get => Items("options");
        set => SetItems("options", value);
    }

    public SnapshotItemContract[] Cards
    {
        get => Items("cards");
        set => SetItems("cards", value);
    }

    public SnapshotItemContract[] Colorless
    {
        get => Items("colorless");
        set => SetItems("colorless", value);
    }

    public SnapshotItemContract[] Relics
    {
        get => Items("relics");
        set => SetItems("relics", value);
    }

    public SnapshotItemContract[] Rewards
    {
        get => Items("rewards");
        set => SetItems("rewards", value);
    }

    public SnapshotItemContract[] Alternatives
    {
        get => Items("alternatives");
        set => SetItems("alternatives", value);
    }

    public SnapshotItemContract[] Bundles
    {
        get => Items("bundles");
        set => SetItems("bundles", value);
    }

    public SnapshotItemContract[] Cells
    {
        get => Items("cells");
        set => SetItems("cells", value);
    }

    public SnapshotEnemyContract[] Enemies
    {
        get => _wire["enemies"] is JsonArray enemies
            ? enemies.OfType<JsonObject>()
                .Select(enemy => new SnapshotEnemyContract(enemy)).ToArray()
            : [];
        set => _wire["enemies"] = new JsonArray(
            value.Select(enemy => (JsonNode)enemy.ToJsonObject()).ToArray());
    }

    public SnapshotCombatantContract? You
    {
        get => _wire["you"] is JsonObject combatant
            ? new SnapshotCombatantContract(combatant)
            : null;
        set => _wire["you"] = value?.ToJsonObject();
    }

    public SnapshotPlayerContract? Player
    {
        get => _wire["player"] is JsonObject player
            ? new SnapshotPlayerContract(player)
            : null;
        set => _wire["player"] = value?.ToJsonObject();
    }

    public SnapshotItemContract? CardRemoval
    {
        get => _wire["cardRemoval"] is JsonObject removal
            ? new SnapshotItemContract(removal)
            : null;
        set => _wire["cardRemoval"] = value?.ToJsonObject();
    }

    public string[] Legal
    {
        get => _wire["legal"] is JsonArray legal
            ? legal.Select(item => item?.GetValue<string>())
                .Where(item => item is not null)
                .Cast<string>()
                .ToArray()
            : [];
        set => _wire["legal"] = new JsonArray(
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
    string Phase,
    string? Id,
    int? Act,
    int[]? Current,
    int? Turn,
    string? Outcome,
    int[]? Hp,
    int? Gold,
    string[]? SemanticState,
    string[]? Selected,
    string? Side,
    bool? ActionsDisabled,
    bool? Available,
    bool? ProceedAvailable,
    bool? ChestOpened,
    bool? Confirmable,
    bool? Cancelable,
    bool HasTopLevelPotions,
    SnapshotItemConsumerProjection[] Next,
    SnapshotItemConsumerProjection[] Hand,
    SnapshotItemConsumerProjection[] Potions,
    SnapshotItemConsumerProjection[] Options,
    SnapshotItemConsumerProjection[] Cards,
    SnapshotItemConsumerProjection[] Colorless,
    SnapshotItemConsumerProjection[] Relics,
    SnapshotItemConsumerProjection[] Rewards,
    SnapshotItemConsumerProjection[] Alternatives,
    SnapshotItemConsumerProjection[] Bundles,
    SnapshotItemConsumerProjection[] Cells,
    SnapshotCombatantConsumerProjection? You,
    SnapshotEnemyConsumerProjection[] Enemies,
    SnapshotPlayerConsumerProjection? Player,
    SnapshotItemConsumerProjection? CardRemoval,
    string[] Legal);

internal sealed record SnapshotItemConsumerProjection(
    int? Index,
    string? Id,
    string? Model,
    string? Selector,
    int? Slot,
    string? Target,
    int? Col,
    int? Row,
    string? Type,
    string[]? SemanticState,
    bool? Selected,
    bool? Playable,
    bool? Locked,
    bool? Chosen,
    bool? Enabled,
    bool? Purchasable,
    bool? Hidden,
    SnapshotItemConsumerProjection[] Cards);

internal sealed record SnapshotPlayerConsumerProjection(
    int[]? Hp,
    int? Gold,
    string[]? SemanticState,
    SnapshotItemConsumerProjection[] Potions);

internal sealed record SnapshotEnemyConsumerProjection(
    uint? Id,
    string? Model,
    int[] Hp,
    int? Block,
    bool? Alive,
    string[]? SemanticState);

internal sealed record SnapshotCombatantConsumerProjection(
    int[] Hp,
    int? Block,
    int[] Energy,
    int? Stars,
    string[]? SemanticState);

internal sealed class SnapshotCombatantContract
{
    private static readonly HashSet<string> ConsumedKeys = new(StringComparer.Ordinal)
    {
        "hp", "block", "energy", "stars", "semanticState",
    };
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
        get => IntArray("hp");
        set => SetIntArray("hp", value);
    }

    public int? Block
    {
        get => Scalar<int>("block");
        set => SetScalar("block", value);
    }

    public int[] Energy
    {
        get => IntArray("energy");
        set => SetIntArray("energy", value);
    }

    public int? Stars
    {
        get => Scalar<int>("stars");
        set => SetScalar("stars", value);
    }

    public string[]? SemanticState
    {
        get => StringArrayOrNull("semanticState");
        set => SetStringArrayOrNull("semanticState", value);
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
    private static readonly HashSet<string> ConsumedKeys = new(StringComparer.Ordinal)
    {
        "id", "model", "hp", "block", "alive", "semanticState",
    };
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
        get => Scalar<uint>("id");
        set => SetScalar("id", value);
    }

    public string? Model
    {
        get => String("model");
        set => SetString("model", value);
    }

    public int[] Hp
    {
        get => _wire["hp"] is JsonArray hp
            ? hp.Select(value => value?.GetValue<int>() ?? 0).ToArray()
            : [];
        set => _wire["hp"] = new JsonArray(
            value.Select(item => (JsonNode)item).ToArray());
    }

    public bool? Alive
    {
        get => Scalar<bool>("alive");
        set => SetScalar("alive", value);
    }

    public int? Block
    {
        get => Scalar<int>("block");
        set => SetScalar("block", value);
    }

    public string[]? SemanticState
    {
        get => StringArrayOrNull("semanticState");
        set => SetStringArrayOrNull("semanticState", value);
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
    private static readonly HashSet<string> ConsumedKeys = new(StringComparer.Ordinal)
    {
        "idx", "id", "model", "selector", "slot", "target", "col", "row", "type",
        "semanticState", "selected", "playable", "locked", "chosen", "enabled", "purchasable", "hidden",
        "cards",
    };
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
        get => Scalar<int>("idx");
        set => SetScalar("idx", value);
    }

    public string? Id
    {
        get => String("id");
        set => SetString("id", value);
    }

    public string? Model
    {
        get => String("model");
        set => SetString("model", value);
    }

    public string? Selector
    {
        get => String("selector");
        set => SetString("selector", value);
    }

    public int? Slot
    {
        get => Scalar<int>("slot");
        set => SetScalar("slot", value);
    }

    public string? Target
    {
        get => String("target");
        set => SetString("target", value);
    }

    public int? Col
    {
        get => Scalar<int>("col");
        set => SetScalar("col", value);
    }

    public int? Row
    {
        get => Scalar<int>("row");
        set => SetScalar("row", value);
    }

    public string? Type
    {
        get => String("type");
        set => SetString("type", value);
    }

    public string[]? SemanticState
    {
        get => StringArrayOrNull("semanticState");
        set => SetStringArrayOrNull("semanticState", value);
    }

    public bool? Selected
    {
        get => Scalar<bool>("selected");
        set => SetScalar("selected", value);
    }

    public bool? Playable
    {
        get => Scalar<bool>("playable");
        set => SetScalar("playable", value);
    }

    public bool? Locked
    {
        get => Scalar<bool>("locked");
        set => SetScalar("locked", value);
    }

    public bool? Chosen
    {
        get => Scalar<bool>("chosen");
        set => SetScalar("chosen", value);
    }

    public bool? Enabled
    {
        get => Scalar<bool>("enabled");
        set => SetScalar("enabled", value);
    }

    public bool? Purchasable
    {
        get => Scalar<bool>("purchasable");
        set => SetScalar("purchasable", value);
    }

    public bool? Hidden
    {
        get => Scalar<bool>("hidden");
        set => SetScalar("hidden", value);
    }

    public SnapshotItemContract[] Cards
    {
        get => _wire["cards"] is JsonArray cards
            ? cards.OfType<JsonObject>()
                .Select(item => new SnapshotItemContract(item)).ToArray()
            : [];
        set => _wire["cards"] = new JsonArray(
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
            if (property is "hp" or "gold" or "potions" or "semanticState")
                throw new InvalidOperationException(
                    $"snapshot producer must set consumed player '{property}' through SnapshotPlayerContract");
            _wire[property] = value?.DeepClone();
        }
    }

    public int[]? Hp
    {
        get => _wire["hp"] is JsonArray hp
            ? hp.Select(value => value?.GetValue<int>() ?? 0).ToArray()
            : null;
        set => _wire["hp"] = value is null
            ? null
            : new JsonArray(value.Select(item => (JsonNode)item).ToArray());
    }

    public int? Gold
    {
        get => Scalar<int>("gold");
        set => SetScalar("gold", value);
    }

    public string[]? SemanticState
    {
        get => StringArrayOrNull("semanticState");
        set => SetStringArrayOrNull("semanticState", value);
    }

    public SnapshotItemContract[] Potions
    {
        get => _wire["potions"] is JsonArray potions
            ? potions.OfType<JsonObject>()
                .Select(item => new SnapshotItemContract(item)).ToArray()
            : [];
        set => _wire["potions"] = new JsonArray(
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
