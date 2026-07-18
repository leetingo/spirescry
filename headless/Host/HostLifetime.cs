using System.Runtime.InteropServices;

namespace Spirescry.Host;

// Owns the host's process-wide last-gasp diagnostics. The first signal asks
// the main thread to exit gracefully; a repeated signal or expired deadline
// writes the final record itself before forcing the process down.
internal sealed class HostLifetime
{
    private static readonly TimeSpan ForcedShutdownDelay = TimeSpan.FromSeconds(2);

    private readonly ManualResetEventSlim _shutdown = new(false);
    private readonly List<PosixSignalRegistration> _signalRegistrations = new();
    private string? _trigger;
    private int _exitLogged;
    private int _terminationRequests;
    private int _forcedExitStarted;
    private Timer? _forcedShutdownTimer;

    private HostLifetime()
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        Console.CancelKeyPress += OnCancelKeyPress;

        Register(PosixSignal.SIGTERM);
        Register(PosixSignal.SIGINT);
        Register(PosixSignal.SIGHUP);
        Register(PosixSignal.SIGQUIT);
    }

    public static HostLifetime Install() => new();

    public void WaitAndLogShutdown()
    {
        _shutdown.Wait();
        LogShutdown(Volatile.Read(ref _trigger) ?? "requested");
    }

    public void LogShutdown(string trigger)
    {
        if (Interlocked.Exchange(ref _exitLogged, 1) == 0)
            HostLog.Info($"shutdown: {trigger}");
    }

    private void Register(PosixSignal signal)
    {
        try
        {
            _signalRegistrations.Add(PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;
                var trigger = $"signal {context.Signal}";
                if (Interlocked.Increment(ref _terminationRequests) > 1)
                {
                    ForceShutdown($"repeated {trigger}", SignalExitCode(context.Signal));
                    return;
                }

                RequestShutdown(trigger);
                ArmForcedShutdown(trigger, SignalExitCode(context.Signal));
            }));
        }
        catch (PlatformNotSupportedException)
        {
            // Console.CancelKeyPress and ProcessExit remain as the portable
            // fallback on platforms without POSIX signal registration.
        }
    }

    private void RequestShutdown(string trigger)
    {
        Interlocked.CompareExchange(ref _trigger, trigger, null);
        _shutdown.Set();
    }

    private void ArmForcedShutdown(string trigger, int exitCode)
    {
        var timer = new Timer(
            _ => ForceShutdown($"timeout after {trigger}", exitCode),
            null,
            ForcedShutdownDelay,
            Timeout.InfiniteTimeSpan);
        if (Interlocked.CompareExchange(ref _forcedShutdownTimer, timer, null) is not null)
            timer.Dispose();
    }

    private void ForceShutdown(string trigger, int exitCode)
    {
        if (Interlocked.Exchange(ref _forcedExitStarted, 1) != 0) return;

        Interlocked.Exchange(ref _exitLogged, 1);
        HostLog.Info($"forced-shutdown: {trigger}");
        Console.Error.Flush();
        Environment.Exit(exitCode);
    }

    private static int SignalExitCode(PosixSignal signal) => signal switch
    {
        PosixSignal.SIGHUP => 129,
        PosixSignal.SIGINT => 130,
        PosixSignal.SIGQUIT => 131,
        PosixSignal.SIGTERM => 143,
        _ => 1,
    };

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        RequestShutdown($"console {e.SpecialKey}");
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (Interlocked.Exchange(ref _exitLogged, 1) != 0) return;

        if (e.ExceptionObject is Exception ex)
            HostLog.Error("unhandled exception", ex);
        else
            HostLog.Info($"unhandled exception: {e.ExceptionObject}");
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _exitLogged) != 0) return;
        LogShutdown(Volatile.Read(ref _trigger) ?? "process exit");
    }
}
