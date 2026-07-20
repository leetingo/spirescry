namespace Spirescry.State;

// Headless boot has no SceneTree, so the engine's root visual node
// NGame.Instance is never created and stays null — unless the host
// installs an inert NGame dummy so model-layer display calls
// (ScreenShakeTrauma mid-event-effect) no-op instead of aborting the
// effect halfway. The dummy makes the null-probe useless as a mode
// signal, so the host boot stamps ForcedHeadless first; the GUI mod
// never sets it and keeps the node probe.
internal static class RunMode
{
    public static bool ForcedHeadless { get; set; }

    public static bool IsHeadless =>
        ForcedHeadless || MegaCrit.Sts2.Core.Nodes.NGame.Instance is null;

    // Keep dummy-aware mode selection out of individual verbs. The host
    // installs inert visual singletons, so checking Instance directly is
    // not a valid boot-mode test.
    public static MegaCrit.Sts2.Core.Nodes.NGame? GuiGame =>
        IsHeadless ? null : MegaCrit.Sts2.Core.Nodes.NGame.Instance;

    public static MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom? GuiEventRoom =>
        IsHeadless
            ? null
            : MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom.Instance;
}
