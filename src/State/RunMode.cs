namespace Spirescry.State;

// Headless boot has no SceneTree, so the engine's root visual node
// NGame.Instance is never created and stays null. This is the one place
// that knows which engine null-check encodes the mode.
internal static class RunMode
{
    public static bool IsHeadless => MegaCrit.Sts2.Core.Nodes.NGame.Instance is null;
}
