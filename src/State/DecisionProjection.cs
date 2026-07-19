using System.Text.Json.Nodes;

namespace Spirescry.State;

// The snapshot is the source of truth for decision availability. Legal verbs
// are derived from the exact targets and gates already exposed to the caller,
// rather than from a second phase-to-verb table that can drift from reality.
internal static class DecisionProjection
{
    public static string[] LegalVerbs(JsonObject snapshot, bool runActive)
    {
        var phase = snapshot["phase"]?.GetValue<string>() ?? "unknown";
        var legal = new List<string>();
        void Add(string verb)
        {
            if (!legal.Contains(verb, StringComparer.Ordinal)) legal.Add(verb);
        }

        switch (phase)
        {
            case "main_menu":
                if (!runActive) Add("new-run");
                break;
            case "map":
                if (HasItems(snapshot, "next")) Add("map-move");
                break;
            case "combat":
                if (snapshot["side"]?.GetValue<string>() == "player"
                    && snapshot["actionsDisabled"]?.GetValue<bool>() != true)
                {
                    if (Any(snapshot, "hand", card => card["playable"]?.GetValue<bool>() == true))
                        Add("play");
                    Add("end-turn");
                    if (HasItems(snapshot, "potions")) Add("potion-use");
                }
                break;
            case "event":
                if (snapshot["available"]?.GetValue<bool>() == false) break;
                if (Any(snapshot, "options", option =>
                    option["locked"]?.GetValue<bool>() != true
                    && option["chosen"]?.GetValue<bool>() != true))
                    Add("option");
                Add("proceed");
                break;
            case "rest_site":
                if (Any(snapshot, "options", option => option["enabled"]?.GetValue<bool>() == true))
                    Add("option");
                if (snapshot["proceedAvailable"]?.GetValue<bool>() == true) Add("proceed");
                break;
            case "shop":
                if (ShopHasPurchase(snapshot)) Add("buy");
                if (snapshot["available"]?.GetValue<bool>() != false) Add("leave");
                break;
            case "treasure":
                if (HasItems(snapshot, "relics"))
                {
                    Add("pick-relic");
                    Add("skip");
                }
                // A closed chest offers nothing yet — pick-relic is the verb
                // that opens it (headless: runs the room rewards and asks
                // for a rescry; GUI: clicks the chest). Without this the
                // opening step is never advertised and an agent that only
                // fires legal verbs can't reach the relic at all.
                else if (snapshot["chestOpened"]?.GetValue<bool>() == false)
                    Add("pick-relic");
                if (snapshot["proceedAvailable"]?.GetValue<bool>() == true) Add("proceed");
                break;
            case "rewards":
                if (snapshot["available"]?.GetValue<bool>() == false) break;
                if (HasItems(snapshot, "rewards")) Add("pick-reward");
                Add("proceed");
                break;
            case "card_reward":
                if (HasItems(snapshot, "cards")) Add("pick-card");
                if (HasItems(snapshot, "alternatives")) Add("skip");
                break;
            case "relic_reward":
                if (HasItems(snapshot, "relics"))
                {
                    Add("pick-relic");
                    Add("skip");
                }
                break;
            case "card_select":
                if (HasItems(snapshot, "cards")) Add("pick-card");
                if (snapshot["confirmable"]?.GetValue<bool>() == true) Add("confirm");
                if (snapshot["cancelable"]?.GetValue<bool>() == true) Add("skip");
                break;
            case "hand_select":
                if (HasItems(snapshot, "cards")) Add("pick-card");
                if (snapshot["confirmable"]?.GetValue<bool>() == true) Add("confirm");
                break;
            case "bundle_select":
                if (HasItems(snapshot, "bundles")) Add("pick-card");
                if (snapshot["confirmable"]?.GetValue<bool>() == true) Add("confirm");
                if (snapshot["cancelable"]?.GetValue<bool>() == true) Add("skip");
                break;
            case "crystal_sphere":
                if (snapshot["available"]?.GetValue<bool>() != false)
                {
                    if (Any(snapshot, "cells", cell => cell["hidden"]?.GetValue<bool>() == true))
                        Add("map-move");
                    Add("option");
                    Add("proceed");
                }
                break;
        }

        if (runActive)
        {
            if (VisiblePotionCount(snapshot) > 0) Add("potion-discard");
            Add("abandon");
        }
        return legal.ToArray();
    }

    private static bool HasItems(JsonObject snapshot, string property) =>
        snapshot[property] is JsonArray { Count: > 0 };

    private static bool Any(JsonObject snapshot, string property, Func<JsonObject, bool> predicate) =>
        snapshot[property] is JsonArray items
        && items.OfType<JsonObject>().Any(predicate);

    private static int VisiblePotionCount(JsonObject snapshot)
    {
        if (snapshot["potions"] is JsonArray direct) return direct.Count;
        if (snapshot["player"] is JsonObject player
            && player["potions"] is JsonArray nested) return nested.Count;
        return 0;
    }

    private static bool ShopHasPurchase(JsonObject snapshot)
    {
        foreach (var key in new[] { "cards", "colorless", "relics", "potions" })
            if (Any(snapshot, key, item => item["purchasable"]?.GetValue<bool>() == true))
                return true;
        return snapshot["cardRemoval"] is JsonObject removal
            && removal["purchasable"]?.GetValue<bool>() == true;
    }
}
