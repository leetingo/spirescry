using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
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
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using CrystalMinigame = MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame;

namespace Spirescry.State;

public static class Snapshotter
{
    // Compact mode elides the big repeats (map graph, per-card deck) for
    // agents that poll often. Set per snapshot; safe as a static because
    // the pump runs one job at a time on the main thread.
    private static bool _compact;
    private static bool _decision;
    private static HashSet<string> _knownCardTexts = new(StringComparer.Ordinal);
    private static HashSet<string> _emittedCardTexts = new(StringComparer.Ordinal);

    // Must be called on the main thread.
    public static object ForCurrentPhase() => ForCurrentPhase(false);

    public static object ForCurrentPhase(bool compact) => ForCurrentPhase(compact, false, []);

    public static object ForCurrentPhase(
        bool compact, bool decision, IEnumerable<string> knownCardTexts)
    {
        _compact = compact || decision;
        _decision = decision;
        // Per-request scope: repeated GETs are identical. The caller owns
        // cross-request caching explicitly through ?known= / --known-card.
        _knownCardTexts = knownCardTexts.ToHashSet(StringComparer.Ordinal);
        _emittedCardTexts = new HashSet<string>(StringComparer.Ordinal);
        var phase = PhaseDetector.Current();
        return phase switch
        {
            Phase.Combat => CombatSnapshot(phase.AsString()),
            Phase.Map => MapSnapshot(phase.AsString()),
            Phase.Event => EventSnapshot(phase.AsString()),
            Phase.Rewards => RewardsSnapshot(phase.AsString()),
            Phase.CardReward => CardRewardSnapshot(phase.AsString()),
            Phase.Shop => ShopSnapshot(phase.AsString()),
            Phase.RestSite => RestSiteSnapshot(phase.AsString()),
            Phase.Treasure => TreasureSnapshot(phase.AsString()),
            Phase.RelicReward => RelicRewardSnapshot(phase.AsString()),
            Phase.CardSelect => CardSelectSnapshot(phase.AsString()),
            Phase.HandSelect => HandSelectSnapshot(phase.AsString()),
            Phase.BundleSelect => BundleSelectSnapshot(phase.AsString()),
            Phase.CrystalSphere => CrystalSphereSnapshot(phase.AsString()),
            Phase.GameOver => GameOverSnapshot(phase.AsString()),
            // Name the capturing overlay so a stuck screen is diagnosable
            // from /obs alone.
            Phase.Overlay or Phase.Unknown => new
            {
                phase = phase.AsString(),
                overlay = NOverlayStack.Instance?.Peek()?.GetType().Name,
            },
            _ => new { phase = phase.AsString() },
        };
    }

    // Bundle offers: pick one pack of cards (Neow's Scroll Boxes, …).
    private static object BundleSelectSnapshot(string phase)
    {
        var gui = RunMode.IsHeadless ? null : Screens.Top<NChooseABundleSelectionScreen>();
        var bundles = RunMode.IsHeadless
            ? HeadlessBundle.Bundles
            : gui is { } screen
                ? Screens.Bundles(screen)
                : null;
        if (bundles is null || bundles.Count == 0) return new { phase, available = false };
        return new
        {
            phase,
            player = FooterView(),
            confirmable = gui is not null
                && Reflect.Field<NClickableControl>(gui, "_confirmButton") is { IsEnabled: true },
            cancelable = RunMode.IsHeadless
                ? HeadlessBundle.IsActive
                : gui is not null
                    && Reflect.Field<NClickableControl>(gui, "_skipButton") is { IsEnabled: true },
            bundles = bundles.Select((b, i) => new
            {
                idx = i,
                cards = b.Select(RewardCardView).ToArray(),
            }).ToArray(),
        };
    }

    // The crystal-sphere event minigame: a cell grid, a divination tool,
    // and a fixed number of reveals. map-move clicks a cell, option picks
    // the tool, proceed leaves. GUI-only — the screen owns the minigame.
    private static object CrystalSphereSnapshot(string phase)
    {
        var entity = RunMode.IsHeadless
            ? HeadlessCrystal.Entity
            : Screens.Crystal() is { } screen
                ? Screens.CrystalEntity(screen)
                : null;
        if (entity is null) return new { phase, available = false };

        var grid = entity.GridSize;
        var cells = new List<object>();
        for (var y = 0; y < grid.Y; y++)
            for (var x = 0; x < grid.X; x++)
            {
                if (entity.cells[x, y] is not { } cell) continue;
                cells.Add(new
                {
                    col = cell.X,
                    row = cell.Y,
                    hidden = cell.IsHidden,
                    hasItem = cell.Item is not null && !cell.IsHidden,
                });
            }
        return new
        {
            phase,
            player = FooterView(),
            grid = new { width = grid.X, height = grid.Y },
            divinationsLeft = entity.DivinationCount,
            tool = entity.CrystalSphereTool.ToString().ToLowerInvariant(),
            finished = entity.IsFinished,
            cells,
        };
    }

    private static object GameOverSnapshot(string phase)
    {
        var rm = RunManager.Instance;
        var rs = rm?.DebugOnlyGetState();
        if (rm is null || rs is null) return new { phase, available = false };
        var player = LocalContext.GetMe(rs);
        var creature = player?.Creature;
        return new
        {
            phase,
            // WinTime is only stamped on a won run; abandon wins the tie
            // so a mid-run bail never reads as a defeat.
            outcome = rm.IsAbandoned ? "abandoned" : rm.WinTime > 0 ? "victory" : "defeat",
            // Compat pair: floor is the run-cumulative TotalFloor, act the
            // zero-based CurrentActIndex. actNumber/actFloor/mapCoord say
            // the same position in the 1-based, act-local terms a run
            // report wants.
            floor = rs.TotalFloor,
            act = rs.CurrentActIndex,
            actNumber = rs.CurrentActIndex + 1,
            actFloor = rs.ActFloor,
            mapCoord = rs.CurrentMapCoord is { } c ? new { c.col, c.row } : null,
            // The room the run ended in — null when it wasn't a combat
            // (abandon from an event, shop, or the map).
            encounter = rs.CurrentRoom is CombatRoom room
                ? new
                {
                    model = room.Encounter.Id.Entry,
                    title = SafeText(room.Encounter.Title),
                }
                : null,
            ascension = rs.AscensionLevel,
            seed = rs.Rng?.StringSeed ?? "",
            hp = creature is null ? null : new[] { creature.CurrentHp, creature.MaxHp },
            gold = player?.Gold,
        };
    }

    // Out-of-combat decisions need run context: HEAL vs SMITH reads hp,
    // card picks read the deck, events price their options in gold.
    private static object? FooterView()
    {
        var rs = RunManager.Instance?.DebugOnlyGetState();
        var player = rs is null ? null : LocalContext.GetMe(rs);
        if (player is null) return null;
        var creature = player.Creature;
        return new
        {
            hp = creature is null ? null : new[] { creature.CurrentHp, creature.MaxHp },
            gold = player.Gold,
            potions = PotionViews(player),
            // Keep the original ID list stable for existing agents; rich,
            // mutable state is additive so a schema upgrade is not required.
            relics = player.Relics.Select(r => r.Id.Entry).ToArray(),
            relicStates = player.Relics.Select(RelicStateView).ToArray(),
            deck = DeckView(player),
        };
    }

    // Compact: counts by model, "+" marking upgraded copies — a 30-card
    // deck collapses to a dozen keys instead of 30 objects per snapshot.
    // Enchantments/afflictions are the run's invisible card modifiers
    // (event rewards like SELF_HELP_BOOK enchant a card and change nothing
    // else observable); compact keys carry them as @ENCHANT / !AFFLICTION.
    private static object DeckView(Player player)
    {
        var cards = (player.Deck?.Cards ?? Enumerable.Empty<CardModel>())
            .Where(c => c != null);
        if (!_compact)
            return cards
                .Select(c => new
                {
                    model = c.Id.Entry,
                    upgraded = c.IsUpgraded,
                    enchant = c.Enchantment?.Id.Entry,
                    affliction = c.Affliction?.Id.Entry,
                })
                .ToArray();
        return cards
            .GroupBy(c => c.Id.Entry
                + (c.IsUpgraded ? "+" : "")
                + (c.Enchantment is { } e ? "@" + e.Id.Entry : "")
                + (c.Affliction is { } a ? "!" + a.Id.Entry : ""))
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    // The GUI inventory badge's number: DisplayAmount, shown only for
    // relics that opt in. null = this relic keeps no visible count.
    private static int? RelicCounter(RelicModel r) =>
        r.ShowCounter ? r.DisplayAmount : null;

    // A relic's whole observable story: its count, whether a one-shot
    // already fired, and the text — the pickup offer was the only place
    // an agent could ever read it.
    private static object RelicStateView(RelicModel r) => new
    {
        model = r.Id.Entry,
        counter = RelicCounter(r),
        usedUp = r.IsUsedUp,
        description = _compact ? null : SafeText(r.DynamicDescription),
    };

    // Empty slots and queued/consumed potions are omitted; `slot` is what
    // potion-use / potion-discard take.
    private static object[] PotionViews(Player player)
    {
        var slots = player.PotionSlots;
        var result = new List<object>();
        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i] is not { } p || p.IsQueued || p.HasBeenRemovedFromState) continue;
            result.Add(new
            {
                slot = i,
                model = p.Id.Entry,
                target = p.TargetType.ToString().ToLowerInvariant(),
                description = SafeText(p.DynamicDescription),
            });
        }
        return result.ToArray();
    }

    // Channeled orbs in slot order (index 0 evokes first) plus the slot
    // count — the Defect's whole economy. null for characters with no orb
    // capacity, so other snapshots stay clean.
    private static object? OrbViews(PlayerCombatState pcs)
    {
        var q = pcs.OrbQueue
            ?? throw new InvalidOperationException("player orb queue is unavailable");
        if (q.Capacity == 0) return null;
        return new
        {
            slots = q.Capacity,
            channeled = q.Orbs
                .Where(o => o != null)
                .Select(o => (object)new
                {
                    id = o.Id.Entry,
                    passive = o.PassiveVal,
                    evoke = o.EvokeVal,
                }).ToArray(),
        };
    }

    private static object[] PowerViews(Creature c)
    {
        try
        {
            return c.Powers.Select(p => (object)new
            {
                id = p.Id.Entry,
                amount = p.Amount,
                description = SafeText(p.Description),
            }).ToArray();
        }
        catch { return []; }
    }

    // Draw contents are sorted so the snapshot can't leak draw order.
    private static object PileView(CardPile? pile, bool sorted = false)
    {
        var models = (pile?.Cards ?? Enumerable.Empty<CardModel>())
            .Where(c => c != null)
            .Select(c => c.Id.Entry);
        // Compact: counts-by-model — pile order is either hidden (draw is
        // shown sorted anyway) or rarely decision-relevant.
        if (_compact)
        {
            var counts = models.GroupBy(m => m)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count());
            return new { count = counts.Values.Sum(), cards = (object)counts };
        }
        var cards = sorted
            ? models.OrderBy(m => m, StringComparer.Ordinal).ToArray()
            : models.ToArray();
        return new { count = cards.Length, cards = (object)cards };
    }

    private static object ShopSnapshot(string phase)
    {
        var rs = RunManager.Instance?.DebugOnlyGetState();
        var inv = Screens.ShopInventory(rs);
        if (rs is null || inv is null) return new { phase, available = false };
        var player = LocalContext.GetMe(rs);

        // `cost` stays the gold amount for wire compatibility; `price`
        // names it explicitly. Cards add playCost/starCost so their combat
        // economy is visible before purchase without changing old clients.
        object CardEntry(MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardEntry e, int i)
        {
            var card = e.CreationResult?.Card;
            var showText = card is not null && ShouldShowCardText(card);
            return new
            {
                idx = i,
                model = card?.Id.Entry,
                title = card?.Title,
                textKey = card is null ? null : CardTextKey(card),
                description = showText ? CardDescription(card!) : null,
                cost = e.Cost,
                price = e.Cost,
                playCost = card?.EnergyCost.Canonical,
                starCost = card is null ? null : StarCost(card),
                stocked = e.IsStocked,
                affordable = e.EnoughGold,
                purchasable = e.IsStocked && e.EnoughGold,
            };
        }
        // Model goes null once the entry is purchased — like CardEntry,
        // render the sold-out tile instead of throwing.
        object StockEntry(string? model, LocString? title, MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry e, int i)
        {
            var potionHasRoom = e is not MegaCrit.Sts2.Core.Entities.Merchant.MerchantPotionEntry
                || player?.HasOpenPotionSlots == true;
            return new
            {
                idx = i,
                model,
                title = SafeText(title),
                cost = e.Cost,
                price = e.Cost,
                stocked = e.IsStocked,
                affordable = e.EnoughGold,
                purchasable = e.IsStocked && e.EnoughGold && potionHasRoom,
            };
        }
        return new
        {
            phase,
            gold = player?.Gold ?? 0,
            player = FooterView(),
            cards = inv.CharacterCardEntries.Select(CardEntry).ToArray(),
            colorless = inv.ColorlessCardEntries.Select(CardEntry).ToArray(),
            relics = inv.RelicEntries
                .Select((e, i) => StockEntry(e.Model?.Id.Entry, e.Model?.Title, e, i)).ToArray(),
            potions = inv.PotionEntries
                .Select((e, i) => StockEntry(e.Model?.Id.Entry, e.Model?.Title, e, i)).ToArray(),
            cardRemoval = inv.CardRemovalEntry is { } cr
                ? new
                {
                    cost = cr.Cost,
                    price = cr.Cost,
                    used = cr.Used,
                    affordable = cr.EnoughGold,
                    purchasable = !cr.Used && cr.EnoughGold,
                }
                : null,
        };
    }

    private static object RestSiteSnapshot(string phase)
    {
        var options = Screens.RestOptions();
        if (options is null) return new { phase, available = false };
        return new
        {
            phase,
            player = FooterView(),
            proceedAvailable = RunMode.IsHeadless
                || NRestSiteRoom.Instance?.ProceedButton is { Visible: true },
            options = options.Select((o, i) => new
            {
                idx = i,
                id = o.OptionId.ToString(),
                title = SafeText(o.Title),
                description = SafeText(o.Description),
                enabled = o.IsEnabled,
            }).ToArray(),
        };
    }

    // Observation never opens the chest. In headless mode pick-relic/skip
    // run the room rewards once; the synchronizer then makes the offer
    // visible here just as the GUI's chest-open callback does.
    private static object TreasureSnapshot(string phase)
    {
        var room = NRun.Instance?.TreasureRoom;
        var opened = room is not null && Screens.ChestOpened(room);
        var sync = RunManager.Instance?.TreasureRoomRelicSynchronizer;

        var relics = sync?.CurrentRelics;
        return new
        {
            phase,
            // Headless has no chest-flag node; offered relics == open chest.
            chestOpened = opened || relics is { Count: > 0 },
            proceedAvailable = RunMode.IsHeadless
                || NRun.Instance?.TreasureRoom?.ProceedButton is { Visible: true },
            player = FooterView(),
            relics = (relics ?? []).Select(RelicView).ToArray(),
        };
    }

    private static object RelicRewardSnapshot(string phase)
    {
        var screen = Screens.Top<NChooseARelicSelection>();
        var holders = screen is null ? null : Screens.RelicHolders(screen);
        if (holders is null || holders.Count == 0)
            return new { phase, available = false };
        return new
        {
            phase,
            player = FooterView(),
            relics = holders.Select((h, i) => RelicView(h.Relic.Model, i)).ToArray(),
        };
    }

    private static object RelicView(RelicModel r, int i) => new
    {
        idx = i,
        model = r.Id.Entry,
        title = SafeText(r.Title),
        rarity = r.Rarity.ToString().ToLowerInvariant(),
        description = SafeText(r.DynamicDescription),
    };

    // Tile idx is the position in the screen's full button list — claimed
    // tiles linger disabled during their hide tween, so sibling indices
    // stay stable; we just omit them from the snapshot.
    private static object RewardsSnapshot(string phase)
    {
        if (RunMode.IsHeadless)
            return new
            {
                phase,
                player = FooterView(),
                rewards = HeadlessRewards.Slotted()
                    .Select(s => RewardView(s.reward, s.idx)).ToList(),
            };

        var screen = Screens.Top<NRewardsScreen>();
        var buttons = screen is null ? null : Screens.RewardButtons(screen);
        if (buttons is null) return new { phase, available = false };

        var rewards = new List<object>();
        for (var i = 0; i < buttons.Count; i++)
            if (Screens.ClaimableReward(buttons[i]) is { } btn)
                rewards.Add(RewardView(btn.Reward!, i));
        return new { phase, player = FooterView(), rewards };
    }

    // Shared reward-tile view — see the per-card views below for why both
    // boots build their snapshots through one shape.
    private static object RewardView(Reward r, int i) => new
    {
        idx = i,
        type = RewardType(r),
        amount = r is GoldReward g ? g.Amount : (int?)null,
        description = SafeText(r.Description),
    };

    private static string RewardType(Reward r) => r switch
    {
        CardReward => "card",
        RelicReward => "relic",
        PotionReward => "potion",
        GoldReward => "gold",
        CardRemovalReward => "remove_card",
        _ => r.GetType().Name.ToLowerInvariant(),
    };

    private static object CardRewardSnapshot(string phase)
    {
        if (RunMode.IsHeadless)
        {
            var offered = HeadlessRewards.ActiveCardPick?.Cards?.ToList();
            if (offered is null) return new { phase, available = false };
            return new
            {
                phase,
                player = FooterView(),
                cards = offered.Select(RewardCardView).ToArray(),
                alternatives = HeadlessRewards.Alternatives()
                    .Select((a, i) => (object)new { idx = i, title = SafeText(a.Title) })
                    .ToArray(),
            };
        }

        var screen = Screens.Top<NCardRewardSelectionScreen>();
        var holders = screen is null ? null : Screens.CardHolders(screen);
        if (holders is null || holders.Count == 0)
            return new { phase, available = false };

        return new
        {
            phase,
            player = FooterView(),
            cards = holders.Select((h, i) => RewardCardView(h.CardModel, i)).ToArray(),
            // Non-card choices (skip, trade offers, …) — target of `skip`.
            alternatives = Screens.ExtraOptions(screen!)
                .Select((o, i) => new
                {
                    idx = i,
                    title = SafeText(Reflect.PropertyValue(o, "Title") as LocString),
                }).ToArray(),
        };
    }

    // GetDescriptionForPile bakes upgraded/runtime stats into the text;
    // the raw Description LocString throws without its variables filled.
    private static string CardDescription(CardModel card)
    {
        try { return RichText.NormalizeIcons(card.GetDescriptionForPile(PileType.None, null)); }
        catch { return SafeText(card.Description); }
    }

    // A caller-cache key for exactly the prose-bearing card variant. Card
    // model alone is insufficient: upgrades, enchantments, and afflictions
    // all change the rendered rules text while leaving Id.Entry unchanged.
    private static string CardTextKey(CardModel card) => DecisionProjection.CardTextKey(
        card.Id.Entry,
        card.CurrentUpgradeLevel,
        card.Enchantment?.Id.Entry,
        card.Affliction?.Id.Entry);

    private static bool ShouldShowCardText(CardModel card, bool compactElides = false)
    {
        if (!_decision) return !compactElides || !_compact;
        var key = CardTextKey(card);
        return !_knownCardTexts.Contains(key) && _emittedCardTexts.Add(key);
    }

    private static bool CanPlayCard(CardModel card)
    {
        try { return card.CanPlay(out _, out _); }
        catch { return false; }
    }

    // Shared per-card views — both boots build their snapshots through
    // these, so the shapes can't drift between modes (tests/parity.py
    // compares the key sets).
    private static object RewardCardView(CardModel c, int i)
    {
        var showText = ShouldShowCardText(c);
        return new
        {
            idx = i,
            model = c.Id.Entry,
            title = c.Title,
            cost = c.EnergyCost.Canonical,
            starCost = StarCost(c),
            type = c.Type.ToString().ToLowerInvariant(),
            rarity = c.Rarity.ToString().ToLowerInvariant(),
            textKey = CardTextKey(c),
            description = showText ? CardDescription(c) : null,
        };
    }

    private static object SelectCardView(CardModel c, int i, bool selected)
    {
        var showText = ShouldShowCardText(c);
        var preview = UpgradePreview(c);
        return new
        {
            idx = i,
            model = c.Id.Entry,
            title = c.Title,
            cost = c.EnergyCost.Canonical,
            starCost = StarCost(c),
            upgraded = c.IsUpgraded,
            enchant = c.Enchantment?.Id.Entry,
            affliction = c.Affliction?.Id.Entry,
            selected,
            textKey = CardTextKey(c),
            description = showText ? CardDescription(c) : null,
            // Preserve upgradedPreview's string/null wire type; numeric
            // upgrade economics do not need to be hidden with cached prose.
            upgradedPreview = showText ? preview?.description : null,
            upgradedPlayCost = preview?.playCost,
            upgradedStarCost = preview?.starCost,
        };
    }

    // -1 = the card has no star cost (the second combat currency);
    // surface null so energy-only cards stay clean. X-star cards report
    // the current star count, mirroring X-energy's cost display.
    private static int? StarCost(CardModel c)
    {
        try
        {
            var s = c.GetStarCostWithModifiers();
            return s >= 0 ? s : null;
        }
        catch { return null; }
    }

    // What the card text becomes if upgraded — the rest-site smith's
    // preview pane. null when the card can't upgrade further. Upgrades
    // mutate a card's dynamic vars in place, so the upgraded numbers only
    // exist on an upgraded instance: build a throwaway twin from the
    // canonical model and replay the upgrades one level past the card.
    private static (string description, int playCost, int? starCost)? UpgradePreview(CardModel c)
    {
        if (!c.IsUpgradable) return null;
        try
        {
            var twin = ModelDb.GetById<CardModel>(c.Id).ToMutable();
            for (var lvl = 0; lvl <= c.CurrentUpgradeLevel; lvl++)
                twin.UpgradeInternal();
            return (CardDescription(twin), twin.EnergyCost.Canonical, StarCost(twin));
        }
        catch { return null; }
    }

    private static object HandCardView(CardModel? card, int i)
    {
        var showText = card is not null
            && ShouldShowCardText(card, compactElides: true);
        return new
        {
            idx = i,
            model = card?.Id.Entry,
            cost = card?.EnergyCost.GetAmountToSpend(),
            starCost = card is null ? null : StarCost(card),
            upgraded = card?.IsUpgraded ?? false,
            enchant = card?.Enchantment?.Id.Entry,
            affliction = card?.Affliction?.Id.Entry,
            textKey = card is null ? null : CardTextKey(card),
            description = showText ? CardText(card!, PileType.Hand) : null,
            vars = card is null ? null : CardVars(card),
        };
    }

    private static object MapSnapshot(string phase)
    {
        var rs = RunManager.Instance?.DebugOnlyGetState();
        if (rs is null) return new { phase, available = false };

        return new
        {
            phase,
            act = rs.CurrentActIndex,
            seed = rs.Rng?.StringSeed ?? "",
            player = FooterView(),
            current = rs.CurrentMapCoord is { } c ? new[] { c.col, c.row } : null,
            next = NextPoints(rs).Select(MapPointView).ToArray(),
            // The act graph is the biggest repeat in the protocol — compact
            // callers keep `next` and re-request the full view when routing.
            graph = _compact ? null
                : rs.Map is { } map
                    ? AllMapPoints(map).Select(MapPointView).ToArray()
                    : [],
        };
    }

    // Shared by obs.next and obs.graph so the two views can't drift.
    private static object MapPointView(MegaCrit.Sts2.Core.Map.MapPoint p) => new
    {
        col = p.coord.col,
        row = p.coord.row,
        type = p.PointType.ToString().ToLowerInvariant(),
        // Outgoing edges — route planning needs the graph's links, not
        // just its nodes.
        next = p.Children
            .Where(ch => ch != null)
            .Select(ch => new[] { ch.coord.col, ch.coord.row })
            .ToArray(),
    };

    // BFS over the act map from its start points. The final act's second
    // boss hangs off the map without a child edge — include it explicitly.
    internal static List<MegaCrit.Sts2.Core.Map.MapPoint> AllMapPoints(
        MegaCrit.Sts2.Core.Map.ActMap map)
    {
        var seen = new HashSet<MegaCrit.Sts2.Core.Map.MapPoint>();
        var queue = new Queue<MegaCrit.Sts2.Core.Map.MapPoint>(map.startMapPoints);
        while (queue.TryDequeue(out var p))
        {
            if (p is null || !seen.Add(p)) continue;
            foreach (var ch in p.Children) queue.Enqueue(ch);
        }
        if (map.SecondBossMapPoint is { } second) seen.Add(second);
        return seen.ToList();
    }

    // Reachable next steps: normal children, or — standing on a beaten
    // first boss — the act's second boss.
    internal static IEnumerable<MegaCrit.Sts2.Core.Map.MapPoint> NextPoints(RunState rs)
    {
        var children = rs.CurrentMapPoint?.Children ?? rs.Map?.startMapPoints;
        foreach (var p in children ?? []) if (p is not null) yield return p;
        if (SecondBossPending(rs)) yield return rs.Map!.SecondBossMapPoint!;
    }

    // Standing on the beaten first boss of a two-boss act: the run stays
    // in the act and the second boss is the only next step. Shared with
    // the rewards-proceed verb so map view and act exit can't disagree.
    internal static bool SecondBossPending(RunState rs) =>
        rs.Map is { SecondBossMapPoint: not null } map
        && rs.CurrentMapPoint is { } here && ReferenceEquals(here, map.BossMapPoint);

    private static object EventSnapshot(string phase)
    {
        var ev = Screens.CurrentEvent();
        if (ev is null) return new { phase, available = false };
        var owner = ev.Owner;

        // EventRoom already called CalculateVars exactly once when the
        // event began. Never call it while observing: several events roll
        // RNG or advance counters there, so a read would change both the
        // advertised choice and the effect that the click later executes.
        return new
        {
            phase,
            id = ev.Id.Entry,
            player = FooterView(),
            title = SafeText(ev.Title),
            description = SafeText(ev.Description, local =>
            {
                owner?.Character.AddDetailsTo(local);
                local.Add("IsMultiplayer",
                    owner is not null && owner.RunState.Players.Count > 1);
                ev.DynamicVars.AddTo(local);
            }),
            finished = ev.IsFinished,
            fakeMerchant = ev is FakeMerchant fake ? FakeMerchantView(fake) : null,
            options = (ev.CurrentOptions ?? []).Select((o, i) => new
            {
                idx = i,
                title = SafeText(o.Title, ev.DynamicVars.AddTo),
                description = SafeText(o.Description, ev.DynamicVars.AddTo),
                locked = o.IsLocked,
                chosen = o.WasChosen,
                proceed = o.IsProceed,
                // The GUI marks choices that kill this player with a red
                // pulse. Null means the option has no lethal predicate.
                lethal = OptionLethal(o, owner),
                // GUI hover cards/tooltips are decision state, not
                // decoration: event choices often name a relic, curse, or
                // enchantment without explaining its effect in the body.
                hints = EventHints(o),
                relic = o.Relic is { } r ? new
                {
                    model = r.Id.Entry,
                    title = SafeText(r.Title),
                    description = SafeText(r.DynamicDescription),
                } : null,
            }).ToArray(),
        };
    }

    private static object FakeMerchantView(FakeMerchant fake)
    {
        var owner = fake.Owner;
        var inventory = fake.Inventory;
        return new
        {
            available = inventory is not null,
            canFight = owner?.PotionSlots.Any(p => p?.Id.Entry == "FOUL_POTION") == true,
            relics = (inventory?.RelicEntries ?? [])
                .Select((entry, i) => new
                {
                    idx = i,
                    model = entry.Model?.Id.Entry,
                    title = entry.Model is { } relic ? SafeText(relic.Title) : null,
                    description = entry.Model is { } described
                        ? SafeText(described.DynamicDescription)
                        : null,
                    price = entry.Cost,
                    // Keep `cost` as an alias for clients that already
                    // consume the ordinary shop's legacy gold-price field.
                    cost = entry.Cost,
                    stocked = entry.IsStocked,
                    affordable = entry.EnoughGold,
                }).ToArray(),
        };
    }

    private static bool? OptionLethal(EventOption option, Player? owner)
    {
        if (option.WillKillPlayer is null) return null;
        if (owner is null) return null;
        return option.WillKillPlayer(owner);
    }

    private static object[] EventHints(EventOption option) =>
        (option.HoverTips ?? []).Select(EventHintView).ToArray();

    private static object EventHintView(IHoverTip hint)
    {
        if (hint is CardHoverTip card)
            return new
            {
                kind = "card",
                model = card.Card.Id.Entry,
                title = RichText.NormalizeIcons(card.Card.Title ?? ""),
                description = CardDescription(card.Card),
                upgraded = card.Card.IsUpgraded,
            };
        if (hint is HoverTip text)
            return new
            {
                kind = "text",
                model = text.CanonicalModel?.Id.Entry,
                title = RichText.NormalizeIcons(text.Title ?? ""),
                description = RichText.NormalizeIcons(text.Description ?? ""),
            };
        return new
        {
            kind = hint.GetType().Name,
            model = hint.CanonicalModel?.Id.Entry,
            title = "",
            description = "",
        };
    }

    // The UI's own composed card text: dynamic vars refreshed first
    // (strength/weak applied through the engine's preview hooks — the GUI
    // card node does the same each frame), then the same description path
    // the card renders with, enchant/affliction lines included.
    // The same per-frame refresh the GUI card node does — bakes strength/
    // weak/etc. into PreviewValue, which both the text and vars read.
    private static void RefreshCardPreview(CardModel c)
    {
        try { c.UpdateDynamicVarPreview(CardPreviewMode.Normal, null, c.DynamicVars); }
        catch { }
    }

    private static string CardText(CardModel c, PileType pile)
    {
        try
        {
            RefreshCardPreview(c);
            return RichText.NormalizeIcons(c.GetDescriptionForPile(pile));
        }
        catch { return ""; }
    }

    // The numbers behind the text — lets an agent do arithmetic without
    // parsing bbcode. PreviewValue is what the card face shows.
    private static Dictionary<string, decimal>? CardVars(CardModel c)
    {
        try
        {
            var vars = new Dictionary<string, decimal>();
            foreach (var v in c.DynamicVars.Values)
                vars[v.Name] = v.PreviewValue;
            return vars.Count == 0 ? null : vars;
        }
        catch { return null; }
    }

    // Missing locale keys throw from GetFormattedText (Neow-style events
    // store their body elsewhere) — fall back to the raw entry key.
    private static string SafeText(LocString? s, Action<LocString>? addVariables = null)
    {
        if (s is null) return "";
        try
        {
            if (s.IsEmpty) return "";
            // Formatting variables belong to this observation, not the
            // shared model LocString. Copy its keys and existing variables
            // before supplying card-pipeline defaults for power/potion text.
            var local = new LocString(s.LocTable, s.LocEntryKey);
            local.AddVariablesFrom(s);
            addVariables?.Invoke(local);
            if (!local.Variables.ContainsKey("energyPrefix"))
                local.AddObj("energyPrefix", "");
            if (!local.Variables.ContainsKey("singleStarIcon"))
                local.AddObj("singleStarIcon", "[star]");
            var text = local.GetFormattedText();
            // The host's GetFormattedText finalizer degrades hard failures
            // to the entry key; a key echo means the entry doesn't exist —
            // the GUI renders nothing there, so neither do we.
            return text == s.LocEntryKey ? "" : RichText.NormalizeIcons(text);
        }
        catch
        {
            try { return RichText.NormalizeIcons(s.GetRawText() ?? ""); } catch { return ""; }
        }
    }

    // The phase can flip to Combat before every singleton is wired
    // (act transitions). Return `available: false` instead of throwing
    // so the agent gets something it can poll on.
    private static object CombatSnapshot(string phase)
    {
        var combat = CombatManager.Instance;
        var state = combat?.DebugOnlyGetState();
        if (state is null) return new { phase, available = false };
        var player = LocalContext.GetMe(state);
        var pcs = player?.PlayerCombatState;
        var creature = player?.Creature;
        if (pcs is null || creature is null)
            return new { phase, available = false };
        var facing = FacingOf(creature);

        return new
        {
            phase,
            turn = state.RoundNumber,
            side = state.CurrentSide.ToString().ToLowerInvariant(),
            actionsDisabled = combat.PlayerActionsDisabled,
            you = new
            {
                hp = new[] { creature.CurrentHp, creature.MaxHp },
                block = creature.Block,
                energy = new[] { pcs.Energy, pcs.MaxEnergy },
                stars = pcs.Stars,
                orbs = OrbViews(pcs),
                powers = PowerViews(creature),
                // Counters tick mid-combat (every-N relics); the full
                // relic story lives in the out-of-combat footer.
                relics = player!.Relics
                    .Select(r => (object)new { model = r.Id.Entry, counter = RelicCounter(r) })
                    .ToArray(),
                facing,
            },
            potions = PotionViews(player!),
            piles = new
            {
                draw = PileView(pcs.DrawPile, sorted: true),
                discard = PileView(pcs.DiscardPile),
                exhaust = PileView(pcs.ExhaustPile),
            },
            hand = pcs.Hand.Cards
                .Where(c => c != null)
                .Select(c =>
                {
                    // Compact keeps the numbers (vars) and drops the prose —
                    // the refresh still runs so vars carry modified values.
                    if (_compact) RefreshCardPreview(c);
                    var showText = ShouldShowCardText(c, compactElides: true);
                    return (object)new
                    {
                        model = c.Id.Entry,
                        cost = c.EnergyCost.GetAmountToSpend(),
                        starCost = StarCost(c),
                        target = c.TargetType.ToString().ToLowerInvariant(),
                        upgraded = c.IsUpgraded,
                        enchant = c.Enchantment?.Id.Entry,
                        affliction = c.Affliction?.Id.Entry,
                        unplayable = c.Keywords.Contains(CardKeyword.Unplayable),
                        playable = CanPlayCard(c),
                        textKey = CardTextKey(c),
                        description = showText ? CardText(c, PileType.Hand) : null,
                        vars = CardVars(c),
                    };
                })
                .ToArray(),
            enemies = state.Enemies
                .Where(c => c != null)
                .Select(c => new
                {
                    id = c.CombatId ?? 0u,
                    model = c.Monster?.Id.Entry,
                    title = SafeText(c.Monster?.Title),
                    hp = new[] { c.CurrentHp, c.MaxHp },
                    block = c.Block,
                    alive = c.IsAlive,
                    side = SideOf(c),
                    isBehind = IsBehind(c, facing),
                    powers = PowerViews(c),
                    intents = IntentViews(state, c),
                })
                .ToArray(),
        };
    }

    // Surround fights: the engine keeps the player's orientation as
    // SurroundedPower.Facing on the player, and marks each flanker with a
    // BackAttackLeft/RightPower. SurroundedPower's damage hook grants ×1.5
    // to the enemy on the side the player faces away from — these fields
    // expose exactly the state that hook reads. Null/false outside
    // surround fights.
    private static string? FacingOf(Creature creature) =>
        creature.GetPower<SurroundedPower>()?.Facing.ToString().ToLowerInvariant();

    private static string? SideOf(Creature enemy) =>
        enemy.HasPower<BackAttackLeftPower>() ? "left"
        : enemy.HasPower<BackAttackRightPower>() ? "right"
        : null;

    private static bool IsBehind(Creature enemy, string? facing) => facing switch
    {
        "right" => enemy.HasPower<BackAttackLeftPower>(),
        "left" => enemy.HasPower<BackAttackRightPower>(),
        _ => false,
    };

    // `damage` rides the intent's own calculator — the number the intent
    // pip shows, every modifier already applied (strength/weak, and in
    // surround fights the ×1.5 back attack). `baseDamage` is the raw roll
    // before the hooks, so an agent can see the modifier gap without
    // diffing turns. NextMove can be mid-roll during turn transitions; an
    // empty list means "poll again".
    private static object[] IntentViews(CombatState state, Creature enemy)
    {
        try
        {
            return (enemy.Monster?.NextMove.Intents ?? [])
                .Select(i => (object)new
                {
                    type = i.IntentType.ToString().ToLowerInvariant(),
                    damage = i is AttackIntent a ? a.GetSingleDamage(state.Allies, enemy) : (int?)null,
                    baseDamage = i is AttackIntent c && c.DamageCalc is { } calc
                        ? (int?)(int)calc()
                        : null,
                    hits = i is AttackIntent b ? b.Repeats : (int?)null,
                    // The game's own hover text — defend/buff magnitudes
                    // are hidden by design (the GUI shows none either),
                    // but status-card intents name their payload here.
                    description = IntentTip(i, state, enemy),
                })
                .ToArray();
        }
        catch { return []; }
    }

    private static string? IntentTip(AbstractIntent i, CombatState state, Creature enemy)
    {
        if (_compact) return null;
        try
        {
            if (!i.HasIntentTip) return null;
            var tip = i.GetHoverTip(state.Allies, enemy);
            return string.IsNullOrEmpty(tip.Description)
                ? null
                : RichText.NormalizeIcons(tip.Description);
        }
        catch { return null; }
    }

    // Every grid picker (deck removal / upgrade / transform / enchant,
    // combat pile selects) shares one base: cards behind an NCardGrid,
    // toggled via OnCardClicked, bounded by CardSelectorPrefs.
    // Headless: card_select and hand_select both read the deferred picker.
    // Key sets mirror the GUI snapshots (prompt is unknown here — the
    // engine doesn't pass it through ICardSelector; skip always cancels,
    // so cancelable is true).
    private static object PickerSnapshot(string phase, bool handSelect)
    {
        if (!HeadlessPicker.IsActive) return new { phase, available = false };
        var picked = HeadlessPicker.Picked;
        if (handSelect)
            return new
            {
                phase,
                prompt = "",
                min = HeadlessPicker.MinSelect,
                max = HeadlessPicker.MaxSelect,
                confirmable = picked.Count >= HeadlessPicker.MinSelect,
                selected = picked.Select(c => c.Id.Entry).ToArray(),
                cards = HeadlessPicker.Candidates
                    .Select((c, i) => HandCardView(c, i)).ToArray(),
            };
        return new
        {
            phase,
            player = FooterView(),
            prompt = "",
            min = HeadlessPicker.MinSelect,
            max = HeadlessPicker.MaxSelect,
            cancelable = true,
            confirmable = picked.Count >= HeadlessPicker.MinSelect,
            cards = HeadlessPicker.Candidates
                .Select((c, i) => SelectCardView(c, i, picked.Contains(c))).ToArray(),
        };
    }

    private static object CardSelectSnapshot(string phase)
    {
        if (RunMode.IsHeadless) return PickerSnapshot(phase, handSelect: false);

        // Choose-a-card overlays (Discovery, event card offers): a card
        // row, pick one, done — no confirm step.
        if (Screens.Top<NChooseACardSelectionScreen>() is { } choose)
        {
            var chooseHolders = Screens.CardHolders(choose);
            if (chooseHolders is null || chooseHolders.Count == 0)
                return new { phase, available = false };
            return new
            {
                phase,
                player = FooterView(),
                prompt = "",
                min = 1,
                max = 1,
                cancelable = Screens.ChooseSkipEnabled(choose),
                confirmable = false,
                cards = chooseHolders.Select((h, i) => SelectCardView(h.CardModel, i, false)).ToArray(),
            };
        }

        var screen = Screens.Top<NCardGridSelectionScreen>();
        var cards = screen is null ? null : Screens.GridCards(screen);
        if (screen is null || cards is null || cards.Count == 0)
            return new { phase, available = false };

        var prefs = Screens.Prefs(screen);
        var selected = Screens.SelectedCards(screen).ToHashSet();
        var selectedCount = selected.Count;
        var confirmable = screen switch
        {
            NSimpleCardSelectScreen or NCombatPileCardSelectScreen =>
                Reflect.Field<NClickableControl>(screen, "_confirmButton") is { IsEnabled: true },
            NDeckTransformSelectScreen => selectedCount >= prefs.MinSelect,
            NDeckCardSelectScreen => selectedCount >= prefs.MinSelect,
            _ => selectedCount >= prefs.MaxSelect,
        };
        return new
        {
            phase,
            player = FooterView(),
            prompt = SafeText(prefs.Prompt),
            min = prefs.MinSelect,
            max = prefs.MaxSelect,
            cancelable = prefs.Cancelable,
            confirmable,
            cards = cards.Select((c, i) => SelectCardView(c, i, selected.Contains(c))).ToArray(),
        };
    }

    // Hand select runs inside the combat room — the hand flips into a
    // selection mode instead of pushing an overlay. Picked cards leave
    // ActiveHolders (into the selected row), so idx tracks what's on screen.
    private static object HandSelectSnapshot(string phase)
    {
        if (RunMode.IsHeadless) return PickerSnapshot(phase, handSelect: true);

        var hand = NPlayerHand.Instance;
        if (hand is null) return new { phase, available = false };

        var prefs = Screens.Prefs(hand);
        var selected = Screens.SelectedCards(hand);
        var confirmable = Reflect.Field<NClickableControl>(
            hand, "_selectModeConfirmButton") is { IsEnabled: true };
        return new
        {
            phase,
            player = FooterView(),
            prompt = SafeText(prefs.Prompt),
            min = prefs.MinSelect,
            max = prefs.MaxSelect,
            confirmable,
            selected = selected.Select(c => c.Id.Entry).ToArray(),
            cards = hand.ActiveHolders.Select((h, i) =>
                HandCardView(h.CardNode?.Model, i))
                .ToArray(),
        };
    }
}
