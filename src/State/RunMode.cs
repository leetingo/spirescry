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
    public static bool ForcedHeadless;

    public static bool IsHeadless =>
        ForcedHeadless || MegaCrit.Sts2.Core.Nodes.NGame.Instance is null;
}
