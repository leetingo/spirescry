using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace Spirescry.State;

// Event-driven waiting. Every state change bumps a monotonic revision:
// point signals come from the engine's own C# events (action executor,
// combat manager, overlay stack — no patches), and a per-tick phase diff
// is the safety net for anything without one. Agents park on
// GET /obs?since=<rev>&wait=<ms> instead of sleep-polling; the response
// carries the new revision plus the named events behind it.
public static class Signals
{
    private const int LogCap = 64;

    private static readonly object Gate = new();
    private static readonly List<(long rev, string type)> Log = new();
    private static readonly List<TaskCompletionSource<bool>> Waiters = new();
    private static long _revision;
    private static string _lastPhase = "";
    public static DateTime LastPhaseChangeUtc { get; private set; }
    private static GameAction? _watchedAction;
    private static DateTime _watchedSinceUtc;
    private static bool _wedgeAnnounced;

    // Milliseconds the executor has been stuck on the SAME action. The
    // serial executor never recovers from a parked action on its own —
    // past the threshold the contract is: abandon and start over.
    public static long ExecutorStuckMs { get; private set; }

    private static ActionExecutor? _exec;
    private static MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSet? _queueSet;
    private static CombatManager? _combat;
    private static NOverlayStack? _stack;

    public static long Revision { get { lock (Gate) return _revision; } }

    public static void Bump(string type)
    {
        List<TaskCompletionSource<bool>>? wake = null;
        lock (Gate)
        {
            _revision++;
            Log.Add((_revision, type));
            if (Log.Count > LogCap) Log.RemoveAt(0);
            if (Waiters.Count > 0)
            {
                wake = new List<TaskCompletionSource<bool>>(Waiters);
                Waiters.Clear();
            }
        }
        if (wake is null) return;
        foreach (var w in wake) w.TrySetResult(true);
    }

    // Runs on the pump every tick (GUI: each frame; host: after each
    // handler + a slow timer for background continuations): re-hook engine
    // events across instance swaps, and fold phase changes into the
    // revision stream.
    public static void Tick()
    {
        EnsureSubscribed();
        WatchExecutor();
        var phase = PhaseDetector.Current().AsString();
        if (phase == _lastPhase) return;
        var from = _lastPhase;
        _lastPhase = phase;
        LastPhaseChangeUtc = DateTime.UtcNow;
        Bump($"phase:{from}->{phase}");
    }

    // True when the revision moved past `since`; false on timeout.
    public static async Task<bool> WaitForChange(long since, int timeoutMs)
    {
        TaskCompletionSource<bool> tcs;
        lock (Gate)
        {
            if (_revision > since) return true;
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Waiters.Add(tcs);
        }
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        lock (Gate) Waiters.Remove(tcs);
        return winner == tcs.Task;
    }

    public static object[] EventsSince(long since)
    {
        lock (Gate)
            return Log.Where(e => e.rev > since)
                .Select(e => (object)new { rev = e.rev, type = e.type })
                .ToArray();
    }

    private static void WatchExecutor()
    {
        var running = RunManager.Instance?.ActionExecutor?.CurrentlyRunningAction;
        if (running is null || !ReferenceEquals(running, _watchedAction))
        {
            _watchedAction = running;
            _watchedSinceUtc = DateTime.UtcNow;
            ExecutorStuckMs = 0;
            _wedgeAnnounced = false;
            return;
        }
        ExecutorStuckMs = (long)(DateTime.UtcNow - _watchedSinceUtc).TotalMilliseconds;
        if (ExecutorStuckMs > 8000 && !_wedgeAnnounced)
        {
            _wedgeAnnounced = true;
            Bump($"wedge:{running.GetType().Name}");
        }
    }

    // New runs / scene loads construct fresh engine singletons; cheap
    // reference compares keep the subscriptions on the live instances.
    private static void EnsureSubscribed()
    {
        var exec = RunManager.Instance?.ActionExecutor;
        if (!ReferenceEquals(exec, _exec))
        {
            if (_exec is not null) _exec.AfterActionExecuted -= OnAction;
            _exec = exec;
            if (exec is not null) exec.AfterActionExecuted += OnAction;
        }

        var queues = RunManager.Instance?.ActionQueueSet;
        if (!ReferenceEquals(queues, _queueSet))
        {
            if (_queueSet is not null) _queueSet.ActionEnqueued -= OnEnqueued;
            _queueSet = queues;
            if (queues is not null) queues.ActionEnqueued += OnEnqueued;
        }

        var combat = CombatManager.Instance;
        if (!ReferenceEquals(combat, _combat))
        {
            if (_combat is not null)
            {
                _combat.TurnStarted -= OnTurn;
                _combat.TurnEnded -= OnTurn;
                _combat.CombatSetUp -= OnTurn;
                _combat.CombatEnded -= OnCombatRoom;
                _combat.PlayerActionsDisabledChanged -= OnTurn;
            }
            _combat = combat;
            if (combat is not null)
            {
                combat.TurnStarted += OnTurn;
                combat.TurnEnded += OnTurn;
                combat.CombatSetUp += OnTurn;
                combat.CombatEnded += OnCombatRoom;
                combat.PlayerActionsDisabledChanged += OnTurn;
            }
        }

        var stack = NOverlayStack.Instance;
        if (!ReferenceEquals(stack, _stack))
        {
            if (_stack is not null) _stack.Changed -= OnOverlay;
            _stack = stack;
            if (stack is not null) stack.Changed += OnOverlay;
        }
    }

    private static void OnAction(GameAction a) => Bump($"action:{a.GetType().Name}");
    private static void OnEnqueued(GameAction a) => Bump($"enqueued:{a.GetType().Name}");
    private static void OnTurn(CombatState _) => Bump("combat");
    private static void OnCombatRoom(MegaCrit.Sts2.Core.Rooms.CombatRoom _) => Bump("combat_ended");
    private static void OnOverlay() => Bump("overlay");
}
