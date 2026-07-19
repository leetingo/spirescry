using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using CrystalMinigame = MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame;

namespace Spirescry.State;

// TreasureRoom.Enter primes relic picking before the GUI chest is clicked.
// The headless host gates that engine callback so merely entering/observing
// the room cannot reveal rewards; a bridge verb opens the gate explicitly.
public static class HeadlessTreasure
{
    private static int _openDepth;

    public static bool CanBeginRelicPicking => _openDepth > 0;

    // The room whose chest a verb already opened. GUI mode reads the chest
    // node's own flag; headless has no node, so this reference is the
    // chest state — the snapshot's chestOpened must stay true after the
    // offer resolves (picked relics empty the offer, not the chest).
    public static MegaCrit.Sts2.Core.Rooms.TreasureRoom? OpenedRoom { get; set; }

    public static void Open(Action beginRelicPicking)
    {
        _openDepth++;
        try { beginRelicPicking(); }
        finally { _openDepth--; }
    }

    public static void Clear() => OpenedRoom = null;
}

// The real GUI/headless boundary is completion ownership: every pending
// player choice is a TaskCompletionSource the engine awaits, and someone
// must hold it. In GUI boots the engine's own screens hold it (the bridge
// clicks them); headless has no screens, so the two stand-ins below hold
// it instead. When adding a new interaction, the only question is: who
// owns this choice's completion in each boot?
//
// Headless has no UI screens, so two stand-ins replace them:
//
// HeadlessPicker — one persistent deferred ICardSelector for every card
// pick the engine would normally route to a selection screen (rest-site
// upgrade, shop removal, event transforms, mid-combat hand/pile picks,
// and choices fired later by turn-start powers). If the engine asks, its
// await parks on a TCS, the phase flips to card_select/hand_select, and
// the agent's pick-card/confirm resolves it inline.
//
// HeadlessRewards — the post-combat rewards flow normally lives on
// NRewardsScreen. Headless builds the same RewardsSet from the room and
// drives Reward.SelectUnsynchronized directly.
public static class HeadlessPicker
{
    private static IDisposable? _scope;
    private static TaskCompletionSource<IEnumerable<CardModel>>? _tcs;
    private static IReadOnlyList<CardModel> _candidates = [];
    private static readonly List<CardModel> _picked = new();

    public static bool IsActive => _tcs is not null;
    public static IReadOnlyList<CardModel> Candidates => _candidates;
    public static IReadOnlyList<CardModel> Picked => _picked;
    public static int MinSelect { get; private set; }
    public static int MaxSelect { get; private set; }

    // Install once for the host lifetime. A per-verb scope misses choices
    // that fire later (TOOLS_OF_THE_TRADE at the next turn start).
    public static void Install()
    {
        if (!RunMode.IsHeadless || _scope is not null) return;
        _scope = CardSelectCmd.PushSelector(new Deferred());
    }

    // Call sites keep Around for readability: it documents that the
    // operation may park on a choice, while Install makes that guarantee
    // hold for delayed hooks too. No-op beyond ensuring installation.
    public static void Around(Action engineCall)
    {
        Install();
        engineCall();
    }

    // Toggle a candidate; auto-resolve when MaxSelect is reached (Confirm
    // covers partial selections >= MinSelect).
    public static string? Pick(int idx)
    {
        if (_tcs is null) return "no selection pending";
        if (idx < 0 || idx >= _candidates.Count)
            return $"card idx {idx} out of range [0,{_candidates.Count - 1}]";
        var card = _candidates[idx];
        if (!_picked.Remove(card))
        {
            _picked.Add(card);
            if (_picked.Count >= MaxSelect) Resolve(_picked.ToArray());
        }
        return null;
    }

    public static string? Confirm()
    {
        if (_tcs is null) return "no selection pending";
        if (_picked.Count < MinSelect)
            return $"{_picked.Count} selected, need at least {MinSelect}";
        Resolve(_picked.ToArray());
        return null;
    }

    // Empty result — the engine treats it as "cancel / skip the effect".
    public static void CancelIfActive()
    {
        if (_tcs is not null) Resolve([]);
    }

    private static void Resolve(IEnumerable<CardModel> picked)
    {
        var tcs = _tcs!;
        _tcs = null;
        _candidates = [];
        _picked.Clear();
        MinSelect = MaxSelect = 0;
        // Synchronous continuation: the engine's awaiting chain (card
        // effect, purchase, event option) completes inline before this
        // returns — headless has no frame loop to resume it later.
        tcs.TrySetResult(picked);
    }

    private sealed class Deferred : ICardSelector
    {
        // Card-reward grids route through HeadlessRewards' indexed
        // selector, never this one.
        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives) => default;

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            _candidates = options.ToList();
            MinSelect = minSelect;
            MaxSelect = maxSelect;
            _tcs = new TaskCompletionSource<IEnumerable<CardModel>>();
            return _tcs.Task;
        }
    }
}

// Bundle offers (Neow's card packs, …). The engine's own entry —
// CardSelectCmd.FromChooseABundleScreen — is UI-only, so the host boot
// reroutes it here via a Harmony prefix: the bundles park on a TCS, the
// agent picks one by idx, the chosen bundle's cards resolve the call.
public static class HeadlessBundle
{
    private static TaskCompletionSource<IEnumerable<CardModel>>? _tcs;
    private static IReadOnlyList<IReadOnlyList<CardModel>> _bundles = [];

    public static bool IsActive => _tcs is not null;
    public static IReadOnlyList<IReadOnlyList<CardModel>> Bundles => _bundles;

    public static Task<IEnumerable<CardModel>> Park(
        IReadOnlyList<IReadOnlyList<CardModel>> bundles)
    {
        _bundles = bundles;
        _tcs = new TaskCompletionSource<IEnumerable<CardModel>>();
        return _tcs.Task;
    }

    public static string? Pick(int idx)
    {
        if (_tcs is null) return "no bundle offer pending";
        if (idx < 0 || idx >= _bundles.Count)
            return $"bundle idx {idx} out of range [0,{_bundles.Count - 1}]";
        Resolve(_bundles[idx]);
        return null;
    }

    public static void CancelIfActive()
    {
        if (_tcs is not null) Resolve([]);
    }

    private static void Resolve(IEnumerable<CardModel> cards)
    {
        var tcs = _tcs!;
        _tcs = null;
        _bundles = [];
        tcs.TrySetResult(cards);
    }
}

// The crystal-sphere minigame runs entirely in the model (cells, tools,
// rewards, completion source); only ShowScreen is UI. The host boot's
// prefix parks the live minigame here and skips the screen — the verbs
// drive the model directly, and the minigame's own completion (last
// divination, or ForceMinigameEnd) resumes the awaiting event.
public static class HeadlessCrystal
{
    public static CrystalMinigame? Entity { get; private set; }

    public static bool IsActive => Entity is { IsFinished: false };

    public static void Park(CrystalMinigame entity) => Entity = entity;

    public static void Clear() => Entity = null;
}

// Every stand-in above holds run-scoped state; abandon calls this so no
// parked completion source or captured entity leaks into the next run.
// A new stand-in belongs on BOTH lists here (ResetAll and
// HasParkedDecision) and in PhaseDetector's stand-in phase mapping.
public static class HeadlessState
{
    // True while any stand-in holds a completion source the agent must
    // resolve with a verb. The follow probe keys on this: an event
    // option's task parked here must not read as busy (the agent acts
    // next, the engine won't). Single owner — the probe must not carry
    // its own copy of this list.
    public static bool HasParkedDecision =>
        HeadlessPicker.IsActive
        || HeadlessBundle.IsActive
        || HeadlessCrystal.IsActive
        || HeadlessRewards.IsActive
        || HeadlessRewards.InCardPick;

    public static void ResetAll()
    {
        HeadlessRewards.Clear();
        HeadlessPicker.CancelIfActive();
        HeadlessBundle.CancelIfActive();
        HeadlessCrystal.Clear();
        HeadlessTreasure.Clear();
        // A dead run's option task may be parked on a combat or dialog
        // that will never resume — one zombie would hold the follow
        // probe's busy flag for every later run.
        Signals.DropEventOptionTracking();
    }
}

public static class HeadlessRewards
{
    // Slot list: a picked entry's slot goes null but stays in place so the
    // other rewards keep their idx between picks.
    private static List<Reward?>? _pending;
    private static CardReward? _activeCardPick;
    // Guards against re-capturing after the agent consumed this room's
    // rewards; goes stale (and self-resets) when CurrentRoom advances.
    private static MegaCrit.Sts2.Core.Rooms.AbstractRoom? _completedFor;

    public static bool IsActive => _pending is not null;
    public static bool InCardPick => _activeCardPick is not null;
    public static CardReward? ActiveCardPick => _activeCardPick;

    public static IEnumerable<(int idx, Reward reward)> Slotted()
    {
        if (_pending is null) yield break;
        for (var i = 0; i < _pending.Count; i++)
            if (_pending[i] is { } r) yield return (i, r);
    }

    public static void Clear()
    {
        _pending = null;
        _activeCardPick = null;
    }

    // Build the RewardsSet for the current (just-finished) combat room and
    // populate every reward. Idempotent while active.
    public static bool CaptureFromCurrentRoom()
    {
        if (IsActive) return true;
        var rm = RunManager.Instance;
        var rs = rm?.DebugOnlyGetState();
        var room = rs?.CurrentRoom;
        var player = rs is null ? null : LocalContext.GetMe(rs);
        if (rm is null || room is null || player is null) return false;
        if (ReferenceEquals(_completedFor, room)) return false;

        try
        {
            var set = new RewardsSet(player, rm.RewardsSetSynchronizer).WithRewardsFromRoom(room);
            // Async but pure logic — drains inline under the host's
            // patches, so GetResult returns synchronously.
            set.GenerateWithoutOffering().GetAwaiter().GetResult();
            _pending = Flatten(set.Rewards).Select(r => (Reward?)r).ToList();
            return true;
        }
        catch (Exception ex)
        {
            SafeLog.Error("headless rewards capture", ex);
            _pending = null;
            return false;
        }
    }

    // Custom reward offers (event trades) arrive through the host's
    // NRewardsScreen.ShowScreen reroute already generated — park them in
    // the same slot list so pick-reward/proceed claim them like any other
    // rewards. Appends when a set is already live: two offers can't
    // overwrite each other.
    public static void CaptureFromSet(RewardsSet set)
    {
        var slots = Flatten(set.Rewards).Select(r => (Reward?)r).ToList();
        if (_pending is null) _pending = slots;
        else _pending.AddRange(slots);
        _completedFor = null;
    }

    // LinkedRewardSet is a no-op wrapper — surface its children flat.
    private static IEnumerable<Reward> Flatten(IEnumerable<Reward> rewards)
    {
        foreach (var r in rewards)
        {
            if (r is LinkedRewardSet linked)
                foreach (var inner in Flatten(linked.Rewards)) yield return inner;
            else yield return r;
        }
    }

    public static string? PickReward(int idx)
    {
        if (_pending is null) return "no rewards pending";
        if (idx < 0 || idx >= _pending.Count || _pending[idx] is not { } reward)
            return $"reward idx {idx} is not claimable";
        if (reward is CardReward cr)
        {
            // Sub-pick: agent follows with pick-card / skip.
            _activeCardPick = cr;
            return null;
        }
        try { reward.SelectUnsynchronized().GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            SafeLog.Error("headless pick-reward", ex);
            return $"{ex.GetType().Name}: {ex.Message}";
        }
        _pending[idx] = null;
        return null;
    }

    // The same "alternatives" the GUI card-reward screen offers (skip,
    // trade options, …), regenerated on demand — Generate is pure over the
    // reward.
    public static IReadOnlyList<CardRewardAlternative> Alternatives() =>
        _activeCardPick is { } cr
            ? CardRewardAlternative.Generate(cr)
            : [];

    // Resolve the pending CardReward sub-pick: a selector pinned to idx
    // feeds CardReward's null-screen branch. altIdx picks an alternative
    // instead of a card.
    public static string? PickCard(int idx, bool alternative = false)
    {
        if (_activeCardPick is not { } cr) return "no card reward pending";
        var count = alternative ? Alternatives().Count : cr.Cards?.Count() ?? 0;
        if (idx < 0 || idx >= count)
            return $"{(alternative ? "alternative" : "card")} idx {idx} out of range [0,{count - 1}]";
        IDisposable? scope = null;
        try
        {
            scope = CardSelectCmd.PushSelector(new IndexedSelector(idx, alternative));
            cr.SelectUnsynchronized().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            SafeLog.Error("headless pick-card", ex);
            return $"{ex.GetType().Name}: {ex.Message}";
        }
        finally { scope?.Dispose(); }
        NullSlotFor(cr);
        _activeCardPick = null;
        return null;
    }

    // Leave the virtual rewards screen: skip whatever's unclaimed, mark
    // this room consumed.
    public static void SkipAllAndClear()
    {
        foreach (var (_, r) in Slotted()) InvokeOnSkipped(r);
        _completedFor = RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom;
        Clear();
    }

    // OnSkipped is protected on Reward; it fires the skip hooks the UI
    // would.
    private static void InvokeOnSkipped(Reward r)
    {
        try
        {
            if (Reflect.Invoke(r, "OnSkipped") is Task t) t.GetAwaiter().GetResult();
        }
        catch (Exception ex) { SafeLog.Error("headless reward skip", ex); }
    }

    private static void NullSlotFor(Reward target)
    {
        if (_pending is null) return;
        for (var i = 0; i < _pending.Count; i++)
            if (ReferenceEquals(_pending[i], target)) { _pending[i] = null; return; }
    }

    private sealed class IndexedSelector : ICardSelector
    {
        private readonly int _idx;
        private readonly bool _alternative;

        public IndexedSelector(int idx, bool alternative = false)
        {
            _idx = idx;
            _alternative = alternative;
        }

        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives) =>
            _alternative
                ? _idx >= 0 && _idx < alternatives.Count
                    ? new CardRewardSelection { card = null, alternative = alternatives[_idx] }
                    : default
                : _idx >= 0 && _idx < options.Count
                    ? new CardRewardSelection { card = options[_idx].Card, alternative = null }
                    : default;

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var list = options as IList<CardModel> ?? options.ToList();
            return Task.FromResult<IEnumerable<CardModel>>(
                _idx >= 0 && _idx < list.Count ? [list[_idx]] : []);
        }
    }
}
