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
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Models.Potions;
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
    // The complete verb surface and the compatibility view of the protocol
    // vocabulary's cheat surface, both in dispatch order. /health advertises
    // them so a CLI newer or older than the host detects skew up front.
    public static readonly string[] Verbs =
    {
        "new-run", "abandon", "option", "proceed", "map-move",
        "pick-reward", "pick-card", "pick-relic", "confirm", "skip",
        "buy", "leave", "cheat", "play", "end-turn", "potion-use",
        "potion-discard",
    };

    public static readonly string[] Cheats =
        ProtocolVocabulary.Cheats.All.Select(shape => shape.Name).ToArray();

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
        "potion-use" => PotionUse(args),
        "play" or "end-turn" => CombatVerb(action, args),
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
            "potion" => CheatPotion(args),
            "relic" => CheatRelic(args),
            "stars" => SetCombatResource("Stars", args),
            "energy" => SetCombatResource("Energy", args),
            "async-fault" => CheatAsyncFault(),
            "engine-error" => CheatEngineError(),
            var n => DispatchResult.Reject(RejectionCodes.BadRequest,
                $"unknown cheat '{n}' (supported: {string.Join(", ", Cheats)})"),
        };
    }

    private static DispatchResult CheatAsyncFault()
    {
        Fire(ForcedAsyncFault(), "forced-async-fault");
        return DispatchResult.Success();
    }

    // Drives the engine's own Error logger synchronously — the regression
    // hook for the engine_error follow channel (async-fault covers the
    // tracked-task stream; this covers log-and-swallow faults).
    private static DispatchResult CheatEngineError()
    {
        MegaCrit.Sts2.Core.Logging.Log.Error(
            "SpirescryForcedException: forced engine log error (cheat engine-error)");
        return DispatchResult.Success();
    }

    private static async Task ForcedAsyncFault()
    {
        await Task.Delay(250).ConfigureAwait(false);
        throw new InvalidOperationException("forced asynchronous failure");
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
        if (RequireRunContext(out var run, "no run in progress") is { } runErr)
            return runErr;
        var player = run.Player;

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
        if (RequireRunContext(out var run, "no combat state") is { } runErr)
            return runErr;
        var pcs = run.Player.PlayerCombatState;
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
        if (RequireRunContext(out var run, "no local creature") is { } runErr)
            return runErr;
        var creature = run.Player.Creature;
        if (creature is null)
            return DispatchResult.Reject(RejectionCodes.BadState, "no local creature");
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
            return DispatchResult.Reject(RejectionCodes.BadState, "run state not available");

        var target = Snapshotter.AllMapPoints(run.State.Map)
            .FirstOrDefault(p => p.coord.col == col && p.coord.row == row);
        if (target is null)
            return DispatchResult.Reject(RejectionCodes.BadTarget, $"no map node at {col},{row} (see obs.graph)");

        return FromDecisionSurface(
            DecisionSurface.Current.TravelTo(run, target));
    }

    private static DispatchResult CheatGold(JsonElement args)
    {
        if (!TryGetInt(args, "value", out var value))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.value");
        if (RequireRunContext(out var run, "no local player") is { } runErr)
            return runErr;
        var player = run.Player;
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
        if (RequireRunContext(out var run, "no run in progress") is { } runErr)
            return runErr;
        var player = run.Player;

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
        if (RequireRunContext(out var run, "no run in progress") is { } runErr)
            return runErr;
        var rs = run.State;
        var player = run.Player;

        var entry = id.ToUpperInvariant();
        var proto = ModelDb.AllRelics.FirstOrDefault(r => r.Id.Entry == entry);
        if (proto is null)
            return DispatchResult.Reject(RejectionCodes.BadRequest, $"no relic model '{entry}'");
        if (!proto.IsAllowed(rs))
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
            && !DecisionSurface.Current.BundleActive
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
        if (LocalRunContext.Current is not { } run)
            return DispatchResult.Reject(RejectionCodes.BadPhase, "no run to abandon");
        return FromDecisionSurface(
            DecisionSurface.Current.AbandonRun(run));
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

    // Single owner of action-level run context rejection semantics. A caller
    // either receives the complete triple or a rejection; the optional
    // missing-player branch preserves established invariant error codes
    // without exposing a partially populated context.
    private static DispatchResult? RequireRunContext(
        out LocalRunContext context,
        string stateMessage,
        string? playerMessage = null,
        string playerCode = RejectionCodes.BadState)
    {
        var status = LocalRunContext.TryGet(out context);
        if (status == LocalRunContextStatus.Available) return null;
        return status == LocalRunContextStatus.MissingLocalPlayer
            && playerMessage is not null
                ? DispatchResult.Reject(playerCode, playerMessage)
                : DispatchResult.Reject(RejectionCodes.BadState, stateMessage);
    }

    private static DispatchResult TravelTo(
        LocalRunContext run, MapPoint target) =>
        FromDecisionSurface(
            DecisionSurface.Current.TravelTo(run, target));

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
        // Current deliberately reports clean no-run until the local seat
        // resolves. The explicit state-only view prevents a second launch in
        // that boot window without exposing a partial local run context.
        if (LocalRunContext.StateOnly is { } prior)
        {
            var parked = _lastNewRunUtc != default
                && DateTime.UtcNow - _lastNewRunUtc > TimeSpan.FromSeconds(8)
                && prior.State.CurrentRoom is null
                && CombatManager.Instance is not { IsInProgress: true };
            if (!parked)
                return DispatchResult.Reject(RejectionCodes.RunExists,
                    "a run is loaded — if you just called new-run, poll /obs; otherwise call abandon first");
            SafeLog.Info("clearing a parked boot launch and relaunching");
            Reflect.SetPropertyOrBackingField(prior.Manager, "State", null);
        }
        _lastNewRunUtc = DateTime.UtcNow;

        // Optional reproducibility knobs; empty seed = engine random.
        var seed = TryGetString(args, "seed", out var seedStr)
            ? seedStr.ToUpperInvariant()
            : "";
        var ascension = 0;
        if (args.TryGetProperty("ascension", out _)
            && (!TryGetInt(args, "ascension", out ascension) || ascension < 0))
            return DispatchResult.Reject(RejectionCodes.BadRequest,
                "args.ascension must be a non-negative 32-bit integer");

        return FromDecisionSurface(
            DecisionSurface.Current.StartNewRun(
                character, seed, ascension));
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
        => FromDecisionSurface(
            DecisionSurface.Current.ClickCrystalCell(col, row));

    private static DispatchResult CrystalTool(int idx)
    {
        if (idx is not (0 or 1))
            return DispatchResult.Reject(RejectionCodes.BadIndex, "tool idx: 0 = small divination, 1 = big");
        return FromDecisionSurface(
            DecisionSurface.Current.ChooseCrystalTool(idx));
    }

    private static DispatchResult EventOption(int idx)
    {
        var ev = Screens.CurrentEvent();
        var opts = ev?.CurrentOptions;
        if (ev is null || opts is null)
            return DispatchResult.Reject(RejectionCodes.NotReady, "event options not mounted yet — retry");
        if (opts.Count == 0 || ev.IsFinished)
            return DispatchResult.Reject(RejectionCodes.BadState,
                "no options to choose (event finished? try proceed)");
        if (BadIdx(idx, opts.Count, "option") is { } err) return err;
        if (opts[idx].IsLocked)
            return DispatchResult.Reject(RejectionCodes.NotPlayable, $"option {idx} is locked");

        return FromDecisionSurface(
            DecisionSurface.Current.ChooseEventOption(idx));
    }

    private static DispatchResult RestOption(int idx)
    {
        var opts = Screens.RestOptions();
        if (opts is null)
            return DispatchResult.Reject(RejectionCodes.NotReady, "rest site not mounted");
        if (BadIdx(idx, opts.Count, "option") is { } err) return err;
        if (!opts[idx].IsEnabled)
            return DispatchResult.Reject(RejectionCodes.NotPlayable, $"option {idx} is disabled");
        return FromDecisionSurface(
            DecisionSurface.Current.ChooseRestOption(idx));
    }

    private static DispatchResult Proceed()
    {
        switch (PhaseDetector.Current())
        {
            case Phase.Event:
                return FromDecisionSurface(
                    DecisionSurface.Current.ProceedEvent());

            case Phase.Rewards:
                return FromDecisionSurface(
                    DecisionSurface.Current.ProceedRewards());

            case Phase.RestSite:
                return FromDecisionSurface(
                    DecisionSurface.Current.ProceedRestSite());

            case Phase.Treasure:
                return FromDecisionSurface(
                    DecisionSurface.Current.ProceedTreasure());

            case Phase.CrystalSphere:
                return FromDecisionSurface(
                    DecisionSurface.Current.ProceedCrystal());

            case var p:
                return DispatchResult.Reject(RejectionCodes.BadPhase,
                    $"proceed is valid in event/rewards/rest_site/treasure/crystal_sphere, current is {p.AsString()}");
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
        if (!TryGetInt(args, "idx", out var idx))
            return DispatchResult.Reject(RejectionCodes.BadRequest,
                "missing or invalid args.idx (32-bit integer required)");
        if (idx < 0)
            return DispatchResult.Reject(RejectionCodes.BadIndex,
                $"{kind} idx {idx} must be non-negative");

        if (RequireRunContext(out var run, "shop inventory not available") is { } runErr)
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
        if (entry is MerchantPotionEntry && run.Player.HasOpenPotionSlots != true)
            return DispatchResult.Reject(RejectionCodes.BadState, "no open potion slots");

        return FromDecisionSurface(
            DecisionSurface.Current.Purchase(inv, entry));
    }

    private static DispatchResult Leave()
    {
        if (RequirePhase(Phase.Shop) is { } err) return err;
        return FromDecisionSurface(
            DecisionSurface.Current.LeaveShop());
    }

    // ---- treasure / relic reward ------------------------------------------

    private static DispatchResult PickRelic(JsonElement args)
    {
        if (!TryGetInt(args, "idx", out var idx))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.idx");
        return PhaseDetector.Current() switch
        {
            Phase.Treasure => FromDecisionSurface(
                DecisionSurface.Current.PickTreasureRelic(idx)),
            Phase.RelicReward => FromDecisionSurface(
                DecisionSurface.Current.PickRelicReward(idx)),
            var phase => DispatchResult.Reject(RejectionCodes.BadPhase,
                $"pick-relic is valid in treasure/relic_reward, current is {phase.AsString()}"),
        };
    }

    // ---- combat rewards --------------------------------------------------

    private static DispatchResult PickReward(JsonElement args)
    {
        if (RequirePhase(Phase.Rewards) is { } err) return err;
        if (!TryGetInt(args, "idx", out var idx))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.idx");

        return FromDecisionSurface(
            DecisionSurface.Current.PickReward(idx));
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
    private static DispatchResult PickBundle(int idx) =>
        FromDecisionSurface(DecisionSurface.Current.PickBundle(idx));

    private static DispatchResult FromDecisionSurface(DecisionSurfaceResult result)
    {
        if (result.Ok) return DispatchResult.Success(result.Message);
        var error = result.Error switch
        {
            DecisionSurfaceError.BadRequest => RejectionCodes.BadRequest,
            DecisionSurfaceError.BadIndex => RejectionCodes.BadIndex,
            DecisionSurfaceError.BadState => RejectionCodes.BadState,
            DecisionSurfaceError.BadTarget => RejectionCodes.BadTarget,
            DecisionSurfaceError.Internal => RejectionCodes.Internal,
            DecisionSurfaceError.NotReady => RejectionCodes.NotReady,
            DecisionSurfaceError.ResolutionFailed =>
                RejectionCodes.ResolutionFailed,
            DecisionSurfaceError.ResolutionPartial =>
                RejectionCodes.ResolutionPartial,
            _ => RejectionCodes.Internal,
        };
        return DispatchResult.Reject(
            error,
            result.Message ?? "decision surface rejected action",
            result.Status);
    }

    private static DispatchResult PickRewardCard(int idx)
    {
        return FromDecisionSurface(
            DecisionSurface.Current.PickRewardCard(idx));
    }

    // ---- card selection (grid pickers + mid-combat hand select) -----------

    // Toggles: picking an already-selected card deselects it. The screen's
    // own OnCardClicked runs, so max-select behavior matches the UI.
    private static DispatchResult PickGridCard(int idx)
    {
        return FromDecisionSurface(
            DecisionSurface.Current.PickCard(idx));
    }

    // Picking routes through OnHolderPressed — the hand's own mode switch,
    // which also auto-swaps the oldest pick when the max is exceeded.
    private static DispatchResult PickHandCard(int idx)
    {
        return FromDecisionSurface(
            DecisionSurface.Current.PickHandCard(idx));
    }

    private static DispatchResult Confirm()
    {
        switch (PhaseDetector.Current())
        {
            case Phase.BundleSelect:
                return FromDecisionSurface(DecisionSurface.Current.ConfirmBundle());

            case Phase.CardSelect:
                return FromDecisionSurface(
                    DecisionSurface.Current.ConfirmCardSelection());

            case Phase.HandSelect:
                return FromDecisionSurface(
                    DecisionSurface.Current.ConfirmHandSelection());

            case var p:
                return DispatchResult.Reject(RejectionCodes.BadPhase,
                    $"confirm is valid in bundle_select/card_select/hand_select, current is {p.AsString()}");
        }
    }

    private static DispatchResult? OptionalAlternativeIndex(
        JsonElement args, out int? idx)
    {
        idx = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("idx", out _))
        {
            if (!TryGetInt(args, "idx", out var requested))
                return DispatchResult.Reject(RejectionCodes.BadRequest,
                    "args.idx must be a 32-bit integer");
            idx = requested;
        }
        return null;
    }

    private static DispatchResult Skip(JsonElement args)
    {
        switch (PhaseDetector.Current())
        {
            case Phase.BundleSelect:
                return FromDecisionSurface(DecisionSurface.Current.SkipBundle());

            case Phase.CardReward:
                if (OptionalAlternativeIndex(args, out var alternativeIdx) is { } idxErr)
                    return idxErr;
                return FromDecisionSurface(
                    DecisionSurface.Current.SkipCardReward(alternativeIdx));

            case Phase.RelicReward:
                return FromDecisionSurface(
                    DecisionSurface.Current.SkipRelicReward());

            case Phase.Treasure:
                return FromDecisionSurface(
                    DecisionSurface.Current.SkipTreasure());

            case Phase.CardSelect:
                return FromDecisionSurface(
                    DecisionSurface.Current.SkipCardSelection());

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

        return TravelTo(run, target);
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
        var player = LocalRunContext.LocalPlayer(state);
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

    private static DispatchResult PotionUse(JsonElement args)
    {
        var phase = PhaseDetector.Current();
        if (phase != Phase.Shop)
        {
            if (CombatManager.Instance is not { IsInProgress: true })
                return DispatchResult.Reject(RejectionCodes.BadPhase,
                    "potion-use is available in combat; Foul Potion can also be redeemed at a merchant");
            return CombatVerb("potion-use", args);
        }
        if (!TryGetInt(args, "slot", out var slot))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.slot");
        if (RequireRunContext(out var run, "no run in progress") is { } runErr)
            return runErr;
        var player = run.Player;
        var slots = player.PotionSlots;
        if (slot < 0 || slot >= slots.Count || slots[slot] is null)
            return DispatchResult.Reject(RejectionCodes.BadIndex, $"no potion in slot {slot}");
        var potion = slots[slot]!;
        if (potion.IsQueued || potion.HasBeenRemovedFromState)
            return DispatchResult.Reject(RejectionCodes.NotPlayable, $"potion in slot {slot} already used");

        // Mirror the potion popup's model-layer gates. The headless fallback
        // covers only the custom UI-node check: Phase.Shop has already
        // established the semantic MerchantRoom and host mode intentionally
        // has no NMerchantButton to satisfy that check.
        var usable = potion is FoulPotion
            && potion.Usage == PotionUsage.AnyTime
            && player.Creature is { IsDead: false }
            && player.CanUseOrRemovePotions
            && DecisionSurface.Current
                .MerchantPotionInteractionAvailable(potion);
        if (!usable)
            return DispatchResult.Reject(RejectionCodes.NotPlayable,
                $"{potion.Id.Entry} has no available merchant interaction in this shop");
        return FromDecisionSurface(
            DecisionSurface.Current.UsePotion(potion, target: null));
    }

    private static DispatchResult PotionUse(
        JsonElement args, CombatState state, Player player)
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
        // Some potions (Attack Potion, …) open a card pick mid-effect;
        // completion ownership belongs to the boot-selected adapter.
        return FromDecisionSurface(
            DecisionSurface.Current.UsePotion(potion, target));
    }

    // Discarding is legal anywhere a run is active (clears a slot for a
    // better potion).
    private static DispatchResult PotionDiscard(JsonElement args)
    {
        if (!TryGetInt(args, "slot", out var slot))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.slot");
        if (RequireRunContext(out var run, "no run in progress") is { } runErr)
            return runErr;
        var player = run.Player;
        // Outside combat, discarding remains legal in every run phase. During
        // combat, however, a hand/card picker temporarily owns input even
        // though CombatManager still reports an in-progress fight.
        if (CombatManager.Instance is { IsInProgress: true }
            && RequirePhase(Phase.Combat) is { } phaseErr)
            return phaseErr;
        var slots = player.PotionSlots;
        if (slot < 0 || slot >= slots.Count || slots[slot] is null)
            return DispatchResult.Reject(RejectionCodes.BadIndex, $"no potion in slot {slot}");

        return FromDecisionSurface(
            DecisionSurface.Current.DiscardPotion(
                run,
                (uint)slot,
                CombatManager.Instance is { IsInProgress: true }));
    }

    private static DispatchResult Play(JsonElement args, CombatState state, Player player)
    {
        if (!TryGetString(args, "model", out var selector))
            return DispatchResult.Reject(RejectionCodes.BadRequest, "missing args.model");

        var pcs = player.PlayerCombatState!;
        var card = pcs.Hand.Cards.FirstOrDefault(c =>
            c != null && string.Equals(State.CardSpecifier.From(c), selector, StringComparison.OrdinalIgnoreCase));
        if (card is null)
        {
            var hand = string.Join(",", pcs.Hand.Cards.Where(c => c != null)
                .Select(State.CardSpecifier.From));
            return DispatchResult.Reject(RejectionCodes.BadIndex,
                $"no '{selector}' in hand [{hand}]");
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

        return FromDecisionSurface(
            DecisionSurface.Current.PlayCard(
                RunManager.Instance!, card, target));
    }

    private static DispatchResult EndTurn(CombatState state, Player player)
    {
        return FromDecisionSurface(
            DecisionSurface.Current.EndTurn(
                RunManager.Instance!, player, state.RoundNumber));
    }

    private static (Creature? target, DispatchResult? err) ResolveTarget(
        TargetType type, JsonElement args, CombatState state, Player player)
    {
        if (args.TryGetProperty("target", out var targetArg)
            && (targetArg.ValueKind != JsonValueKind.Number || !targetArg.TryGetUInt32(out _)))
            return (null, DispatchResult.Reject(RejectionCodes.BadRequest,
                "args.target must be an unsigned 32-bit combat id"));

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
