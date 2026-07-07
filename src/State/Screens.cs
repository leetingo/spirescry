using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using CrystalMinigame = MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame;
using CrystalScreen = MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen;

namespace Spirescry.State;

// One accessor per GUI screen the bridge scrapes. Reflection over private
// engine members is the most version-fragile surface here, so every such
// member name lives exactly once, shared by the snapshot (observe) and
// dispatch (act) paths — an engine rename breaks both in one place, and
// the snapshot's idx contract can't drift from the verbs' index
// resolution.
internal static class Screens
{
    public static T? Top<T>() where T : class => NOverlayStack.Instance?.Peek() as T;

    public static CrystalScreen? Crystal() => Top<CrystalScreen>();

    // Relic reward overlay: tiles are NRelicBasicHolder children of _relicRow.
    public static List<NRelicBasicHolder>? RelicHolders(NChooseARelicSelection screen) =>
        Reflect.Field<Godot.Control>(screen, "_relicRow")
            ?.GetChildren().OfType<NRelicBasicHolder>().ToList();

    // Rewards screen: the full button list — claimed tiles linger disabled
    // during their hide tween, so sibling indices stay stable.
    public static List<object>? RewardButtons(NRewardsScreen screen) =>
        Reflect.Field<System.Collections.IEnumerable>(screen, "_rewardButtons")
            ?.Cast<object>().ToList();

    // A tile is claimable while its button is enabled with a live reward.
    public static NRewardButton? ClaimableReward(object buttonItem) =>
        buttonItem is NRewardButton { IsEnabled: true, Reward: not null } btn ? btn : null;

    // Card reward / choose-a-card overlays: cards are NCardHolder children
    // of _cardRow. The row wires a frame or two after the screen mounts —
    // null/empty means "poll again", not "zero cards".
    public static List<NCardHolder>? CardHolders(object screen) =>
        Reflect.Field<Godot.Control>(screen, "_cardRow")
            ?.GetChildren().OfType<NCardHolder>().ToList();

    // Non-card choices on the card-reward screen (skip, trade offers, …).
    public static List<object> ExtraOptions(NCardRewardSelectionScreen screen) =>
        Reflect.Field<System.Collections.IEnumerable>(screen, "_extraOptions")
            ?.Cast<object>().ToList() ?? [];

    public static bool ChooseSkipEnabled(NChooseACardSelectionScreen screen) =>
        Reflect.Field<NClickableControl>(screen, "_skipButton") is { IsEnabled: true };

    // Grid pickers: cards behind an NCardGrid, bounded by CardSelectorPrefs.
    public static List<CardModel>? GridCards(NCardGridSelectionScreen screen) =>
        Reflect.Field<NCardGrid>(screen, "_grid")?.CurrentlyDisplayedCards.ToList();

    // _prefs / _selectedCards live on both the grid screens and NPlayerHand.
    public static CardSelectorPrefs Prefs(object screenOrHand) =>
        (CardSelectorPrefs)Reflect.FieldValue(screenOrHand, "_prefs")!;

    public static IEnumerable<CardModel> SelectedCards(object screenOrHand) =>
        Reflect.Field<IEnumerable<CardModel>>(screenOrHand, "_selectedCards") ?? [];

    // Headless has no chest-flag node; the flag lives on the visual room.
    public static bool ChestOpened(object treasureRoomNode) =>
        Reflect.FieldValue(treasureRoomNode, "_hasChestBeenOpened") is true;

    // Rest site: in the GUI the visual node owns the options and the room
    // model's list stays empty; headless it's the other way around.
    public static IReadOnlyList<RestSiteOption>? RestOptions() =>
        NRestSiteRoom.Instance?.Options
        ?? (RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom as RestSiteRoom)?.Options;

    // The event model both the snapshot and the option verb act on.
    public static EventModel? CurrentEvent() =>
        (RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom as EventRoom)?.LocalMutableEvent;

    public static MerchantInventory? ShopInventory(RunState? rs) =>
        (rs?.CurrentRoom as MerchantRoom)?.GetLocalInventory();

    // Bundle screen: _bundles carries the models (snapshot), _bundleRow's
    // NCardBundle children are the click targets — the engine fills both
    // in the same order, so idx maps 1:1 across observe and act.
    public static IReadOnlyList<IReadOnlyList<CardModel>>? Bundles(NChooseABundleSelectionScreen screen) =>
        Reflect.Field<IReadOnlyList<IReadOnlyList<CardModel>>>(screen, "_bundles");

    public static List<Godot.Node>? BundleNodes(NChooseABundleSelectionScreen screen) =>
        Reflect.Field<Godot.Control>(screen, "_bundleRow")?.GetChildren()
            .Where(n => n.GetType().Name == "NCardBundle").ToList();

    // Crystal sphere: the minigame model (snapshot) and the cell-node
    // container (click targets) hang off the same screen.
    public static CrystalMinigame? CrystalEntity(CrystalScreen screen) =>
        Reflect.FieldValue(screen, "_entity") as CrystalMinigame;

    public static Godot.Control? CrystalCellContainer(CrystalScreen screen) =>
        Reflect.Field<Godot.Control>(screen, "_cellContainer");
}

// The multiplayer action-queue internals (private on ActionQueueSet),
// read by both the /health diagnostics and the not_ready gating.
internal static class EngineQueues
{
    public static IEnumerable<(ulong owner, int depth, bool paused)> All(RunManager? rm)
    {
        if (rm?.ActionQueueSet is not { } aqs
            || Reflect.FieldValue(aqs, "_actionQueues")
                is not System.Collections.IEnumerable queues) yield break;
        foreach (var q in queues)
        {
            if (Reflect.FieldValue(q, "ownerId") is not ulong owner) continue;
            yield return (
                owner,
                (Reflect.FieldValue(q, "actions") as System.Collections.ICollection)?.Count ?? 0,
                Reflect.FieldValue(q, "isPaused") is true);
        }
    }
}
