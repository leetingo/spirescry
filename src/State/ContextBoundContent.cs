namespace Spirescry.State;

// Where a construction-context value comes from. Both members are resolved
// against engine types by the cheat surface; naming them keeps the table
// itself free of the game's dlls, so the unit tests CI *does* run cover it.
internal enum ConstructionValue
{
    // The owning player's own character model id — a CharacterModel.Id.
    OwnerCharacterId,

    // A named member of the target property's own enum type.
    EnumMember,
}

// One saved property an event or reward factory would have stamped, and the
// value to stamp it with. `Member` is only read for EnumMember.
internal readonly record struct ConstructionContext(
    string Property, ConstructionValue Value, string? Member = null);

// Content the game never constructs bare.
//
// Most models are directly executable: clone the prototype, hand it to the
// engine, and it behaves exactly as it would in a real run. A few are not.
// Their event or reward factory assigns a saved property first and the
// model's own code assumes it was assigned — SEA_GLASS is only ever picked
// from OROBAS, which stamps the character whose card pool it draws from, and
// MAD_SCIENCE is only ever minted by TINKER_TIME, which stamps the card type
// it resolves as. Injecting either one raw is not gameplay the product ever
// performs; it is a broken fixture, and the model is right to complain
// (SEA_GLASS logs an engine error, MAD_SCIENCE throws from OnPlay).
//
// So the sweeps get a fixture instead: this table names the context-bound
// models and the property each needs, Dispatcher stamps it at injection, and
// /models advertises it so a sweep can tell directly executable content from
// context-bound content — and hold the bridge to actually exercising the
// latter rather than skipping it.
//
// Only the construction context goes here — every property the factory
// assigns before the model reaches a pile or a belt, and nothing else. Which
// of the three riders TINKER_TIME happens to roll, or which character OROBAS
// happens to be visited by, is a combinatorial interaction and stays out of
// scope, same as everywhere else in the atomic sweeps: the table picks one
// reachable value per property, not every value.
internal static class ContextBoundContent
{
    private static readonly IReadOnlyDictionary<string, ConstructionContext[]>
        Required = new Dictionary<string, ConstructionContext[]>(StringComparer.Ordinal)
        {
            // OROBAS offers one Sea Glass per character and stamps the choice
            // before the pick; unstamped, AfterObtained logs "obtained without
            // a character ID assigned" and falls back to Ironclad.
            ["SEA_GLASS"] =
            [
                new ConstructionContext(
                    "CharacterId", ConstructionValue.OwnerCharacterId),
            ],

            // TINKER_TIME mints the one real Mad Science in RiderChosen, and
            // assigns *both* saved properties in the same statement block
            // before the card is added to the deck. Neither type default is a
            // state the event can produce: CardType.None makes OnPlay throw
            // ArgumentOutOfRangeException, and RiderEffect.None skips the
            // rider half of the card entirely and renders its description as
            // "???" (the game calls None "not a valid rider" outright). So
            // both are stamped. Attack is the type the card's own constructor
            // advertises, and Sapping is one of the three riders the event
            // offers for Attack — the pair is reachable, and its Weak plus
            // Vulnerable land on the attack's own target, so a sweep can see
            // that the rider actually ran.
            ["MAD_SCIENCE"] =
            [
                new ConstructionContext(
                    "TinkerTimeType", ConstructionValue.EnumMember, "Attack"),
                new ConstructionContext(
                    "TinkerTimeRider", ConstructionValue.EnumMember, "Sapping"),
            ],
        };

    // The construction context `modelEntry` needs, empty when the model is
    // directly executable. `modelEntry` is a model id entry as the cheats
    // normalize it — upper case.
    internal static IReadOnlyList<ConstructionContext> For(string modelEntry) =>
        Required.TryGetValue(modelEntry, out var contexts)
            ? contexts
            : Array.Empty<ConstructionContext>();

    internal static bool IsContextBound(string modelEntry) =>
        Required.ContainsKey(modelEntry);

    // The property names /models publishes for one entry — the wire form of
    // the distinction, and null for directly executable content so the field
    // stays absent from the overwhelming majority of registry entries.
    internal static string[]? PublishedContext(string modelEntry) =>
        Required.TryGetValue(modelEntry, out var contexts)
            ? contexts.Select(c => c.Property).ToArray()
            : null;
}
