// Host entry point — resolves the IL-patched sts2.headless.dll and its
// third-party deps from the lib dir, boots the bridge, and parks.
//
// Usage: STS2_HEADLESS_LIB=/path/to/lib ./spirescry_host
// Default lib dir: ../../build/lib relative to the host binary.

using System.Runtime.Loader;
using Spirescry.Host;

var libDir = Environment.GetEnvironmentVariable("STS2_HEADLESS_LIB");
if (string.IsNullOrEmpty(libDir))
    libDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "build", "lib"));
if (!Directory.Exists(libDir))
{
    HostLog.Info($"lib dir not found: {libDir} — run ./build.sh headless-setup");
    return 2;
}
Environment.SetEnvironmentVariable("STS2_HEADLESS_LIB", libDir);

// Resolve sts2 + third-party deps from the lib dir. GodotSharp (the stub)
// is already in the default ALC via the project reference.
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    var path = Path.Combine(libDir, name.Name + ".dll");
    return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
};

var sts2Path = Path.Combine(libDir, "sts2.headless.dll");
if (!File.Exists(sts2Path))
{
    HostLog.Info($"missing {sts2Path} — run ./build.sh headless-setup");
    return 2;
}
AssemblyLoadContext.Default.LoadFromAssemblyPath(sts2Path);

try
{
    HeadlessBoot.Start();
}
catch (Exception ex)
{
    HostLog.Info($"boot failed: {ex}");
    return 1;
}

// Park; the bridge listens on its own threads. SIGINT/SIGTERM exit.
var done = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
done.Wait();
return 0;
