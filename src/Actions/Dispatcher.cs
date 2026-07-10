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

public readonly record struct DispatchResult(bool Ok, string? Err = null, string? Msg = null)
{
    public static DispatchResult Success() => new(true);
    public static DispatchResult Reject(string err, string msg) => new(false, err, msg);
}

// Each verb validates its own phase, then acts through the same engine
// entry points the UI uses. Must be called on the main thread.
public static class Dispatcher
{
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
        _ => DispatchResult.Reject("bad_request",
            $"unknown action '{action}' (supported: new-run, abandon, option, proceed, map-move, pick-reward, pick-card, pick-relic, confirm, skip, buy, leave, cheat, play, end-turn, potion-use, potion-discard)"),
    };

    // ---- cheats — dev/verification only, not part of the play surface ----

    private static DispatchResult Cheat(JsonElement args)
    {
        if (!TryGetString(args, "name", out var name))
            return DispatchResult.Reject("bad_request", "missing args.name");
        return name switch
        {
            "goto" => CheatGoto(args),
            "gold" => CheatGold(args),
            "heal" => CheatHeal(),
            "hp" => CheatHp(args),
            "wound-enemies" => CheatWoundEnemies(),
            "event" => CheatEvent(args),
            "card" => CheatCard(args),
            var n => DispatchResult.Reject("bad_request",
                $"unknown cheat '{n}' (supported: goto, gold, heal, hp, wound-enemies, event, card)"),
        };
    }

    // Force-enter any event room by model id — the same direct entry the
    // finale uses. Makes every event deterministically testable.
    private static DispatchResult CheatEvent(JsonElement args)
    {
        if (RequirePhase(Phase.Map) is { } err) return err;
        if (!TryGetString(args, "id", out var id))
            return DispatchResult.Reject("bad_request", "missing args.id (event model entry)");
        var model = ModelDb.AllEvents.FirstOrDefault(e =>
            string.Equals(e.Id.Entry, id, StringComparison.OrdinalIgnoreCase));
        if (model is null)
            return DispatchResult.Reject("bad_target",
                $"no event model '{id}' (known: {string.Join(",", ModelDb.AllEvents.Select(e => e.Id.Entry))})");
        var rm = RunManager.Instance;
        if (rm?.DebugOnlyGetState() is null)
            return DispatchResult.Reject("not_ready", "run state not available");

        // The map screen would otherwise stay on top and mask the event.
        NMapScreen.Instance?.Close(animateOut: false);
        if (RunMode.IsHeadless)
            rm.EnterRoom(new EventRoom(model)).GetAwaiter().GetResult();
        else
            Fire(rm.EnterRoom(new EventRoom(model)), "cheat-event");
        return DispatchResult.Success();
    }

    // Set your own HP — lets a test die on the next enemy hit instead of
    // grinding turns.
    private static DispatchResult CheatHp(JsonElement args)
    {
        if (!TryGetInt(args, "value", out var value))
            return DispatchResult.Reject("bad_request", "missing args.value");
        return SetLocalHp(c => Math.Clamp(value, 1, c.MaxHp));
    }

    private static DispatchResult SetLocalHp(Func<Creature, int> hp)
    {
        var rs = RunManager.Instance?.DebugOnlyGetState();
        var creature = rs is null ? null : LocalContext.GetMe(rs)?.Creature;
        if (creature is null)
            return DispatchResult.Reject("not_ready", "no local creature");
        return Reflect.SetProperty(creature, "CurrentHp", hp(creature))
            ? DispatchResult.Success()
            : DispatchResult.Reject("internal", "CurrentHp setter not found");
    }

    // map-move without the reachability check: jump to any node in the act.
    private static DispatchResult CheatGoto(JsonElement args)
    {
        if (RequirePhase(Phase.Map) is { } err) return err;
        if (!TryGetInt(args, "col", out var col) || !TryGetInt(args, "row", out var row))
            return DispatchResult.Reject("bad_request", "missing args.col / args.row");

        var rm = RunManager.Instance;
        var rs = rm?.DebugOnlyGetState();
        if (rm is null || rs?.Map is null)
            return DispatchResult.Reject("not_ready", "run state not available");
        var player = LocalContext.GetMe(rs);
        if (player is null)
            return DispatchResult.Reject("internal", "local player not found");

        var target = Snapshotter.AllMapPoints(rs.Map)
            .FirstOrDefault(p => p.coord.col == col && p.coord.row == row);
        if (target is null)
            return DispatchResult.Reject("bad_target", $"no map node at {col},{row} (see obs.graph)");

        return TravelTo(rm, rs, player, target);
    }

    private static DispatchResult CheatGold(JsonElement args)
    {
        if (!TryGetInt(args, "value", out var value))
            return DispatchResult.Reject("bad_request", "missing args.value");
        var rs = RunManager.Instance?.DebugOnlyGetState();
        var player = rs is null ? null : LocalContext.GetMe(rs);
        if (player is null)
            return DispatchResult.Reject("not_ready", "no local player");
        return Reflect.SetProperty(player, "Gold", Math.Max(0, value))
            ? DispatchResult.Success()
            : DispatchResult.Reject("internal", "Gold setter not found");
    }

    private static DispatchResult CheatHeal() => SetLocalHp(c => c.MaxHp);

    // Spawn a card into the hand (in combat) or the deck (outside) — lets a
    // test exercise one card without replaying runs until one offers it.
    // Mirrors the game's own CardConsoleCmd.
    private static DispatchResult CheatCard(JsonElement args)
    {
        if (!TryGetString(args, "id", out var id))
            return DispatchResult.Reject("bad_request", "missing args.id");
        var rs = RunManager.Instance?.DebugOnlyGetState();
        var player = rs is null ? null : LocalContext.GetMe(rs);
        if (player is null)
            return DispatchResult.Reject("not_ready", "no run in progress");

        var entry = id.ToUpperInvariant();
        var proto = ModelDb.AllCards.FirstOrDefault(c => c.Id.Entry == entry);
        if (proto is null)
            return DispatchResult.Reject("bad_request", $"no card model '{entry}'");

        var inCombat = CombatManager.Instance is { IsInProgress: true };
        ICardScope scope = inCombat ? CombatManager.Instance!.DebugOnlyGetState()! : rs;
        var card = scope.CreateCard(proto, player);
        var add = CardPileCmd.Add(card, inCombat ? PileType.Hand : PileType.Deck);
        if (RunMode.IsHeadless) add.GetAwaiter().GetResult();
        else Fire(add, "cheat-card");
        return DispatchResult.Success();
    }

    // Leaves every enemy at 1 HP with no block, so one normal play ends the
    // fight through the engine's real death pipeline. (Writing 0 directly
    // would skip OnCreatureDeath/EndCombat and wedge the room.)
    private static DispatchResult CheatWoundEnemies()
    {
        var combat = CombatManager.Instance;
        if (combat is null || !combat.IsInProgress)
            return DispatchResult.Reject("bad_phase", "not in combat");
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
            return DispatchResult.Reject("bad_phase", "no run to abandon");
        var game = NGame.Instance;
        if (!rm.IsAbandoned && !rm.IsGameOver)
        {
            // Mid-combat abandon runs visual teardown that NREs headless;
            // swallow it — the forced reset below is what matters there.
            if (game is null)
                try { rm.Abandon(); }
                catch (Exception ex) { SafeLog.Error("abandon (headless)", ex); }
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
        // headless stand-ins referencing the dead run. A combat abandoned
        // mid-fight also leaves CombatManager live — clear it or the phase
        // sticks on combat.
        if (CombatManager.Instance is { IsInProgress: true } cm)
        {
            Reflect.SetPropertyOrBackingField(cm, "IsInProgress", false);
            Reflect.SetField(cm, "_state", null);
        }
        // Stale queued actions must not leak into the next run.
        try { rm.ActionQueueSet?.Reset(); } catch { }
        Reflect.SetPropertyOrBackingField(rm, "State", null);
        Reflect.SetPropertyOrBackingField(rm, "IsAbandoned", false);
        HeadlessState.ResetAll();
        return DispatchResult.Success();
    }

    private static DispatchResult? RequirePhase(Phase need)
    {
        var current = PhaseDetector.Current();
        return current == need
            ? null
            : DispatchResult.Reject("bad_phase",
                $"requires phase {need.AsString()}, current is {current.AsString()}");
    }

    // Single owner of the bad_index reject shape — the grammar agents parse.
    private static DispatchResult? BadIdx(int idx, int count, string what) =>
        idx < 0 || idx >= count
            ? DispatchResult.Reject("bad_index",
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

    // Shared travel tail — the gates every travel verb must pass, then the
    // engine's own vote enqueue.
    private static DispatchResult TravelTo(
        RunManager rm, RunState rs, Player player, MapPoint target)
    {
        if (LocalQueueBlocked(rm, player))
            return DispatchResult.Reject("not_ready", "action queue is paused — retry");
        if (MapIntroBlocksTravel())
            return DispatchResult.Reject("not_ready", "map intro animation — retry");
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
    private static void Enqueue(RunManager rm, GameAction action)
    {
        if (RunMode.IsHeadless) rm.ActionQueueSet.EnqueueWithoutSynchronizing(action);
        else rm.ActionQueueSynchronizer.RequestEnqueue(action);
    }

    // Engine calls that return Tasks must not block the main thread —
    // fire them and surface failures in the log.
    private static void Fire(Task task, string context) =>
        task.ContinueWith(t =>
        {
            if (t.Exception is { } ex) SafeLog.Error(context, ex.InnerException ?? ex);
        }, TaskContinuationOptions.OnlyOnFaulted);

    // ---- main menu ----------------------------------------------------

    private static DispatchResult NewRun(JsonElement args)
    {
        if (RequirePhase(Phase.MainMenu) is { } err) return err;

        if (!TryGetString(args, "character", out var entry))
            return DispatchResult.Reject("bad_request", "missing args.character");

        var character = ModelDb.AllCharacters.FirstOrDefault(c =>
            string.Equals(c.Id.Entry, entry, StringComparison.OrdinalIgnoreCase));
        if (character is null)
        {
            var known = string.Join(",", ModelDb.AllCharacters.Select(c => c.Id.Entry));
            return DispatchResult.Reject("bad_request", $"unknown character '{entry}' (known: {known})");
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
                return DispatchResult.Reject("run_exists",
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
            return DispatchResult.Reject("not_ready", "main menu not ready — retry");

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
            return DispatchResult.Reject("internal",
                $"headless new-run failed: {root.GetType().Name}: {root.Message}");
        }
    }

    // ---- event / rest site ----------------------------------------------

    private static DispatchResult Option(JsonElement args)
    {
        if (!TryGetInt(args, "idx", out var idx))
            return DispatchResult.Reject("bad_request", "missing args.idx");
        return PhaseDetector.Current() switch
        {
            Phase.Event => EventOption(idx),
            Phase.RestSite => RestOption(idx),
            Phase.CrystalSphere => CrystalTool(idx),
            var p => DispatchResult.Reject("bad_phase",
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
                return DispatchResult.Reject("not_ready", "no crystal sphere in progress");
            var grid = entity.GridSize;
            if (col < 0 || col >= grid.X || row < 0 || row >= grid.Y
                || entity.cells[col, row] is not { } cell)
                return DispatchResult.Reject("bad_target", $"no cell at {col},{row} (see obs.cells)");
            entity.CellClicked(cell).GetAwaiter().GetResult();
            return DispatchResult.Success();
        }

        if (Screens.Crystal() is not { } screen)
            return DispatchResult.Reject("not_ready", "crystal sphere screen not mounted");
        var uiCell = FindCrystalCell(Screens.CrystalCellContainer(screen), col, row);
        if (uiCell is null)
            return DispatchResult.Reject("bad_target", $"no cell at {col},{row} (see obs.cells)");
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
            return DispatchResult.Reject("bad_index", "tool idx: 0 = small divination, 1 = big");

        if (RunMode.IsHeadless)
        {
            if (HeadlessCrystal.Entity is not { } entity)
                return DispatchResult.Reject("not_ready", "no crystal sphere in progress");
            entity.SetTool(idx == 0
                ? CrystalMinigame.CrystalSphereToolType.Small
                : CrystalMinigame.CrystalSphereToolType.Big);
            return DispatchResult.Success();
        }

        if (Screens.Crystal() is not { } screen)
            return DispatchResult.Reject("not_ready", "crystal sphere screen not mounted");
        Reflect.Invoke(screen, idx == 0 ? "SetSmallDivination" : "SetBigDivination",
            new object?[] { null });
        return DispatchResult.Success();
    }

    private static DispatchResult EventOption(int idx)
    {
        var ev = Screens.CurrentEvent();
        var opts = ev?.CurrentOptions;
        if (ev is null || opts is null || opts.Count == 0 || ev.IsFinished)
            return DispatchResult.Reject("not_ready", "no options to choose (event finished? try proceed)");
        if (BadIdx(idx, opts.Count, "option") is { } err) return err;
        if (opts[idx].IsLocked)
            return DispatchResult.Reject("not_playable", $"option {idx} is locked");

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
            return DispatchResult.Reject("not_ready", "rest site not mounted");
        if (BadIdx(idx, opts.Count, "option") is { } err) return err;
        if (!opts[idx].IsEnabled)
            return DispatchResult.Reject("not_playable", $"option {idx} is disabled");

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
                if (RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom is { IsVictoryRoom: true })
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
                    var rs2 = RunManager.Instance?.DebugOnlyGetState();
                    if (rs2?.CurrentMapPoint?.PointType == MapPointType.Boss)
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
                    return DispatchResult.Reject("not_ready", "rewards screen not mounted");
                // A debug override left set short-circuits the handler.
                RunManager.Instance!.debugAfterCombatRewardsOverride = null;
                Reflect.Invoke(screen, "OnProceedButtonPressed", new object?[] { null });
                return DispatchResult.Success();

            case Phase.RestSite:
                if (NRestSiteRoom.Instance is { } restRoom)
                {
                    if (restRoom.ProceedButton is not { Visible: true } restBtn)
                        return DispatchResult.Reject("not_ready",
                            "proceed button not visible — choose an option first");
                    restBtn.ForceClick();
                    return DispatchResult.Success();
                }
                return ExitRoomToMap("rest-site proceed");

            case Phase.Treasure:
                if (NRun.Instance?.TreasureRoom is { } chestRoom)
                {
                    if (chestRoom.ProceedButton is not { Visible: true } chestBtn)
                        return DispatchResult.Reject("not_ready",
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
                    return DispatchResult.Reject("not_ready", "crystal sphere screen not mounted");
                Reflect.Invoke(sphere, "OnProceedButtonPressed", new object?[] { null });
                return DispatchResult.Success();

            case var p:
                return DispatchResult.Reject("bad_phase",
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
            return DispatchResult.Reject("internal",
                $"act transition failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Headless room exit: run the room model's own Exit (synchronizer
    // cleanup), then force a fresh MapRoom so PhaseDetector reads map.
    private static DispatchResult ExitRoomToMap(string label, bool exitRoom = true)
    {
        var rm = RunManager.Instance;
        var rs = rm?.DebugOnlyGetState();
        if (rm is null || rs is null)
            return DispatchResult.Reject("not_ready", "run state not available");
        try
        {
            if (exitRoom && rs.CurrentRoom is { } room) Fire(room.Exit(rs), label);
            if (rs.CurrentRoom is not MapRoom)
                rm.EnterRoom(new MapRoom()).GetAwaiter().GetResult();
            return DispatchResult.Success();
        }
        catch (Exception ex)
        {
            return DispatchResult.Reject("internal",
                $"headless {label} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- shop ------------------------------------------------------------

    private static DispatchResult Buy(JsonElement args)
    {
        if (RequirePhase(Phase.Shop) is { } err) return err;
        if (!TryGetString(args, "kind", out var kind))
            return DispatchResult.Reject("bad_request", "missing args.kind");
        if (kind is not ("card" or "colorless" or "relic" or "potion" or "card_removal"))
            return DispatchResult.Reject("bad_request",
                $"unknown kind '{kind}' (card, colorless, relic, potion, card_removal)");
        TryGetInt(args, "idx", out var idx);

        var rs = RunManager.Instance?.DebugOnlyGetState();
        var inv = Screens.ShopInventory(rs);
        if (rs is null || inv is null)
            return DispatchResult.Reject("not_ready", "shop inventory not available");

        MerchantEntry? entry = kind switch
        {
            "card" => inv.CharacterCardEntries.ElementAtOrDefault(idx),
            "colorless" => inv.ColorlessCardEntries.ElementAtOrDefault(idx),
            "relic" => inv.RelicEntries.ElementAtOrDefault(idx),
            "potion" => inv.PotionEntries.ElementAtOrDefault(idx),
            _ => inv.CardRemovalEntry,
        };
        if (entry is null)
            return DispatchResult.Reject("bad_index", $"no {kind} at idx {idx}");
        if (!entry.IsStocked)
            return DispatchResult.Reject("bad_index", $"{kind} idx {idx} is sold out");
        if (!entry.EnoughGold)
            return DispatchResult.Reject("not_enough_gold",
                $"{kind} idx {idx} costs {entry.Cost}");
        if (entry is MerchantPotionEntry && LocalContext.GetMe(rs)?.HasOpenPotionSlots != true)
            return DispatchResult.Reject("not_ready", "no open potion slots");

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
            return DispatchResult.Reject("bad_request", "missing args.idx");
        switch (PhaseDetector.Current())
        {
            case Phase.Treasure:
                {
                    var room = NRun.Instance?.TreasureRoom;
                    var sync = RunManager.Instance?.TreasureRoomRelicSynchronizer;
                    if (sync is null || (room is null && !RunMode.IsHeadless))
                        return DispatchResult.Reject("not_ready", "treasure room not mounted");
                    // The chest must be opened before picking or the room
                    // never wires its exit. (Headless: the treasure snapshot
                    // opens the chest through the room model instead.)
                    if (room is not null && !Screens.ChestOpened(room))
                    {
                        var chest = Reflect.Field<NButton>(room, "_chestButton");
                        if (chest is null)
                            return DispatchResult.Reject("not_ready", "chest button not found");
                        chest.ForceClick();
                    }
                    var relics = sync.CurrentRelics;
                    if (relics is null || relics.Count == 0)
                        return DispatchResult.Reject("not_ready",
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
                        return DispatchResult.Reject("not_ready", "relic row not wired yet — retry");
                    if (BadIdx(idx, holders.Count, "relic") is { } err) return err;
                    Reflect.Invoke(screen, "SelectHolder", holders[idx]);
                    return DispatchResult.Success();
                }

            case var p:
                return DispatchResult.Reject("bad_phase",
                    $"pick-relic is valid in treasure/relic_reward, current is {p.AsString()}");
        }
    }

    // ---- combat rewards --------------------------------------------------

    private static DispatchResult PickReward(JsonElement args)
    {
        if (RequirePhase(Phase.Rewards) is { } err) return err;
        if (!TryGetInt(args, "idx", out var idx))
            return DispatchResult.Reject("bad_request", "missing args.idx");

        if (RunMode.IsHeadless)
            return HeadlessRewards.PickReward(idx) is { } msg
                ? DispatchResult.Reject("bad_index", msg)
                : DispatchResult.Success();

        var screen = Screens.Top<NRewardsScreen>();
        var buttons = screen is null ? null : Screens.RewardButtons(screen);
        if (buttons is null)
            return DispatchResult.Reject("not_ready", "rewards screen not mounted");
        if (BadIdx(idx, buttons.Count, "reward") is { } idxErr) return idxErr;
        if (Screens.ClaimableReward(buttons[idx]) is not { } btn)
            return DispatchResult.Reject("bad_index", $"reward idx {idx} is not claimable (already taken?)");

        // The button's own async claim path; for card tiles the Task stays
        // pending until the pushed sub-screen is driven — poll /obs.
        if (Reflect.Invoke(btn, "GetReward") is Task t) Fire(t, "pick-reward");
        return DispatchResult.Success();
    }

    private static DispatchResult PickCard(JsonElement args)
    {
        if (!TryGetInt(args, "idx", out var idx))
            return DispatchResult.Reject("bad_request", "missing args.idx");
        return PhaseDetector.Current() switch
        {
            Phase.CardReward => PickRewardCard(idx),
            Phase.CardSelect => PickGridCard(idx),
            Phase.HandSelect => PickHandCard(idx),
            Phase.BundleSelect => PickBundle(idx),
            var p => DispatchResult.Reject("bad_phase",
                $"pick-card is valid in card_reward/card_select/hand_select/bundle_select, current is {p.AsString()}"),
        };
    }

    // Bundle offers: pick-card selects the pack. GUI needs a follow-up
    // confirm (the screen previews the pack); host resolves on the pick.
    private static DispatchResult PickBundle(int idx)
    {
        if (RunMode.IsHeadless)
            return HeadlessBundle.Pick(idx) is { } msg
                ? DispatchResult.Reject("bad_index", msg)
                : DispatchResult.Success();

        if (Screens.Top<NChooseABundleSelectionScreen>() is not { } screen)
            return DispatchResult.Reject("not_ready", "bundle screen not mounted");
        var nodes = Screens.BundleNodes(screen);
        if (nodes is null || nodes.Count == 0)
            return DispatchResult.Reject("not_ready", "bundle row not wired yet — retry");
        if (BadIdx(idx, nodes.Count, "bundle") is { } err) return err;
        Reflect.Invoke(screen, "OnBundleClicked", nodes[idx]);
        return DispatchResult.Success();
    }

    private static DispatchResult PickRewardCard(int idx)
    {
        if (RunMode.IsHeadless)
            return HeadlessRewards.PickCard(idx) is { } msg
                ? DispatchResult.Reject("bad_index", msg)
                : DispatchResult.Success();

        var screen = Screens.Top<NCardRewardSelectionScreen>();
        var holders = screen is null ? null : Screens.CardHolders(screen);
        if (screen is null || holders is null || holders.Count == 0)
            return DispatchResult.Reject("not_ready", "card row not wired yet — retry");
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
                ? DispatchResult.Reject("bad_index", msg)
                : DispatchResult.Success();

        // Choose-a-card overlays resolve on the pick itself.
        if (Screens.Top<NChooseACardSelectionScreen>() is { } choose)
        {
            var chooseHolders = Screens.CardHolders(choose);
            if (chooseHolders is null || chooseHolders.Count == 0)
                return DispatchResult.Reject("not_ready", "card row not wired yet — retry");
            if (BadIdx(idx, chooseHolders.Count, "card") is { } chooseErr) return chooseErr;
            Reflect.Invoke(choose, "SelectHolder", chooseHolders[idx]);
            return DispatchResult.Success();
        }

        var screen = Screens.Top<NCardGridSelectionScreen>();
        var cards = screen is null ? null : Screens.GridCards(screen);
        if (screen is null || cards is null || cards.Count == 0)
            return DispatchResult.Reject("not_ready", "card grid not wired yet — retry");
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
                ? DispatchResult.Reject("bad_index", msg)
                : DispatchResult.Success();

        var hand = NPlayerHand.Instance;
        var holders = hand?.ActiveHolders;
        if (hand is null || holders is null || holders.Count == 0)
            return DispatchResult.Reject("not_ready", "no selectable cards in hand — retry");
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
                    return DispatchResult.Reject("not_ready", "host bundle picks resolve on pick-card");
                if (Screens.Top<NChooseABundleSelectionScreen>() is not { } bundleScreen)
                    return DispatchResult.Reject("not_ready", "bundle screen not mounted");
                Reflect.Invoke(bundleScreen, "ConfirmSelection", new object?[] { null });
                return DispatchResult.Success();

            case Phase.CardSelect or Phase.HandSelect when RunMode.IsHeadless:
                return HeadlessPicker.Confirm() is { } msg
                    ? DispatchResult.Reject("not_ready", msg)
                    : DispatchResult.Success();

            case Phase.CardSelect:
            {
                var screen = Screens.Top<NCardGridSelectionScreen>();
                if (screen is null)
                    return DispatchResult.Reject("not_ready", "selection screen not mounted");
                var prefs = Screens.Prefs(screen);
                var count = Screens.SelectedCards(screen).Count();
                switch (screen)
                {
                    // These complete through their confirm button; mirror its gate.
                    case NSimpleCardSelectScreen or NCombatPileCardSelectScreen:
                        var btn = Reflect.Field<NClickableControl>(screen, "_confirmButton");
                        if (btn is not { IsEnabled: true })
                            return DispatchResult.Reject("not_ready",
                                $"confirm not available — {count} selected, need {prefs.MinSelect}..{prefs.MaxSelect}");
                        btn.ForceClick();
                        return DispatchResult.Success();

                    // Transform completes ungated — pre-check or an empty
                    // pick would resolve the selection.
                    case NDeckTransformSelectScreen:
                        if (count < prefs.MinSelect)
                            return DispatchResult.Reject("not_ready",
                                $"{count} selected, need {prefs.MinSelect} (pick-card first)");
                        Reflect.Invoke(screen, "CompleteSelection", new object?[] { null });
                        return DispatchResult.Success();

                    // Deck select confirms at MinSelect; upgrade/enchant only
                    // at MaxSelect (their CheckIfSelectionComplete no-ops
                    // below it) — pre-check so a short pick errors instead.
                    default:
                        var need = screen is NDeckCardSelectScreen ? prefs.MinSelect : prefs.MaxSelect;
                        if (count < need)
                            return DispatchResult.Reject("not_ready",
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
                    return DispatchResult.Reject("not_ready",
                        $"confirm not available — {count} selected, need {prefs.MinSelect}..{prefs.MaxSelect}");
                }
                btn.ForceClick();
                return DispatchResult.Success();
            }

            case var p:
                return DispatchResult.Reject("bad_phase",
                    $"confirm is valid in bundle_select/card_select/hand_select, current is {p.AsString()}");
        }
    }

    // Skip's alternative choice defaults to the lone alternative;
    // otherwise args.idx must name one — same rule in both boots.
    private static DispatchResult? ResolveAltIdx(JsonElement args, int count, out int idx)
    {
        idx = TryGetInt(args, "idx", out var i) ? i : (count == 1 ? 0 : -1);
        return idx < 0 || idx >= count
            ? DispatchResult.Reject("bad_request",
                $"multiple alternatives — pass args.idx in [0,{count - 1}] (see obs.alternatives)")
            : null;
    }

    private static DispatchResult Skip(JsonElement args)
    {
        switch (PhaseDetector.Current())
        {
            case Phase.CardReward when RunMode.IsHeadless:
                if (!HeadlessRewards.InCardPick)
                    return DispatchResult.Reject("not_ready", "no card reward pending");
                // Skip is one of the reward's own "alternative" choices —
                // mirrors the GUI branch below (its _extraOptions gate),
                // not a separate decline path.
                var headlessAlts = HeadlessRewards.Alternatives();
                if (headlessAlts.Count == 0)
                    return DispatchResult.Reject("bad_request", "this card reward cannot be skipped");
                if (ResolveAltIdx(args, headlessAlts.Count, out var headlessAltIdx) is { } headlessErr)
                    return headlessErr;
                return HeadlessRewards.PickCard(headlessAltIdx, alternative: true) is { } altMsg
                    ? DispatchResult.Reject("bad_index", altMsg)
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
                    return DispatchResult.Reject("not_ready", "bundle screen not mounted");
                Reflect.Invoke(bundleScr, "CancelSelection", new object?[] { null });
                return DispatchResult.Success();

            case Phase.CardReward:
                if (Screens.Top<NCardRewardSelectionScreen>() is not { } cardScreen)
                    return DispatchResult.Reject("not_ready", "card reward screen not mounted");
                // Skip is one of the screen's "alternative" choices; picking
                // alternative j resolves the screen's completion source just
                // like a card click does.
                var extras = Screens.ExtraOptions(cardScreen);
                if (extras.Count == 0)
                    return DispatchResult.Reject("bad_request", "this card reward cannot be skipped");
                if (ResolveAltIdx(args, extras.Count, out var altIdx) is { } altErr)
                    return altErr;
                Reflect.Invoke(cardScreen, "OnAlternateRewardSelected", altIdx);
                return DispatchResult.Success();

            case Phase.RelicReward:
                if (Screens.Top<NChooseARelicSelection>() is not { } relicScreen)
                    return DispatchResult.Reject("not_ready", "relic reward screen not mounted");
                Reflect.Invoke(relicScreen, "OnSkipButtonReleased", new object?[] { null });
                return DispatchResult.Success();

            case Phase.Treasure:
                var sync = RunManager.Instance?.TreasureRoomRelicSynchronizer;
                if (sync is null || sync.CurrentRelics is not { Count: > 0 })
                    return DispatchResult.Reject("not_ready", "no relic offer to skip");
                sync.SkipRelicLocally();
                return DispatchResult.Success();

            case Phase.CardSelect:
                if (Screens.Top<NChooseACardSelectionScreen>() is { } chooseSel)
                {
                    if (!Screens.ChooseSkipEnabled(chooseSel))
                        return DispatchResult.Reject("bad_request", "this selection cannot be skipped");
                    Reflect.Invoke(chooseSel, "OnSkipButtonReleased", new object?[] { null });
                    return DispatchResult.Success();
                }
                if (Screens.Top<NCardGridSelectionScreen>() is not { } sel)
                    return DispatchResult.Reject("not_ready", "selection screen not mounted");
                // Only the deck pickers have a close button, gated by
                // prefs.Cancelable (shop removal is; rest-site upgrade isn't).
                if (sel is NSimpleCardSelectScreen or NCombatPileCardSelectScreen
                    || !Screens.Prefs(sel).Cancelable)
                    return DispatchResult.Reject("bad_request", "this selection cannot be skipped");
                Reflect.Invoke(sel, "CloseSelection", new object?[] { null });
                return DispatchResult.Success();

            case var p:
                return DispatchResult.Reject("bad_phase",
                    $"skip is valid in card_reward/card_select/bundle_select/relic_reward/treasure, current is {p.AsString()}");
        }
    }

    // ---- map -----------------------------------------------------------

    private static DispatchResult MapMove(JsonElement args)
    {
        if (!TryGetInt(args, "col", out var col) || !TryGetInt(args, "row", out var row))
            return DispatchResult.Reject("bad_request", "missing args.col / args.row");

        // Crystal-sphere minigame: map-move doubles as the cell click.
        if (PhaseDetector.Current() == Phase.CrystalSphere)
            return CrystalClick(col, row);

        if (RequirePhase(Phase.Map) is { } err) return err;

        var rm = RunManager.Instance;
        var rs = rm?.DebugOnlyGetState();
        if (rm is null || rs is null)
            return DispatchResult.Reject("not_ready", "run state not available");
        var player = LocalContext.GetMe(rs);
        if (player is null)
            return DispatchResult.Reject("internal", "local player not found");

        var reachable = Snapshotter.NextPoints(rs).ToList();
        if (reachable.Count == 0)
            return DispatchResult.Reject("not_ready", "no reachable map nodes");
        var target = reachable.FirstOrDefault(p =>
            p.coord.col == col && p.coord.row == row);
        if (target is null)
        {
            var legal = string.Join(" ", reachable.Select(p => $"{p.coord.col},{p.coord.row}"));
            return DispatchResult.Reject("bad_target",
                $"node {col},{row} not reachable; reachable col,row: [{legal}]");
        }

        return TravelTo(rm, rs, player, target);
    }

    // ---- combat ----------------------------------------------------------

    private static DispatchResult CombatVerb(string action, JsonElement args)
    {
        var combat = CombatManager.Instance;
        if (combat is null || !combat.IsInProgress)
            return DispatchResult.Reject("bad_phase", "not in combat");

        var state = combat.DebugOnlyGetState()!;
        var player = LocalContext.GetMe(state);
        if (player is null)
            return DispatchResult.Reject("internal", "local player not found in combat");

        // Same gates the UI uses to decide whether input registers.
        if (state.CurrentSide != CombatSide.Player)
            return DispatchResult.Reject("not_ready",
                $"current side is {state.CurrentSide.ToString().ToLowerInvariant()}");
        if (combat.PlayerActionsDisabled)
            return DispatchResult.Reject("not_ready", "player actions disabled");

        return action switch
        {
            "play" => Play(args, state, player),
            "potion-use" => PotionUse(args, state, player),
            "end-turn" => EndTurn(state, player),
            // Unreachable via Dispatch's verb grouping — reject rather than
            // silently ending the turn if the grouping ever grows.
            _ => DispatchResult.Reject("bad_request", $"unknown combat verb '{action}'"),
        };
    }

    private static DispatchResult PotionUse(JsonElement args, CombatState state, Player player)
    {
        if (!TryGetInt(args, "slot", out var slot))
            return DispatchResult.Reject("bad_request", "missing args.slot");
        var slots = player.PotionSlots;
        if (slot < 0 || slot >= slots.Count || slots[slot] is null)
            return DispatchResult.Reject("bad_index", $"no potion in slot {slot}");
        var potion = slots[slot]!;
        if (potion.IsQueued || potion.HasBeenRemovedFromState)
            return DispatchResult.Reject("not_playable", $"potion in slot {slot} already used");

        var (target, err) = ResolveTarget(potion.TargetType, args, state, player);
        if (err is not null) return err.Value;
        // Some potions (Attack Potion, …) open a card pick mid-effect.
        HeadlessPicker.Around(() => potion.EnqueueManualUse(target!));
        return DispatchResult.Success();
    }

    // Discarding is legal anywhere a run is active (clears a slot for a
    // better potion).
    private static DispatchResult PotionDiscard(JsonElement args)
    {
        if (!TryGetInt(args, "slot", out var slot))
            return DispatchResult.Reject("bad_request", "missing args.slot");
        var rm = RunManager.Instance;
        var rs = rm?.DebugOnlyGetState();
        var player = rs is null ? null : LocalContext.GetMe(rs);
        if (rm is null || player is null)
            return DispatchResult.Reject("not_ready", "no run in progress");
        var slots = player.PotionSlots;
        if (slot < 0 || slot >= slots.Count || slots[slot] is null)
            return DispatchResult.Reject("bad_index", $"no potion in slot {slot}");

        Enqueue(rm, new DiscardPotionGameAction(
            player, (uint)slot, CombatManager.Instance is { IsInProgress: true }));
        return DispatchResult.Success();
    }

    private static DispatchResult Play(JsonElement args, CombatState state, Player player)
    {
        if (!TryGetString(args, "model", out var model))
            return DispatchResult.Reject("bad_request", "missing args.model");

        var pcs = player.PlayerCombatState!;
        var card = pcs.Hand.Cards.FirstOrDefault(c =>
            c != null && string.Equals(c.Id.Entry, model, StringComparison.OrdinalIgnoreCase));
        if (card is null)
        {
            var hand = string.Join(",", pcs.Hand.Cards.Where(c => c != null).Select(c => c.Id.Entry));
            return DispatchResult.Reject("bad_index", $"no '{model}' in hand [{hand}]");
        }

        // The engine's own playability gate — Unplayable keyword, energy
        // AND stars, ally requirements, hooks, card logic. Without it a
        // star-starved play is accepted, then PlayCardAction silently
        // cancels: card stays, nothing spends, no error.
        if (!card.CanPlay(out var reason, out var preventer))
        {
            var code = reason.HasFlag(UnplayableReason.StarCostTooHigh) ? "not_enough_stars"
                : reason.HasFlag(UnplayableReason.EnergyCostTooHigh) ? "not_enough_energy"
                : "not_playable";
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
        HeadlessPicker.Around(() =>
            Enqueue(RunManager.Instance!, new PlayCardAction(card, target!)));
        return DispatchResult.Success();
    }

    private static DispatchResult EndTurn(CombatState state, Player player)
    {
        Enqueue(RunManager.Instance!, new EndPlayerTurnAction(player, state.RoundNumber));
        return DispatchResult.Success();
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
                            ? (null, DispatchResult.Reject("bad_target", $"no living enemy with id {id}"))
                            : (hit, null);
                    }
                    // Auto-target when there's exactly one alive enemy.
                    var alive = state.Enemies.Where(e => e != null && e.IsAlive).ToList();
                    if (alive.Count == 1) return (alive[0], null);
                    var ids = string.Join(",", alive.Select(e => e.CombatId));
                    return (null, DispatchResult.Reject("bad_target",
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
