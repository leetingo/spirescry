using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using CrystalMinigame = MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame;
using CrystalScreen = MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen;

namespace Spirescry.State;

// One boot-selected boundary for enumerating and acting on the local seat's
// current decision without consulting the boot mode at each call site.
internal interface IDecisionSurface
{
    Phase? ActivePhase { get; }
    bool RequiresSettlementFrameStability { get; }
    bool DeferredCardChoiceActive { get; }
    bool CanBeginTreasureRelicPicking { get; }
    bool BundleActive { get; }
    BundleDecision? Bundle { get; }
    CrystalMinigame? Crystal { get; }
    RestSiteDecision? RestSite { get; }
    TreasureDecision Treasure { get; }
    RewardsDecision? Rewards { get; }
    CardRewardDecision? CardReward { get; }
    CardSelectDecision? CardSelect { get; }
    HandSelectDecision? HandSelect { get; }

    DecisionSurfaceResult PickBundle(int idx);
    DecisionSurfaceResult ConfirmBundle();
    DecisionSurfaceResult SkipBundle();

    DecisionSurfaceResult ChooseEventOption(int idx);
    DecisionSurfaceResult ChooseRestOption(int idx);
    DecisionSurfaceResult ChooseCrystalTool(int idx);
    DecisionSurfaceResult ProceedEvent();
    DecisionSurfaceResult ProceedRewards();
    DecisionSurfaceResult ProceedRestSite();
    DecisionSurfaceResult ProceedTreasure();
    DecisionSurfaceResult ProceedCrystal();
    DecisionSurfaceResult Purchase(MerchantInventory inventory, MerchantEntry entry);
    DecisionSurfaceResult LeaveShop();
    DecisionSurfaceResult PickReward(int idx);
    DecisionSurfaceResult PickRewardCard(int idx);
    DecisionSurfaceResult PickCard(int idx);
    DecisionSurfaceResult PickHandCard(int idx);
    DecisionSurfaceResult ConfirmCardSelection();
    DecisionSurfaceResult ConfirmHandSelection();
    DecisionSurfaceResult SkipCardReward(int? alternativeIdx);
    DecisionSurfaceResult SkipCardSelection();
    DecisionSurfaceResult PickRelicReward(int idx);
    DecisionSurfaceResult SkipRelicReward();
    DecisionSurfaceResult PickTreasureRelic(int idx);
    DecisionSurfaceResult SkipTreasure();
    DecisionSurfaceResult StartNewRun(
        CharacterModel character, string seed, int ascension);
    DecisionSurfaceResult AbandonRun(LocalRunContext run);
    DecisionSurfaceResult TravelTo(LocalRunContext run, MapPoint target);
    DecisionSurfaceResult ClickCrystalCell(int col, int row);
    bool MerchantPotionInteractionAvailable(PotionModel potion);
    DecisionSurfaceResult UsePotion(PotionModel potion, Creature? target);
    DecisionSurfaceResult DiscardPotion(
        LocalRunContext run, uint slot, bool inCombat);
    DecisionSurfaceResult PlayCard(
        RunManager manager, CardModel card, Creature? target);
    DecisionSurfaceResult EndTurn(
        RunManager manager, Player player, int roundNumber);
    DecisionSurfaceResult ObtainRelic(RelicModel relic, Player player);
    DecisionSurfaceResult AcceptAbandonConfirmation(RunManager manager);
    DecisionSurfaceResult ParkCombatRewards(CombatRoom room);
    void InstallPersistentCardSelector();
    void TrackOrAwaitModelTask(Task task, string context);
    void ResetRunChoices();

    // The headless Harmony hook asks the selected adapter whether it owns
    // this completion. GUI returns false so the engine's real screen keeps
    // ownership; headless parks the offer on its stand-in and returns true.
    bool TryOwnBundleCompletion(
        IReadOnlyList<IReadOnlyList<CardModel>> bundles,
        out Task<IEnumerable<CardModel>> completion);
    bool TryOwnCrystalScreen(CrystalMinigame minigame);
    bool TryOwnCustomRewardCompletion(
        Player player,
        IReadOnlyList<Reward> rewards,
        out Task completion);
}

internal sealed record BundleDecision(
    IReadOnlyList<IReadOnlyList<CardModel>> Bundles,
    bool Confirmable,
    bool Cancelable);

internal sealed record RestSiteDecision(
    IReadOnlyList<RestSiteOption> Options,
    bool ProceedAvailable);

internal sealed record TreasureDecision(
    bool ChestOpened,
    bool ProceedAvailable,
    IReadOnlyList<RelicModel> Relics);

internal sealed record RewardSlot(int Index, Reward Reward);

internal sealed record RewardsDecision(IReadOnlyList<RewardSlot> Rewards);

internal sealed record CardRewardDecision(
    IReadOnlyList<CardModel> Cards,
    IReadOnlyList<LocString?> AlternativeTitles);

internal sealed record CardSelectDecision(
    IReadOnlyList<CardModel> Cards,
    IReadOnlySet<CardModel> Selected,
    LocString? Prompt,
    int MinSelect,
    int MaxSelect,
    bool Cancelable,
    bool Confirmable);

internal sealed record HandSelectDecision(
    IReadOnlyList<CardModel?> Cards,
    IReadOnlyList<CardModel> Selected,
    LocString? Prompt,
    int MinSelect,
    int MaxSelect,
    bool Confirmable,
    bool IncludePlayer);

internal enum DecisionSurfaceError
{
    BadRequest,
    BadIndex,
    BadState,
    BadTarget,
    Internal,
    NotReady,
    ResolutionFailed,
    ResolutionPartial,
}

internal readonly record struct DecisionSurfaceResult(
    DecisionSurfaceError? Error,
    string? Message,
    int Status)
{
    public bool Ok => Error is null;

    public static DecisionSurfaceResult Success(string? message = null) =>
        new(null, message, 200);
    public static DecisionSurfaceResult Reject(
        DecisionSurfaceError error, string message, int status = 400) =>
        new(error, message, status);
}

internal static class DecisionSurfaceActions
{
    public static void Track(Task task, string context)
    {
        Signals.TrackAsync(task, context);
        _ = task.ContinueWith(t =>
        {
            if (t.Exception is { } ex) SafeLog.Error(context, ex.InnerException ?? ex);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public static DecisionSurfaceResult ResolveAlternativeIndex(
        int? requestedIdx, int count, out int idx)
    {
        idx = requestedIdx ?? (count == 1 ? 0 : -1);
        return idx < 0 || idx >= count
            ? DecisionSurfaceResult.Reject(
                DecisionSurfaceError.BadRequest,
                $"multiple alternatives — pass args.idx in [0,{count - 1}] (see obs.alternatives)")
            : DecisionSurfaceResult.Success();
    }

    public static DecisionSurfaceResult TravelTo(
        LocalRunContext run, MapPoint target)
    {
        if (LocalQueueBlocked(run.Manager, run.Player))
            return DecisionSurfaceResult.Reject(
                DecisionSurfaceError.NotReady, "action queue is paused — retry");

        // Read the source from the synchronizer itself: recomputing it races
        // OnLocationChanged, and the engine silently drops a mismatched vote.
        var synchronizer = run.Manager.MapSelectionSynchronizer;
        var source = Reflect.FieldValue(
            synchronizer, "_acceptingVotesFromSource") is MapLocation location
                ? location
                : new MapLocation(
                    run.State.CurrentMapCoord, run.State.CurrentActIndex);
        var destination = new MapVote
        {
            coord = target.coord,
            mapGenerationCount = synchronizer.MapGenerationCount,
        };
        run.Manager.ActionQueueSet.EnqueueWithoutSynchronizing(
            new VoteForMapCoordAction(run.Player, source, destination));
        return DecisionSurfaceResult.Success();
    }

    public static bool MapAnimationBlocksTravel()
    {
        if (NMapScreen.Instance is not { } map) return false;
        var introBlocked = Reflect.FieldValue(map, "_actAnimTween") is not null
            && Reflect.FieldValue(map, "_canInterruptAnim") is not true;
        var openFadeRunning = Reflect.FieldValue(map, "_tween") is Godot.Tween tween
            && Godot.GodotObject.IsInstanceValid(tween) && tween.IsRunning();
        return introBlocked || openFadeRunning
            || Reflect.FieldValue(map, "_isInputDisabled") is true;
    }

    public static DecisionSurfaceResult ResolveInline(
        string actionName, Action resolve)
    {
        var revisionBefore = Signals.Revision;
        try
        {
            resolve();
            ClearFaultedCompletedCombatExecutor();
            return DecisionSurfaceResult.Success();
        }
        catch (Exception ex)
        {
            var fault = Settlement.Current.ClassifyInlineFault(
                ex,
                actionName,
                CombatManager.Instance is { IsInProgress: true },
                Signals.Revision != revisionBefore);
            if (fault is InlineFaultKind.VictorySettled)
            {
                ClearFaultedCompletedCombatExecutor();
                SafeLog.Info(
                    $"{actionName} settled by combat teardown ({ex.Message})");
                return DecisionSurfaceResult.Success(
                    $"{actionName} fully settled with victory cleanup");
            }

            Signals.Bump($"wedge:{actionName}");
            SafeLog.Error($"{actionName} died mid-resolution", ex);
            var partial = fault is InlineFaultKind.Partial;
            return DecisionSurfaceResult.Reject(
                partial
                    ? DecisionSurfaceError.ResolutionPartial
                    : DecisionSurfaceError.ResolutionFailed,
                partial
                    ? $"{actionName} changed the world before {ex.GetType().Name}: {ex.Message}"
                    : $"{actionName} failed before an observable change: {ex.GetType().Name}: {ex.Message}",
                status: 500);
        }
    }

    private static bool LocalQueueBlocked(RunManager manager, Player player)
    {
        foreach (var queue in EngineQueues.All(manager))
            if (queue.owner == player.NetId)
                return queue.paused;
        return false;
    }

    private static void ClearFaultedCompletedCombatExecutor()
    {
        if (LocalRunContext.Current is not { } run
            || CombatManager.Instance is { IsInProgress: true }
            || run.State.CurrentRoom is not CombatRoom { IsPreFinished: true })
            return;

        var executor = run.Manager.ActionExecutor;
        if (executor?.CurrentlyRunningAction is not EndPlayerTurnAction
            || Reflect.FieldValue(executor, "_queueTaskCompletionSource")
                is not TaskCompletionSource<bool> completion
            || !completion.Task.IsFaulted
            || completion.Task.Exception is not { } completionError
            || Settlement.Current.ClassifyInlineFault(
                completionError,
                "EndPlayerTurnAction",
                combatInProgress: false,
                revisionChanged: false) is not InlineFaultKind.VictorySettled
            || EngineQueues.All(run.Manager).Any(queue => queue.depth > 0))
            return;

        var completionCleared = Reflect.SetField(
            executor, "_queueTaskCompletionSource", null);
        var actionCleared = Reflect.SetPropertyOrBackingField(
            executor, "CurrentlyRunningAction", null);
        SafeLog.Info(completionCleared && actionCleared
            ? "cleared faulted victory EndPlayerTurnAction executor state"
            : "could not fully clear faulted victory EndPlayerTurnAction executor state");
    }

    // Relic-reward overlays are engine screens in both boots; unlike the
    // parked reward and picker flows, they need no adapter-specific path.
    // Keep the screen grammar in one place while both adapters expose it
    // through the boot-selected contract.
    public static DecisionSurfaceResult PickRelicReward(int idx)
    {
        var screen = Screens.Top<NChooseARelicSelection>();
        var holders = screen is null ? null : Screens.RelicHolders(screen);
        if (screen is null || holders is null || holders.Count == 0)
            return DecisionSurfaceResult.Reject(
                DecisionSurfaceError.NotReady, "relic row not wired yet — retry");
        if (idx < 0 || idx >= holders.Count)
            return DecisionSurfaceResult.Reject(
                DecisionSurfaceError.BadIndex,
                $"relic idx {idx} out of range [0,{holders.Count - 1}]");
        Reflect.Invoke(screen, "SelectHolder", holders[idx]);
        return DecisionSurfaceResult.Success();
    }

    public static DecisionSurfaceResult SkipRelicReward()
    {
        if (Screens.Top<NChooseARelicSelection>() is not { } screen)
            return DecisionSurfaceResult.Reject(
                DecisionSurfaceError.NotReady, "relic reward screen not mounted");
        Reflect.Invoke(screen, "OnSkipButtonReleased", new object?[] { null });
        return DecisionSurfaceResult.Success();
    }
}

internal static class DecisionSurface
{
    private static IDecisionSurface? _current;

    public static IDecisionSurface Current => _current
        ?? throw new InvalidOperationException("decision surface not selected at boot");

    public static void UseGui() => Select(new GuiDecisionSurface());

    public static void UseHeadless() => Select(new HeadlessDecisionSurface());

    private static void Select(IDecisionSurface adapter)
    {
        if (Interlocked.CompareExchange(ref _current, adapter, null) is not null)
            throw new InvalidOperationException("decision surface already selected");
    }
}

internal sealed class GuiDecisionSurface : IDecisionSurface
{
    private static NChooseABundleSelectionScreen? BundleScreen =>
        Screens.Top<NChooseABundleSelectionScreen>();

    public Phase? ActivePhase
    {
        get
        {
            if (BundleActive) return Phase.BundleSelect;
            // Preserve the historical precedence: a travel-ready map wins
            // over an overlay that is still mounted during its teardown.
            if (NMapScreen.Instance is
                { IsOpen: true, IsTravelEnabled: true, IsTraveling: false }
                && LocalRunContext.Current is not null)
                return Phase.Map;
            if (NOverlayStack.Instance?.Peek() is { } top)
                return top switch
                {
                    NRewardsScreen => Phase.Rewards,
                    NCardRewardSelectionScreen => Phase.CardReward,
                    NChooseARelicSelection => Phase.RelicReward,
                    NCardGridSelectionScreen or NChooseACardSelectionScreen =>
                        Phase.CardSelect,
                    CrystalScreen => Phase.CrystalSphere,
                    _ => Phase.Overlay,
                };
            if (CombatManager.Instance is not { IsInProgress: true }) return null;
            return NPlayerHand.Instance is { IsInCardSelection: true }
                ? Phase.HandSelect
                : Phase.Combat;
        }
    }

    public bool RequiresSettlementFrameStability => true;

    public bool DeferredCardChoiceActive => false;

    public bool CanBeginTreasureRelicPicking => true;

    public bool BundleActive => BundleScreen is not null;

    public CrystalMinigame? Crystal => Screens.Crystal() is { } screen
        ? Screens.CrystalEntity(screen)
        : null;

    public RestSiteDecision? RestSite => Screens.RestOptions() is { } options
        ? new RestSiteDecision(
            options,
            NRestSiteRoom.Instance?.ProceedButton is { Visible: true })
        : null;

    public TreasureDecision Treasure
    {
        get
        {
            var room = NRun.Instance?.TreasureRoom;
            var relics = LocalRunContext.Current?.Manager
                .TreasureRoomRelicSynchronizer
                ?.CurrentRelics?.ToArray() ?? [];
            var opened = room is not null && Screens.ChestOpened(room);
            return new TreasureDecision(
                opened || relics.Length > 0,
                room?.ProceedButton is { Visible: true },
                relics);
        }
    }

    public RewardsDecision? Rewards
    {
        get
        {
            var screen = Screens.Top<NRewardsScreen>();
            var buttons = screen is null ? null : Screens.RewardButtons(screen);
            if (buttons is null) return null;
            var rewards = new List<RewardSlot>();
            for (var i = 0; i < buttons.Count; i++)
                if (Screens.ClaimableReward(buttons[i]) is { } button)
                    rewards.Add(new RewardSlot(i, button.Reward!));
            return new RewardsDecision(rewards);
        }
    }

    public CardRewardDecision? CardReward
    {
        get
        {
            var screen = Screens.Top<NCardRewardSelectionScreen>();
            var holders = screen is null ? null : Screens.CardHolders(screen);
            if (holders is null || holders.Count == 0) return null;
            var cards = new List<CardModel>(holders.Count);
            foreach (var holder in holders)
            {
                if (holder.CardModel is not { } card) return null;
                cards.Add(card);
            }
            var alternatives = Screens.ExtraOptions(screen!)
                .Select(option => Reflect.PropertyValue(option, "Title") as LocString)
                .ToArray();
            return new CardRewardDecision(cards, alternatives);
        }
    }

    public CardSelectDecision? CardSelect
    {
        get
        {
            // Choose-a-card overlays (Discovery, event card offers): a
            // card row, pick one, done — no confirm step.
            if (Screens.Top<NChooseACardSelectionScreen>() is { } choose)
            {
                var holders = Screens.CardHolders(choose);
                if (holders is null || holders.Count == 0) return null;
                var cards = new List<CardModel>(holders.Count);
                foreach (var holder in holders)
                {
                    if (holder.CardModel is not { } card) return null;
                    cards.Add(card);
                }
                return new CardSelectDecision(
                    cards,
                    new HashSet<CardModel>(),
                    Prompt: null,
                    MinSelect: 1,
                    MaxSelect: 1,
                    Screens.ChooseSkipEnabled(choose),
                    Confirmable: false);
            }

            var screen = Screens.Top<NCardGridSelectionScreen>();
            var cardsInGrid = screen is null ? null : Screens.GridCards(screen);
            if (screen is null || cardsInGrid is null || cardsInGrid.Count == 0)
                return null;
            var prefs = Screens.Prefs(screen);
            var selected = Screens.SelectedCards(screen).ToHashSet();
            var confirmable = screen switch
            {
                NSimpleCardSelectScreen or NCombatPileCardSelectScreen =>
                    Reflect.Field<NClickableControl>(screen, "_confirmButton")
                        is { IsEnabled: true },
                NDeckTransformSelectScreen => selected.Count >= prefs.MinSelect,
                NDeckCardSelectScreen => selected.Count >= prefs.MinSelect,
                _ => selected.Count >= prefs.MaxSelect,
            };
            return new CardSelectDecision(
                cardsInGrid,
                selected,
                prefs.Prompt,
                prefs.MinSelect,
                prefs.MaxSelect,
                prefs.Cancelable,
                confirmable);
        }
    }

    public HandSelectDecision? HandSelect
    {
        get
        {
            var hand = NPlayerHand.Instance;
            if (hand is null) return null;
            var prefs = Screens.Prefs(hand);
            return new HandSelectDecision(
                hand.ActiveHolders.Select(holder => holder.CardNode?.Model).ToArray(),
                Screens.SelectedCards(hand).ToArray(),
                prefs.Prompt,
                prefs.MinSelect,
                prefs.MaxSelect,
                Reflect.Field<NClickableControl>(hand, "_selectModeConfirmButton")
                    is { IsEnabled: true },
                IncludePlayer: true);
        }
    }

    public BundleDecision? Bundle
    {
        get
        {
            if (BundleScreen is not { } screen) return null;
            return new BundleDecision(
                Screens.Bundles(screen) ?? [],
                Reflect.Field<NClickableControl>(screen, "_confirmButton")
                    is { IsEnabled: true },
                Reflect.Field<NClickableControl>(screen, "_skipButton")
                    is { IsEnabled: true });
        }
    }

    public DecisionSurfaceResult PickBundle(int idx)
    {
        if (BundleScreen is not { } screen)
            return NotReady("bundle screen not mounted");
        var nodes = Screens.BundleNodes(screen);
        if (nodes is null || nodes.Count == 0)
            return NotReady("bundle row not wired yet — retry");
        if (idx < 0 || idx >= nodes.Count)
            return BadIndex($"bundle idx {idx} out of range [0,{nodes.Count - 1}]");
        Reflect.Invoke(screen, "OnBundleClicked", nodes[idx]);
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult ConfirmBundle()
    {
        if (BundleScreen is not { } screen)
            return NotReady("bundle screen not mounted");
        Reflect.Invoke(screen, "ConfirmSelection", new object?[] { null });
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult SkipBundle()
    {
        if (BundleScreen is not { } screen)
            return NotReady("bundle screen not mounted");
        Reflect.Invoke(screen, "CancelSelection", new object?[] { null });
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult ChooseEventOption(int idx)
    {
        if (LocalRunContext.Current is not { } run)
            return BadState("run state not available");
        run.Manager.EventSynchronizer.ChooseLocalOption(idx);
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult ChooseRestOption(int idx)
    {
        var room = NRestSiteRoom.Instance;
        var options = Screens.RestOptions();
        if (room is null || options is null)
            return NotReady("rest site not mounted");
        if (idx < 0 || idx >= options.Count)
            return BadIndex($"option idx {idx} out of range [0,{options.Count - 1}]");
        // ForceClick fires the Godot Released signal so the engine's wired
        // handler chain runs; invoking the handler directly does nothing.
        if (room.GetButtonForOption(options[idx]) is not { } button)
            return NotReady("rest option button not wired yet — retry");
        button.ForceClick();
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult ChooseCrystalTool(int idx)
    {
        if (Screens.Crystal() is not { } screen)
            return NotReady("crystal sphere screen not mounted");
        Reflect.Invoke(screen, idx == 0 ? "SetSmallDivination" : "SetBigDivination",
            new object?[] { null });
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult ProceedEvent()
    {
        if (NEventRoom.Instance is null)
            return NotReady("event room not mounted");
        DecisionSurfaceActions.Track(NEventRoom.Proceed(), "proceed");
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult ProceedRewards()
    {
        if (Screens.Top<NRewardsScreen>() is not { } screen)
            return NotReady("rewards screen not mounted");
        // A debug override left set short-circuits the handler.
        if (LocalRunContext.Current is not { } run)
            return BadState("run state not available");
        run.Manager.debugAfterCombatRewardsOverride = null;
        Reflect.Invoke(screen, "OnProceedButtonPressed", new object?[] { null });
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult ProceedRestSite()
    {
        if (NRestSiteRoom.Instance is not { } room)
            return NotReady("rest site not mounted");
        if (room.ProceedButton is not { Visible: true } button)
            return BadState("proceed button not visible — choose an option first");
        button.ForceClick();
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult ProceedTreasure()
    {
        if (NRun.Instance?.TreasureRoom is not { } room)
            return NotReady("treasure room not mounted");
        if (room.ProceedButton is not { Visible: true } button)
            return BadState(
                "proceed button not visible — resolve the chest first (pick-relic / skip)");
        button.ForceClick();
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult ProceedCrystal()
    {
        if (Screens.Crystal() is not { } screen)
            return NotReady("crystal sphere screen not mounted");
        Reflect.Invoke(screen, "OnProceedButtonPressed", new object?[] { null });
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult Purchase(MerchantInventory inventory, MerchantEntry entry)
    {
        DecisionSurfaceActions.Track(
            entry.OnTryPurchaseWrapper(inventory, false), "buy");
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult LeaveShop()
    {
        if (NMapScreen.Instance is not { } map)
            return NotReady("map screen not mounted");
        map.Open(false);
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult PickReward(int idx)
    {
        var screen = Screens.Top<NRewardsScreen>();
        var buttons = screen is null ? null : Screens.RewardButtons(screen);
        if (buttons is null)
            return NotReady("rewards screen not mounted");
        if (idx < 0 || idx >= buttons.Count)
            return BadIndex($"reward idx {idx} out of range [0,{buttons.Count - 1}]");
        if (Screens.ClaimableReward(buttons[idx]) is not { } button)
            return BadIndex($"reward idx {idx} is not claimable (already taken?)");

        if (Reflect.Invoke(button, "GetReward") is Task task)
            DecisionSurfaceActions.Track(task, "pick-reward");
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult PickRewardCard(int idx)
    {
        var screen = Screens.Top<NCardRewardSelectionScreen>();
        var holders = screen is null ? null : Screens.CardHolders(screen);
        if (screen is null || holders is null || holders.Count == 0)
            return NotReady("card row not wired yet — retry");
        if (idx < 0 || idx >= holders.Count)
            return BadIndex($"card idx {idx} out of range [0,{holders.Count - 1}]");
        Reflect.Invoke(screen, "SelectCard", holders[idx]);
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult PickCard(int idx)
    {
        if (Screens.Top<NChooseACardSelectionScreen>() is { } choose)
        {
            var holders = Screens.CardHolders(choose);
            if (holders is null || holders.Count == 0)
                return NotReady("card row not wired yet — retry");
            if (idx < 0 || idx >= holders.Count)
                return BadIndex($"card idx {idx} out of range [0,{holders.Count - 1}]");
            Reflect.Invoke(choose, "SelectHolder", holders[idx]);
            return DecisionSurfaceResult.Success();
        }

        var screen = Screens.Top<NCardGridSelectionScreen>();
        var cards = screen is null ? null : Screens.GridCards(screen);
        if (screen is null || cards is null || cards.Count == 0)
            return NotReady("card grid not wired yet — retry");
        if (idx < 0 || idx >= cards.Count)
            return BadIndex($"card idx {idx} out of range [0,{cards.Count - 1}]");
        Reflect.Invoke(screen, "OnCardClicked", cards[idx]);
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult PickHandCard(int idx)
    {
        var hand = NPlayerHand.Instance;
        var holders = hand?.ActiveHolders;
        if (hand is null || holders is null || holders.Count == 0)
            return NotReady("no selectable cards in hand — retry");
        if (idx < 0 || idx >= holders.Count)
            return BadIndex($"card idx {idx} out of range [0,{holders.Count - 1}]");
        Reflect.Invoke(hand, "OnHolderPressed", holders[idx]);
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult ConfirmCardSelection()
    {
        var screen = Screens.Top<NCardGridSelectionScreen>();
        if (screen is null)
            return NotReady("selection screen not mounted");
        var prefs = Screens.Prefs(screen);
        var count = Screens.SelectedCards(screen).Count();
        switch (screen)
        {
            case NSimpleCardSelectScreen or NCombatPileCardSelectScreen:
                var button = Reflect.Field<NClickableControl>(screen, "_confirmButton");
                if (button is not { IsEnabled: true })
                    return BadState(
                        $"confirm not available — {count} selected, need {prefs.MinSelect}..{prefs.MaxSelect}");
                button.ForceClick();
                return DecisionSurfaceResult.Success();

            case NDeckTransformSelectScreen:
                if (count < prefs.MinSelect)
                    return BadState(
                        $"{count} selected, need {prefs.MinSelect} (pick-card first)");
                Reflect.Invoke(screen, "CompleteSelection", new object?[] { null });
                return DecisionSurfaceResult.Success();

            default:
                var need = screen is NDeckCardSelectScreen
                    ? prefs.MinSelect
                    : prefs.MaxSelect;
                if (count < need)
                    return BadState($"{count} selected, need {need} (pick-card first)");
                Reflect.Invoke(screen, "CheckIfSelectionComplete");
                return DecisionSurfaceResult.Success();
        }
    }

    public DecisionSurfaceResult ConfirmHandSelection()
    {
        var hand = NPlayerHand.Instance;
        if (hand is null)
            return NotReady("hand selection not mounted");
        var button = Reflect.Field<NClickableControl>(hand, "_selectModeConfirmButton");
        if (button is not { IsEnabled: true })
        {
            var prefs = Screens.Prefs(hand);
            var count = Screens.SelectedCards(hand).Count();
            return BadState(
                $"confirm not available — {count} selected, need {prefs.MinSelect}..{prefs.MaxSelect}");
        }
        button.ForceClick();
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult SkipCardReward(int? alternativeIdx)
    {
        if (Screens.Top<NCardRewardSelectionScreen>() is not { } screen)
            return NotReady("card reward screen not mounted");
        var alternatives = Screens.ExtraOptions(screen);
        if (alternatives.Count == 0)
            return BadRequest("this card reward cannot be skipped");
        var resolved = DecisionSurfaceActions.ResolveAlternativeIndex(
            alternativeIdx, alternatives.Count, out var idx);
        if (!resolved.Ok) return resolved;
        Reflect.Invoke(screen, "OnAlternateRewardSelected", idx);
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult SkipCardSelection()
    {
        if (Screens.Top<NChooseACardSelectionScreen>() is { } choose)
        {
            if (!Screens.ChooseSkipEnabled(choose))
                return BadRequest("this selection cannot be skipped");
            Reflect.Invoke(choose, "OnSkipButtonReleased", new object?[] { null });
            return DecisionSurfaceResult.Success();
        }

        if (Screens.Top<NCardGridSelectionScreen>() is not { } screen)
            return NotReady("selection screen not mounted");
        if (screen is NSimpleCardSelectScreen or NCombatPileCardSelectScreen
            || !Screens.Prefs(screen).Cancelable)
            return BadRequest("this selection cannot be skipped");
        Reflect.Invoke(screen, "CloseSelection", new object?[] { null });
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult PickRelicReward(int idx) =>
        DecisionSurfaceActions.PickRelicReward(idx);

    public DecisionSurfaceResult SkipRelicReward() =>
        DecisionSurfaceActions.SkipRelicReward();

    public DecisionSurfaceResult PickTreasureRelic(int idx)
    {
        var room = NRun.Instance?.TreasureRoom;
        var synchronizer = LocalRunContext.Current?.Manager
            .TreasureRoomRelicSynchronizer;
        if (room is null || synchronizer is null)
            return NotReady("treasure room not mounted");
        if (!Screens.ChestOpened(room))
        {
            var chest = Reflect.Field<NButton>(room, "_chestButton");
            if (chest is null)
                return BadState("chest button not found");
            chest.ForceClick();
        }
        var relics = synchronizer.CurrentRelics;
        if (relics is null || relics.Count == 0)
            return NotReady("chest opening — poll /obs, then pick-relic again");
        if (idx < 0 || idx >= relics.Count)
            return BadIndex($"relic idx {idx} out of range [0,{relics.Count - 1}]");
        synchronizer.PickRelicLocally(idx);
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult SkipTreasure()
    {
        var synchronizer = LocalRunContext.Current?.Manager
            .TreasureRoomRelicSynchronizer;
        if (synchronizer is null)
            return NotReady("treasure room not mounted");
        if (synchronizer.CurrentRelics is { Count: > 0 })
        {
            synchronizer.SkipRelicLocally();
            return DecisionSurfaceResult.Success();
        }
        if (NRun.Instance?.TreasureRoom is not { } room)
            return NotReady("treasure room not mounted");
        if (room.ProceedButton is not { Visible: true } button)
            return BadState(
                "proceed button not visible — resolve the chest first (pick-relic / skip)");
        button.ForceClick();
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult StartNewRun(
        CharacterModel character, string seed, int ascension)
    {
        if (NGame.Instance is not { } game
            || game.MainMenu is not { } menu
            || Reflect.FieldValue(menu, "_singleplayerButton")
                is not NClickableControl { IsEnabled: true })
            return NotReady("main menu not ready — retry");

        DecisionSurfaceActions.Track(game.StartNewSingleplayerRun(
            character,
            shouldSave: true,
            acts: ModelDb.Acts.ToList(),
            modifiers: new List<ModifierModel>(),
            seed: seed,
            gameMode: GameMode.Standard,
            ascensionLevel: ascension,
            dailyTime: null), "new-run");
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult AbandonRun(LocalRunContext run)
    {
        if (NGame.Instance is not { } game)
            return NotReady("game shell not mounted — retry");
        if (!run.Manager.IsAbandoned && !run.Manager.IsGameOver)
            run.Manager.Abandon();
        DecisionSurfaceActions.Track(
            game.ReturnToMainMenuAfterRun(), "abandon");
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult TravelTo(LocalRunContext run, MapPoint target) =>
        DecisionSurfaceActions.MapAnimationBlocksTravel()
            ? NotReady("map intro animation — retry")
            : DecisionSurfaceActions.TravelTo(run, target);

    public DecisionSurfaceResult ClickCrystalCell(int col, int row)
    {
        if (Screens.Crystal() is not { } screen)
            return NotReady("crystal sphere screen not mounted");
        var cell = FindCrystalCell(
            Screens.CrystalCellContainer(screen), col, row);
        if (cell is null)
            return BadTarget($"no cell at {col},{row} (see obs.cells)");
        if (Reflect.Invoke(screen, "OnCellClicked", cell) is Task task)
            DecisionSurfaceActions.Track(task, "crystal-click");
        return DecisionSurfaceResult.Success();
    }

    public bool MerchantPotionInteractionAvailable(PotionModel potion) =>
        potion.PassesCustomUsabilityCheck;

    public DecisionSurfaceResult UsePotion(
        PotionModel potion, Creature? target)
    {
        potion.EnqueueManualUse(target!);
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult DiscardPotion(
        LocalRunContext run, uint slot, bool inCombat)
    {
        run.Manager.ActionQueueSynchronizer.RequestEnqueue(
            new DiscardPotionGameAction(run.Player, slot, inCombat));
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult PlayCard(
        RunManager manager, CardModel card, Creature? target)
    {
        manager.ActionQueueSynchronizer.RequestEnqueue(
            new PlayCardAction(card, target!));
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult EndTurn(
        RunManager manager, Player player, int roundNumber)
    {
        manager.ActionQueueSynchronizer.RequestEnqueue(
            new EndPlayerTurnAction(player, roundNumber));
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult ObtainRelic(RelicModel relic, Player player)
    {
        DecisionSurfaceActions.Track(RelicCmd.Obtain(relic, player), "cheat-relic");
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult AcceptAbandonConfirmation(RunManager manager)
    {
        manager.Abandon();
        return DecisionSurfaceResult.Success();
    }

    public void InstallPersistentCardSelector() { }

    public void TrackOrAwaitModelTask(Task task, string context) =>
        DecisionSurfaceActions.Track(task, context);

    public void ResetRunChoices() { }

    public DecisionSurfaceResult ParkCombatRewards(CombatRoom room) =>
        DecisionSurfaceResult.Success();

    public bool TryOwnBundleCompletion(
        IReadOnlyList<IReadOnlyList<CardModel>> bundles,
        out Task<IEnumerable<CardModel>> completion)
    {
        completion = null!;
        return false;
    }

    public bool TryOwnCrystalScreen(CrystalMinigame minigame) => false;

    public bool TryOwnCustomRewardCompletion(
        Player player,
        IReadOnlyList<Reward> rewards,
        out Task completion)
    {
        completion = null!;
        return false;
    }

    private static DecisionSurfaceResult BadIndex(string message) =>
        DecisionSurfaceResult.Reject(DecisionSurfaceError.BadIndex, message);

    private static DecisionSurfaceResult BadRequest(string message) =>
        DecisionSurfaceResult.Reject(DecisionSurfaceError.BadRequest, message);

    private static DecisionSurfaceResult BadState(string message) =>
        DecisionSurfaceResult.Reject(DecisionSurfaceError.BadState, message);

    private static DecisionSurfaceResult BadTarget(string message) =>
        DecisionSurfaceResult.Reject(DecisionSurfaceError.BadTarget, message);

    private static DecisionSurfaceResult NotReady(string message) =>
        DecisionSurfaceResult.Reject(DecisionSurfaceError.NotReady, message);

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
            if (FindCrystalCell(child, col, row) is { } deeper)
                return deeper;
        }
        return null;
    }
}

internal sealed class HeadlessDecisionSurface : IDecisionSurface
{
    private TreasureRoom? _normalRewardsGrantedFor;

    public Phase? ActivePhase
    {
        get
        {
            if (BundleActive) return Phase.BundleSelect;
            if (HeadlessRewards.InCardPick) return Phase.CardReward;
            if (HeadlessRewards.IsActive) return Phase.Rewards;
            if (HeadlessCrystal.IsActive) return Phase.CrystalSphere;
            if (HeadlessPicker.IsActive)
                return CombatManager.Instance is { IsInProgress: true }
                    ? Phase.HandSelect
                    : Phase.CardSelect;
            return CombatManager.Instance is { IsInProgress: true }
                ? Phase.Combat
                : null;
        }
    }

    public bool RequiresSettlementFrameStability => false;

    public bool DeferredCardChoiceActive => HeadlessPicker.IsActive;

    public bool CanBeginTreasureRelicPicking =>
        HeadlessTreasure.CanBeginRelicPicking;

    public bool BundleActive => HeadlessBundle.IsActive;

    public CrystalMinigame? Crystal => HeadlessCrystal.Entity;

    public RestSiteDecision? RestSite => Screens.RestOptions() is { } options
        ? new RestSiteDecision(options, ProceedAvailable: true)
        : null;

    public TreasureDecision Treasure
    {
        get
        {
            var run = LocalRunContext.Current;
            var relics = run?.Manager.TreasureRoomRelicSynchronizer
                ?.CurrentRelics?.ToArray() ?? [];
            var opened = ReferenceEquals(
                HeadlessTreasure.OpenedRoom,
                run?.State.CurrentRoom);
            return new TreasureDecision(
                opened || relics.Length > 0,
                ProceedAvailable: true,
                relics);
        }
    }

    public RewardsDecision? Rewards => new(
        HeadlessRewards.Slotted()
            .Select(slot => new RewardSlot(slot.idx, slot.reward))
            .ToArray());

    public CardRewardDecision? CardReward =>
        HeadlessRewards.ActiveCardPick?.Cards?.ToList() is { } cards
            ? new CardRewardDecision(
                cards,
                HeadlessRewards.Alternatives().Select(option => option.Title).ToArray())
            : null;

    public CardSelectDecision? CardSelect => !HeadlessPicker.IsActive
        ? null
        : new CardSelectDecision(
            HeadlessPicker.Candidates,
            HeadlessPicker.Picked.ToHashSet(),
            Prompt: null,
            HeadlessPicker.MinSelect,
            HeadlessPicker.MaxSelect,
            Cancelable: true,
            HeadlessPicker.Picked.Count >= HeadlessPicker.MinSelect);

    public HandSelectDecision? HandSelect => !HeadlessPicker.IsActive
        ? null
        : new HandSelectDecision(
            HeadlessPicker.Candidates.Select(card => (CardModel?)card).ToArray(),
            HeadlessPicker.Picked,
            Prompt: null,
            HeadlessPicker.MinSelect,
            HeadlessPicker.MaxSelect,
            HeadlessPicker.Picked.Count >= HeadlessPicker.MinSelect,
            IncludePlayer: false);

    public BundleDecision? Bundle => !HeadlessBundle.IsActive
        ? null
        : new BundleDecision(
            HeadlessBundle.Bundles,
            Confirmable: false,
            Cancelable: true);

    public DecisionSurfaceResult PickBundle(int idx) =>
        HeadlessBundle.Pick(idx) is { } message
            ? DecisionSurfaceResult.Reject(DecisionSurfaceError.BadIndex, message)
            : DecisionSurfaceResult.Success();

    public DecisionSurfaceResult ConfirmBundle() =>
        DecisionSurfaceResult.Reject(
            DecisionSurfaceError.BadState,
            "host bundle picks resolve on pick-card");

    public DecisionSurfaceResult SkipBundle()
    {
        HeadlessBundle.CancelIfActive();
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult ChooseEventOption(int idx)
    {
        if (LocalRunContext.Current is not { } run)
            return BadState("run state not available");
        HeadlessPicker.Around(() =>
            run.Manager.EventSynchronizer.ChooseLocalOption(idx));
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult ChooseRestOption(int idx)
    {
        if (LocalRunContext.Current is not { } run)
            return BadState("run state not available");
        var options = Screens.RestOptions();
        if (options is null)
            return NotReady("rest site not mounted");
        if (idx < 0 || idx >= options.Count)
            return BadIndex($"option idx {idx} out of range [0,{options.Count - 1}]");
        Task? choice = null;
        HeadlessPicker.Around(() =>
            choice = run.Manager.RestSiteSynchronizer.ChooseLocalOption(idx));
        DecisionSurfaceActions.Track(choice!, "rest-option");
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult ChooseCrystalTool(int idx)
    {
        if (HeadlessCrystal.Entity is not { } entity)
            return BadState("no crystal sphere in progress");
        entity.SetTool(idx == 0
            ? CrystalMinigame.CrystalSphereToolType.Small
            : CrystalMinigame.CrystalSphereToolType.Big);
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult ProceedEvent()
    {
        if (LocalRunContext.Current is not { } run)
            return BadState("run state not available");
        // Events can finish on a dialogue page without flipping IsFinished;
        // headless exits the room model. The finale room advances the run.
        return run.State.CurrentRoom is { IsVictoryRoom: true }
            ? EnterNextAct()
            : ExitRoomToMap(run.Manager, run.State, "event proceed");
    }

    public DecisionSurfaceResult ProceedRewards()
    {
        HeadlessRewards.SkipAllAndClear();
        if (LocalRunContext.Current is not { } run)
            return BadState("run state not available");
        if (run.State.CurrentMapPoint?.PointType == MapPointType.Boss)
        {
            if (run.State.CurrentRoom is { } bossRoom)
                DecisionSurfaceActions.Track(bossRoom.Exit(run.State), "boss exit");
            if (Snapshotter.SecondBossPending(run.State))
                return ExitRoomToMap(
                    run.Manager, run.State, "boss exit", exitRoom: false);
            return EnterNextAct();
        }
        return ExitRoomToMap(run.Manager, run.State, "rewards proceed");
    }

    public DecisionSurfaceResult ProceedRestSite()
    {
        if (LocalRunContext.Current is not { } run)
            return BadState("run state not available");
        return ExitRoomToMap(run.Manager, run.State, "rest-site proceed");
    }

    public DecisionSurfaceResult ProceedTreasure()
    {
        if (LocalRunContext.Current is not { } run)
            return BadState("run state not available");
        if (run.Manager.TreasureRoomRelicSynchronizer?.CurrentRelics is { Count: > 0 })
            run.Manager.TreasureRoomRelicSynchronizer.SkipRelicLocally();
        return ExitRoomToMap(run.Manager, run.State, "treasure proceed");
    }

    public DecisionSurfaceResult ProceedCrystal()
    {
        HeadlessCrystal.Entity?.ForceMinigameEnd();
        HeadlessCrystal.Clear();
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult Purchase(MerchantInventory inventory, MerchantEntry entry)
    {
        Task? purchase = null;
        HeadlessPicker.Around(() =>
            purchase = entry.OnTryPurchaseWrapper(inventory, false));
        DecisionSurfaceActions.Track(purchase!, "buy");
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult LeaveShop()
    {
        if (LocalRunContext.Current is not { } run)
            return BadState("run state not available");
        return ExitRoomToMap(
            run.Manager, run.State, "shop leave", exitRoom: false);
    }

    public DecisionSurfaceResult PickReward(int idx) =>
        HeadlessRewards.PickReward(idx) is { } message
            ? BadIndex(message)
            : DecisionSurfaceResult.Success();

    public DecisionSurfaceResult PickRewardCard(int idx) =>
        HeadlessRewards.PickCard(idx) is { } message
            ? BadIndex(message)
            : DecisionSurfaceResult.Success();

    public DecisionSurfaceResult PickCard(int idx) =>
        HeadlessPicker.Pick(idx) is { } message
            ? BadIndex(message)
            : DecisionSurfaceResult.Success();

    public DecisionSurfaceResult PickHandCard(int idx) => PickCard(idx);

    public DecisionSurfaceResult ConfirmCardSelection() =>
        ConfirmPicker();

    public DecisionSurfaceResult ConfirmHandSelection() =>
        ConfirmPicker();

    public DecisionSurfaceResult SkipCardReward(int? alternativeIdx)
    {
        if (!HeadlessRewards.InCardPick)
            return BadState("no card reward pending");
        var alternatives = HeadlessRewards.Alternatives();
        if (alternatives.Count == 0)
            return BadRequest("this card reward cannot be skipped");
        var resolved = DecisionSurfaceActions.ResolveAlternativeIndex(
            alternativeIdx, alternatives.Count, out var idx);
        if (!resolved.Ok) return resolved;
        return HeadlessRewards.PickCard(idx, alternative: true) is { } message
            ? BadIndex(message)
            : DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult SkipCardSelection()
    {
        HeadlessPicker.CancelIfActive();
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult PickRelicReward(int idx) =>
        DecisionSurfaceActions.PickRelicReward(idx);

    public DecisionSurfaceResult SkipRelicReward() =>
        DecisionSurfaceActions.SkipRelicReward();

    public DecisionSurfaceResult PickTreasureRelic(int idx)
    {
        var synchronizer = LocalRunContext.Current?.Manager
            .TreasureRoomRelicSynchronizer;
        if (synchronizer is null)
            return NotReady("treasure room not mounted");
        if (OpenTreasureIfNeeded() is { } opened)
            return opened;
        var relics = synchronizer.CurrentRelics;
        if (relics is null || relics.Count == 0)
            return NotReady("chest opening — poll /obs, then pick-relic again");
        if (idx < 0 || idx >= relics.Count)
            return BadIndex($"relic idx {idx} out of range [0,{relics.Count - 1}]");

        Action<List<RelicPickingResult>>? award = null;
        award = results =>
        {
            synchronizer.RelicsAwarded -= award;
            foreach (var result in results)
            {
                if (result.type == RelicPickingResultType.Skipped
                    || result.player is null || result.relic is null) continue;
                DecisionSurfaceActions.Track(
                    RelicCmd.Obtain(result.relic.ToMutable(), result.player),
                    "treasure-relic");
            }
        };
        synchronizer.RelicsAwarded += award;
        synchronizer.PickRelicLocally(idx);
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult SkipTreasure()
    {
        if (LocalRunContext.Current is not { } run)
            return BadState("run state not available");
        var synchronizer = run.Manager.TreasureRoomRelicSynchronizer;
        if (synchronizer is null)
            return NotReady("treasure room not mounted");
        if (run.State.CurrentRoom is not TreasureRoom room)
            return NotReady("treasure room not available");
        if (!ReferenceEquals(HeadlessTreasure.OpenedRoom, room)
            && OpenTreasureIfNeeded() is { } opened)
            return opened;
        if (synchronizer.CurrentRelics is { Count: > 0 })
        {
            synchronizer.SkipRelicLocally();
            return DecisionSurfaceResult.Success();
        }
        return ExitRoomToMap(run.Manager, run.State, "treasure proceed");
    }

    public DecisionSurfaceResult StartNewRun(
        CharacterModel character, string seed, int ascension)
    {
        try
        {
            var create = typeof(Player).GetMethods(
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Static)
                .First(method => method.Name == "CreateForNewRun"
                    && method.IsGenericMethodDefinition
                    && method.GetParameters().Length == 2)
                .MakeGenericMethod(character.GetType());
            var unlockAll = typeof(ModelDb).Assembly
                .GetType("MegaCrit.Sts2.Core.Unlocks.UnlockState")!
                .GetField("all")!.GetValue(null)!;
            var player = (Player)create.Invoke(
                null, new object[] { unlockAll, 1ul })!;

            if (string.IsNullOrEmpty(seed))
            {
                var seedRng = new MegaCrit.Sts2.Core.Random.Rng(
                    (uint)Random.Shared.Next(), 0);
                seed = MegaCrit.Sts2.Core.Helpers.SeedHelper.GetRandomSeed(
                    seedRng, 10);
            }
            var state = RunState.CreateForTest(
                [player],
                ModelDb.Acts.ToList(),
                new List<ModifierModel>(),
                GameMode.Standard,
                ascension,
                seed);

            var manager = RunManager.Instance!;
            var netService = new NetSingleplayerGameService();
            manager.SetUpTest(state, netService, false, false);
            LocalContext.NetId = netService.NetId;
            Reflect.SetProperty(state.ExtraFields, "StartedWithNeow", true);
            manager.GenerateRooms();
            manager.Launch();
            manager.EnterAct(0, false).GetAwaiter().GetResult();
            return DecisionSurfaceResult.Success();
        }
        catch (Exception ex)
        {
            var root = ex.GetBaseException();
            return DecisionSurfaceResult.Reject(
                DecisionSurfaceError.Internal,
                $"headless new-run failed: {root.GetType().Name}: {root.Message}");
        }
    }

    public DecisionSurfaceResult AbandonRun(LocalRunContext run)
    {
        var manager = run.Manager;
        if (!manager.IsAbandoned && !manager.IsGameOver)
            TeardownAbandon(manager);

        if (CombatManager.Instance is { } combat)
        {
            try { combat.Reset(graceful: true); }
            catch (Exception ex)
            {
                SafeLog.Error("abandon combat reset", ex);
            }
        }
        try { manager.ActionQueueSet?.Reset(); }
        catch { }
        Reflect.SetPropertyOrBackingField(manager, "State", null);
        Reflect.SetPropertyOrBackingField(manager, "IsAbandoned", false);
        ResetRunChoices();
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult TravelTo(LocalRunContext run, MapPoint target) =>
        DecisionSurfaceActions.TravelTo(run, target);

    public DecisionSurfaceResult ClickCrystalCell(int col, int row)
    {
        if (HeadlessCrystal.Entity is not { } entity)
            return BadState("no crystal sphere in progress");
        var grid = entity.GridSize;
        if (col < 0 || col >= grid.X || row < 0 || row >= grid.Y
            || entity.cells[col, row] is not { } cell)
            return BadTarget($"no cell at {col},{row} (see obs.cells)");
        entity.CellClicked(cell).GetAwaiter().GetResult();
        return DecisionSurfaceResult.Success();
    }

    public bool MerchantPotionInteractionAvailable(PotionModel potion) => true;

    public DecisionSurfaceResult UsePotion(
        PotionModel potion, Creature? target)
    {
        var result = DecisionSurfaceResult.Success();
        HeadlessPicker.Around(() =>
            result = DecisionSurfaceActions.ResolveInline(
                potion.GetType().Name,
                () => potion.EnqueueManualUse(target!)));
        return result;
    }

    public DecisionSurfaceResult DiscardPotion(
        LocalRunContext run, uint slot, bool inCombat) =>
        DecisionSurfaceActions.ResolveInline(
            nameof(DiscardPotionGameAction),
            () => run.Manager.ActionQueueSet.EnqueueWithoutSynchronizing(
                new DiscardPotionGameAction(run.Player, slot, inCombat)));

    public DecisionSurfaceResult PlayCard(
        RunManager manager, CardModel card, Creature? target)
    {
        var result = DecisionSurfaceResult.Success();
        HeadlessPicker.Around(() =>
            result = DecisionSurfaceActions.ResolveInline(
                nameof(PlayCardAction),
                () => manager.ActionQueueSet.EnqueueWithoutSynchronizing(
                    new PlayCardAction(card, target!))));
        return result;
    }

    public DecisionSurfaceResult EndTurn(
        RunManager manager, Player player, int roundNumber) =>
        DecisionSurfaceActions.ResolveInline(
            nameof(EndPlayerTurnAction),
            () => manager.ActionQueueSet.EnqueueWithoutSynchronizing(
                new EndPlayerTurnAction(player, roundNumber)));

    public DecisionSurfaceResult ObtainRelic(RelicModel relic, Player player)
    {
        Task? obtain = null;
        HeadlessPicker.Around(() => obtain = RelicCmd.Obtain(relic, player));
        if (obtain is null)
            return DecisionSurfaceResult.Reject(
                DecisionSurfaceError.Internal, "relic obtain did not start");
        if (!DeferredCardChoiceActive && !BundleActive && !HeadlessRewards.IsActive)
            obtain.GetAwaiter().GetResult();
        else
            DecisionSurfaceActions.Track(obtain, "cheat-relic");
        return DecisionSurfaceResult.Success();
    }

    public DecisionSurfaceResult AcceptAbandonConfirmation(RunManager manager)
    {
        TeardownAbandon(manager);
        return DecisionSurfaceResult.Success();
    }

    public void InstallPersistentCardSelector() => HeadlessPicker.Install();

    public void TrackOrAwaitModelTask(Task task, string context) =>
        task.GetAwaiter().GetResult();

    private static void TeardownAbandon(RunManager manager)
    {
        try
        {
            Reflect.SetPropertyOrBackingField(manager, "IsAbandoned", true);
            if (Reflect.Invoke(manager, "GuaranteeKillAllPlayers") is Task kill
                && !kill.Wait(TimeSpan.FromSeconds(5)))
                SafeLog.Info(
                    "abandon: kill-players teardown still pending after 5s — resetting anyway");
        }
        catch (Exception ex)
        {
            SafeLog.Error("abandon (headless)", ex);
        }
    }

    public void ResetRunChoices()
    {
        _normalRewardsGrantedFor = null;
        HeadlessState.ResetAll();
    }

    public DecisionSurfaceResult ParkCombatRewards(CombatRoom room) =>
        HeadlessRewards.CaptureFromRoom(room) is { } message
            ? DecisionSurfaceResult.Reject(DecisionSurfaceError.BadState, message)
            : DecisionSurfaceResult.Success();

    private static DecisionSurfaceResult ConfirmPicker() =>
        HeadlessPicker.Confirm() is { } message
            ? BadState(message)
            : DecisionSurfaceResult.Success();

    private DecisionSurfaceResult? OpenTreasureIfNeeded()
    {
        if (LocalRunContext.Current is not { } run)
            return BadState("run state not available");
        var synchronizer = run.Manager.TreasureRoomRelicSynchronizer;
        if (synchronizer is null)
            return NotReady("treasure room not mounted");
        if (run.State.CurrentRoom is not TreasureRoom room)
            return NotReady("treasure room not available");
        if (ReferenceEquals(HeadlessTreasure.OpenedRoom, room))
            return synchronizer.CurrentRelics is { Count: > 0 }
                ? null
                : BadState("no relic offer available");

        try
        {
            if (synchronizer.CurrentRelics is not { Count: > 0 })
                HeadlessTreasure.Open(synchronizer.BeginRelicPicking);
            if (!ReferenceEquals(_normalRewardsGrantedFor, room))
            {
                room.DoNormalRewards().GetAwaiter().GetResult();
                _normalRewardsGrantedFor = room;
            }
            room.DoExtraRewardsIfNeeded().GetAwaiter().GetResult();
            HeadlessTreasure.OpenedRoom = room;
            return DecisionSurfaceResult.Success(
                "chest opened — rescry, then pick-relic / skip");
        }
        catch (Exception ex)
        {
            SafeLog.Error("headless treasure open", ex);
            return DecisionSurfaceResult.Reject(
                DecisionSurfaceError.Internal,
                $"chest open failed: {ex.GetType().Name}: {ex.Message}", 500);
        }
    }

    private static DecisionSurfaceResult EnterNextAct()
    {
        try
        {
            if (LocalRunContext.Current is not { } run)
                return BadState("run state not available");
            run.Manager.EnterNextAct().GetAwaiter().GetResult();
            return DecisionSurfaceResult.Success();
        }
        catch (Exception ex)
        {
            return DecisionSurfaceResult.Reject(
                DecisionSurfaceError.Internal,
                $"act transition failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static DecisionSurfaceResult ExitRoomToMap(
        RunManager manager, RunState state, string label, bool exitRoom = true)
    {
        try
        {
            if (exitRoom && state.CurrentRoom is { } room)
                DecisionSurfaceActions.Track(room.Exit(state), label);
            if (state.CurrentRoom is not MapRoom)
                manager.EnterRoom(new MapRoom()).GetAwaiter().GetResult();
            return DecisionSurfaceResult.Success();
        }
        catch (Exception ex)
        {
            return DecisionSurfaceResult.Reject(
                DecisionSurfaceError.Internal,
                $"headless {label} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public bool TryOwnBundleCompletion(
        IReadOnlyList<IReadOnlyList<CardModel>> bundles,
        out Task<IEnumerable<CardModel>> completion)
    {
        completion = HeadlessBundle.Park(bundles);
        return true;
    }

    public bool TryOwnCrystalScreen(CrystalMinigame minigame)
    {
        HeadlessCrystal.Park(minigame);
        return true;
    }

    public bool TryOwnCustomRewardCompletion(
        Player player,
        IReadOnlyList<Reward> rewards,
        out Task completion)
    {
        completion = CaptureCustomRewards(player, rewards);
        return true;
    }

    private static async Task CaptureCustomRewards(
        Player player, IReadOnlyList<Reward> rewards)
    {
        var set = new RewardsSet(player).WithCustomRewards(rewards.ToList());
        await set.GenerateWithoutOffering();
        HeadlessRewards.CaptureFromSet(set);
    }

    private static DecisionSurfaceResult BadIndex(string message) =>
        DecisionSurfaceResult.Reject(DecisionSurfaceError.BadIndex, message);

    private static DecisionSurfaceResult BadRequest(string message) =>
        DecisionSurfaceResult.Reject(DecisionSurfaceError.BadRequest, message);

    private static DecisionSurfaceResult BadState(string message) =>
        DecisionSurfaceResult.Reject(DecisionSurfaceError.BadState, message);

    private static DecisionSurfaceResult BadTarget(string message) =>
        DecisionSurfaceResult.Reject(DecisionSurfaceError.BadTarget, message);

    private static DecisionSurfaceResult NotReady(string message) =>
        DecisionSurfaceResult.Reject(DecisionSurfaceError.NotReady, message);
}
