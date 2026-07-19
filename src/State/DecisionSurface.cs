using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using CrystalMinigame = MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame;

namespace Spirescry.State;

// One boot-selected boundary for the local seat's current decision. The
// bundle offer is the pilot: later migrations add other surfaces without
// adding more boot checks at their call sites.
internal interface IDecisionSurface
{
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

    // The headless Harmony hook asks the selected adapter whether it owns
    // this completion. GUI returns false so the engine's real screen keeps
    // ownership; headless parks the offer on its stand-in and returns true.
    bool TryOwnBundleCompletion(
        IReadOnlyList<IReadOnlyList<CardModel>> bundles,
        out Task<IEnumerable<CardModel>> completion);
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
    BadIndex,
    BadState,
    NotReady,
}

internal readonly record struct DecisionSurfaceResult(
    DecisionSurfaceError? Error,
    string? Message)
{
    public bool Ok => Error is null;

    public static DecisionSurfaceResult Success() => new(null, null);
    public static DecisionSurfaceResult Reject(DecisionSurfaceError error, string message) =>
        new(error, message);
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

    public bool TryOwnBundleCompletion(
        IReadOnlyList<IReadOnlyList<CardModel>> bundles,
        out Task<IEnumerable<CardModel>> completion)
    {
        completion = null!;
        return false;
    }

    private static DecisionSurfaceResult BadIndex(string message) =>
        DecisionSurfaceResult.Reject(DecisionSurfaceError.BadIndex, message);

    private static DecisionSurfaceResult NotReady(string message) =>
        DecisionSurfaceResult.Reject(DecisionSurfaceError.NotReady, message);
}

internal sealed class HeadlessDecisionSurface : IDecisionSurface
{
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

    public bool TryOwnBundleCompletion(
        IReadOnlyList<IReadOnlyList<CardModel>> bundles,
        out Task<IEnumerable<CardModel>> completion)
    {
        completion = HeadlessBundle.Park(bundles);
        return true;
    }
}
