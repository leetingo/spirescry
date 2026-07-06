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

namespace Spirescry.State;

public static class Snapshotter
{
    // Must be called on the main thread.
    public static object ForCurrentPhase()
    {
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
            : NOverlayStack.Instance?.Peek() is NChooseABundleSelectionScreen screen
                ? Reflect.Field<IReadOnlyList<IReadOnlyList<CardModel>>>(screen, "_bundles")
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
            : NOverlayStack.Instance?.Peek()
                    is MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen screen
                ? Reflect.FieldValue(screen, "_entity")
                    as MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame
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
            floor = rs.TotalFloor,
            act = rs.CurrentActIndex,
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
            relics = player.Relics.Select(r => r.Id.Entry).ToArray(),
            deck = (player.Deck?.Cards ?? Enumerable.Empty<CardModel>())
                .Where(c => c != null)
                .Select(c => new { model = c.Id.Entry, upgraded = c.IsUpgraded })
                .ToArray(),
        };
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
        var cards = sorted
            ? models.OrderBy(m => m, StringComparer.Ordinal).ToArray()
            : models.ToArray();
        return new { count = cards.Length, cards };
    }

    private static object ShopSnapshot(string phase)
    {
        var rs = RunManager.Instance?.DebugOnlyGetState();
        var inv = (rs?.CurrentRoom as MerchantRoom)?.GetLocalInventory();
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
        return new
        {
            phase,
            gold = player?.Gold ?? 0,
            player = FooterView(),
            cards = inv.CharacterCardEntries.Select(CardEntry).ToArray(),
            colorless = inv.ColorlessCardEntries.Select(CardEntry).ToArray(),
            relics = inv.RelicEntries.Select((e, i) => new
            {
                idx = i,
                model = e.Model.Id.Entry,
                title = SafeText(e.Model.Title),
                cost = e.Cost,
                stocked = e.IsStocked,
                affordable = e.EnoughGold,
            }).ToArray(),
            potions = inv.PotionEntries.Select((e, i) => new
            {
                idx = i,
                model = e.Model.Id.Entry,
                title = SafeText(e.Model.Title),
                cost = e.Cost,
                stocked = e.IsStocked,
                affordable = e.EnoughGold,
            }).ToArray(),
            cardRemoval = inv.CardRemovalEntry is { } cr
                ? new { cost = cr.Cost, used = cr.Used, affordable = cr.EnoughGold }
                : null,
        };
    }

    // In the GUI the visual node owns the options and the room model's
    // list stays empty; headless it's the other way around.
    private static object RestSiteSnapshot(string phase)
    {
        var options = NRestSiteRoom.Instance?.Options
            ?? (RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom as RestSiteRoom)?.Options;
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
    private static object TreasureSnapshot(string phase)
    {
        var room = NRun.Instance?.TreasureRoom;
        var opened = room is not null
            && Reflect.FieldValue(room, "_hasChestBeenOpened") is true;
        var sync = RunManager.Instance?.TreasureRoomRelicSynchronizer;

        // Headless: no chest button to click — open the chest through the
        // room model the first time the agent looks at it.
        if (RunMode.IsHeadless && sync is { CurrentRelics: null or { Count: 0 } }
            && RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom is TreasureRoom tr)
        {
            try
            {
                tr.DoNormalRewards().GetAwaiter().GetResult();
                tr.DoExtraRewardsIfNeeded().GetAwaiter().GetResult();
                opened = true;
            }
            catch (Exception ex) { SafeLog.Error("headless treasure open", ex); }
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
        var screen = NOverlayStack.Instance?.Peek() as NChooseARelicSelection;
        var row = screen is null ? null : Reflect.Field<Godot.Control>(screen, "_relicRow");
        var holders = row?.GetChildren().OfType<NRelicBasicHolder>().ToList();
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
                rewards = HeadlessRewards.Slotted().Select(s => (object)new
                {
                    idx = s.idx,
                    type = RewardType(s.reward),
                    amount = s.reward is GoldReward g ? g.Amount : (int?)null,
                    description = SafeText(s.reward.Description),
                }).ToList(),
            };

        var screen = NOverlayStack.Instance?.Peek() as NRewardsScreen;
        var buttons = screen is null
            ? null
            : Reflect.Field<System.Collections.IEnumerable>(screen, "_rewardButtons");
        if (buttons is null) return new { phase, available = false };

        var rewards = new List<object>();
        var idx = 0;
        foreach (var item in buttons)
        {
            var i = idx++;
            if (item is not NRewardButton btn || !btn.IsEnabled || btn.Reward is null)
                continue;
            rewards.Add(new
            {
                idx = i,
                type = RewardType(btn.Reward),
                amount = btn.Reward is GoldReward g ? g.Amount : (int?)null,
                description = SafeText(btn.Reward.Description),
            });
        }
        return new { phase, player = FooterView(), rewards };
    }

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

        var screen = NOverlayStack.Instance?.Peek() as NCardRewardSelectionScreen;
        var row = screen is null ? null : Reflect.Field<Godot.Control>(screen, "_cardRow");
        var holders = row?.GetChildren().OfType<NCardHolder>().ToList();
        // The screen exists a frame or two before its card row is wired —
        // an empty list means "poll again", not "zero cards".
        if (holders is null || holders.Count == 0)
            return new { phase, available = false };

        return new
        {
            phase,
            player = FooterView(),
            cards = holders.Select((h, i) => RewardCardView(h.CardModel, i)).ToArray(),
            // Non-card choices (skip, trade offers, …) — target of `skip`.
            alternatives = (Reflect.Field<System.Collections.IEnumerable>(screen!, "_extraOptions")
                    ?.Cast<object>() ?? [])
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
        try { return card.GetDescriptionForPile(PileType.None, null); }
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
        type = c.Type.ToString().ToLowerInvariant(),
        rarity = c.Rarity.ToString().ToLowerInvariant(),
        description = CardDescription(c),
    };

    private static object SelectCardView(CardModel c, int i, bool selected) => new
    {
        idx = i,
        model = c.Id.Entry,
        title = c.Title,
        upgraded = c.IsUpgraded,
        selected,
        description = CardDescription(c),
    };

    private static object MapSnapshot(string phase)
    {
        var rs = RunManager.Instance?.DebugOnlyGetState();
        if (rs is null) return new { phase, available = false };

        var next = NextPoints(rs)
            .Select(p => new
            {
                col = p.coord.col,
                row = p.coord.row,
                type = p.PointType.ToString().ToLowerInvariant(),
            })
            .ToArray();
        return new
        {
            phase,
            act = rs.CurrentActIndex,
            seed = rs.Rng?.StringSeed ?? "",
            player = FooterView(),
            current = rs.CurrentMapCoord is { } c ? new[] { c.col, c.row } : null,
            next = next ?? [],
            graph = rs.Map is { } map
                ? AllMapPoints(map).Select(p => new
                {
                    col = p.coord.col,
                    row = p.coord.row,
                    type = p.PointType.ToString().ToLowerInvariant(),
                }).ToArray()
                : [],
        };
    }

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
        if (rs.Map is { SecondBossMapPoint: { } second } map
            && rs.CurrentMapPoint is { } here && ReferenceEquals(here, map.BossMapPoint))
            yield return second;
    }

    private static object EventSnapshot(string phase)
    {
        var ev = (RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom as EventRoom)
            ?.LocalMutableEvent;
        if (ev is null) return new { phase, available = false };

        // Fills per-event vars into option text. Neow-style events format
        // through a separate dialogue table and can throw here — their
        // options still read fine with default vars.
        try { ev.CalculateVars(); } catch { }
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

    // Missing locale keys throw from GetFormattedText (Neow-style events
    // store their body elsewhere) — fall back to the raw entry key.
    private static string SafeText(LocString? s)
    {
        if (s is null) return "";
        try
        {
            if (s.IsEmpty) return "";
            return s.GetFormattedText();
        }
        catch
        {
            try { return s.GetRawText(); } catch { return ""; }
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
                .Select(c => new
                {
                    model = c.Id.Entry,
                    cost = c.EnergyCost.GetAmountToSpend(),
                    target = c.TargetType.ToString().ToLowerInvariant(),
                    upgraded = c.IsUpgraded,
                    unplayable = c.Keywords.Contains(CardKeyword.Unplayable),
                })
                .ToArray(),
            enemies = state.Enemies
                .Where(c => c != null)
                .Select(c => new
                {
                    id = c.CombatId ?? 0u,
                    model = c.Monster?.Id.Entry,
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
    private static object PickerSnapshot(string phase)
    {
        if (!HeadlessPicker.IsActive) return new { phase, available = false };
        var picked = HeadlessPicker.Picked;
        if (phase == Phase.HandSelect.AsString())
            return new
            {
                phase,
                prompt = "",
                min = HeadlessPicker.MinSelect,
                max = HeadlessPicker.MaxSelect,
                selected = picked.Select(c => c.Id.Entry).ToArray(),
                cards = HeadlessPicker.Candidates.Select((c, i) => new
                {
                    idx = i,
                    model = c.Id.Entry,
                    upgraded = c.IsUpgraded,
                }).ToArray(),
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
        if (RunMode.IsHeadless) return PickerSnapshot(phase);

        // Choose-a-card overlays (Discovery, event card offers): a card
        // row, pick one, done — no confirm step.
        if (NOverlayStack.Instance?.Peek() is NChooseACardSelectionScreen choose)
        {
            var chooseRow = Reflect.Field<Godot.Control>(choose, "_cardRow");
            var chooseHolders = chooseRow?.GetChildren().OfType<NCardHolder>().ToList();
            if (chooseHolders is null || chooseHolders.Count == 0)
                return new { phase, available = false };
            return new
            {
                phase,
                player = FooterView(),
                prompt = "",
                min = 1,
                max = 1,
                cancelable = Reflect.Field<MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl>(
                    choose, "_skipButton") is { IsEnabled: true },
                cards = chooseHolders.Select((h, i) => SelectCardView(h.CardModel, i, false)).ToArray(),
            };
        }

        var screen = NOverlayStack.Instance?.Peek() as NCardGridSelectionScreen;
        var grid = screen is null ? null : Reflect.Field<NCardGrid>(screen, "_grid");
        var cards = grid?.CurrentlyDisplayedCards.ToList();
        if (screen is null || cards is null || cards.Count == 0)
            return new { phase, available = false };

        var prefs = (CardSelectorPrefs)Reflect.FieldValue(screen, "_prefs")!;
        var selected = Reflect.Field<IEnumerable<CardModel>>(screen, "_selectedCards")
            ?.ToHashSet() ?? [];
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
        if (RunMode.IsHeadless) return PickerSnapshot(phase);

        var hand = NPlayerHand.Instance;
        if (hand is null) return new { phase, available = false };

        var prefs = (CardSelectorPrefs)Reflect.FieldValue(hand, "_prefs")!;
        var selected = Reflect.Field<IEnumerable<CardModel>>(hand, "_selectedCards") ?? [];
        return new
        {
            phase,
            player = FooterView(),
            prompt = SafeText(prefs.Prompt),
            min = prefs.MinSelect,
            max = prefs.MaxSelect,
            selected = selected.Select(c => c.Id.Entry).ToArray(),
            cards = hand.ActiveHolders.Select((h, i) => new
            {
                idx = i,
                model = h.CardNode?.Model.Id.Entry,
                upgraded = h.CardNode?.Model.IsUpgraded ?? false,
            }).ToArray(),
        };
    }
}
