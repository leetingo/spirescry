#if !CARD_GRAMMAR_ONLY
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
#endif

namespace Spirescry.State;

// The `err` code and its explanation, decided together so a caller cannot
// pair one rule's code with another's message.
internal readonly record struct MerchantRejection(string Code, string Message);

// Merchant rules that need no engine types to decide. Like RunOutcomeRules
// they live apart from Snapshotter and Dispatcher so one statement gates both
// the observation and the dispatch — and so the unit tests CI does run can
// reach them, which the end-to-end suite cannot be in GitHub-hosted CI.
internal static class MerchantRules
{
    // A merchant sells exactly one card removal, so `buy card_removal` has
    // exactly one target. The snapshot publishes it at this index.
    internal const int CardRemovalIndex = 0;

    // Everything `buy` can settle from the request alone. Stocked, affordable
    // and open-slot checks need the inventory and stay with the dispatcher;
    // these do not, so the indices an observation publishes and the reject
    // any other index earns are one statement. Ignoring idx for card_removal
    // let `--idx 7` silently remove a card — an action no observation
    // advertised.
    internal static MerchantRejection? BuyIndexRejection(string kind, int idx)
    {
        if (idx < 0)
            return new MerchantRejection(RejectionCodes.BadIndex,
                $"{kind} idx {idx} must be non-negative");
        if (kind == "card_removal" && idx != CardRemovalIndex)
            return new MerchantRejection(RejectionCodes.BadIndex,
                $"card_removal has one entry, at idx {CardRemovalIndex}; got {idx}");
        return null;
    }

    // The Foul Potion is the only potion a merchant trades for — everything
    // else is a combat item and `potion-use` outside combat rejects it. The
    // remaining flags are the potion popup's own model-layer gates, read from
    // the engine by the caller; stating the conjunction here keeps obs.legal,
    // the belt item's `playable`, and a rejected potion-use in agreement.
    // The custom usability check walks the run's current room, so it stays a
    // callback and is asked only once the cheap flags have passed — a shop
    // snapshot runs this over every belt slot.
    internal static bool RedeemableAtMerchant(
        bool isFoulPotion,
        bool usableAnyTime,
        bool ownerAlive,
        bool canUseOrRemovePotions,
        Func<bool> interactionAvailable) =>
        isFoulPotion
            && usableAnyTime
            && ownerAlive
            && canUseOrRemovePotions
            && interactionAvailable();
}

#if !CARD_GRAMMAR_ONLY
// The engine-reading half of the redemption rule, kept beside the rule rather
// than inside Snapshotter: the observation marks a belt entry `playable` with
// it and the dispatcher rejects a potion-use with it, so neither layer owns
// the gate and the two cannot drift apart.
internal static class MerchantPotionGate
{
    internal static bool Redeemable(PotionModel potion, Player player) =>
        MerchantRules.RedeemableAtMerchant(
            potion is FoulPotion,
            potion.Usage == PotionUsage.AnyTime,
            player.Creature is { IsDead: false },
            player.CanUseOrRemovePotions,
            () => DecisionSurface.Current.MerchantPotionInteractionAvailable(potion));
}
#endif
