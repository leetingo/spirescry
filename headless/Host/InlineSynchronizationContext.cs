// Drains async continuations inline on the posting thread. sts2 scatters
// `await Task.Yield()` through combat and room transitions; with no frame
// loop those continuations must run immediately (and in order) or the
// chains never finish.

namespace Spirescry.Host;

internal sealed class InlineSynchronizationContext : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback, object?)> _queue = new();
    private bool _executing;

    public override void Post(SendOrPostCallback d, object? state)
    {
        // Re-entrant posts queue up and drain after the current callback,
        // so long async chains can't blow the stack.
        if (_executing) { _queue.Enqueue((d, state)); return; }
        _executing = true;
        try
        {
            d(state);
            while (_queue.Count > 0)
            {
                var (cb, st) = _queue.Dequeue();
                cb(st);
            }
        }
        finally { _executing = false; }
    }

    public override void Send(SendOrPostCallback d, object? state) => d(state);
}
