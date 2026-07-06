namespace Spirescry.Host;

// Console logging that owns the "[spirescry_host]" prefix — the host runs
// before/without the engine logger, so everything goes to stderr.
internal static class HostLog
{
    public static void Info(string msg) =>
        Console.Error.WriteLine($"[spirescry_host] {msg}");

    public static void Error(string context, Exception ex) =>
        Console.Error.WriteLine($"[spirescry_host] {context}: {ex.GetType().Name}: {ex.Message}");
}
