using System.Runtime.InteropServices;

namespace Spirescry.Host;

// Owns the host's process-wide last-gasp diagnostics. Signal handlers only
// request a graceful exit; the main thread writes the final shutdown record.
internal sealed class HostLifetime
{
    private readonly ManualResetEventSlim _shutdown = new(false);
    private readonly List<PosixSignalRegistration> _signalRegistrations = new();
    private string? _trigger;
    private int _exitLogged;

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
                RequestShutdown($"signal {context.Signal}");
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
