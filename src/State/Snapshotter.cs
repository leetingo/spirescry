using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
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

    // Must be called on the main thread.
    public static object ForCurrentPhase() => ForCurrentPhase(false);

    public static object ForCurrentPhase(bool compact)
    {
        _compact = compact;
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
        var bundles = RunMode.IsHeadless
            ? HeadlessBundle.Bundles
            : Screens.Top<NChooseABundleSelectionScreen>() is { } screen
                ? Screens.Bundles(screen)
                : null;
        if (bundles is null || bundles.Count == 0) return new { phase, available = false };
        return new
        {
            phase,
            player = FooterView(),
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
            // Kept for compatibility: floor is the run-cumulative count,
            // act the ZERO-based index. The fields below say it plainly.
            floor = rs.TotalFloor,
            act = rs.CurrentActIndex,
            actNumber = rs.CurrentActIndex + 1,
            actFloor = rs.ActFloor,
            mapCoord = rs.CurrentMapCoord is { } mc ? new[] { mc.col, mc.row } : null,
            encounter = EncounterView(rs),
            ascension = rs.AscensionLevel,
            seed = rs.Rng?.StringSeed ?? "",
            hp = creature is null ? null : new[] { creature.CurrentHp, creature.MaxHp },
            gold = player?.Gold,
        };
    }

    // Where the run ended, when it ended in a fight: the encounter's id
    // and its localized name — "who killed you" without a lookup.
    private static object? EncounterView(RunState rs)
    {
        try
        {
            if (rs.CurrentRoom is not CombatRoom cr || cr.Encounter is not { } enc)
                return null;
            return new { model = enc.Id.Entry, title = SafeText(enc.Title) };
        }
        catch { return null; }
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
            relics = player.Relics.Select(r => r.Id.Entry).ToArray(),
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

        object CardEntry(MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardEntry e, int i) => new
        {
            idx = i,
            model = e.CreationResult?.Card?.Id.Entry,
            title = e.CreationResult?.Card?.Title,
            description = e.CreationResult?.Card is { } c ? CardDescription(c) : null,
            cost = e.Cost,
            stocked = e.IsStocked,
            affordable = e.EnoughGold,
        };
        // Model goes null once the entry is purchased — like CardEntry,
        // render the sold-out tile instead of throwing.
        object StockEntry(string? model, LocString? title, MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry e, int i) => new
        {
            idx = i,
            model,
            title = SafeText(title),
            cost = e.Cost,
            stocked = e.IsStocked,
            affordable = e.EnoughGold,
        };
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
                ? new { cost = cr.Cost, used = cr.Used, affordable = cr.EnoughGold }
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

    // relics[] stays empty until the chest is opened (pick-relic opens it).
    // One chest per room instance: DoNormalRewards grants the chest's
    // gold, so re-running it on later reads (the relic offer empties once
    // picking ends) would mint gold on every obs.
    private static TreasureRoom? _openedChest;

    private static object TreasureSnapshot(string phase)
    {
        var room = NRun.Instance?.TreasureRoom;
        var opened = room is not null && Screens.ChestOpened(room);
        var sync = RunManager.Instance?.TreasureRoomRelicSynchronizer;

        // Headless: no chest button to click — open the chest through the
        // room model the first time the agent looks at it.
        if (RunMode.IsHeadless
            && RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom is TreasureRoom tr)
        {
            if (!ReferenceEquals(_openedChest, tr)
                && sync is { CurrentRelics: null or { Count: 0 } })
            {
                try
                {
                    tr.DoNormalRewards().GetAwaiter().GetResult();
                    tr.DoExtraRewardsIfNeeded().GetAwaiter().GetResult();
                    _openedChest = tr;
                }
                catch (Exception ex) { SafeLog.Error("headless treasure open", ex); }
            }
            opened = opened || ReferenceEquals(_openedChest, tr);
        }

        var relics = sync?.CurrentRelics;
        return new
        {
            phase,
            // Headless has no chest-flag node; offered relics == open chest.
            chestOpened = opened || relics is { Count: > 0 },
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

    // Shared per-card views — both boots build their snapshots through
    // these, so the shapes can't drift between modes (tests/parity.py
    // compares the key sets).
    private static object RewardCardView(CardModel c, int i) => new
    {
        idx = i,
        model = c.Id.Entry,
        title = c.Title,
        cost = c.EnergyCost.Canonical,
        starCost = StarCost(c),
        type = c.Type.ToString().ToLowerInvariant(),
        rarity = c.Rarity.ToString().ToLowerInvariant(),
        description = CardDescription(c),
    };

    private static object SelectCardView(CardModel c, int i, bool selected) => new
    {
        idx = i,
        model = c.Id.Entry,
        title = c.Title,
        cost = c.EnergyCost.Canonical,
        starCost = StarCost(c),
        upgraded = c.IsUpgraded,
        selected,
        description = CardDescription(c),
        upgradedPreview = UpgradePreview(c),
    };

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
    private static string? UpgradePreview(CardModel c)
    {
        if (!c.IsUpgradable) return null;
        try
        {
            var twin = ModelDb.GetById<CardModel>(c.Id).ToMutable();
            for (var lvl = 0; lvl <= c.CurrentUpgradeLevel; lvl++)
                twin.UpgradeInternal();
            return CardDescription(twin);
        }
        catch { return null; }
    }

    private static object HandCardView(string? model, bool upgraded, int i) => new
    {
        idx = i,
        model,
        upgraded,
    };

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

        // Fills per-event vars into option text. Neow-style events format
        // through a separate dialogue table and can throw here — their
        // options still read fine with default vars.
        try { ev.CalculateVars(); } catch { }
        // The GUI's event nodes copy the event's DynamicVars into each
        // text's LocString before rendering; headless has no nodes, so do
        // it here or var-bearing descriptions ({SoloGold}, …) fail to
        // format and degrade to raw text.
        try
        {
            if (ev.Description is { } pageDesc) ev.DynamicVars.AddTo(pageDesc);
            foreach (var o in ev.CurrentOptions ?? [])
            {
                if (o?.Title is { } t) ev.DynamicVars.AddTo(t);
                if (o?.Description is { } d) ev.DynamicVars.AddTo(d);
            }
        }
        catch { }
        return new
        {
            phase,
            id = ev.Id.Entry,
            player = FooterView(),
            title = SafeText(ev.Title),
            description = SafeText(ev.Description),
            finished = ev.IsFinished,
            options = (ev.CurrentOptions ?? []).Select((o, i) => new
            {
                idx = i,
                title = SafeText(o.Title),
                description = SafeText(o.Description),
                locked = o.IsLocked,
                chosen = o.WasChosen,
                proceed = o.IsProceed,
            }).ToArray(),
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
    private static string SafeText(LocString? s)
    {
        if (s is null) return "";
        try
        {
            if (s.IsEmpty) return "";
            var text = s.GetFormattedText();
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

        return new
        {
            phase,
            turn = state.RoundNumber,
            side = state.CurrentSide.ToString().ToLowerInvariant(),
            you = new
            {
                hp = new[] { creature.CurrentHp, creature.MaxHp },
                block = creature.Block,
                energy = new[] { pcs.Energy, pcs.MaxEnergy },
                stars = pcs.Stars,
                powers = PowerViews(creature),
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
                    return (object)new
                    {
                        model = c.Id.Entry,
                        cost = c.EnergyCost.GetAmountToSpend(),
                        starCost = StarCost(c),
                        target = c.TargetType.ToString().ToLowerInvariant(),
                        upgraded = c.IsUpgraded,
                        unplayable = c.Keywords.Contains(CardKeyword.Unplayable),
                        description = _compact ? null : CardText(c, PileType.Hand),
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
                    powers = PowerViews(c),
                    intents = IntentViews(state, c),
                })
                .ToArray(),
        };
    }

    // Damage rides the intent's own calculator — the number the intent pip
    // shows, strength/weak already applied. NextMove can be mid-roll during
    // turn transitions; an empty list means "poll again".
    private static object[] IntentViews(CombatState state, Creature enemy)
    {
        try
        {
            return (enemy.Monster?.NextMove.Intents ?? [])
                .Select(i => (object)new
                {
                    type = i.IntentType.ToString().ToLowerInvariant(),
                    damage = i is AttackIntent a ? a.GetSingleDamage(state.Allies, enemy) : (int?)null,
                    hits = i is AttackIntent b ? b.Repeats : (int?)null,
                })
                .ToArray();
        }
        catch { return []; }
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
                selected = picked.Select(c => c.Id.Entry).ToArray(),
                cards = HeadlessPicker.Candidates
                    .Select((c, i) => HandCardView(c.Id.Entry, c.IsUpgraded, i)).ToArray(),
            };
        return new
        {
            phase,
            player = FooterView(),
            prompt = "",
            min = HeadlessPicker.MinSelect,
            max = HeadlessPicker.MaxSelect,
            cancelable = true,
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
                cards = chooseHolders.Select((h, i) => SelectCardView(h.CardModel, i, false)).ToArray(),
            };
        }

        var screen = Screens.Top<NCardGridSelectionScreen>();
        var cards = screen is null ? null : Screens.GridCards(screen);
        if (screen is null || cards is null || cards.Count == 0)
            return new { phase, available = false };

        var prefs = Screens.Prefs(screen);
        var selected = Screens.SelectedCards(screen).ToHashSet();
        return new
        {
            phase,
            player = FooterView(),
            prompt = SafeText(prefs.Prompt),
            min = prefs.MinSelect,
            max = prefs.MaxSelect,
            cancelable = prefs.Cancelable,
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
        return new
        {
            phase,
            player = FooterView(),
            prompt = SafeText(prefs.Prompt),
            min = prefs.MinSelect,
            max = prefs.MaxSelect,
            selected = selected.Select(c => c.Id.Entry).ToArray(),
            cards = hand.ActiveHolders.Select((h, i) =>
                HandCardView(h.CardNode?.Model.Id.Entry, h.CardNode?.Model.IsUpgraded ?? false, i))
                .ToArray(),
        };
    }
}
