using MegaCrit.Sts2.Core.Models;

namespace Spirescry.State;

// Stable card identity shared by observations and the play action. A bare
// model names only a bare copy; modifiers compose in +, @, ! order.
internal static class CardSpecifier
{
    public static string From(CardModel card) =>
        card.Id.Entry
        + (card.IsUpgraded ? "+" : "")
        + (card.Enchantment is { } enchantment ? $"@{enchantment.Id.Entry}" : "")
        + (card.Affliction is { } affliction ? $"!{affliction.Id.Entry}" : "");
}
