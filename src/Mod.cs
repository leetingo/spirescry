using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using Spirescry.Bridge;
using Spirescry.State;
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

    // Bumped whenever the bridge's request/response contract changes
    // shape — the CLI's detector for a host it doesn't understand.
    public const int ProtocolVersion = 2;

    // The short git hash build.sh stamps via -p:SourceRevisionId (the
    // SDK appends it to InformationalVersion after a '+'). The stamp's
    // prefix distinguishes it from the SDK's automatic full git SHA.
    public static string BuildHash { get; } = ReadBuildHash();

    private static string ReadBuildHash()
    {
        const string stampPrefix = "spirescry.";
        var info = typeof(Mod).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        var plus = info?.IndexOf('+') ?? -1;
        var metadata = plus >= 0 ? info![(plus + 1)..] : "";
        return metadata.StartsWith(stampPrefix, StringComparison.Ordinal)
            ? metadata[stampPrefix.Length..]
            : "unknown";
    }

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
    public static void Info(string msg)
    {
        var line = $"[{Mod.Id}] {msg}";
        try { Log.Info(line, 1); }
        catch { Fallback(line); }
    }

    public static void Error(string context, Exception ex)
    {
        var line = $"[{Mod.Id}] {context}: {ex.GetType().Name}: {ex.Message}";
        try
        {
            Log.Error(line, 1);
        }
        catch
        {
            // Error reporting is on the recovery path and must never mask
            // the original exception. The pure host can temporarily lose
            // the engine logger after a model hook faults; stderr remains
            // available in both host and game boots.
            Fallback(line);
        }
    }

    private static void Fallback(string line)
    {
        try { Console.Error.WriteLine(line); }
        catch { /* no logging sink is worth failing the request */ }
    }
}

internal static class Boot
{
    private static bool _started;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        DecisionSurface.UseGui();

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
