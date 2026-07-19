namespace Spirescry.State;

// The snapshot is the source of truth for decision availability. Legal verbs
// are derived from the exact targets and gates already exposed to the caller,
// rather than from a second phase-to-verb table that can drift from reality.
internal static class DecisionProjection
{
    public static string[] LegalVerbs(SnapshotContract snapshot, bool runActive)
    {
        var legal = new List<string>();
        void Add(string verb)
        {
            if (!legal.Contains(verb, StringComparer.Ordinal)) legal.Add(verb);
        }

        switch (snapshot.Phase)
        {
            case Phase.MainMenu:
                if (!runActive) Add("new-run");
                break;
            case Phase.Map:
                if (snapshot.Next.Length > 0) Add("map-move");
                break;
            case Phase.Combat:
                if (snapshot.Side == "player" && snapshot.ActionsDisabled != true)
                {
                    if (snapshot.Hand.Any(card => card.Playable == true))
                        Add("play");
                    Add("end-turn");
                    if (snapshot.Potions.Length > 0) Add("potion-use");
                }
                break;
            case Phase.Event:
                if (snapshot.Available == false) break;
                if (snapshot.Options.Any(option =>
                    option.Locked != true && option.Chosen != true))
                    Add("option");
                Add("proceed");
                break;
            case Phase.RestSite:
                if (snapshot.Options.Any(option => option.Enabled == true))
                    Add("option");
                if (snapshot.ProceedAvailable == true) Add("proceed");
                break;
            case Phase.Shop:
                if (ShopHasPurchase(snapshot)) Add("buy");
                if (snapshot.Available != false) Add("leave");
                break;
            case Phase.Treasure:
                if (snapshot.Relics.Length > 0)
                {
                    Add("pick-relic");
                    Add("skip");
                }
                // A closed chest offers nothing yet — pick-relic is the verb
                // that opens it (headless: runs the room rewards and asks
                // for a rescry; GUI: clicks the chest). Without this the
                // opening step is never advertised and an agent that only
                // fires legal verbs can't reach the relic at all.
                else if (snapshot.ChestOpened == false)
                    Add("pick-relic");
                if (snapshot.ProceedAvailable == true) Add("proceed");
                break;
            case Phase.Rewards:
                if (snapshot.Available == false) break;
                if (snapshot.Rewards.Length > 0) Add("pick-reward");
                Add("proceed");
                break;
            case Phase.CardReward:
                if (snapshot.Cards.Length > 0) Add("pick-card");
                if (snapshot.Alternatives.Length > 0) Add("skip");
                break;
            case Phase.RelicReward:
                if (snapshot.Relics.Length > 0)
                {
                    Add("pick-relic");
                    Add("skip");
                }
                break;
            case Phase.CardSelect:
                if (snapshot.Cards.Length > 0) Add("pick-card");
                if (snapshot.Confirmable == true) Add("confirm");
                if (snapshot.Cancelable == true) Add("skip");
                break;
            case Phase.HandSelect:
                if (snapshot.Cards.Length > 0) Add("pick-card");
                if (snapshot.Confirmable == true) Add("confirm");
                break;
            case Phase.BundleSelect:
                if (snapshot.Bundles.Length > 0) Add("pick-card");
                if (snapshot.Confirmable == true) Add("confirm");
                if (snapshot.Cancelable == true) Add("skip");
                break;
            case Phase.CrystalSphere:
                if (snapshot.Available != false)
                {
                    if (snapshot.Cells.Any(cell => cell.Hidden == true))
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

    private static int VisiblePotionCount(SnapshotContract snapshot) =>
        snapshot.HasTopLevelPotions
            ? snapshot.Potions.Length
            : snapshot.Player?.Potions.Length ?? 0;

    private static bool ShopHasPurchase(SnapshotContract snapshot)
    {
        return snapshot.Cards.Any(item => item.Purchasable == true)
            || snapshot.Colorless.Any(item => item.Purchasable == true)
            || snapshot.Relics.Any(item => item.Purchasable == true)
            || snapshot.Potions.Any(item => item.Purchasable == true)
            || snapshot.CardRemoval?.Purchasable == true;
    }
}
