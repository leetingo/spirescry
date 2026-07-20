#if !CARD_GRAMMAR_ONLY
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
#endif

namespace Spirescry.State;

internal enum CardPlayabilityFailure
{
    None,
    NotEnoughStars,
    NotEnoughEnergy,
    NotPlayable,
}

// One evaluation result feeds both the combat observation and the final
// dispatch rejection. Keeping the engine reason and preventing model here
// makes `playable`, `legal`, and a rejected `play` describe the same gate.
internal readonly record struct CardPlayabilityState(
    bool Playable,
    string? UnplayableReason,
    string? Preventer,
    CardPlayabilityFailure Failure)
{
    internal static CardPlayabilityState PlayableCard { get; } =
        new(true, null, null, CardPlayabilityFailure.None);

    internal static CardPlayabilityState Blocked(
        string reason,
        string? preventer = null,
        CardPlayabilityFailure failure = CardPlayabilityFailure.NotPlayable) =>
        new(false, reason, preventer, failure);

    internal string? RejectionCode => Failure switch
    {
        CardPlayabilityFailure.NotEnoughStars => RejectionCodes.NotEnoughStars,
        CardPlayabilityFailure.NotEnoughEnergy => RejectionCodes.NotEnoughEnergy,
        CardPlayabilityFailure.NotPlayable => RejectionCodes.NotPlayable,
        _ => null,
    };
}

internal static class CardCombatObservation
{
    internal static void ApplyPlayability(
        SnapshotItemContract card,
        CardPlayabilityState playability)
    {
        card.Playable = playability.Playable;
        if (playability.Playable) return;

        card.AddExtensions(new
        {
            unplayableReason = playability.UnplayableReason,
            unplayablePreventer = playability.Preventer,
        });
    }
}

#if !CARD_GRAMMAR_ONLY
internal static class CardPlayabilityGate
{
    internal static CardPlayabilityState Evaluate(CardModel card) =>
        CollectionSnapshot.ReadStable(
            "card playable semantic state",
            () => EvaluateOnce(card));

    private static CardPlayabilityState EvaluateOnce(CardModel card)
    {
        if (card.CanPlay(out var reason, out var preventer))
            return CardPlayabilityState.PlayableCard;

        var failure = reason.HasFlag(UnplayableReason.StarCostTooHigh)
            ? CardPlayabilityFailure.NotEnoughStars
            : reason.HasFlag(UnplayableReason.EnergyCostTooHigh)
                ? CardPlayabilityFailure.NotEnoughEnergy
                : CardPlayabilityFailure.NotPlayable;
        return CardPlayabilityState.Blocked(
            reason.ToString(), preventer?.Id.Entry, failure);
    }
}
#endif
