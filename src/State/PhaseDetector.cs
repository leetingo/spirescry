using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;

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
        var stateOnly = LocalRunContext.StateOnly;
        var rm = stateOnly?.Manager;
        var menuVisible = NGame.Instance?.MainMenu is { Visible: true };
        if (rm is not null && (rm.IsGameOver || rm.IsAbandoned) && !menuVisible)
            return Phase.GameOver;

        if (menuVisible)
            return Phase.MainMenu;

        // The selected surface owns boot-specific ordering as well as the
        // active decision. GUI preserves bundle → map → overlay → combat;
        // headless preserves parked choices → combat.
        if (DecisionSurface.Current.ActivePhase is { } activePhase)
            return activePhase;

        var run = LocalRunContext.Current;
        if (run is null) return Phase.MainMenu;
        var state = run.Value.State;

        return state.CurrentRoom switch
        {
            CombatRoom => Phase.Unknown,
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
