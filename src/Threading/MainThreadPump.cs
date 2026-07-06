using System.Collections.Concurrent;
using Godot;

namespace Spirescry.Threading;

// Main-thread dispatcher. Two adapters behind one seam:
//
// GUI (Bootstrap with a SceneTree): Run() enqueues onto a thread-safe
// queue; the engine's `process_frame` signal drains it on the main thread
// every frame. Engine singletons are not thread-safe, so every handler
// that touches game state hops through here.
//
// Headless (BootstrapHeadless): no SceneTree, no frames. Run() executes
// inline on the calling thread — there is no rendering pipeline to guard,
// and async chains complete inline thanks to the host's IL patches
// (queue-wait rewrite) and Cmd.Wait prefix.
public abstract class MainThreadPump
{
    public static MainThreadPump? Instance { get; private set; }

    protected MainThreadPump() => Instance = this;

    public static MainThreadPump Bootstrap(SceneTree tree) => new GuiPump(tree);
    public static MainThreadPump BootstrapHeadless() => new HeadlessPump();

    public abstract Task<T> Run<T>(Func<T> fn);

    private sealed class GuiPump : MainThreadPump
    {
        private readonly ConcurrentQueue<Action> _queue = new();

        public GuiPump(SceneTree tree) =>
            tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(Drain));

        private void Drain()
        {
            while (_queue.TryDequeue(out var job))
            {
                try { job(); }
                catch (Exception ex) { SafeLog.Error("pump job threw", ex); }
            }
            // The frame loop is the engine's own heartbeat — fold it into
            // the signal stream (phase diffs, event resubscription).
            try { State.Signals.Tick(); }
            catch (Exception ex) { SafeLog.Error("signals tick", ex); }
        }

        public override Task<T> Run<T>(Func<T> fn)
        {
            // RunContinuationsAsynchronously: awaiting code must not resume
            // synchronously on the main thread and block frame processing.
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(() =>
            {
                try { tcs.SetResult(fn()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }
    }

    private sealed class HeadlessPump : MainThreadPump
    {
        // The engine's singletons are not thread-safe and the bridge
        // serves requests concurrently; without frames to serialize on,
        // the pump itself is the mutual exclusion.
        private readonly object _gate = new();

        public override Task<T> Run<T>(Func<T> fn)
        {
            lock (_gate)
            {
                try
                {
                    var result = fn();
                    try { State.Signals.Tick(); }
                    catch (Exception ex) { SafeLog.Error("signals tick", ex); }
                    return Task.FromResult(result);
                }
                catch (Exception ex) { return Task.FromException<T>(ex); }
            }
        }
    }
}
