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
        "phase", "rev", "runId", "side", "actionsDisabled", "available",
        "proceedAvailable", "chestOpened", "confirmable", "cancelable",
        "next", "hand", "potions", "options", "cards", "colorless",
        "relics", "rewards", "alternatives", "bundles", "cells", "player",
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
    SnapshotPlayerConsumerProjection? Player,
    SnapshotItemConsumerProjection? CardRemoval,
    string[] Legal);

internal sealed record SnapshotItemConsumerProjection(
    int? Index,
    bool? Playable,
    bool? Locked,
    bool? Chosen,
    bool? Enabled,
    bool? Purchasable,
    bool? Hidden,
    SnapshotItemConsumerProjection[] Cards);

internal sealed record SnapshotPlayerConsumerProjection(
    SnapshotItemConsumerProjection[] Potions);

internal sealed class SnapshotItemContract
{
    private static readonly HashSet<string> ConsumedKeys = new(StringComparer.Ordinal)
    {
        "idx", "playable", "locked", "chosen", "enabled", "purchasable", "hidden",
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
        Index, Playable, Locked, Chosen, Enabled, Purchasable, Hidden,
        Cards.Select(item => item.ConsumerProjection()).ToArray());

    private T? Scalar<T>(string property) where T : struct =>
        _wire[property] is JsonValue value && value.TryGetValue<T>(out var result)
            ? result
            : null;

    private void SetScalar<T>(string property, T? value) where T : struct
    {
        if (value is null) _wire.Remove(property);
        else _wire[property] = JsonValue.Create(value.Value);
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
            if (property == "potions")
                throw new InvalidOperationException(
                    "snapshot producer must set consumed player 'potions' through SnapshotPlayerContract");
            _wire[property] = value?.DeepClone();
        }
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
        Potions.Select(item => item.ConsumerProjection()).ToArray());
}
