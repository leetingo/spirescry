namespace Spirescry.State;

// Merchant rules that need no engine types to decide. Like RunOutcomeRules
// they live apart from Snapshotter and Dispatcher so the same statement gates
// the observation and the dispatch — and so the unit tests CI does run can
// reach them, which the end-to-end suite cannot be in GitHub-hosted CI.
internal static class MerchantRules
{
    // A merchant sells exactly one card removal, so `buy card_removal` has
    // exactly one target. The snapshot publishes it at this index and the
    // dispatcher accepts no other: an ignored idx let `--idx 7` silently
    // remove a card, which is not an action the observation advertised.
    internal const int CardRemovalIndex = 0;

    internal static bool IsCardRemovalIndex(int idx) => idx == CardRemovalIndex;

    // The Foul Potion is the only potion a merchant trades for — everything
    // else is a combat item and `potion-use` outside combat rejects it. The
    // remaining flags are the potion popup's own model-layer gates, read from
    // the engine by the caller; stating the conjunction here keeps obs.legal,
    // the belt item's `playable`, and a rejected potion-use in agreement.
    internal static bool RedeemableAtMerchant(
        bool isFoulPotion,
        bool usableAnyTime,
        bool ownerAlive,
        bool canUseOrRemovePotions,
        bool interactionAvailable) =>
        isFoulPotion
            && usableAnyTime
            && ownerAlive
            && canUseOrRemovePotions
            && interactionAvailable;
}
