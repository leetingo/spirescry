using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
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
using System.Text.Json;
using CrystalItem = MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItem;
using CrystalMinigame = MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame;

namespace Spirescry.State;

internal static class Snapshotter
{
    // Compact mode elides the big repeats (map graph, per-card deck) for
    // agents that poll often. Set per snapshot; safe as a static because
    // the pump runs one job at a time on the main thread.
    private static bool _compact;
    private static bool _decision;
    private static HashSet<string> _knownCardTexts = new(StringComparer.Ordinal);
    private static HashSet<string> _emittedCardTexts = new(StringComparer.Ordinal);

    // Must be called on the main thread.
    internal static SnapshotContract ForCurrentPhase() => ForCurrentPhase(false);

    internal static SnapshotContract ForCurrentPhase(bool compact) =>
        ForCurrentPhase(compact, false, []);

    internal static SnapshotContract ForCurrentPhase(
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
            Phase.Combat => CombatSnapshot(phase),
            Phase.Map => MapSnapshot(phase),
            Phase.Event => EventSnapshot(phase),
            Phase.Rewards => RewardsSnapshot(phase),
            Phase.CardReward => CardRewardSnapshot(phase),
            Phase.Shop => ShopSnapshot(phase),
            Phase.RestSite => RestSiteSnapshot(phase),
            Phase.Treasure => TreasureSnapshot(phase),
            Phase.RelicReward => RelicRewardSnapshot(phase),
            Phase.CardSelect => CardSelectSnapshot(phase),
            Phase.HandSelect => HandSelectSnapshot(phase),
            Phase.BundleSelect => BundleSelectSnapshot(phase),
            Phase.CrystalSphere => CrystalSphereSnapshot(phase),
            Phase.GameOver => GameOverSnapshot(phase),
            // Name the capturing overlay so a stuck screen is diagnosable
            // from /obs alone.
            Phase.Overlay or Phase.Unknown => Snapshot(
                phase,
                new { overlay = NOverlayStack.Instance?.Peek()?.GetType().Name }),
            _ => new SnapshotContract(phase),
        };
    }

    private static SnapshotContract Snapshot(Phase phase, object extensions)
    {
        var snapshot = new SnapshotContract(phase);
        snapshot.AddExtensions(extensions);
        return snapshot;
    }

    private static SnapshotItemContract Item(
        object extensions,
        int? index = null,
        string? id = null,
        string? model = null,
        string? selector = null,
        int? slot = null,
        string? target = null,
        int? col = null,
        int? row = null,
        string? type = null,
        string[]? semanticState = null,
        bool? selected = null)
    {
        var item = new SnapshotItemContract
        {
            Index = index,
            Id = id,
            Model = model,
            Selector = selector,
            Slot = slot,
            Target = target,
            Col = col,
            Row = row,
            Type = type,
            SemanticState = semanticState,
            Selected = selected,
        };
        item.AddExtensions(extensions);
        return item;
    }

    // Rich/localized wire fields remain available to agents. These compact,
    // locale-free tokens are their typed semantic mirror for settlement and
    // replay; callers sort collections whose domain order is irrelevant.
    private static string SemanticToken(string kind, params object?[] values) =>
        $"{kind}:{JsonSerializer.Serialize(values)}";

    private static string CardStateToken(CardModel card, bool liveCost = false)
    {
        var variables = CardSpecifier.SemanticDynamicVars(card)?
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new object[] { pair.Key, pair.Value })
            .ToArray() ?? [];
        return SemanticToken(
            "card",
            CardSpecifier.From(card),
            liveCost ? card.EnergyCost.GetAmountToSpend() : card.EnergyCost.Canonical,
            CardSpecifier.SemanticStarCost(card),
            card.Keywords.Contains(CardKeyword.Unplayable),
            variables);
    }

    private static string RelicStateToken(RelicModel relic) =>
        SemanticToken("relic", relic.Id.Entry, RelicCounter(relic), relic.IsUsedUp);

    private static string[] EventDynamicVarState(DynamicVarSet variables) =>
        CollectionSnapshot.ReadStable(
            "event dynamic vars semantic state",
            () => variables
                .Select(pair => SemanticToken(
                    "eventVar",
                    pair.Key,
                    pair.Value.GetType().FullName,
                    pair.Value.BaseValue,
                    pair.Value.EnchantedValue,
                    pair.Value.PreviewValue))
                .OrderBy(token => token, StringComparer.Ordinal)
                .ToArray());

    private static string[] PowerState(Creature creature)
        => CollectionSnapshot.ReadStable(
            "power semantic state",
            () => creature.Powers
                .Select(power => SemanticToken("power", power.Id.Entry, power.Amount))
                .OrderBy(token => token, StringComparer.Ordinal)
                .ToArray());

    // Bundle offers: pick one pack of cards (Neow's Scroll Boxes, …).
    private static SnapshotContract BundleSelectSnapshot(Phase phase)
    {
        var decision = DecisionSurface.Current.Bundle;
        var bundles = decision?.Bundles;
        if (bundles is null || bundles.Count == 0)
            return new SnapshotContract(phase) { Available = false };
        return new SnapshotContract(phase)
        {
            Player = FooterView(),
            Confirmable = decision!.Confirmable,
            Cancelable = decision.Cancelable,
            Bundles = bundles.Select((bundle, bundleIndex) =>
            {
                var item = new SnapshotItemContract
                {
                    Index = bundleIndex,
                    Cards = bundle.Select((card, cardIndex) =>
                        Item(RewardCardView(card), cardIndex,
                            model: card.Id.Entry,
                            selector: CardSpecifier.From(card),
                            type: card.Type.ToString().ToLowerInvariant(),
                            semanticState: [CardStateToken(card)])).ToArray(),
                };
                item.SemanticState = item.Cards
                    .SelectMany(card => card.SemanticState ?? [])
                    .ToArray();
                return item;
            }).ToArray(),
        };
    }

    // The crystal-sphere event minigame: a cell grid, a divination tool,
    // and a fixed number of reveals. map-move clicks a cell, option picks
    // the tool, proceed leaves.
    private static SnapshotContract CrystalSphereSnapshot(Phase phase)
    {
        var entity = DecisionSurface.Current.Crystal;
        if (entity is null) return new SnapshotContract(phase) { Available = false };

        var grid = entity.GridSize;
        var cells = new List<SnapshotItemContract>();
        var hiddenCells = 0;
        for (var y = 0; y < grid.Y; y++)
            for (var x = 0; x < grid.X; x++)
            {
                if (entity.cells[x, y] is not { } cell) continue;
                if (cell.IsHidden)
                {
                    hiddenCells++;
                    if (_compact) continue;
                }
                object? item = null;
                if (!cell.IsHidden && cell.Item is { } revealed)
                {
                    item = new
                    {
                        type = CrystalItemType(revealed),
                        good = revealed.IsGood,
                        footprint = new
                        {
                            col = revealed.Position.X,
                            row = revealed.Position.Y,
                            width = revealed.Size.X,
                            height = revealed.Size.Y,
                        },
                    };
                }
                var cellView = Item(new
                {
                    hasItem = cell.Item is not null && !cell.IsHidden,
                    item,
                }, col: cell.X, row: cell.Y,
                    semanticState:
                    [
                        SemanticToken(
                            "cell",
                            cell.IsHidden,
                            cell.Item is not null && !cell.IsHidden,
                            !cell.IsHidden && cell.Item is { } stateItem
                                ? CrystalItemType(stateItem)
                                : null,
                            !cell.IsHidden && cell.Item is { } positioned
                                ? positioned.Position.X
                                : null,
                            !cell.IsHidden && cell.Item is { } positionedY
                                ? positionedY.Position.Y
                                : null,
                            !cell.IsHidden && cell.Item is { } sized
                                ? sized.Size.X
                                : null,
                            !cell.IsHidden && cell.Item is { } sizedY
                                ? sizedY.Size.Y
                                : null)
                    ]);
                cellView.Hidden = cell.IsHidden;
                cells.Add(cellView);
            }
        var snapshot = new SnapshotContract(phase)
        {
            Player = FooterView(),
            Cells = cells.ToArray(),
            SemanticState =
            [
                SemanticToken(
                    "crystal",
                    grid.X,
                    grid.Y,
                    entity.DivinationCount,
                    entity.CrystalSphereTool.ToString().ToLowerInvariant(),
                    entity.IsFinished,
                    hiddenCells)
            ],
        };
        snapshot.AddExtensions(new
        {
            grid = new { width = grid.X, height = grid.Y },
            divinationsLeft = entity.DivinationCount,
            tool = entity.CrystalSphereTool.ToString().ToLowerInvariant(),
            finished = entity.IsFinished,
            hiddenCells,
        });
        return snapshot;
    }

    private static string CrystalItemType(CrystalItem item) => item switch
    {
        CrystalSphereCardReward => "card_reward",
        CrystalSphereCurse => "curse",
        CrystalSphereGold => "gold",
        CrystalSpherePotion => "potion",
        CrystalSphereRelic => "relic",
        _ => "unknown",
    };

    private static SnapshotContract GameOverSnapshot(Phase phase)
    {
        if (LocalRunContext.StateOnly is not { } run)
            return new SnapshotContract(phase) { Available = false };
        var rm = run.Manager;
        var rs = run.State;
        var player = LocalRunContext.LocalPlayer(rs);
        var creature = player?.Creature;
        var snapshot = new SnapshotContract(phase)
        {
            Act = rs.CurrentActIndex,
            Outcome = rm.IsAbandoned
                ? "abandoned"
                : rm.WinTime > 0 ? "victory" : "defeat",
            Hp = creature is null ? null : [creature.CurrentHp, creature.MaxHp],
            Gold = player?.Gold,
            SemanticState =
            [
                SemanticToken(
                    "gameOver",
                    rs.TotalFloor,
                    rs.ActFloor,
                    rs.CurrentMapCoord?.col,
                    rs.CurrentMapCoord?.row,
                    rs.CurrentRoom is CombatRoom semanticRoom
                        ? semanticRoom.Encounter.Id.Entry
                        : null,
                    rs.AscensionLevel,
                    rs.Rng?.StringSeed ?? "")
            ],
        };
        snapshot.AddExtensions(new
        {
            // Compat pair: floor is the run-cumulative TotalFloor, act the
            // zero-based CurrentActIndex. actNumber/actFloor/mapCoord say
            // the same position in the 1-based, act-local terms a run
            // report wants.
            floor = rs.TotalFloor,
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
        });
        return snapshot;
    }

    // Out-of-combat decisions need run context: HEAL vs SMITH reads hp,
    // card picks read the deck, events price their options in gold.
    private static SnapshotPlayerContract? FooterView()
    {
        var player = LocalRunContext.Current?.Player;
        if (player is null) return null;
        var creature = player.Creature;
        var footer = new SnapshotPlayerContract
        {
            Hp = creature is null ? null : [creature.CurrentHp, creature.MaxHp],
            Gold = player.Gold,
            SemanticState = PlayerSemanticState(player),
        };
        footer.Potions = PotionViews(player);
        footer.AddExtensions(new
        {
            // Keep the original ID list stable for existing agents; rich,
            // mutable state is additive so a schema upgrade is not required.
            relics = player.Relics.Select(r => r.Id.Entry).ToArray(),
            relicStates = player.Relics.Select(RelicStateView).ToArray(),
            deck = DeckView(player),
        });
        return footer;
    }

    private static string[] PlayerSemanticState(Player player)
    {
        var deck = (player.Deck?.Cards ?? Enumerable.Empty<CardModel>())
            .Where(card => card is not null)
            .Select(card => SemanticToken("deck", CardStateToken(card)))
            .OrderBy(token => token, StringComparer.Ordinal);
        var relics = player.Relics
            .Select(relic => SemanticToken("owned", RelicStateToken(relic)))
            .OrderBy(token => token, StringComparer.Ordinal);
        return deck.Concat(relics).ToArray();
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
            .GroupBy(CardSpecifier.From)
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
    private static SnapshotItemContract[] PotionViews(Player player)
    {
        var slots = player.PotionSlots;
        var result = new List<SnapshotItemContract>();
        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i] is not { } p || p.IsQueued || p.HasBeenRemovedFromState) continue;
            result.Add(Item(new
            {
                description = SafeText(p.DynamicDescription),
            }, model: p.Id.Entry, slot: i,
                target: p.TargetType.ToString().ToLowerInvariant()));
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

    private static string[] OrbState(PlayerCombatState pcs)
    {
        var queue = pcs.OrbQueue
            ?? throw new InvalidOperationException("player orb queue is unavailable");
        return
        [
            SemanticToken("orbSlots", queue.Capacity),
            .. queue.Orbs
                .Where(orb => orb is not null)
                .Select((orb, index) => SemanticToken(
                    "orb", index, orb.Id.Entry, orb.PassiveVal, orb.EvokeVal)),
        ];
    }

    private static object[] PowerViews(Creature c)
    {
        try
        {
            return c.Powers.Select(p => (object)new
            {
                id = p.Id.Entry,
                amount = p.Amount,
                description = PowerDescription(p),
            }).ToArray();
        }
        catch { return []; }
    }

    // Power descriptions have the same split as cards: Description is the
    // static catalog text, while SmartDescription carries live values such
    // as Amount. The GUI fills both the power-specific dynamic variables and
    // the common amount/icon variables before rendering.
    private static string PowerDescription(PowerModel power)
    {
        try
        {
            var rendered = SafeText(power.SmartDescription, description =>
            {
                description.Add("Amount", power.Amount);
                AddPowerContext(power, description);
                power.DynamicVars.AddTo(description);
            });
            return string.IsNullOrEmpty(rendered)
                ? SafeText(power.Description)
                : rendered;
        }
        catch { return SafeText(power.Description); }
    }

    // Mirrors the context variables PowerModel.HoverTips supplies to smart
    // descriptions. Amount and model-specific DynamicVars are handled by
    // the caller; these names describe the creatures around the power.
    private static void AddPowerContext(PowerModel power, LocString description)
    {
        var owner = power.Owner;
        description.Add("OnPlayer", owner.IsPlayer);
        var playerCount = owner.CombatState?.Players.Count ?? 1;
        description.Add("IsMultiplayer", playerCount > 1);
        description.Add("PlayerCount", playerCount);
        var ownerTitle = owner.Player?.Character?.Title ?? owner.Monster?.Title;
        if (ownerTitle is not null)
            description.Add("OwnerName", ownerTitle);
        else
            description.Add("OwnerName", owner.Name);
        if (power.Applier is { } applier)
            description.Add("ApplierName", applier.Name);
        if (power.Target is { } target)
            description.Add("TargetName", target.Name);
    }

    // Draw contents are sorted so the snapshot can't leak draw order.
    private static object PileView(CardPile? pile, bool sorted = false)
    {
        var models = (pile?.Cards ?? Enumerable.Empty<CardModel>())
            .Where(c => c != null)
            .Select(CardSpecifier.From);
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

    private static string[] PileState(string name, CardPile? pile, bool sorted = false)
    {
        var cards = CollectionSnapshot.Once(
            (pile?.Cards ?? Enumerable.Empty<CardModel>())
                .Where(card => card is not null)
                .Select(card => CardStateToken(card)));
        if (sorted) Array.Sort(cards, StringComparer.Ordinal);
        return cards.Select((card, index) => SemanticToken("pile", name, index, card))
            .Prepend(SemanticToken("pileCount", name, cards.Length))
            .ToArray();
    }

    private static SnapshotContract ShopSnapshot(Phase phase)
    {
        if (LocalRunContext.Current is not { } run)
            return new SnapshotContract(phase) { Available = false };
        var rs = run.State;
        var inv = Screens.ShopInventory(rs);
        if (inv is null)
            return new SnapshotContract(phase) { Available = false };
        var player = run.Player;

        // `cost` stays the gold amount for wire compatibility; `price`
        // names it explicitly. Cards add playCost/starCost so their combat
        // economy is visible before purchase without changing old clients.
        SnapshotItemContract CardEntry(
            MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardEntry e, int i)
        {
            var card = e.CreationResult?.Card;
            var showText = card is not null && ShouldShowCardText(card);
            var entry = Item(new
            {
                title = card?.Title,
                textKey = card is null ? null : CardSpecifier.TextKey(card),
                description = showText ? CardSpecifier.Description(card!) : null,
                cost = e.Cost,
                price = e.Cost,
                playCost = card?.EnergyCost.Canonical,
                starCost = card is null ? null : CardSpecifier.StarCost(card),
                stocked = e.IsStocked,
                affordable = e.EnoughGold,
            }, i, model: card?.Id.Entry,
                selector: card is null ? null : CardSpecifier.From(card),
                semanticState:
                [
                    SemanticToken(
                        "shopCard",
                        e.Cost,
                        e.IsStocked,
                        e.EnoughGold,
                        card is null ? null : CardStateToken(card))
                ]);
            entry.Purchasable = e.IsStocked && e.EnoughGold;
            return entry;
        }
        // Model goes null once the entry is purchased — like CardEntry,
        // render the sold-out tile instead of throwing.
        SnapshotItemContract StockEntry(
            string? model, LocString? title,
            MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry e, int i)
        {
            var potionHasRoom = e is not MegaCrit.Sts2.Core.Entities.Merchant.MerchantPotionEntry
                || player?.HasOpenPotionSlots == true;
            var entry = Item(new
            {
                title = SafeText(title),
                cost = e.Cost,
                price = e.Cost,
                stocked = e.IsStocked,
                affordable = e.EnoughGold,
            }, i, model: model,
                semanticState:
                [
                    SemanticToken(
                        "shopStock",
                        e.Cost,
                        e.IsStocked,
                        e.EnoughGold,
                        potionHasRoom)
                ]);
            entry.Purchasable = e.IsStocked && e.EnoughGold && potionHasRoom;
            return entry;
        }
        var removal = inv.CardRemovalEntry is { } cr
            ? Item(new
            {
                cost = cr.Cost,
                price = cr.Cost,
                used = cr.Used,
                affordable = cr.EnoughGold,
            }, semanticState:
            [
                SemanticToken(
                    "cardRemoval",
                    cr.Cost,
                    cr.Used,
                    cr.EnoughGold)
            ])
            : null;
        if (removal is not null)
            removal.Purchasable = !inv.CardRemovalEntry!.Used
                && inv.CardRemovalEntry.EnoughGold;
        var snapshot = new SnapshotContract(phase)
        {
            Player = FooterView(),
            Cards = inv.CharacterCardEntries.Select(CardEntry).ToArray(),
            Colorless = inv.ColorlessCardEntries.Select(CardEntry).ToArray(),
            Relics = inv.RelicEntries
                .Select((e, i) => StockEntry(e.Model?.Id.Entry, e.Model?.Title, e, i)).ToArray(),
            Potions = inv.PotionEntries
                .Select((e, i) => StockEntry(e.Model?.Id.Entry, e.Model?.Title, e, i)).ToArray(),
            CardRemoval = removal,
            Gold = player?.Gold ?? 0,
        };
        return snapshot;
    }

    private static SnapshotContract RestSiteSnapshot(Phase phase)
    {
        var decision = DecisionSurface.Current.RestSite;
        if (decision is null) return new SnapshotContract(phase) { Available = false };
        return new SnapshotContract(phase)
        {
            Player = FooterView(),
            ProceedAvailable = decision.ProceedAvailable,
            Options = decision.Options.Select((o, i) =>
            {
                var option = Item(new
                {
                    title = SafeText(o.Title),
                    description = SafeText(o.Description),
                }, i, id: o.OptionId.ToString());
                option.Enabled = o.IsEnabled;
                return option;
            }).ToArray(),
        };
    }

    // Observation never opens the chest. In headless mode the first
    // pick-relic/skip runs the room rewards once; the synchronizer then
    // makes the offer visible here just as the GUI's chest-open callback
    // does. chestOpened=false therefore means "unopened", never "empty":
    // an empty relics array with an open chest is a resolved offer.
    private static SnapshotContract TreasureSnapshot(Phase phase)
    {
        var decision = DecisionSurface.Current.Treasure;
        return new SnapshotContract(phase)
        {
            ChestOpened = decision.ChestOpened,
            // Headless can always leave the room (proceed declines a
            // pending offer first); the GUI gates leaving on resolving
            // the chest.
            ProceedAvailable = decision.ProceedAvailable,
            Player = FooterView(),
            Relics = decision.Relics
                .Select((relic, i) => Item(
                    RelicView(relic), i, model: relic.Id.Entry)).ToArray(),
        };
    }

    private static SnapshotContract RelicRewardSnapshot(Phase phase)
    {
        var screen = Screens.Top<NChooseARelicSelection>();
        var holders = screen is null ? null : Screens.RelicHolders(screen);
        if (holders is null || holders.Count == 0)
            return new SnapshotContract(phase) { Available = false };
        return new SnapshotContract(phase)
        {
            Player = FooterView(),
            Relics = holders.Select((h, i) => Item(
                RelicView(h.Relic.Model), i,
                model: h.Relic.Model.Id.Entry)).ToArray(),
        };
    }

    private static object RelicView(RelicModel r) => new
    {
        title = SafeText(r.Title),
        rarity = r.Rarity.ToString().ToLowerInvariant(),
        description = SafeText(r.DynamicDescription),
    };

    // Tile idx is the position in the screen's full button list — claimed
    // tiles linger disabled during their hide tween, so sibling indices
    // stay stable; we just omit them from the snapshot.
    private static SnapshotContract RewardsSnapshot(Phase phase)
    {
        var decision = DecisionSurface.Current.Rewards;
        if (decision is null) return new SnapshotContract(phase) { Available = false };
        return new SnapshotContract(phase)
        {
            Player = FooterView(),
            Rewards = decision.Rewards
                .Select(slot => Item(
                    RewardView(slot.Reward), slot.Index,
                    type: RewardType(slot.Reward),
                    semanticState:
                    [
                        SemanticToken(
                            "reward",
                            slot.Reward.GetType().FullName,
                            slot.Reward is GoldReward gold ? gold.Amount : null)
                    ])).ToArray(),
        };
    }

    // Shared reward-tile view — see the per-card views below for why both
    // boots build their snapshots through one shape.
    private static object RewardView(Reward r) => new
    {
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

    private static SnapshotContract CardRewardSnapshot(Phase phase)
    {
        var decision = DecisionSurface.Current.CardReward;
        if (decision is null) return new SnapshotContract(phase) { Available = false };
        return new SnapshotContract(phase)
        {
            Player = FooterView(),
            Cards = decision.Cards
                .Select((card, i) => Item(
                    RewardCardView(card), i,
                    model: card.Id.Entry,
                    selector: CardSpecifier.From(card),
                    type: card.Type.ToString().ToLowerInvariant(),
                    semanticState: [CardStateToken(card)])).ToArray(),
            // Non-card choices (skip, trade offers, …) — target of `skip`.
            Alternatives = decision.AlternativeTitles
                .Select((title, i) => Item(new
                {
                    title = SafeText(title),
                }, i,
                    semanticState:
                    [
                        SemanticToken(
                            "alternative",
                            title?.LocTable,
                            title?.LocEntryKey)
                    ])).ToArray(),
        };
    }

    private static bool ShouldShowCardText(CardModel card, bool compactElides = false)
    {
        if (!_decision) return !compactElides || !_compact;
        var key = CardSpecifier.TextKey(card);
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
    private static object RewardCardView(CardModel c)
    {
        var showText = ShouldShowCardText(c);
        return new
        {
            title = c.Title,
            cost = c.EnergyCost.Canonical,
            starCost = CardSpecifier.StarCost(c),
            rarity = c.Rarity.ToString().ToLowerInvariant(),
            textKey = CardSpecifier.TextKey(c),
            description = showText ? CardSpecifier.Description(c) : null,
        };
    }

    private static object SelectCardView(CardModel c)
    {
        var showText = ShouldShowCardText(c);
        var preview = CardSpecifier.UpgradePreview(c);
        return new
        {
            title = c.Title,
            cost = c.EnergyCost.Canonical,
            starCost = CardSpecifier.StarCost(c),
            upgraded = c.IsUpgraded,
            enchant = c.Enchantment?.Id.Entry,
            affliction = c.Affliction?.Id.Entry,
            textKey = CardSpecifier.TextKey(c),
            description = showText ? CardSpecifier.Description(c) : null,
            // Preserve upgradedPreview's string/null wire type; numeric
            // upgrade economics do not need to be hidden with cached prose.
            upgradedPreview = showText ? preview?.Description : null,
            upgradedPlayCost = preview?.PlayCost,
            upgradedStarCost = preview?.StarCost,
        };
    }

    private static object HandCardView(CardModel? card)
    {
        var showText = card is not null
            && ShouldShowCardText(card, compactElides: true);
        return new
        {
            cost = card?.EnergyCost.GetAmountToSpend(),
            starCost = card is null ? null : CardSpecifier.StarCost(card),
            upgraded = card?.IsUpgraded ?? false,
            enchant = card?.Enchantment?.Id.Entry,
            affliction = card?.Affliction?.Id.Entry,
            textKey = card is null ? null : CardSpecifier.TextKey(card),
            description = showText ? CardSpecifier.Text(card!, PileType.Hand) : null,
            vars = card is null ? null : CardSpecifier.DynamicVars(card),
        };
    }

    private static SnapshotContract MapSnapshot(Phase phase)
    {
        if (LocalRunContext.Current is not { } run)
            return new SnapshotContract(phase) { Available = false };
        var rs = run.State;

        var points = rs.Map is { } map ? AllMapPoints(map) : [];
        var snapshot = new SnapshotContract(phase)
        {
            Player = FooterView(),
            Act = rs.CurrentActIndex,
            Current = rs.CurrentMapCoord is { } current
                ? [current.col, current.row]
                : null,
            SemanticState = points
                .Select(MapPointState)
                .OrderBy(token => token, StringComparer.Ordinal)
                .ToArray(),
        };
        snapshot.Next = NextPoints(rs)
            .Select(MapPointItem).ToArray();
        snapshot.AddExtensions(new
        {
            seed = rs.Rng?.StringSeed ?? "",
            // The act graph is the biggest repeat in the protocol — compact
            // callers keep `next` and re-request the full view when routing.
            graph = _compact ? null : points.Select(MapPointView).ToArray(),
        });

        // Quest markers can sit anywhere in the next act, not necessarily
        // on a currently reachable node. Keep compact graph elision, but
        // retain just the marked node(s) when an effect such as Spoils Map
        // makes them decision-relevant. Omit the field entirely otherwise.
        var marked = points.Where(p => MapMarkers(p).Length > 0)
            .Select(MapPointView)
            .ToArray();
        if (marked.Length > 0) snapshot.AddExtensions(new { marked });
        return snapshot;
    }

    // Shared by obs.next and obs.graph so the two views can't drift.
    private static object MapPointView(MegaCrit.Sts2.Core.Map.MapPoint p)
    {
        var view = new Dictionary<string, object?>
        {
            ["col"] = p.coord.col,
            ["row"] = p.coord.row,
            ["type"] = p.PointType.ToString().ToLowerInvariant(),
            // Outgoing edges — route planning needs the graph's links, not
            // just its nodes.
            ["next"] = p.Children
                .Where(ch => ch != null)
                .Select(ch => new[] { ch.coord.col, ch.coord.row })
                .ToArray(),
        };
        var markers = MapMarkers(p);
        if (markers.Length > 0) view["markers"] = markers;
        return view;
    }

    private static SnapshotItemContract MapPointItem(
        MegaCrit.Sts2.Core.Map.MapPoint point)
    {
        var extension = new Dictionary<string, object?>
        {
            ["next"] = point.Children
                .Where(child => child != null)
                .Select(child => new[] { child.coord.col, child.coord.row })
                .ToArray(),
        };
        var markers = MapMarkers(point);
        if (markers.Length > 0) extension["markers"] = markers;
        return Item(
            extension,
            col: point.coord.col,
            row: point.coord.row,
            type: point.PointType.ToString().ToLowerInvariant(),
            semanticState: [MapPointState(point)]);
    }

    private static string MapPointState(MegaCrit.Sts2.Core.Map.MapPoint point) =>
        SemanticToken(
            "mapPoint",
            point.coord.col,
            point.coord.row,
            point.PointType.ToString().ToLowerInvariant(),
            point.Children
                .Where(child => child is not null)
                .Select(child => new[] { child.coord.col, child.coord.row })
                .OrderBy(coord => coord[1])
                .ThenBy(coord => coord[0])
                .ToArray(),
            MapMarkers(point));

    private static string[] MapMarkers(MegaCrit.Sts2.Core.Map.MapPoint point)
    {
        try
        {
            return point.Quests.Select(q => q.Id.Entry)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex)
        {
            SafeLog.Error($"map markers at {point.coord.col},{point.coord.row}", ex);
            return [];
        }
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
        if (SecondBossPending(rs)) yield return rs.Map!.SecondBossMapPoint!;
    }

    // Standing on the beaten first boss of a two-boss act: the run stays
    // in the act and the second boss is the only next step. Shared with
    // the rewards-proceed verb so map view and act exit can't disagree.
    internal static bool SecondBossPending(RunState rs) =>
        rs.Map is { SecondBossMapPoint: not null } map
        && rs.CurrentMapPoint is { } here && ReferenceEquals(here, map.BossMapPoint);

    private static SnapshotContract EventSnapshot(Phase phase)
    {
        var ev = Screens.CurrentEvent();
        if (ev is null) return new SnapshotContract(phase) { Available = false };
        var owner = ev.Owner;

        // EventRoom already called CalculateVars exactly once when the
        // event began. Never call it while observing: several events roll
        // RNG or advance counters there, so a read would change both the
        // advertised choice and the effect that the click later executes.
        var snapshot = new SnapshotContract(phase)
        {
            Id = ev.Id.Entry,
            Player = FooterView(),
            SemanticState =
            [
                SemanticToken("event", ev.GetType().FullName, ev.IsFinished),
                .. EventDynamicVarState(ev.DynamicVars),
                .. (ev is FakeMerchant semanticMerchant
                    ? FakeMerchantState(semanticMerchant)
                    : []),
            ],
            Options = (ev.CurrentOptions ?? []).Select((o, i) =>
            {
                var lethal = OptionLethal(o, owner);
                var option = Item(new
                {
                    title = SafeText(o.Title, ev.DynamicVars.AddTo),
                    description = SafeText(o.Description, ev.DynamicVars.AddTo),
                    proceed = o.IsProceed,
                    // The GUI marks choices that kill this player with a red
                    // pulse. Null means the option has no lethal predicate.
                    lethal,
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
                }, i,
                    semanticState:
                    [
                        SemanticToken(
                            "eventOption",
                            o.GetType().FullName,
                            o.Title?.LocTable,
                            o.Title?.LocEntryKey,
                            o.Description?.LocTable,
                            o.Description?.LocEntryKey,
                            o.TextKey,
                            o.IsProceed,
                            lethal,
                            o.Relic?.Id.Entry,
                            EventHintState(o))
                    ]);
                option.Locked = o.IsLocked;
                option.Chosen = o.WasChosen;
                return option;
            }).ToArray(),
        };
        snapshot.AddExtensions(new
        {
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
        });
        return snapshot;
    }

    private static string[] FakeMerchantState(FakeMerchant fake)
    {
        var inventory = fake.Inventory;
        return
        [
            SemanticToken(
                "fakeMerchant",
                inventory is not null,
                fake.Owner?.PotionSlots.Any(
                    potion => potion?.Id.Entry == "FOUL_POTION") == true),
            .. (inventory?.RelicEntries ?? [])
                .Select((entry, index) => SemanticToken(
                    "fakeMerchantRelic",
                    index,
                    entry.Model?.Id.Entry,
                    entry.Cost,
                    entry.IsStocked,
                    entry.EnoughGold)),
        ];
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

    private static object[] EventHintState(EventOption option) =>
        (option.HoverTips ?? []).Select(hint => (object)new
        {
            kind = hint.GetType().FullName,
            model = hint.CanonicalModel?.Id.Entry,
            upgraded = hint is CardHoverTip card && card.Card.IsUpgraded,
        }).ToArray();

    private static object EventHintView(IHoverTip hint)
    {
        if (hint is CardHoverTip card)
            return new
            {
                kind = "card",
                model = card.Card.Id.Entry,
                title = RichText.NormalizeIcons(card.Card.Title ?? ""),
                description = CardSpecifier.Description(card.Card),
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
    private static SnapshotContract CombatSnapshot(Phase phase)
    {
        var combat = CombatManager.Instance;
        var state = combat?.DebugOnlyGetState();
        if (combat is null || state is null)
            return new SnapshotContract(phase) { Available = false };
        var player = LocalRunContext.LocalPlayer(state);
        var pcs = player?.PlayerCombatState;
        var creature = player?.Creature;
        if (pcs is null || creature is null)
            return new SnapshotContract(phase) { Available = false };
        var facing = FacingOf(creature);

        var you = new SnapshotCombatantContract
        {
            Hp = [creature.CurrentHp, creature.MaxHp],
            Block = creature.Block,
            Energy = [pcs.Energy, pcs.MaxEnergy],
            Stars = pcs.Stars,
            SemanticState =
            [
                .. OrbState(pcs),
                .. PowerState(creature),
                .. player!.Relics
                    .Select(relic => SemanticToken("combatRelic", RelicStateToken(relic)))
                    .OrderBy(token => token, StringComparer.Ordinal),
                SemanticToken("facing", facing),
            ],
        };
        you.AddExtensions(new
        {
            orbs = OrbViews(pcs),
            powers = PowerViews(creature),
            // Counters tick mid-combat (every-N relics); the full
            // relic story lives in the out-of-combat footer.
            relics = player!.Relics
                .Select(r => (object)new { model = r.Id.Entry, counter = RelicCounter(r) })
                .ToArray(),
            facing,
        });
        var enemies = state.Enemies
            .Where(c => c != null)
            .Select(c =>
            {
                var enemy = new SnapshotEnemyContract
                {
                    Id = c.CombatId ?? 0u,
                    Model = c.Monster?.Id.Entry,
                    Hp = [c.CurrentHp, c.MaxHp],
                    Block = c.Block,
                    Alive = c.IsAlive,
                    SemanticState =
                    [
                        .. PowerState(c),
                        .. IntentState(state, c),
                        SemanticToken("side", SideOf(c), IsBehind(c, facing)),
                    ],
                };
                enemy.AddExtensions(new
                {
                    title = SafeText(c.Monster?.Title),
                    side = SideOf(c),
                    isBehind = IsBehind(c, facing),
                    powers = PowerViews(c),
                    intents = IntentViews(state, c),
                });
                return enemy;
            }).ToArray();
        var snapshot = new SnapshotContract(phase)
        {
            Turn = state.RoundNumber,
            You = you,
            Enemies = enemies,
            SemanticState =
            [
                .. PileState("draw", pcs.DrawPile, sorted: true),
                .. PileState("discard", pcs.DiscardPile),
                .. PileState("exhaust", pcs.ExhaustPile),
            ],
        };
        snapshot.Side = state.CurrentSide.ToString().ToLowerInvariant();
        snapshot.ActionsDisabled = combat.PlayerActionsDisabled;
        snapshot.AddExtensions(new
        {
            piles = new
            {
                draw = PileView(pcs.DrawPile, sorted: true),
                discard = PileView(pcs.DiscardPile),
                exhaust = PileView(pcs.ExhaustPile),
            },
        });
        snapshot.Potions = PotionViews(player!);
        snapshot.Hand = pcs.Hand.Cards
            .Where(c => c != null)
            .Select(c =>
            {
                // Compact keeps the numbers (vars) and drops the prose —
                // the refresh still runs so vars carry modified values.
                if (_compact) CardSpecifier.RefreshPreview(c);
                var showText = ShouldShowCardText(c, compactElides: true);
                var selector = CardSpecifier.From(c);
                var card = Item(new
                {
                    cost = c.EnergyCost.GetAmountToSpend(),
                    starCost = CardSpecifier.StarCost(c),
                    upgraded = c.IsUpgraded,
                    enchant = c.Enchantment?.Id.Entry,
                    affliction = c.Affliction?.Id.Entry,
                    unplayable = c.Keywords.Contains(CardKeyword.Unplayable),
                    textKey = CardSpecifier.TextKey(c),
                    description = showText ? CardSpecifier.Text(c, PileType.Hand) : null,
                    vars = CardSpecifier.DynamicVars(c),
                }, model: c.Id.Entry, selector: selector,
                    target: c.TargetType.ToString().ToLowerInvariant(),
                    semanticState: [CardStateToken(c, liveCost: true)]);
                card.Playable = CanPlayCard(c);
                return card;
            })
            .ToArray();
        return snapshot;
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

    private static string[] IntentState(CombatState state, Creature enemy)
        => CollectionSnapshot.ReadStable(
            "intent semantic state",
            () => (enemy.Monster?.NextMove.Intents ?? [])
                .Select((intent, index) => SemanticToken(
                    "intent",
                    index,
                    intent.IntentType.ToString().ToLowerInvariant(),
                    intent is AttackIntent attack
                        ? attack.GetSingleDamage(state.Allies, enemy)
                        : null,
                    intent is AttackIntent raw && raw.DamageCalc is { } calc
                        ? (int?)(int)calc()
                        : null,
                    intent is AttackIntent repeated ? repeated.Repeats : null))
                .ToArray());

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

    private static SnapshotContract CardSelectSnapshot(Phase phase)
    {
        var decision = DecisionSurface.Current.CardSelect;
        if (decision is null) return new SnapshotContract(phase) { Available = false };
        var snapshot = new SnapshotContract(phase)
        {
            Player = FooterView(),
            Cancelable = decision.Cancelable,
            Confirmable = decision.Confirmable,
            Cards = decision.Cards.Select((card, i) => Item(
                SelectCardView(card), i,
                model: card.Id.Entry,
                selector: CardSpecifier.From(card),
                semanticState: [CardStateToken(card)],
                selected: decision.Selected.Contains(card))).ToArray(),
            Selected = decision.Selected.Select(CardSpecifier.From).ToArray(),
            SemanticState =
            [
                SemanticToken("selectionBounds", decision.MinSelect, decision.MaxSelect)
            ],
        };
        snapshot.AddExtensions(new
        {
            prompt = SafeText(decision.Prompt),
            min = decision.MinSelect,
            max = decision.MaxSelect,
        });
        return snapshot;
    }

    // Hand select runs inside the combat room — the hand flips into a
    // selection mode instead of pushing an overlay. Picked cards leave
    // ActiveHolders (into the selected row), so idx tracks what's on screen.
    private static SnapshotContract HandSelectSnapshot(Phase phase)
    {
        var decision = DecisionSurface.Current.HandSelect;
        if (decision is null) return new SnapshotContract(phase) { Available = false };
        var snapshot = new SnapshotContract(phase)
        {
            Confirmable = decision.Confirmable,
            Cards = decision.Cards.Select((card, i) => Item(
                HandCardView(card), i,
                model: card?.Id.Entry,
                selector: card is null ? null : CardSpecifier.From(card),
                semanticState: card is null ? [SemanticToken("card", (object?)null)]
                    : [CardStateToken(card, liveCost: true)])).ToArray(),
            Selected = decision.Selected.Select(CardSpecifier.From).ToArray(),
            SemanticState =
            [
                SemanticToken("selectionBounds", decision.MinSelect, decision.MaxSelect)
            ],
        };
        if (decision.IncludePlayer) snapshot.Player = FooterView();
        snapshot.AddExtensions(new
        {
            prompt = SafeText(decision.Prompt),
            min = decision.MinSelect,
            max = decision.MaxSelect,
        });
        return snapshot;
    }
}
