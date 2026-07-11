namespace Spirescry.Host;

// Console logging that owns the "[spirescry_host]" prefix — the host runs
// before/without the engine logger, so everything goes to stderr. Lines
// carry a UTC clock so they can be lined up with CLI-side traces
// (spirescry --verbose stamps the same clock).
internal static class HostLog
{
    private static string Now => DateTime.UtcNow.ToString("HH:mm:ss.fff");

    public static void Info(string msg) =>
        Console.Error.WriteLine($"[{Now}] [spirescry_host] {msg}");

    // Unlike the mod's SafeLog, printing the stack is safe here: the CLR
    // walks real host frames, not GodotSharp loader fallout.
    public static void Error(string context, Exception ex)
    {
        var line = $"[{Now}] [spirescry_host] {context}: {ex.GetType().Name}: {ex.Message}";
        Console.Error.WriteLine(ex.StackTrace is { } stack ? $"{line}\n{stack}" : line);
    }
}
