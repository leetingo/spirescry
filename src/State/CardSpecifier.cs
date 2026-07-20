#if !CARD_GRAMMAR_ONLY
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
#endif

namespace Spirescry.State;

internal readonly record struct CardIdentity(string Selector, string TextKey);

#if !CARD_GRAMMAR_ONLY
internal readonly record struct CardUpgradePreview(
    string Description, int PlayCost, int? StarCost);
#endif

// Stable card identity shared by observations and the play action. A bare
// model names only a bare copy; modifiers compose in +, @, ! order.
internal static class CardSpecifier
{
    internal static CardIdentity Encode(
        string model,
        bool isUpgraded,
        int upgradeLevel,
        string? enchantment,
        string? affliction)
    {
        var modifiers = (enchantment is null ? "" : "@" + enchantment)
            + (affliction is null ? "" : "!" + affliction);
        return new CardIdentity(
            model + (isUpgraded ? "+" : "") + modifiers,
            model + "+" + upgradeLevel + modifiers);
    }

#if !CARD_GRAMMAR_ONLY
    internal static CardIdentity Identity(CardModel card) => Encode(
        card.Id.Entry,
        card.IsUpgraded,
        card.CurrentUpgradeLevel,
        card.Enchantment?.Id.Entry,
        card.Affliction?.Id.Entry);

    public static string From(CardModel card) => Identity(card).Selector;

    internal static string TextKey(CardModel card) => Identity(card).TextKey;

    // Offer/pile prose uses the engine's composed description, which includes
    // upgrades, enchantments, and afflictions. Icon paths are normalized at
    // the module edge so every card view gets the same readable tokens.
    internal static string Description(CardModel card)
    {
        try
        {
            return RichText.NormalizeIcons(
                card.GetDescriptionForPile(PileType.None, null));
        }
        catch { return SafeDescription(card.Description); }
    }

    // Combat prose first refreshes the same dynamic previews the GUI refreshes
    // each frame (strength, weak, and other live values).
    internal static string Text(CardModel card, PileType pile)
    {
        try
        {
            RefreshPreview(card);
            return RichText.NormalizeIcons(card.GetDescriptionForPile(pile));
        }
        catch { return ""; }
    }

    internal static void RefreshPreview(CardModel card)
    {
        try
        {
            card.UpdateDynamicVarPreview(
                CardPreviewMode.Normal, null, card.DynamicVars);
        }
        catch { }
    }

    internal static Dictionary<string, decimal>? DynamicVars(CardModel card)
    {
        try
        {
            return ReadDynamicVars(card);
        }
        catch { return null; }
    }

    internal static Dictionary<string, decimal>? SemanticDynamicVars(CardModel card) =>
        CollectionSnapshot.ReadStable(
            "card dynamic vars semantic state", () => ReadDynamicVars(card));

    internal static int? StarCost(CardModel card)
    {
        try
        {
            return ReadStarCost(card);
        }
        catch { return null; }
    }

    internal static int? SemanticStarCost(CardModel card) =>
        CollectionSnapshot.ReadStable(
            "card star cost semantic state", () => ReadStarCost(card));

    private static Dictionary<string, decimal>? ReadDynamicVars(CardModel card)
    {
        var variables = new Dictionary<string, decimal>();
        foreach (var variable in card.DynamicVars.Values)
            variables[variable.Name] = variable.PreviewValue;
        return variables.Count == 0 ? null : variables;
    }

    private static int? ReadStarCost(CardModel card)
    {
        var cost = card.GetStarCostWithModifiers();
        return cost >= 0 ? cost : null;
    }

    // Upgrades mutate dynamic vars in place, so render a throwaway canonical
    // twin one level beyond the observed card.
    internal static CardUpgradePreview? UpgradePreview(CardModel card)
    {
        if (!card.IsUpgradable) return null;
        try
        {
            var twin = ModelDb.GetById<CardModel>(card.Id).ToMutable();
            for (var level = 0; level <= card.CurrentUpgradeLevel; level++)
                twin.UpgradeInternal();
            return new CardUpgradePreview(
                Description(twin), twin.EnergyCost.Canonical, StarCost(twin));
        }
        catch { return null; }
    }

    private static string SafeDescription(LocString? description)
    {
        if (description is null) return "";
        try
        {
            if (description.IsEmpty) return "";
            var local = new LocString(
                description.LocTable, description.LocEntryKey);
            local.AddVariablesFrom(description);
            if (!local.Variables.ContainsKey("energyPrefix"))
                local.AddObj("energyPrefix", "");
            if (!local.Variables.ContainsKey("singleStarIcon"))
                local.AddObj("singleStarIcon", "[star]");
            var text = local.GetFormattedText();
            return text == description.LocEntryKey
                ? ""
                : RichText.NormalizeIcons(text);
        }
        catch
        {
            try
            {
                return RichText.NormalizeIcons(description.GetRawText() ?? "");
            }
            catch { return ""; }
        }
    }
#endif
}
