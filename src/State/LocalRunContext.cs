using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace Spirescry.State;

internal enum LocalRunContextStatus
{
    Available,
    NoRun,
    MissingLocalPlayer,
}

// An explicitly state-only view for lifecycle edges where the engine has a
// run state but no local seat: terminal metadata and duplicate-launch guards.
// It is deliberately a different type from the complete local run context.
internal readonly record struct RunStateOnlyView(
    RunManager Manager,
    RunState State);

// The one main-thread lookup for the active run and the local seat. A missing
// manager, state, or local player is represented by null instead of exposing a
// partially populated run context.
internal readonly record struct LocalRunContext(
    RunManager Manager,
    RunState State,
    Player Player)
{
    public static LocalRunContext? Current
    {
        get
        {
            return TryGet(out var context) == LocalRunContextStatus.Available
                ? context
                : null;
        }
    }

    public static RunStateOnlyView? StateOnly
    {
        get
        {
            var manager = RunManager.Instance;
            var state = manager?.DebugOnlyGetState();
            return manager is null || state is null
                ? null
                : new RunStateOnlyView(manager, state);
        }
    }

    // Preserve the historical missing-local-player rejection distinction
    // without ever returning a partially populated LocalRunContext.
    public static LocalRunContextStatus TryGet(out LocalRunContext context)
    {
        context = default;
        if (StateOnly is not { } run)
            return LocalRunContextStatus.NoRun;
        var player = LocalPlayer(run.State);
        if (player is null)
            return LocalRunContextStatus.MissingLocalPlayer;
        context = new LocalRunContext(run.Manager, run.State, player);
        return LocalRunContextStatus.Available;
    }

    // Exact-state overloads keep combat and run snapshots atomic with the
    // state they are rendering while centralizing local-seat resolution.
    public static Player? LocalPlayer(RunState state) => LocalContext.GetMe(state);
    public static Player? LocalPlayer(CombatState state) => LocalContext.GetMe(state);
}
