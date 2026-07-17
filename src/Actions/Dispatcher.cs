using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using Spirescry.State;
using CrystalMinigame = MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame;

namespace Spirescry.Actions;

public readonly record struct DispatchResult(
    bool Ok,
    string? Err = null,
    string? Msg = null,
    int Status = 400)
{
    // On success, Msg is an optional note for the response (e.g. "the
    // action settled with victory cleanup") — not an error.
    public static DispatchResult Success(string? msg = null) => new(true, null, msg, 200);
    public static DispatchResult Reject(string err, string msg, int status = 400) =>
        new(false, err, msg, status);
}

// Each verb validates its own phase, then acts through the same engine
// entry points the UI uses. Must be called on the main thread.
public static class Dispatcher
{
    private readonly record struct RunContext(
        RunManager Manager,
        RunState State,
        Player? Player);

    // The complete verb and cheat surfaces, in dispatch order. /health
    // advertises them as capabilities so a CLI newer or older than the
    // host detects the skew up front instead of mid-run; the rejection
    // messages below quote the same lists.
    public static readonly string[] Verbs =
    {
        "new-run", "abandon", "option", "proceed", "map-move",
        "pick-reward", "pick-card", "pick-relic", "confirm", "skip",
        "buy", "leave", "cheat", "play", "end-turn", "potion-use",
        "potion-discard",
    };

    public static readonly string[] Cheats =
    {
        "goto", "gold", "heal", "hp", "wound-enemies", "event", "combat",
        "card", "card-upgraded", "relic", "potion", "stars", "energy",
    };

    public static DispatchResult Dispatch(string action, JsonElement args) => action switch
    {
        "new-run" => NewRun(args),
        "abandon" => Abandon(),
        "option" => Option(args),
        "proceed" => Proceed(),
        "map-move" => MapMove(args),
        "pick-reward" => PickReward(args),
        "pick-card" => PickCard(args),
        "pick-relic" => PickRelic(args),
        "confirm" => Confirm(),
        "skip" => Skip(args),
        "buy" => Buy(args),
        "leave" => Leave(),
        "cheat" => Cheat(args),
        "potion-discard" => PotionDiscard(args),
        "play" or "end-turn" or "potion-use" => CombatVerb(action, args),
        _ => DispatchResult.Reject(RejectionCodes.BadRequest,
            $"unknown action '{action}' (supported: {string.Join(", ", Verbs)})"),
    };

    // ---- cheats — dev/verification only, not part of the play surface ----

    private static DispatchResult Cheat(JsonElement args)
    {
        if (!TryGetString(args, "name", out var name))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.name");
        return name switch
        {
            "goto" => CheatGoto(args),
            "gold" => CheatGold(args),
            "heal" => CheatHeal(),
            "hp" => CheatHp(args),
            "wound-enemies" => CheatWoundEnemies(),
            "event" => CheatEvent(args),
            "combat" => CheatCombat(args),
            "card" => CheatCard(args),
            "card-upgraded" => CheatCard(args),
            "relic" => CheatRelic(args),
            "potion" => CheatPotion(args),
            "stars" => SetCombatResource("Stars", args),
            "energy" => SetCombatResource("Energy", args),
            var n => DispatchResult.Reject(RejectionCodes.BadRequest,
                $"unknown cheat '{n}' (supported: {string.Join(", ", Cheats)})"),
        };
    }

    // Force-enter any combat encounter by model id — the combat analog of
    // the event cheat; makes the whole bestiary deterministically testable.
    private static DispatchResult CheatCombat(JsonElement args)
    {
        if (RequirePhase(Phase.Map) is { } err) return err;
        if (!TryGetString(args, "id", out var id))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.id (encounter model entry)");
        var model = ModelDb.AllEncounters.FirstOrDefault(e =>
            string.Equals(e.Id.Entry, id, StringComparison.OrdinalIgnoreCase));
        if (model is null)
            return DispatchResult.Reject(RejectionCodes.BadTarget,
                $"no encounter model '{id}' (known: {string.Join(",", ModelDb.AllEncounters.Select(e => e.Id.Entry))})");
        if (RequireRunContext(out var run, "run state not available") is { } runErr)
            return runErr;

        NMapScreen.Instance?.Close(animateOut: false);
        // The registry holds canonical prototypes; rooms want a run-scoped
        // mutable copy (same contract as the relic cheat).
        var room = new CombatRoom(model.ToMutable(), run.State);
        ResolveOrFire(run.Manager.EnterRoom(room), "cheat-combat");
        return DispatchResult.Success();
    }

    private static DispatchResult CheatPotion(JsonElement args)
    {
        if (!TryGetString(args, "id", out var id))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.id");
        if (RequireRunContext(
            out var run, "no run in progress", "no run in progress") is { } runErr)
            return runErr;
        var player = run.Player!;

        var entry = id.ToUpperInvariant();
        var proto = ModelDb.AllPotions.FirstOrDefault(p => p.Id.Entry == entry);
        if (proto is null)
            return DispatchResult.Reject(RejectionCodes.BadRequest, $"no potion model '{entry}'");

        var procure = PotionCmd.TryToProcure(proto.ToMutable(), player);
        ResolveOrFire(procure, "cheat-potion");
        return DispatchResult.Success();
    }

    // Stars/energy live on the combat state; sweeps refill between plays
    // instead of grinding end-turn cycles to regenerate resources.
    private static DispatchResult SetCombatResource(string prop, JsonElement args)
    {
        if (!TryGetInt(args, "value", out var value))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.value");
        if (CombatManager.Instance is not { IsInProgress: true })
            return DispatchResult.Reject(RejectionCodes.BadPhase, "not in combat");
        if (RequireRunContext(
            out var run, "no combat state", "no combat state") is { } runErr)
            return runErr;
        var pcs = run.Player!.PlayerCombatState;
        if (pcs is null)
            return DispatchResult.Reject(RejectionCodes.NotReady, "no combat state");
        Reflect.SetPropertyOrBackingField(pcs, prop, Math.Max(0, value));
        return DispatchResult.Success();
    }

    // Force-enter any event room by model id — the same direct entry the
    // finale uses. Makes every event deterministically testable.
    private static DispatchResult CheatEvent(JsonElement args)
    {
        if (RequirePhase(Phase.Map) is { } err) return err;
        if (!TryGetString(args, "id", out var id))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.id (event model entry)");
        var model = ModelDb.AllEvents.FirstOrDefault(e =>
            string.Equals(e.Id.Entry, id, StringComparison.OrdinalIgnoreCase));
        if (model is null)
            return DispatchResult.Reject(RejectionCodes.BadTarget,
                $"no event model '{id}' (known: {string.Join(",", ModelDb.AllEvents.Select(e => e.Id.Entry))})");
        if (RequireRunContext(out var run, "run state not available") is { } runErr)
            return runErr;

        // The map screen would otherwise stay on top and mask the event.
        NMapScreen.Instance?.Close(animateOut: false);
        ResolveOrFire(run.Manager.EnterRoom(new EventRoom(model)), "cheat-event");
        return DispatchResult.Success();
    }

    // Set your own HP — lets a test die on the next enemy hit instead of
    // grinding turns.
    private static DispatchResult CheatHp(JsonElement args)
    {
        if (!TryGetInt(args, "value", out var value))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.value");
        return SetLocalHp(c => Math.Clamp(value, 1, c.MaxHp));
    }

    private static DispatchResult SetLocalHp(Func<Creature, int> hp)
    {
        if (RequireRunContext(
            out var run, "no local creature", "no local creature") is { } runErr)
            return runErr;
        var creature = run.Player!.Creature;
        if (creature is null)
            return DispatchResult.Reject(RejectionCodes.NotReady, "no local creature");
        return Reflect.SetProperty(creature, "CurrentHp", hp(creature))
            ? DispatchResult.Success()
            : DispatchResult.Reject(RejectionCodes.Internal, "CurrentHp setter not found");
    }

    // map-move without the reachability check: jump to any node in the act.
    private static DispatchResult CheatGoto(JsonElement args)
    {
        if (RequirePhase(Phase.Map) is { } err) return err;
        if (!TryGetInt(args, "col", out var col) || !TryGetInt(args, "row", out var row))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.col / args.row");

        if (RequireRunContext(
            out var run, "run state not available", "local player not found",
            playerCode: RejectionCodes.Internal) is { } runErr)
            return runErr;
        if (run.State.Map is null)
            return DispatchResult.Reject(RejectionCodes.NotReady, "run state not available");

        var target = Snapshotter.AllMapPoints(run.State.Map)
            .FirstOrDefault(p => p.coord.col == col && p.coord.row == row);
        if (target is null)
            return DispatchResult.Reject(RejectionCodes.BadTarget, $"no map node at {col},{row} (see obs.graph)");

        return TravelTo(run.Manager, run.State, run.Player!, target);
    }

    private static DispatchResult CheatGold(JsonElement args)
    {
        if (!TryGetInt(args, "value", out var value))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.value");
        if (RequireRunContext(
            out var run, "no local player", "no local player") is { } runErr)
            return runErr;
        var player = run.Player!;
        return Reflect.SetProperty(player, "Gold", Math.Max(0, value))
            ? DispatchResult.Success()
            : DispatchResult.Reject(RejectionCodes.Internal, "Gold setter not found");
    }

    private static DispatchResult CheatHeal() => SetLocalHp(c => c.MaxHp);

    // Spawn a card into the hand (in combat) or the deck (outside) — lets a
    // test exercise one card without replaying runs until one offers it.
    // Mirrors the game's own CardConsoleCmd.
    private static DispatchResult CheatCard(JsonElement args)
    {
        if (!TryGetString(args, "id", out var id))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.id");
        if (RequireRunContext(
            out var run, "no run in progress", "no run in progress") is { } runErr)
            return runErr;
        var player = run.Player!;

        var entry = id.ToUpperInvariant();
        var proto = ModelDb.AllCards.FirstOrDefault(c => c.Id.Entry == entry);
        if (proto is null)
            return DispatchResult.Reject(RejectionCodes.BadRequest, $"no card model '{entry}'");

        var inCombat = CombatManager.Instance is { IsInProgress: true };
        ICardScope scope = inCombat
            ? CombatManager.Instance!.DebugOnlyGetState()!
            : run.State;
        var card = scope.CreateCard(proto, player);
        var makeUpgraded = args.TryGetProperty("upgraded", out var upgraded)
            && upgraded.ValueKind == JsonValueKind.True;
        if (TryGetString(args, "name", out var cheatName) && cheatName == "card-upgraded")
            makeUpgraded = true;
        if (makeUpgraded)
        {
            Reflect.Invoke(card, "UpgradeInternal");
            Reflect.Invoke(card, "FinalizeUpgradeInternal");
        }
        var add = CardPileCmd.Add(card, inCombat ? PileType.Hand : PileType.Deck);
        ResolveOrFire(add, "cheat-card");
        return DispatchResult.Success();
    }

    private static DispatchResult CheatRelic(JsonElement args)
    {
        if (!TryGetString(args, "id", out var id))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.id");
        if (RequireRunContext(
            out var run, "no run in progress", "no run in progress") is { } runErr)
            return runErr;
        var rs = run.State;
        var player = run.Player!;

        var entry = id.ToUpperInvariant();
        var proto = ModelDb.AllRelics.FirstOrDefault(r => r.Id.Entry == entry);
        if (proto is null)
            return DispatchResult.Reject(RejectionCodes.BadRequest, $"no relic model '{entry}'");
        if (!proto.IsAllowed(rs!))
            return DispatchResult.Reject(RejectionCodes.NotPlayable,
                $"relic '{entry}' is not allowed in this run");

        var relic = proto.ToMutable();
        // Reward factories initialize player-specific saved properties
        // before obtain (Dusty Tome chooses its AncientCard, for example).
        // A raw model clone does not, so honor the same optional hook.
        relic.GetType().GetMethod("SetupForPlayer",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: [typeof(Player)], modifiers: null)
            ?.Invoke(relic, [player]);

        Task? obtain = null;
        // Pickup relics such as ASTROLABE open a deck picker from their
        // AfterObtained hook. Pre-arm the same deferred selector used by
        // event/shop/rest choices; if a choice is pending, return so the
        // agent can drive it through card_select instead of blocking on a
        // GUI screen that does not exist in the pure host.
        HeadlessPicker.Around(() => obtain = RelicCmd.Obtain(relic, player));
        if (obtain is null)
            return DispatchResult.Reject(RejectionCodes.Internal, "relic obtain did not start");
        if (RunMode.IsHeadless
            && !HeadlessPicker.IsActive
            && !HeadlessBundle.IsActive
            && !HeadlessRewards.IsActive)
            obtain.GetAwaiter().GetResult();
        else
            Fire(obtain, "cheat-relic");
        return DispatchResult.Success();
    }

    // Leaves every enemy at 1 HP with no block, so one normal play ends the
    // fight through the engine's real death pipeline. (Writing 0 directly
    // would skip OnCreatureDeath/EndCombat and wedge the room.)
    private static DispatchResult CheatWoundEnemies()
    {
        var combat = CombatManager.Instance;
        if (combat is null || !combat.IsInProgress)
            return DispatchResult.Reject(RejectionCodes.BadPhase, "not in combat");
        var state = combat.DebugOnlyGetState()!;
        foreach (var c in state.Enemies)
        {
            if (c is null || !c.IsAlive || c.CurrentHp <= 1) continue;
            Reflect.SetProperty(c, "CurrentHp", 1);
            Reflect.SetProperty(c, "Block", 0);
        }
        return DispatchResult.Success();
    }

    private static DateTime _lastNewRunUtc;

    // Ends the active (or save-loaded) run and returns to the main menu,
    // clearing RunManager state so new-run can start fresh.
    private static DispatchResult Abandon()
    {
        var rm = RunManager.Instance;
        if (rm is null || rm.DebugOnlyGetState() is null)
            return DispatchResult.Reject(RejectionCodes.BadPhase, "no run to abandon");
        var game = NGame.Instance;
        if (!rm.IsAbandoned && !rm.IsGameOver)
        {
            if (game is null) HeadlessAbandonTeardown(rm);
            else rm.Abandon();
        }
        if (game is { })
        {
            Fire(game.ReturnToMainMenuAfterRun(), "abandon");
            return DispatchResult.Success();
        }

        // Headless: no NGame to run the menu transition. The post-state it
        // would leave is just State == null and IsAbandoned == false; wipe
        // both so PhaseDetector reads main_menu, and drop any parked
        // headless stand-ins referencing the dead run.
        //
        // CombatManager is a static singleton, so a mid-combat abandon
        // must go through the engine's own Reset: the forced player kills
        // mint a _pendingLoss that references the DEAD run's state, and
        // anything short of Reset leaves it (plus the queue synchronizer's
        // in-combat state) on the instance — the next run's first combat
        // then "ends" the moment it sets up, parking the room transition
        // with a paused queue and phase unknown.
        try { CombatManager.Instance.Reset(graceful: true); }
        catch (Exception ex) { SafeLog.Error("abandon combat reset", ex); }
        // Stale queued actions must not leak into the next run.
        try { rm.ActionQueueSet?.Reset(); } catch { }
        Reflect.SetPropertyOrBackingField(rm, "State", null);
        Reflect.SetPropertyOrBackingField(rm, "IsAbandoned", false);
        HeadlessState.ResetAll();
        return DispatchResult.Success();
    }

    // The engine's Abandon() is UI-first and fire-and-forget: AbandonInternal
    // closes screens (null headless — the engine swallows the NRE) and then
    // kills the players ASYNCHRONOUSLY (per player: forced kill + a scaled
    // wait). Calling it and wiping state right after raced that teardown —
    // its parked continuations fired into the NEXT run's combat, ending it
    // the moment it loaded (instant combat_ended, transition queue left
    // paused, phase parked at unknown). Replicate the meaningful half here,
    // synchronously: same forced-kill pipeline, no UI closes, bounded wait
    // so the wipe below always runs against a quiescent engine.
    private static void HeadlessAbandonTeardown(RunManager rm)
    {
        try
        {
            Reflect.SetPropertyOrBackingField(rm, "IsAbandoned", true);
            if (Reflect.Invoke(rm, "GuaranteeKillAllPlayers") is Task kill
                && !kill.Wait(TimeSpan.FromSeconds(5)))
                SafeLog.Info("abandon: kill-players teardown still pending after 5s — resetting anyway");
        }
        catch (Exception ex) { SafeLog.Error("abandon (headless)", ex); }
    }

    private static DispatchResult? RequirePhase(Phase need)
    {
        var current = PhaseDetector.Current();
        return current == need
            ? null
            : DispatchResult.Reject(RejectionCodes.BadPhase,
                $"requires phase {need.AsString()}, current is {current.AsString()}");
    }

    // Single owner of the bad_index reject shape — the grammar agents parse.
    private static DispatchResult? BadIdx(int idx, int count, string what) =>
        idx < 0 || idx >= count
            ? DispatchResult.Reject(RejectionCodes.BadIndex,
                $"{what} idx {idx} out of range [0,{count - 1}]")
            : null;

    private static bool TryGetInt(JsonElement args, string name, out int value)
    {
        value = 0;
        return args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }

    private static bool TryGetString(JsonElement args, string name, out string value)
    {
        value = "";
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var el)
            || el.ValueKind != JsonValueKind.String)
            return false;
        value = el.GetString()!;
        return true;
    }

    // Single owner of the run singleton/state/local-player lookup. Callers
    // that only need run-level state omit playerMessage; callers that need a
    // local player choose whether its absence is still a readiness gap or an
    // internal invariant violation without repeating the singleton preamble.
    private static DispatchResult? RequireRunContext(
        out RunContext context,
        string stateMessage,
        string? playerMessage = null,
        string playerCode = RejectionCodes.NotReady)
    {
        context = default;
        var manager = RunManager.Instance;
        var state = manager?.DebugOnlyGetState();
        if (manager is null || state is null)
            return DispatchResult.Reject(RejectionCodes.NotReady, stateMessage);

        var player = LocalContext.GetMe(state);
        if (playerMessage is not null && player is null)
            return DispatchResult.Reject(playerCode, playerMessage);

        context = new RunContext(manager, state, player);
        return null;
    }

    // Shared travel tail — the gates every travel verb must pass, then the
    // engine's own vote enqueue.
    private static DispatchResult TravelTo(
        RunManager rm, RunState rs, Player player, MapPoint target)
    {
        if (LocalQueueBlocked(rm, player))
            return DispatchResult.Reject(RejectionCodes.NotReady, "action queue is paused — retry");
        if (MapIntroBlocksTravel())
            return DispatchResult.Reject(RejectionCodes.NotReady, "map intro animation — retry");
        EnqueueMapVote(rm, rs, player, target.coord);
        return DispatchResult.Success();
    }

    // The engine's own travel entry: a vote whose source is read from the
    // synchronizer itself (recomputing it races OnLocationChanged and the
    // mismatch is dropped with a silent Warn). Direct MoveToMapCoordAction
    // is NOT equivalent — TravelToMapCoord's split-vote animation reads
    // the vote display state and parks without it.
    private static void EnqueueMapVote(
        RunManager rm, RunState rs, Player player, MapCoord coord)
    {
        var sync = rm.MapSelectionSynchronizer;
        var source = Reflect.FieldValue(sync, "_acceptingVotesFromSource") is MapLocation loc
            ? loc
            : new MapLocation(rs.CurrentMapCoord, rs.CurrentActIndex);
        var dest = new MapVote { coord = coord, mapGenerationCount = sync.MapGenerationCount };
        rm.ActionQueueSet.EnqueueWithoutSynchronizing(
            new VoteForMapCoordAction(player, source, dest));
    }

    // Traveling while the act-intro animation is still uninterruptable
    // parks MoveToMapCoordAction inside NMapScreen.TravelToMapCoord
    // forever (executor shows Executing, nothing progresses). This is the
    // engine's own CanScroll predicate: safe once the intro tween is gone
    // or past its interruptable point, and input isn't briefly disabled.
    private static bool MapIntroBlocksTravel()
    {
        if (NMapScreen.Instance is not { } map) return false;
        var introBlocked = Reflect.FieldValue(map, "_actAnimTween") is not null
            && Reflect.FieldValue(map, "_canInterruptAnim") is not true;
        // A travel started while the map's open fade is still running
        // parks inside TravelToMapCoord's animation awaits and wedges the
        // serial executor permanently. Gate on the fade tween itself —
        // the engine state, not a wall clock.
        var openFadeRunning = Reflect.FieldValue(map, "_tween") is Godot.Tween t
            && Godot.GodotObject.IsInstanceValid(t) && t.IsRunning();
        return introBlocked || openFadeRunning
            || Reflect.FieldValue(map, "_isInputDisabled") is true;
    }

    // The engine parks paused player queues (event choices, screen
    // transitions); an action enqueued into one sits invisibly until an
    // unpause that may be frames away. Surface that as not_ready so the
    // agent retries deterministically instead of waiting on silence.
    private static bool LocalQueueBlocked(RunManager rm, Player player)
    {
        foreach (var q in EngineQueues.All(rm))
            if (q.owner == player.NetId)
                return q.paused;
        return false;
    }

    // Single owner of the enqueue-mode swap: GUI rides the synchronizer
    // (the queue drains on frames), headless enqueues directly and the
    // queue drains inline before this returns.
    //
    // Returns a success note when the action's own resolution ended the combat out
    // from under its queue bookkeeping: victory teardown clears the
    // queues, so the engine's pop of the finished action finds nothing
    // and throws even though every effect already settled. Any other
    // mid-drain throw aborts the rest of the resolution chain with no
    // executor left running — a shape the stuck-executor watchdog can
    // never see — so announce the wedge here before rethrowing.
    private static DispatchResult Enqueue(RunManager rm, GameAction action)
    {
        if (!RunMode.IsHeadless)
        {
            rm.ActionQueueSynchronizer.RequestEnqueue(action);
            return DispatchResult.Success();
        }
        return ResolveInline(action.GetType().Name,
            () => rm.ActionQueueSet.EnqueueWithoutSynchronizing(action));
    }

    // Headless engine entry points drain synchronously. Victory teardown
    // can therefore clear the queue before the just-finished action pops
    // itself; that exact exception means the effect completed, while any
    // other exception is a genuine abandoned-resolution wedge.
    private static DispatchResult ResolveInline(string actionName, Action resolve)
    {
        var revisionBefore = Signals.Revision;
        try
        {
            resolve();
            ClearFaultedCompletedCombatExecutor();
            return DispatchResult.Success();
        }
        catch (Exception ex)
        {
            var fault = ResolutionGuards.ClassifyInlineFault(
                ex,
                actionName,
                CombatManager.Instance is { IsInProgress: true },
                Signals.Revision != revisionBefore);
            if (fault is InlineFaultKind.VictorySettled)
            {
                // Victory teardown clears the queues before the completed
                // action pops itself. The executor then retains its faulted
                // completion task even though combat and its effects finished;
                // StartCombatInternal observes that same task in the next
                // room and faults before the new fight can start. Normalize
                // the already-empty bookkeeping at this exact known-success
                // boundary. Other inline faults still become wedges below.
                ClearFaultedCompletedCombatExecutor();
                SafeLog.Info($"{actionName} settled by combat teardown ({ex.Message})");
                return DispatchResult.Success(
                    $"{actionName} fully settled with victory cleanup");
            }

            Signals.Bump($"wedge:{actionName}");
            SafeLog.Error($"{actionName} died mid-resolution", ex);
            var partial = fault is InlineFaultKind.Partial;
            return DispatchResult.Reject(
                partial ? RejectionCodes.ResolutionPartial : RejectionCodes.ResolutionFailed,
                partial
                    ? $"{actionName} changed the world before {ex.GetType().Name}: {ex.Message}"
                    : $"{actionName} failed before an observable change: {ex.GetType().Name}: {ex.Message}",
                status: 500);
        }
    }

    private static void ClearFaultedCompletedCombatExecutor()
    {
        var rm = RunManager.Instance;
        if (CombatManager.Instance is { IsInProgress: true }
            || rm?.DebugOnlyGetState()?.CurrentRoom is not CombatRoom { IsPreFinished: true })
            return;

        var executor = rm.ActionExecutor;
        if (executor?.CurrentlyRunningAction is not EndPlayerTurnAction
            || Reflect.FieldValue(executor, "_queueTaskCompletionSource")
                is not TaskCompletionSource<bool> completion
            || !completion.Task.IsFaulted
            || completion.Task.Exception is not { } completionError
            || ResolutionGuards.ClassifyInlineFault(
                completionError,
                "EndPlayerTurnAction",
                combatInProgress: false,
                revisionChanged: false) is not InlineFaultKind.VictorySettled
            || EngineQueues.All(rm).Any(queue => queue.depth > 0))
            return;

        // ActionExecutor stores its last run in this completion source.
        // The engine logs the stale-pop exception via RunSafely, but leaves
        // the faulted task here; the next StartCombatInternal awaits it and
        // faults before registering the new fight. Clear only this completed,
        // faulted EndPlayerTurnAction state. ActionQueueSet.Reset is not safe
        // between rooms because it also deletes every player queue.
        Reflect.SetField(executor, "_queueTaskCompletionSource", null);
        Reflect.SetPropertyOrBackingField(executor, "CurrentlyRunningAction", null);
        SafeLog.Info("cleared faulted victory EndPlayerTurnAction executor state");
    }

    // Engine calls that return Tasks must not block the main thread —
    // fire them and surface failures in the log.
    private static void Fire(Task task, string context)
    {
        Signals.TrackAsync(task, context);
        _ = task.ContinueWith(t =>
        {
            if (t.Exception is { } ex) SafeLog.Error(context, ex.InnerException ?? ex);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    // Model-layer tasks drain synchronously in the pure host; GUI tasks must
    // be tracked without blocking the engine's main thread.
    private static void ResolveOrFire(Task task, string context)
    {
        if (RunMode.IsHeadless)
            task.GetAwaiter().GetResult();
        else
            Fire(task, context);
    }

    // ---- main menu ----------------------------------------------------

    private static DispatchResult NewRun(JsonElement args)
    {
        if (RequirePhase(Phase.MainMenu) is { } err) return err;

        if (!TryGetString(args, "character", out var entry))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.character");

        var character = ModelDb.AllCharacters.FirstOrDefault(c =>
            string.Equals(c.Id.Entry, entry, StringComparison.OrdinalIgnoreCase));
        if (character is null)
        {
            var known = string.Join(",", ModelDb.AllCharacters.Select(c => c.Id.Entry));
            return DispatchResult.Reject(RejectionCodes.BadRequest, $"unknown character '{entry}' (known: {known})");
        }

        // A saved run gets preloaded into RunManager some time after boot
        // (the menu's Continue), and StartNewSingleplayerRun throws "State
        // is already set" while it lingers. Never auto-clear a run we did
        // not launch. Exception: a launch WE fired during the boot window
        // can park inside StartRun's asset preload — State set, no room,
        // nothing ever progressing. Heal that on retry: clear the husk and
        // relaunch. The age gate keeps a genuinely-starting run safe.
        if (RunManager.Instance is { } prior && prior.DebugOnlyGetState() is { } priorState)
        {
            var parked = _lastNewRunUtc != default
                && DateTime.UtcNow - _lastNewRunUtc > TimeSpan.FromSeconds(8)
                && priorState.CurrentRoom is null
                && CombatManager.Instance is not { IsInProgress: true };
            if (!parked)
                return DispatchResult.Reject(RejectionCodes.RunExists,
                    "a run is loaded — if you just called new-run, poll /obs; otherwise call abandon first");
            SafeLog.Info("clearing a parked boot launch and relaunching");
            Reflect.SetPropertyOrBackingField(prior, "State", null);
        }
        _lastNewRunUtc = DateTime.UtcNow;

        // Optional reproducibility knobs; empty seed = engine random.
        var seed = TryGetString(args, "seed", out var seedStr)
            ? seedStr.ToUpperInvariant()
            : "";
        TryGetInt(args, "ascension", out var ascension);

        var game = NGame.Instance;
        if (game is null) return NewRunHeadless(character, seed, ascension);

        // The same gate a human click passes: the menu enables its
        // singleplayer button only once boot preloads settle. A launch
        // fired earlier parks StartRun inside asset loading forever.
        if (game.MainMenu is not { } menu
            || Reflect.FieldValue(menu, "_singleplayerButton")
                is not NClickableControl { IsEnabled: true })
            return DispatchResult.Reject(RejectionCodes.NotReady, "main menu not ready — retry");

        Fire(game.StartNewSingleplayerRun(
            character,
            shouldSave: true,
            acts: ModelDb.Acts.ToList(),
            modifiers: new List<ModifierModel>(),
            seed: seed,
            gameMode: GameMode.Standard,
            ascensionLevel: ascension,
            dailyTime: null), "new-run");
        return DispatchResult.Success();
    }

    // Headless run launch through the engine's own test entry points
    // (RunState.CreateForTest / RunManager.SetUpTest) — the same path
    // sts2's tests use, which never touches NGame, scenes, or Steam.
    private static DispatchResult NewRunHeadless(CharacterModel character, string seed, int ascension)
    {
        try
        {
            var asm = typeof(ModelDb).Assembly;

            // Player.CreateForNewRun<TChar>(UnlockState, seed) is generic in
            // the concrete character subtype — character.GetType() is it.
            var create = typeof(Player).GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .First(m => m.Name == "CreateForNewRun"
                    && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)
                .MakeGenericMethod(character.GetType());
            var unlockAll = asm.GetType("MegaCrit.Sts2.Core.Unlocks.UnlockState")!
                .GetField("all")!.GetValue(null)!;
            var player = (Player)create.Invoke(null, new object[] { unlockAll, 1ul })!;

            if (string.IsNullOrEmpty(seed))
            {
                var seedRng = new MegaCrit.Sts2.Core.Random.Rng((uint)Random.Shared.Next(), 0);
                seed = MegaCrit.Sts2.Core.Helpers.SeedHelper.GetRandomSeed(seedRng, 10);
            }
            var runState = RunState.CreateForTest(
                new[] { player }, ModelDb.Acts.ToList(), new List<ModifierModel>(),
                GameMode.Standard, ascension, seed);

            var rm = RunManager.Instance!;
            var netSvc = new MegaCrit.Sts2.Core.Multiplayer.NetSingleplayerGameService();
            rm.SetUpTest(runState, netSvc, false, false);
            LocalContext.NetId = netSvc.NetId;

            // Without StartedWithNeow the run skips the Neow event and
            // drops straight onto the act-1 map — GUI runs start at Neow,
            // so keep the modes identical.
            Reflect.SetProperty(runState.ExtraFields, "StartedWithNeow", true);

            rm.GenerateRooms();
            rm.Launch();
            rm.EnterAct(0, false).GetAwaiter().GetResult();
            return DispatchResult.Success();
        }
        catch (Exception ex)
        {
            var root = ex.GetBaseException();
            return DispatchResult.Reject(RejectionCodes.Internal,
                $"headless new-run failed: {root.GetType().Name}: {root.Message}");
        }
    }

    // ---- event / rest site ----------------------------------------------

    private static DispatchResult Option(JsonElement args)
    {
        if (!TryGetInt(args, "idx", out var idx))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.idx");
        return PhaseDetector.Current() switch
        {
            Phase.Event => EventOption(idx),
            Phase.RestSite => RestOption(idx),
            Phase.CrystalSphere => CrystalTool(idx),
            var p => DispatchResult.Reject(RejectionCodes.BadPhase,
                $"option is valid in event/rest_site/crystal_sphere, current is {p.AsString()}"),
        };
    }

    // ---- crystal sphere ---------------------------------------------------

    private static DispatchResult CrystalClick(int col, int row)
    {
        // Host: no screen — click the minigame model itself; its own
        // completion fires on the last divination.
        if (RunMode.IsHeadless)
        {
            if (HeadlessCrystal.Entity is not { } entity)
                return DispatchResult.Reject(RejectionCodes.NotReady, "no crystal sphere in progress");
            var grid = entity.GridSize;
            if (col < 0 || col >= grid.X || row < 0 || row >= grid.Y
                || entity.cells[col, row] is not { } cell)
                return DispatchResult.Reject(RejectionCodes.BadTarget, $"no cell at {col},{row} (see obs.cells)");
            entity.CellClicked(cell).GetAwaiter().GetResult();
            return DispatchResult.Success();
        }

        if (Screens.Crystal() is not { } screen)
            return DispatchResult.Reject(RejectionCodes.NotReady, "crystal sphere screen not mounted");
        var uiCell = FindCrystalCell(Screens.CrystalCellContainer(screen), col, row);
        if (uiCell is null)
            return DispatchResult.Reject(RejectionCodes.BadTarget, $"no cell at {col},{row} (see obs.cells)");
        if (Reflect.Invoke(screen, "OnCellClicked", uiCell) is Task cellTask) Fire(cellTask, "crystal-click");
        return DispatchResult.Success();
    }

    private static object? FindCrystalCell(Godot.Node? node, int col, int row)
    {
        if (node is null) return null;
        foreach (var child in node.GetChildren())
        {
            if (child.GetType().Name == "NCrystalSphereCell"
                && Reflect.PropertyValue(child, "Entity") is { } entity
                && Reflect.PropertyValue(entity, "X") is int x && x == col
                && Reflect.PropertyValue(entity, "Y") is int y && y == row)
                return child;
            if (FindCrystalCell(child, col, row) is { } deeper) return deeper;
        }
        return null;
    }

    private static DispatchResult CrystalTool(int idx)
    {
        if (idx is not (0 or 1))
            return DispatchResult.Reject(RejectionCodes.BadIndex, "tool idx: 0 = small divination, 1 = big");

        if (RunMode.IsHeadless)
        {
            if (HeadlessCrystal.Entity is not { } entity)
                return DispatchResult.Reject(RejectionCodes.NotReady, "no crystal sphere in progress");
            entity.SetTool(idx == 0
                ? CrystalMinigame.CrystalSphereToolType.Small
                : CrystalMinigame.CrystalSphereToolType.Big);
            return DispatchResult.Success();
        }

        if (Screens.Crystal() is not { } screen)
            return DispatchResult.Reject(RejectionCodes.NotReady, "crystal sphere screen not mounted");
        Reflect.Invoke(screen, idx == 0 ? "SetSmallDivination" : "SetBigDivination",
            new object?[] { null });
        return DispatchResult.Success();
    }

    private static DispatchResult EventOption(int idx)
    {
        var ev = Screens.CurrentEvent();
        var opts = ev?.CurrentOptions;
        if (ev is null || opts is null || opts.Count == 0 || ev.IsFinished)
            return DispatchResult.Reject(RejectionCodes.NotReady, "no options to choose (event finished? try proceed)");
        if (BadIdx(idx, opts.Count, "option") is { } err) return err;
        if (opts[idx].IsLocked)
            return DispatchResult.Reject(RejectionCodes.NotPlayable, $"option {idx} is locked");

        // Headless: an option that opens a deck picker (transform,
        // upgrade, …) awaits a card selection with no screen to serve it —
        // Around pre-arms the deferred picker for that.
        HeadlessPicker.Around(() =>
            RunManager.Instance!.EventSynchronizer.ChooseLocalOption(idx));
        return DispatchResult.Success();
    }

    private static DispatchResult RestOption(int idx)
    {
        var room = NRestSiteRoom.Instance;
        var opts = Screens.RestOptions();
        if (opts is null)
            return DispatchResult.Reject(RejectionCodes.NotReady, "rest site not mounted");
        if (BadIdx(idx, opts.Count, "option") is { } err) return err;
        if (!opts[idx].IsEnabled)
            return DispatchResult.Reject(RejectionCodes.NotPlayable, $"option {idx} is disabled");

        if (room is not null)
        {
            // ForceClick fires the Godot Released signal so the engine's
            // wired handler chain runs — invoking the handler directly does
            // nothing.
            room.GetButtonForOption(opts[idx]).ForceClick();
            return DispatchResult.Success();
        }

        // Headless: drive the synchronizer directly. SMITH opens a deck
        // picker — the pre-armed deferred picker serves it; the option's
        // Task stays pending until pick-card resolves the selection, so
        // fire it rather than block.
        HeadlessPicker.Around(() =>
            Fire(RunManager.Instance!.RestSiteSynchronizer.ChooseLocalOption(idx), "rest-option"));
        return DispatchResult.Success();
    }

    private static DispatchResult Proceed()
    {
        switch (PhaseDetector.Current())
        {
            case Phase.Event:
                if (NEventRoom.Instance is { })
                {
                    Fire(NEventRoom.Proceed(), "proceed");
                    return DispatchResult.Success();
                }
                // Headless: no visual room to page the dialogue — exit the
                // room model directly (some events, e.g. Neow, end on a
                // dialogue page and never flip IsFinished). The finale event
                // is the exception: its exit is the win.
                if (RequireRunContext(out var eventRun, "run state not available") is { } eventErr)
                    return eventErr;
                if (eventRun.State.CurrentRoom is { IsVictoryRoom: true })
                    return EnterNextActHeadless();
                return ExitRoomToMap("event proceed");

            case Phase.Rewards:
                if (RunMode.IsHeadless)
                {
                    // Skip whatever's unclaimed and leave the combat room —
                    // the GUI proceed button does both in one click.
                    HeadlessRewards.SkipAllAndClear();
                    // A beaten boss doesn't return to this act's map — the
                    // run moves on (the GUI's transition screen makes this
                    // same engine call).
                    if (RequireRunContext(out var rewardsRun, "run state not available") is { } rewardsErr)
                        return rewardsErr;
                    var rs2 = rewardsRun.State;
                    if (rs2.CurrentMapPoint?.PointType == MapPointType.Boss)
                    {
                        if (rs2.CurrentRoom is { } bossRoom) Fire(bossRoom.Exit(rs2), "boss exit");
                        // First boss of a two-boss act exits back to the map
                        // — the agent walks to the second (obs.next carries
                        // it); only the act's last boss moves the run on.
                        if (Snapshotter.SecondBossPending(rs2))
                            return ExitRoomToMap("boss exit", exitRoom: false);
                        return EnterNextActHeadless();
                    }
                    return ExitRoomToMap("rewards proceed");
                }
                if (Screens.Top<NRewardsScreen>() is not { } screen)
                    return DispatchResult.Reject(RejectionCodes.NotReady, "rewards screen not mounted");
                // A debug override left set short-circuits the handler.
                RunManager.Instance!.debugAfterCombatRewardsOverride = null;
                Reflect.Invoke(screen, "OnProceedButtonPressed", new object?[] { null });
                return DispatchResult.Success();

            case Phase.RestSite:
                if (NRestSiteRoom.Instance is { } restRoom)
                {
                    if (restRoom.ProceedButton is not { Visible: true } restBtn)
                        return DispatchResult.Reject(RejectionCodes.NotReady,
                            "proceed button not visible — choose an option first");
                    restBtn.ForceClick();
                    return DispatchResult.Success();
                }
                return ExitRoomToMap("rest-site proceed");

            case Phase.Treasure:
                if (NRun.Instance?.TreasureRoom is { } chestRoom)
                {
                    if (chestRoom.ProceedButton is not { Visible: true } chestBtn)
                        return DispatchResult.Reject(RejectionCodes.NotReady,
                            "proceed button not visible — resolve the chest first (pick-relic / skip)");
                    chestBtn.ForceClick();
                    return DispatchResult.Success();
                }
                // Headless: decline a still-pending offer, then leave.
                var sync = RunManager.Instance?.TreasureRoomRelicSynchronizer;
                if (sync?.CurrentRelics is { Count: > 0 }) sync.SkipRelicLocally();
                return ExitRoomToMap("treasure proceed");

            case Phase.CrystalSphere:
                if (RunMode.IsHeadless)
                {
                    // Cancels the completion source — the engine's own
                    // early-exit path; the awaiting event resumes.
                    HeadlessCrystal.Entity?.ForceMinigameEnd();
                    HeadlessCrystal.Clear();
                    return DispatchResult.Success();
                }
                if (Screens.Crystal() is not { } sphere)
                    return DispatchResult.Reject(RejectionCodes.NotReady, "crystal sphere screen not mounted");
                Reflect.Invoke(sphere, "OnProceedButtonPressed", new object?[] { null });
                return DispatchResult.Success();

            case var p:
                return DispatchResult.Reject(RejectionCodes.BadPhase,
                    $"proceed is valid in event/rewards/rest_site/treasure/crystal_sphere, current is {p.AsString()}");
        }
    }

    // The engine's own act advance: EnterAct(n+1) mid-run, the finale
    // event at the end of the last act, and WinRun when the finale room
    // itself is done.
    private static DispatchResult EnterNextActHeadless()
    {
        try
        {
            RunManager.Instance!.EnterNextAct().GetAwaiter().GetResult();
            return DispatchResult.Success();
        }
        catch (Exception ex)
        {
            return DispatchResult.Reject(RejectionCodes.Internal,
                $"act transition failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Headless room exit: run the room model's own Exit (synchronizer
    // cleanup), then force a fresh MapRoom so PhaseDetector reads map.
    private static DispatchResult ExitRoomToMap(string label, bool exitRoom = true)
    {
        if (RequireRunContext(out var run, "run state not available") is { } runErr)
            return runErr;
        var rm = run.Manager;
        var rs = run.State;
        try
        {
            if (exitRoom && rs.CurrentRoom is { } room) Fire(room.Exit(rs), label);
            if (rs.CurrentRoom is not MapRoom)
                rm.EnterRoom(new MapRoom()).GetAwaiter().GetResult();
            return DispatchResult.Success();
        }
        catch (Exception ex)
        {
            return DispatchResult.Reject(RejectionCodes.Internal,
                $"headless {label} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- shop ------------------------------------------------------------

    private static DispatchResult Buy(JsonElement args)
    {
        var phase = PhaseDetector.Current();
        var fakeMerchant = phase == Phase.Event
            ? Screens.CurrentEvent() as FakeMerchant
            : null;
        if (phase != Phase.Shop && fakeMerchant is null)
            return DispatchResult.Reject(RejectionCodes.BadPhase,
                $"buy requires shop/fake merchant, current is {phase.AsString()}");
        if (!TryGetString(args, "kind", out var kind))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.kind");
        if (kind is not ("card" or "colorless" or "relic" or "potion" or "card_removal"))
            return DispatchResult.Reject(RejectionCodes.BadRequest,
                $"unknown kind '{kind}' (card, colorless, relic, potion, card_removal)");
        TryGetInt(args, "idx", out var idx);

        if (RequireRunContext(
            out var run, "shop inventory not available", "shop inventory not available") is { } runErr)
            return runErr;
        var inv = fakeMerchant?.Inventory ?? Screens.ShopInventory(run.State);
        if (inv is null)
            return DispatchResult.Reject(RejectionCodes.NotReady, "shop inventory not available");

        MerchantEntry? entry = kind switch
        {
            "card" => inv.CharacterCardEntries.ElementAtOrDefault(idx),
            "colorless" => inv.ColorlessCardEntries.ElementAtOrDefault(idx),
            "relic" => inv.RelicEntries.ElementAtOrDefault(idx),
            "potion" => inv.PotionEntries.ElementAtOrDefault(idx),
            _ => inv.CardRemovalEntry,
        };
        if (entry is null)
            return DispatchResult.Reject(RejectionCodes.BadIndex, $"no {kind} at idx {idx}");
        if (!entry.IsStocked)
            return DispatchResult.Reject(RejectionCodes.BadIndex, $"{kind} idx {idx} is sold out");
        if (!entry.EnoughGold)
            return DispatchResult.Reject(RejectionCodes.NotEnoughGold,
                $"{kind} idx {idx} costs {entry.Cost}");
        if (entry is MerchantPotionEntry && run.Player!.HasOpenPotionSlots != true)
            return DispatchResult.Reject(RejectionCodes.NotReady, "no open potion slots");

        // Card removal opens a deck-select sub-screen and the Task stays
        // pending until it's driven — poll /obs. Headless pre-arms the
        // deferred picker to stand in for that screen.
        HeadlessPicker.Around(() => Fire(entry.OnTryPurchaseWrapper(inv, false), "buy"));
        return DispatchResult.Success();
    }

    private static DispatchResult Leave()
    {
        if (RequirePhase(Phase.Shop) is { } err) return err;
        if (NMapScreen.Instance is { } map)
        {
            map.Open(false);
            return DispatchResult.Success();
        }
        // Headless: no map screen — the shop model needs no exit hook,
        // just force the map room.
        return ExitRoomToMap("shop leave", exitRoom: false);
    }

    // ---- treasure / relic reward ------------------------------------------

    private static DispatchResult PickRelic(JsonElement args)
    {
        if (!TryGetInt(args, "idx", out var idx))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.idx");
        switch (PhaseDetector.Current())
        {
            case Phase.Treasure:
                {
                    var room = NRun.Instance?.TreasureRoom;
                    var sync = RunManager.Instance?.TreasureRoomRelicSynchronizer;
                    if (sync is null || (room is null && !RunMode.IsHeadless))
                        return DispatchResult.Reject(RejectionCodes.NotReady, "treasure room not mounted");
                    // The chest must be opened before picking or the room
                    // never wires its exit. (Headless: the treasure snapshot
                    // opens the chest through the room model instead.)
                    if (room is not null && !Screens.ChestOpened(room))
                    {
                        var chest = Reflect.Field<NButton>(room, "_chestButton");
                        if (chest is null)
                            return DispatchResult.Reject(RejectionCodes.NotReady, "chest button not found");
                        chest.ForceClick();
                    }
                    var relics = sync.CurrentRelics;
                    if (relics is null || relics.Count == 0)
                        return DispatchResult.Reject(RejectionCodes.NotReady,
                            "chest opening — poll /obs, then pick-relic again");
                    if (BadIdx(idx, relics.Count, "relic") is { } err) return err;
                    // The award itself lives in the GUI's collection node
                    // (RelicsAwarded → RelicCmd.Obtain); headless has no
                    // node, so grant here. One-shot: the event fires inside
                    // the PickRelicAction this pick enqueues.
                    if (RunMode.IsHeadless)
                    {
                        Action<List<RelicPickingResult>>? award = null;
                        award = results =>
                        {
                            sync.RelicsAwarded -= award;
                            foreach (var r in results)
                            {
                                if (r.type == RelicPickingResultType.Skipped
                                    || r.player is null || r.relic is null) continue;
                                Fire(RelicCmd.Obtain(r.relic.ToMutable(), r.player),
                                    "treasure-relic");
                            }
                        };
                        sync.RelicsAwarded += award;
                    }
                    sync.PickRelicLocally(idx);
                    return DispatchResult.Success();
                }

            case Phase.RelicReward:
                {
                    var screen = Screens.Top<NChooseARelicSelection>();
                    var holders = screen is null ? null : Screens.RelicHolders(screen);
                    if (screen is null || holders is null || holders.Count == 0)
                        return DispatchResult.Reject(RejectionCodes.NotReady, "relic row not wired yet — retry");
                    if (BadIdx(idx, holders.Count, "relic") is { } err) return err;
                    Reflect.Invoke(screen, "SelectHolder", holders[idx]);
                    return DispatchResult.Success();
                }

            case var p:
                return DispatchResult.Reject(RejectionCodes.BadPhase,
                    $"pick-relic is valid in treasure/relic_reward, current is {p.AsString()}");
        }
    }

    // ---- combat rewards --------------------------------------------------

    private static DispatchResult PickReward(JsonElement args)
    {
        if (RequirePhase(Phase.Rewards) is { } err) return err;
        if (!TryGetInt(args, "idx", out var idx))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.idx");

        if (RunMode.IsHeadless)
            return HeadlessRewards.PickReward(idx) is { } msg
                ? DispatchResult.Reject(RejectionCodes.BadIndex, msg)
                : DispatchResult.Success();

        var screen = Screens.Top<NRewardsScreen>();
        var buttons = screen is null ? null : Screens.RewardButtons(screen);
        if (buttons is null)
            return DispatchResult.Reject(RejectionCodes.NotReady, "rewards screen not mounted");
        if (BadIdx(idx, buttons.Count, "reward") is { } idxErr) return idxErr;
        if (Screens.ClaimableReward(buttons[idx]) is not { } btn)
            return DispatchResult.Reject(RejectionCodes.BadIndex, $"reward idx {idx} is not claimable (already taken?)");

        // The button's own async claim path; for card tiles the Task stays
        // pending until the pushed sub-screen is driven — poll /obs.
        if (Reflect.Invoke(btn, "GetReward") is Task t) Fire(t, "pick-reward");
        return DispatchResult.Success();
    }

    private static DispatchResult PickCard(JsonElement args)
    {
        if (!TryGetInt(args, "idx", out var idx))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.idx");
        return PhaseDetector.Current() switch
        {
            Phase.CardReward => PickRewardCard(idx),
            Phase.CardSelect => PickGridCard(idx),
            Phase.HandSelect => PickHandCard(idx),
            Phase.BundleSelect => PickBundle(idx),
            var p => DispatchResult.Reject(RejectionCodes.BadPhase,
                $"pick-card is valid in card_reward/card_select/hand_select/bundle_select, current is {p.AsString()}"),
        };
    }

    // Bundle offers: pick-card selects the pack. GUI needs a follow-up
    // confirm (the screen previews the pack); host resolves on the pick.
    private static DispatchResult PickBundle(int idx)
    {
        if (RunMode.IsHeadless)
            return HeadlessBundle.Pick(idx) is { } msg
                ? DispatchResult.Reject(RejectionCodes.BadIndex, msg)
                : DispatchResult.Success();

        if (Screens.Top<NChooseABundleSelectionScreen>() is not { } screen)
            return DispatchResult.Reject(RejectionCodes.NotReady, "bundle screen not mounted");
        var nodes = Screens.BundleNodes(screen);
        if (nodes is null || nodes.Count == 0)
            return DispatchResult.Reject(RejectionCodes.NotReady, "bundle row not wired yet — retry");
        if (BadIdx(idx, nodes.Count, "bundle") is { } err) return err;
        Reflect.Invoke(screen, "OnBundleClicked", nodes[idx]);
        return DispatchResult.Success();
    }

    private static DispatchResult PickRewardCard(int idx)
    {
        if (RunMode.IsHeadless)
            return HeadlessRewards.PickCard(idx) is { } msg
                ? DispatchResult.Reject(RejectionCodes.BadIndex, msg)
                : DispatchResult.Success();

        var screen = Screens.Top<NCardRewardSelectionScreen>();
        var holders = screen is null ? null : Screens.CardHolders(screen);
        if (screen is null || holders is null || holders.Count == 0)
            return DispatchResult.Reject(RejectionCodes.NotReady, "card row not wired yet — retry");
        if (BadIdx(idx, holders.Count, "card") is { } err) return err;

        Reflect.Invoke(screen, "SelectCard", holders[idx]);
        return DispatchResult.Success();
    }

    // ---- card selection (grid pickers + mid-combat hand select) -----------

    // Toggles: picking an already-selected card deselects it. The screen's
    // own OnCardClicked runs, so max-select behavior matches the UI.
    private static DispatchResult PickGridCard(int idx)
    {
        if (RunMode.IsHeadless)
            return HeadlessPicker.Pick(idx) is { } msg
                ? DispatchResult.Reject(RejectionCodes.BadIndex, msg)
                : DispatchResult.Success();

        // Choose-a-card overlays resolve on the pick itself.
        if (Screens.Top<NChooseACardSelectionScreen>() is { } choose)
        {
            var chooseHolders = Screens.CardHolders(choose);
            if (chooseHolders is null || chooseHolders.Count == 0)
                return DispatchResult.Reject(RejectionCodes.NotReady, "card row not wired yet — retry");
            if (BadIdx(idx, chooseHolders.Count, "card") is { } chooseErr) return chooseErr;
            Reflect.Invoke(choose, "SelectHolder", chooseHolders[idx]);
            return DispatchResult.Success();
        }

        var screen = Screens.Top<NCardGridSelectionScreen>();
        var cards = screen is null ? null : Screens.GridCards(screen);
        if (screen is null || cards is null || cards.Count == 0)
            return DispatchResult.Reject(RejectionCodes.NotReady, "card grid not wired yet — retry");
        if (BadIdx(idx, cards.Count, "card") is { } err) return err;

        Reflect.Invoke(screen, "OnCardClicked", cards[idx]);
        return DispatchResult.Success();
    }

    // Picking routes through OnHolderPressed — the hand's own mode switch,
    // which also auto-swaps the oldest pick when the max is exceeded.
    private static DispatchResult PickHandCard(int idx)
    {
        if (RunMode.IsHeadless)
            return HeadlessPicker.Pick(idx) is { } msg
                ? DispatchResult.Reject(RejectionCodes.BadIndex, msg)
                : DispatchResult.Success();

        var hand = NPlayerHand.Instance;
        var holders = hand?.ActiveHolders;
        if (hand is null || holders is null || holders.Count == 0)
            return DispatchResult.Reject(RejectionCodes.NotReady, "no selectable cards in hand — retry");
        if (BadIdx(idx, holders.Count, "card") is { } err) return err;

        Reflect.Invoke(hand, "OnHolderPressed", holders[idx]);
        return DispatchResult.Success();
    }

    private static DispatchResult Confirm()
    {
        switch (PhaseDetector.Current())
        {
            case Phase.BundleSelect:
                if (RunMode.IsHeadless)
                    return DispatchResult.Reject(RejectionCodes.NotReady, "host bundle picks resolve on pick-card");
                if (Screens.Top<NChooseABundleSelectionScreen>() is not { } bundleScreen)
                    return DispatchResult.Reject(RejectionCodes.NotReady, "bundle screen not mounted");
                Reflect.Invoke(bundleScreen, "ConfirmSelection", new object?[] { null });
                return DispatchResult.Success();

            case Phase.CardSelect or Phase.HandSelect when RunMode.IsHeadless:
                return HeadlessPicker.Confirm() is { } msg
                    ? DispatchResult.Reject(RejectionCodes.NotReady, msg)
                    : DispatchResult.Success();

            case Phase.CardSelect:
            {
                var screen = Screens.Top<NCardGridSelectionScreen>();
                if (screen is null)
                    return DispatchResult.Reject(RejectionCodes.NotReady, "selection screen not mounted");
                var prefs = Screens.Prefs(screen);
                var count = Screens.SelectedCards(screen).Count();
                switch (screen)
                {
                    // These complete through their confirm button; mirror its gate.
                    case NSimpleCardSelectScreen or NCombatPileCardSelectScreen:
                        var btn = Reflect.Field<NClickableControl>(screen, "_confirmButton");
                        if (btn is not { IsEnabled: true })
                            return DispatchResult.Reject(RejectionCodes.NotReady,
                                $"confirm not available — {count} selected, need {prefs.MinSelect}..{prefs.MaxSelect}");
                        btn.ForceClick();
                        return DispatchResult.Success();

                    // Transform completes ungated — pre-check or an empty
                    // pick would resolve the selection.
                    case NDeckTransformSelectScreen:
                        if (count < prefs.MinSelect)
                            return DispatchResult.Reject(RejectionCodes.NotReady,
                                $"{count} selected, need {prefs.MinSelect} (pick-card first)");
                        Reflect.Invoke(screen, "CompleteSelection", new object?[] { null });
                        return DispatchResult.Success();

                    // Deck select confirms at MinSelect; upgrade/enchant only
                    // at MaxSelect (their CheckIfSelectionComplete no-ops
                    // below it) — pre-check so a short pick errors instead.
                    default:
                        var need = screen is NDeckCardSelectScreen ? prefs.MinSelect : prefs.MaxSelect;
                        if (count < need)
                            return DispatchResult.Reject(RejectionCodes.NotReady,
                                $"{count} selected, need {need} (pick-card first)");
                        Reflect.Invoke(screen, "CheckIfSelectionComplete");
                        return DispatchResult.Success();
                }
            }

            case Phase.HandSelect:
            {
                var hand = NPlayerHand.Instance!;
                var btn = Reflect.Field<NClickableControl>(hand, "_selectModeConfirmButton");
                if (btn is not { IsEnabled: true })
                {
                    var prefs = Screens.Prefs(hand);
                    var count = Screens.SelectedCards(hand).Count();
                    return DispatchResult.Reject(RejectionCodes.NotReady,
                        $"confirm not available — {count} selected, need {prefs.MinSelect}..{prefs.MaxSelect}");
                }
                btn.ForceClick();
                return DispatchResult.Success();
            }

            case var p:
                return DispatchResult.Reject(RejectionCodes.BadPhase,
                    $"confirm is valid in bundle_select/card_select/hand_select, current is {p.AsString()}");
        }
    }

    // Skip's alternative choice defaults to the lone alternative;
    // otherwise args.idx must name one — same rule in both boots.
    private static DispatchResult? ResolveAltIdx(JsonElement args, int count, out int idx)
    {
        idx = TryGetInt(args, "idx", out var i) ? i : (count == 1 ? 0 : -1);
        return idx < 0 || idx >= count
            ? DispatchResult.Reject(RejectionCodes.BadRequest,
                $"multiple alternatives — pass args.idx in [0,{count - 1}] (see obs.alternatives)")
            : null;
    }

    private static DispatchResult Skip(JsonElement args)
    {
        switch (PhaseDetector.Current())
        {
            case Phase.CardReward when RunMode.IsHeadless:
                if (!HeadlessRewards.InCardPick)
                    return DispatchResult.Reject(RejectionCodes.NotReady, "no card reward pending");
                // Skip is one of the reward's own "alternative" choices —
                // mirrors the GUI branch below (its _extraOptions gate),
                // not a separate decline path.
                var headlessAlts = HeadlessRewards.Alternatives();
                if (headlessAlts.Count == 0)
                    return DispatchResult.Reject(RejectionCodes.BadRequest, "this card reward cannot be skipped");
                if (ResolveAltIdx(args, headlessAlts.Count, out var headlessAltIdx) is { } headlessErr)
                    return headlessErr;
                return HeadlessRewards.PickCard(headlessAltIdx, alternative: true) is { } altMsg
                    ? DispatchResult.Reject(RejectionCodes.BadIndex, altMsg)
                    : DispatchResult.Success();

            case Phase.CardSelect when RunMode.IsHeadless:
                // Empty selection = the engine's own cancel path.
                HeadlessPicker.CancelIfActive();
                return DispatchResult.Success();

            case Phase.BundleSelect when RunMode.IsHeadless:
                HeadlessBundle.CancelIfActive();
                return DispatchResult.Success();

            case Phase.BundleSelect:
                if (Screens.Top<NChooseABundleSelectionScreen>() is not { } bundleScr)
                    return DispatchResult.Reject(RejectionCodes.NotReady, "bundle screen not mounted");
                Reflect.Invoke(bundleScr, "CancelSelection", new object?[] { null });
                return DispatchResult.Success();

            case Phase.CardReward:
                if (Screens.Top<NCardRewardSelectionScreen>() is not { } cardScreen)
                    return DispatchResult.Reject(RejectionCodes.NotReady, "card reward screen not mounted");
                // Skip is one of the screen's "alternative" choices; picking
                // alternative j resolves the screen's completion source just
                // like a card click does.
                var extras = Screens.ExtraOptions(cardScreen);
                if (extras.Count == 0)
                    return DispatchResult.Reject(RejectionCodes.BadRequest, "this card reward cannot be skipped");
                if (ResolveAltIdx(args, extras.Count, out var altIdx) is { } altErr)
                    return altErr;
                Reflect.Invoke(cardScreen, "OnAlternateRewardSelected", altIdx);
                return DispatchResult.Success();

            case Phase.RelicReward:
                if (Screens.Top<NChooseARelicSelection>() is not { } relicScreen)
                    return DispatchResult.Reject(RejectionCodes.NotReady, "relic reward screen not mounted");
                Reflect.Invoke(relicScreen, "OnSkipButtonReleased", new object?[] { null });
                return DispatchResult.Success();

            case Phase.Treasure:
                var sync = RunManager.Instance?.TreasureRoomRelicSynchronizer;
                if (sync is null || sync.CurrentRelics is not { Count: > 0 })
                    return DispatchResult.Reject(RejectionCodes.NotReady, "no relic offer to skip");
                sync.SkipRelicLocally();
                return DispatchResult.Success();

            case Phase.CardSelect:
                if (Screens.Top<NChooseACardSelectionScreen>() is { } chooseSel)
                {
                    if (!Screens.ChooseSkipEnabled(chooseSel))
                        return DispatchResult.Reject(RejectionCodes.BadRequest, "this selection cannot be skipped");
                    Reflect.Invoke(chooseSel, "OnSkipButtonReleased", new object?[] { null });
                    return DispatchResult.Success();
                }
                if (Screens.Top<NCardGridSelectionScreen>() is not { } sel)
                    return DispatchResult.Reject(RejectionCodes.NotReady, "selection screen not mounted");
                // Only the deck pickers have a close button, gated by
                // prefs.Cancelable (shop removal is; rest-site upgrade isn't).
                if (sel is NSimpleCardSelectScreen or NCombatPileCardSelectScreen
                    || !Screens.Prefs(sel).Cancelable)
                    return DispatchResult.Reject(RejectionCodes.BadRequest, "this selection cannot be skipped");
                Reflect.Invoke(sel, "CloseSelection", new object?[] { null });
                return DispatchResult.Success();

            case var p:
                return DispatchResult.Reject(RejectionCodes.BadPhase,
                    $"skip is valid in card_reward/card_select/bundle_select/relic_reward/treasure, current is {p.AsString()}");
        }
    }

    // ---- map -----------------------------------------------------------

    private static DispatchResult MapMove(JsonElement args)
    {
        if (!TryGetInt(args, "col", out var col) || !TryGetInt(args, "row", out var row))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.col / args.row");

        // Crystal-sphere minigame: map-move doubles as the cell click.
        if (PhaseDetector.Current() == Phase.CrystalSphere)
            return CrystalClick(col, row);

        if (RequirePhase(Phase.Map) is { } err) return err;

        if (RequireRunContext(
            out var run, "run state not available", "local player not found",
            playerCode: RejectionCodes.Internal) is { } runErr)
            return runErr;

        var reachable = Snapshotter.NextPoints(run.State).ToList();
        if (reachable.Count == 0)
            return DispatchResult.Reject(RejectionCodes.NotReady, "no reachable map nodes");
        var target = reachable.FirstOrDefault(p =>
            p.coord.col == col && p.coord.row == row);
        if (target is null)
        {
            var legal = string.Join(" ", reachable.Select(p => $"{p.coord.col},{p.coord.row}"));
            return DispatchResult.Reject(RejectionCodes.BadTarget,
                $"node {col},{row} not reachable; reachable col,row: [{legal}]");
        }

        return TravelTo(run.Manager, run.State, run.Player!, target);
    }

    // ---- combat ----------------------------------------------------------

    private static DispatchResult CombatVerb(string action, JsonElement args)
    {
        // A hand/card picker owns combat input until its completion source is
        // resolved. CombatManager remains in progress underneath, so checking
        // it alone would accept play/end-turn/potion-use and let a second
        // picker overwrite the first HeadlessPicker frame (#31).
        if (RequirePhase(Phase.Combat) is { } phaseErr) return phaseErr;

        var combat = CombatManager.Instance;
        if (combat is null || !combat.IsInProgress)
            return DispatchResult.Reject(RejectionCodes.BadPhase, "not in combat");

        var state = combat.DebugOnlyGetState()!;
        var player = LocalContext.GetMe(state);
        if (player is null)
            return DispatchResult.Reject(RejectionCodes.Internal, "local player not found in combat");

        // Same gates the UI uses to decide whether input registers.
        if (state.CurrentSide != CombatSide.Player)
            return DispatchResult.Reject(RejectionCodes.NotReady,
                $"current side is {state.CurrentSide.ToString().ToLowerInvariant()}");
        if (combat.PlayerActionsDisabled)
            return DispatchResult.Reject(RejectionCodes.NotReady, "player actions disabled");

        return action switch
        {
            "play" => Play(args, state, player),
            "potion-use" => PotionUse(args, state, player),
            "end-turn" => EndTurn(state, player),
            // Unreachable via Dispatch's verb grouping — reject rather than
            // silently ending the turn if the grouping ever grows.
            _ => DispatchResult.Reject(RejectionCodes.BadRequest, $"unknown combat verb '{action}'"),
        };
    }

    private static DispatchResult PotionUse(JsonElement args, CombatState state, Player player)
    {
        if (!TryGetInt(args, "slot", out var slot))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.slot");
        var slots = player.PotionSlots;
        if (slot < 0 || slot >= slots.Count || slots[slot] is null)
            return DispatchResult.Reject(RejectionCodes.BadIndex, $"no potion in slot {slot}");
        var potion = slots[slot]!;
        if (potion.IsQueued || potion.HasBeenRemovedFromState)
            return DispatchResult.Reject(RejectionCodes.NotPlayable, $"potion in slot {slot} already used");

        var (target, err) = ResolveTarget(potion.TargetType, args, state, player);
        if (err is not null) return err.Value;
        // Some potions (Attack Potion, …) open a card pick mid-effect.
        var result = DispatchResult.Success();
        HeadlessPicker.Around(() =>
        {
            if (RunMode.IsHeadless)
                result = ResolveInline(potion.GetType().Name,
                    () => potion.EnqueueManualUse(target!));
            else
                potion.EnqueueManualUse(target!);
        });
        return result;
    }

    // Discarding is legal anywhere a run is active (clears a slot for a
    // better potion).
    private static DispatchResult PotionDiscard(JsonElement args)
    {
        if (!TryGetInt(args, "slot", out var slot))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.slot");
        if (RequireRunContext(
            out var run, "no run in progress", "no run in progress") is { } runErr)
            return runErr;
        var player = run.Player!;
        // Outside combat, discarding remains legal in every run phase. During
        // combat, however, a hand/card picker temporarily owns input even
        // though CombatManager still reports an in-progress fight.
        if (CombatManager.Instance is { IsInProgress: true }
            && RequirePhase(Phase.Combat) is { } phaseErr)
            return phaseErr;
        var slots = player.PotionSlots;
        if (slot < 0 || slot >= slots.Count || slots[slot] is null)
            return DispatchResult.Reject(RejectionCodes.BadIndex, $"no potion in slot {slot}");

        return Enqueue(run.Manager, new DiscardPotionGameAction(
            player, (uint)slot, CombatManager.Instance is { IsInProgress: true }));
    }

    private static DispatchResult Play(JsonElement args, CombatState state, Player player)
    {
        if (!TryGetString(args, "model", out var model))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.model");

        var pcs = player.PlayerCombatState!;
        var card = pcs.Hand.Cards.FirstOrDefault(c =>
            c != null && string.Equals(c.Id.Entry, model, StringComparison.OrdinalIgnoreCase));
        if (card is null)
        {
            var hand = string.Join(",", pcs.Hand.Cards.Where(c => c != null).Select(c => c.Id.Entry));
            return DispatchResult.Reject(RejectionCodes.BadIndex, $"no '{model}' in hand [{hand}]");
        }

        // The engine's own playability gate — Unplayable keyword, energy
        // AND stars, ally requirements, hooks, card logic. Without it a
        // star-starved play is accepted, then PlayCardAction silently
        // cancels: card stays, nothing spends, no error.
        if (!card.CanPlay(out var reason, out var preventer))
        {
            var code = reason.HasFlag(UnplayableReason.StarCostTooHigh) ? RejectionCodes.NotEnoughStars
                : reason.HasFlag(UnplayableReason.EnergyCostTooHigh) ? RejectionCodes.NotEnoughEnergy
                : RejectionCodes.NotPlayable;
            var detail = reason.HasFlag(UnplayableReason.StarCostTooHigh)
                    ? $" (needs {card.GetStarCostWithModifiers()} stars, have {pcs.Stars})"
                : reason.HasFlag(UnplayableReason.EnergyCostTooHigh)
                    ? $" (needs {card.EnergyCost.GetAmountToSpend()} energy, have {pcs.Energy})"
                : preventer is null ? "" : $" (blocked by {preventer.Id.Entry})";
            return DispatchResult.Reject(code, $"{card.Id.Entry}: {reason}{detail}");
        }

        var (target, err) = ResolveTarget(card.TargetType, args, state, player);
        if (err is not null) return err.Value;

        // Headless: cards that ask for a hand/pile pick mid-resolution
        // await the deferred picker; the play action stays pending (phase
        // flips to hand_select) until pick-card resolves it.
        var result = DispatchResult.Success();
        HeadlessPicker.Around(() =>
            result = Enqueue(RunManager.Instance!, new PlayCardAction(card, target!)));
        return result;
    }

    private static DispatchResult EndTurn(CombatState state, Player player)
    {
        return Enqueue(RunManager.Instance!,
            new EndPlayerTurnAction(player, state.RoundNumber));
    }

    private static (Creature? target, DispatchResult? err) ResolveTarget(
        TargetType type, JsonElement args, CombatState state, Player player)
    {
        switch (type)
        {
            case TargetType.AnyEnemy:
                {
                    if (args.TryGetProperty("target", out var tEl)
                        && tEl.ValueKind == JsonValueKind.Number && tEl.TryGetUInt32(out var id))
                    {
                        var hit = state.Enemies.FirstOrDefault(e =>
                            e != null && (e.CombatId ?? 0u) == id && e.IsAlive);
                        return hit is null
                            ? (null, DispatchResult.Reject(RejectionCodes.BadTarget, $"no living enemy with id {id}"))
                            : (hit, null);
                    }
                    // Auto-target when there's exactly one alive enemy.
                    var alive = state.Enemies.Where(e => e != null && e.IsAlive).ToList();
                    if (alive.Count == 1) return (alive[0], null);
                    var ids = string.Join(",", alive.Select(e => e.CombatId));
                    return (null, DispatchResult.Reject(RejectionCodes.BadTarget,
                        $"this card requires args.target; alive enemy ids: [{ids}]"));
                }

            // A non-null target is only valid for AnyEnemy/AnyAlly;
            // PlayCardAction's own resolver picks the recipients for the
            // rest from the card's TargetType.
            case TargetType.AnyAlly:
            case TargetType.AnyPlayer:
                return (player.Creature, null);

            default:
                return (null, null);
        }
    }
}
