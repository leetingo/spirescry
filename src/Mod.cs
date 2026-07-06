using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using Spirescry.Bridge;
using Spirescry.Threading;

namespace Spirescry;

// [ModInitializer] makes the game's ModManager call Initialize and skip
// Harmony.PatchAll — this mod carries no patches, so it never needs to
// reference 0Harmony.
[ModInitializer(nameof(Initialize))]
public static class Mod
{
    public const string Id = "spirescry";
    public const string Version = "0.1.0";

    public static void Initialize()
    {
        Boot.Start();
        SafeLog.Info($"loaded v{Version}");
    }
}

// Log wrappers that own the "[spirescry]" prefix. Error never renders
// the exception itself: Exception.ToString()/StackTrace walk stack-frame
// MethodDescs whose names can be NULL (GodotSharp loader fallout) and
// segfault the CLR — only the type name and Message are safe.
internal static class SafeLog
{
    public static void Info(string msg) => Log.Info($"[{Mod.Id}] {msg}", 1);

    public static void Error(string context, Exception ex) =>
        Log.Error($"[{Mod.Id}] {context}: {ex.GetType().Name}: {ex.Message}", 1);
}

internal static class Boot
{
    private static bool _started;

    public static void Start()
    {
        if (_started) return;
        _started = true;

        // SceneTree.process_frame fires every frame regardless of pause
        // state and is safe to subscribe at mod-init time.
        var tree = (SceneTree)Engine.GetMainLoop();
        MainThreadPump.Bootstrap(tree);

        try
        {
            HttpBridge.StartFromEnv();
        }
        catch (Exception ex)
        {
            SafeLog.Error("bridge disabled", ex);
        }
    }
}
