using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using CrystalScreen = MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen;

namespace Spirescry.State;

public static class PhaseDetector
{
    // Compatibility extension; ProtocolVocabulary owns the wire names.
    public static string AsString(this Phase p) => ProtocolVocabulary.Phases.Name(p);

    // Must be called on the main thread; reads game singletons that are
    // not thread-safe.
    public static Phase Current()
    {
        // Terminal-run check first — but after a run ends and the player
        // returns to the main menu, the terminal flags linger on
        // RunManager, so a visible main menu wins over game_over.
        var rm = RunManager.Instance;
        var menuVisible = NGame.Instance?.MainMenu is { Visible: true };
        if (rm is not null && (rm.IsGameOver || rm.IsAbandoned) && !menuVisible)
            return Phase.GameOver;

        if (menuVisible)
            return Phase.MainMenu;

        // Pilot decision surface: the GUI adapter reads the real overlay;
        // the headless adapter reads its parked completion owner.
        if (DecisionSurface.Current.BundleActive)
            return Phase.BundleSelect;

        // Headless stand-ins for the screens that don't exist without a
        // scene tree: the virtual rewards flow and the deferred card
        // picker (rest upgrade / removal / event picks / mid-combat hand
        // picks). Checked before combat so a parked mid-combat pick
        // surfaces as a selection, not combat.
        if (RunMode.IsHeadless)
        {
            if (HeadlessRewards.InCardPick) return Phase.CardReward;
            if (HeadlessRewards.IsActive) return Phase.Rewards;
            if (HeadlessCrystal.IsActive) return Phase.CrystalSphere;
            if (HeadlessPicker.IsActive)
                return CombatManager.Instance is { IsInProgress: true }
                    ? Phase.HandSelect
                    : Phase.CardSelect;
        }

        // IsOpen alone stays true through the act-intro animation;
        // IsTravelEnabled is the same flag the map UI gates clicks on.
        // The run-state check kills a boot-window flap where the map screen
        // briefly reads open+travel-enabled with no run behind it.
        if (NMapScreen.Instance is { IsOpen: true, IsTravelEnabled: true, IsTraveling: false }
            && rm?.DebugOnlyGetState() is not null)
            return Phase.Map;

        // An overlay captures input, so the room underneath is not
        // actionable. The reward screens are driveable phases; everything
        // else surfaces as one opaque phase.
        if (NOverlayStack.Instance?.Peek() is { } top)
            return top switch
            {
                NRewardsScreen => Phase.Rewards,
                NCardRewardSelectionScreen => Phase.CardReward,
                NChooseARelicSelection => Phase.RelicReward,
                // Base type of every grid picker: deck removal / upgrade /
                // transform / enchant and combat pile selects. The
                // choose-a-card screen (Discovery, event card offers) is a
                // separate type with the same pick-one semantics.
                NCardGridSelectionScreen or NChooseACardSelectionScreen => Phase.CardSelect,
                CrystalScreen => Phase.CrystalSphere,
                _ => Phase.Overlay,
            };

        var combat = CombatManager.Instance;
        if (combat != null && combat.IsInProgress)
            // Hand-select mode (discard/exhaust/upgrade picks) captures the
            // hand's input in place — play/end-turn don't apply until it
            // resolves.
            return NPlayerHand.Instance is { IsInCardSelection: true }
                ? Phase.HandSelect
                : Phase.Combat;

        if (rm == null) return Phase.MainMenu;

        var state = rm.DebugOnlyGetState();
        if (state == null) return Phase.MainMenu;

        return state.CurrentRoom switch
        {
            // Combat over but the room hasn't advanced: in GUI the rewards
            // overlay handles this; headless captures the same rewards into
            // the virtual flow. Gate on IsPreFinished — the engine flips it
            // in the same breath as OnCombatEnded — because right after
            // room entry combat hasn't STARTED yet, and capturing there
            // would pin the phase on rewards for an unfought room.
            CombatRoom room when RunMode.IsHeadless =>
                room.IsPreFinished && HeadlessRewards.CaptureFromCurrentRoom()
                    ? Phase.Rewards
                    : Phase.Unknown,
            EventRoom => Phase.Event,
            MerchantRoom => Phase.Shop,
            RestSiteRoom => Phase.RestSite,
            TreasureRoom => Phase.Treasure,
            MapRoom => Phase.Map,
            null => Phase.Map,
            _ => Phase.Unknown,
        };
    }
}
