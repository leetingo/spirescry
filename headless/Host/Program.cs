// Host entry point — resolves the IL-patched sts2.headless.dll and its
// third-party deps from the lib dir, boots the bridge, and parks.
//
// Usage: STS2_HEADLESS_LIB=/path/to/lib ./spirescry_host
// Default lib dir: ../../build/lib relative to the host binary.

using System.Runtime.Loader;
using Spirescry.Host;

var lifetime = HostLifetime.Install();
var exitTrailTest = Environment.GetEnvironmentVariable("STS2_HOST_EXIT_TRAIL_TEST");

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
    HostLog.Error("boot failed", ex);
    lifetime.LogShutdown("boot failure");
    return 1;
}

// Process-level regression hooks deliberately run after a successful real
// boot. This proves the final log trail survives the same loaded runtime and
// bridge state a gameplay-session exit would have.
switch (exitTrailTest)
{
    case "clean":
        lifetime.LogShutdown("clean self-test");
        return 0;
    case "process-exit":
        return 0;
    case "unhandled-thread":
        var thread = new Thread(static () =>
            throw new InvalidOperationException("exit-trail test exception"));
        thread.Start();
        thread.Join();
        return 99;
    case "hung-main-thread":
        Thread.Sleep(Timeout.Infinite);
        return 98;
}

// Park; the bridge listens on its own threads. Trappable termination signals
// wake this thread so the final log record is flushed before exit.
lifetime.WaitAndLogShutdown();
return 0;
