using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.addons.mega_text;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// Adds a quality-of-life info tooltip to upcoming (not-yet-visited) map nodes, summarising what
// each room type is worth. Everything shown is wiki-level information a diligent player could
// already derive (probability distributions, gold/price ranges, heal amount) — never the actual
// pre-rolled outcome of a specific node. The "?"-room distribution and potion chance are read
// live, since both drift purely from outcomes the player has already seen.
//
// Values account for the player's current relics by going through the game's own reward hooks
// where they're side-effect free: gold via Hook.ModifyGoldGained (e.g. Bowler Hat / Ectoplasm),
// forced potions via Hook.ShouldForcePotionReward (White Beast Statue), and empty chests via
// Hook.ShouldGenerateTreasure (Silver Crucible). Reward-count relics that the hooks can't be
// queried for without generating rewards (which would consume run RNG) are handled explicitly:
// White Star, Black Star and Prayer Wheel.
//
// Coexists with CurrentNodeTooltipPatch, which owns the current/traveled nodes' room-history
// tooltip; this patch only fires on upcoming, still-reachable nodes, and exposes helpers so the
// current-node tooltip can show the live potion chance mid-combat.
[HarmonyPatch(typeof(NMapPoint), "OnFocus")]
public static class MapNodeInfoTooltipPatch
{
    // Headers reuse the game's map legend loc keys so they read exactly like the legend
    // ("Enemy", "Elite", "Merchant", ...).
    private const string LocTable = "map";

    // Gold amounts are tinted with the game's gold color (StsColors.gold) via BBCode, matching the
    // vanilla convention for gold numbers. The Description hover-tip label renders BBCode.
    private const string GoldColorHex = "EFC851";

    // Compendium rarity colors (StsColors.cream / blue / gold) for the merchant price columns, and
    // a disabled light-gray (StsColors.lightGray) for prices the player can't afford.
    private const string CommonColorHex = "FFF6E2";
    private const string UncommonColorHex = "87CEEB";
    private const string RareColorHex = "EFC851";
    private const string DisabledColorHex = "BFBFBF";

    // The currently-hovered Unknown/Monster node, tracked so MapNodeInfoModifierPatch can rebuild
    // its tooltip (to add/remove the possible-events / possible-enemies list) when Cmd/Ctrl is
    // pressed or released.
    private static NMapPoint? _hoveredExpandable;
    private static IRunState? _hoveredRunState;
    private static NMapScreen? _hoveredScreen;
    private static bool _expandedShown;

    // Cached reflection handles for the protected ActModel._rooms field (generated room pools)
    // and NumberOfWeakEncounters property (how many opening combats use the easy pool).
    private static FieldInfo? _roomsField;
    private static PropertyInfo? _weakCountProp;

    public static void Postfix(NMapPoint __instance, IRunState ____runState, NMapScreen ____screen)
    {
        try
        {
            ShowMapNodeInfoTooltip(__instance, ____runState, ____screen);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to show map-node-info tooltip: {e}");
        }
    }

    private static void ShowMapNodeInfoTooltip(NMapPoint point, IRunState runState, NMapScreen screen)
    {
        if (!ColinsPatchKitConfig.ShowMapNodeInfoTooltips)
        {
            return;
        }
        // Upcoming nodes only. The node you're standing on and previously-visited nodes already
        // show the room-history tooltip (vanilla OnFocus + CurrentNodeTooltipPatch).
        if (point.State == MapPointState.Traveled || runState.MapLocation.coord == point.Point.coord)
        {
            return;
        }
        // Don't advertise nodes you can no longer reach (a branch you pathed away from).
        if (!IsReachable(runState, point.Point))
        {
            return;
        }
        // Mirror vanilla guards: no tooltips on controller, while traveling, or while map drawing.
        if (NControllerManager.Instance?.IsUsingController != false || LocalContext.NetId == null)
        {
            return;
        }
        if (screen.IsTraveling || screen.Drawings.GetLocalDrawingMode() != DrawingMode.None)
        {
            return;
        }
        Player? localPlayer = runState.Players.FirstOrDefault(p => p.NetId == LocalContext.NetId.Value);
        if (localPlayer == null)
        {
            return;
        }

        (string titleKey, string? description) = BuildTooltip(point, runState, localPlayer);
        if (description == null)
        {
            // Boss / Ancient / Unassigned: nothing useful to add.
            return;
        }

        NHoverTipSet.Remove(point);
        HoverTip tip = new(new LocString(LocTable, titleKey), description);
        // The merchant price rows are wide; disable text wrapping so the tooltip grows to fit
        // them on one line each instead of wrapping at the default 360px width.
        if (point.Point.PointType == MapPointType.Shop)
        {
            tip.ShouldOverrideTextOverflow = true;
        }
        NHoverTipSet? set = NHoverTipSet.CreateAndShow(point, tip, HoverTipAlignment.None);
        if (set == null)
        {
            return;
        }
        // Track expandable nodes so MapNodeInfoModifierPatch can rebuild the tooltip when Cmd/Ctrl
        // is pressed/released (to expand/collapse the possible events / enemies / elites list).
        if (point.Point.PointType is MapPointType.Unknown or MapPointType.Monster or MapPointType.Elite)
        {
            _hoveredExpandable = point;
            _hoveredRunState = runState;
            _hoveredScreen = screen;
            _expandedShown = IsModifierHeld();
        }
        // Defer alignment a frame so the tooltip is sized before it's positioned (as in
        // CurrentNodeTooltipPatch).
        Callable.From(delegate
        {
            set.SetAlignment(point, HoverTip.GetHoverTipAlignment(point));
        }).CallDeferred();
    }

    private static (string titleKey, string? description) BuildTooltip(NMapPoint point, IRunState runState, Player player)
    {
        MapPoint mapPoint = point.Point;
        bool dependsOnUnresolved = PotionChanceDrifts(runState, mapPoint);
        return mapPoint.PointType switch
        {
            MapPointType.Unknown => ("LEGEND_UNKNOWN.title", BuildUnknown(runState)),
            MapPointType.Elite => ("LEGEND_ELITE.title", BuildElite(runState, player, dependsOnUnresolved)),
            MapPointType.Treasure => ("LEGEND_TREASURE.title", BuildTreasure(mapPoint, runState, player)),
            MapPointType.Monster => ("LEGEND_ENEMY.title", BuildMonster(mapPoint, runState, player, dependsOnUnresolved)),
            MapPointType.Shop => ("LEGEND_MERCHANT.title", BuildMerchant(mapPoint, runState, player)),
            MapPointType.RestSite => ("LEGEND_REST.title", BuildRest(player)),
            _ => (string.Empty, null),
        };
    }

    // "?" node: the live resolution distribution. Elite is disabled by default (base odds -1),
    // so only show it if a hook has enabled it.
    private static string BuildUnknown(IRunState runState)
    {
        UnknownMapPointOdds odds = runState.Odds.UnknownMapPoint;
        List<string> lines = new()
        {
            $"Monster: {Pct(odds.MonsterOdds)}",
            $"Treasure: {Pct(odds.TreasureOdds)}",
            $"Shop: {Pct(odds.ShopOdds)}",
            $"Event: {Pct(odds.EventOdds)}",
        };
        if (odds.EliteOdds > 0f)
        {
            lines.Insert(1, $"Elite: {Pct(odds.EliteOdds)}");
        }
        AppendExpandableSection(lines, "events", runState, rs => NamedList("Possible events", GetPossibleEventNames(rs)));
        return string.Join("\n", lines);
    }

    // The events that could still spawn this run, minus ones already encountered (run-wide, as
    // events don't repeat). Uses the run's generated event pool (_rooms.events) when reachable,
    // which is already filtered to the epochs revealed for this run — so it reflects what's
    // actually visible in the current act rather than every event that theoretically exists.
    private static List<string> GetPossibleEventNames(IRunState runState)
    {
        HashSet<string> seen = CollectFoughtIds(runState, RoomType.Event, allActs: true);
        IEnumerable<EventModel> pool = GetRooms(runState)?.events
            ?? (runState.Act?.AllEvents ?? Enumerable.Empty<EventModel>()).Concat(ModelDb.AllSharedEvents);
        return pool
            .Where(e => e != null)
            .Where(e => !seen.Contains(e.Id.Entry))
            .Select(e => e.Title.GetFormattedText())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // "Hold Cmd/Ctrl to list X" hint, expanding to the full body (built by getBody, which already
    // includes any headers) while the modifier is held.
    private static void AppendExpandableSection(List<string> lines, string noun, IRunState runState, Func<IRunState, List<string>> getBody)
    {
        if (IsModifierHeld())
        {
            List<string> body = getBody(runState);
            if (body.Count > 0)
            {
                lines.Add("");
                lines.AddRange(body);
            }
        }
        else
        {
            lines.Add($"[color=#888888]Hold {ModifierName()} to list {noun}[/color]");
        }
    }

    // A "Header:" line followed by "  - item" lines, or nothing when the list is empty.
    private static List<string> NamedList(string header, IEnumerable<string> items)
    {
        List<string> materialized = items.ToList();
        List<string> result = new();
        if (materialized.Count > 0)
        {
            result.Add($"{header}:");
            result.AddRange(materialized.Select(i => $"  - {i}"));
        }
        return result;
    }

    // Elite node: which (not-yet-fought) elites this act can throw at you, the gold range, and the
    // (elite-boosted) potion chance, plus any reward-count relic bonuses.
    private static string BuildElite(IRunState runState, Player player, bool dependsOnUnresolved)
    {
        List<string> lines = new() { GoldText(runState, player, 35, 45, CombatGoldBonus(runState, player)) };
        float chance = PotionChance(runState, player, RoomType.Elite, out bool forced);
        bool drifts = dependsOnUnresolved && !forced;
        lines.Add(PotionLine(chance, drifts));
        if (player.GetRelic<WhiteStar>() != null)
        {
            lines.Add("+1 card reward (White Star)");
        }
        if (player.GetRelic<BlackStar>() != null)
        {
            lines.Add("+1 relic (Black Star)");
        }
        // Behind Cmd/Ctrl, like the enemy list, for consistency.
        AppendExpandableSection(lines, "elites", runState, rs => NamedList("Possible elites", GetUnfoughtEliteNames(rs)));
        return string.Join("\n", lines);
    }

    // Treasure node: the relic pick, gold range (Poverty + gold relics), and any Spoils Map bonus.
    // Some run states (e.g. the Silver Crucible relic / Neow choice) leave the first chest empty.
    private static string BuildTreasure(MapPoint mapPoint, IRunState runState, Player player)
    {
        if (!Hook.ShouldGenerateTreasure(runState, player))
        {
            return "Empty (no relic or gold)";
        }
        List<string> lines = new()
        {
            "Relic: 1",
            GoldText(runState, player, 42, 52),
        };
        // A Spoils Map quest on this node pays out 600 per Spoils Map still in your deck.
        if (mapPoint.Quests.Any(q => q is SpoilsMap))
        {
            int spoils = player.Deck.Cards.OfType<SpoilsMap>().Count();
            if (spoils > 0)
            {
                lines.Add($"+{Gold($"{600 * spoils}")} gold (Spoils Map)");
            }
        }
        return string.Join("\n", lines);
    }

    // Enemy node: gold range, potion chance, and any reward-count relic bonus.
    private static string BuildMonster(MapPoint mapPoint, IRunState runState, Player player, bool dependsOnUnresolved)
    {
        List<string> lines = new() { GoldText(runState, player, 10, 20, CombatGoldBonus(runState, player)) };
        float chance = PotionChance(runState, player, RoomType.Monster, out bool forced);
        bool drifts = dependsOnUnresolved && !forced;
        lines.Add(PotionLine(chance, drifts));
        if (player.GetRelic<PrayerWheel>() != null)
        {
            lines.Add("+1 card reward (Prayer Wheel)");
        }
        AppendExpandableSection(lines, "enemies", runState, rs => GetEnemyBody(rs, mapPoint));
        return string.Join("\n", lines);
    }

    // Merchant node: a rarity-colored column header, then approximate price ranges by rarity
    // (prices vary ~±5-15%), then the live next card-removal cost. Prices reflect price relics
    // (Membership Card, The Courier) and ascension, and are greyed when unaffordable.
    private static string BuildMerchant(MapPoint mapPoint, IRunState runState, Player player)
    {
        // Project gold forward: what you're guaranteed to have on arrival even on the worst route.
        int gold = player.Gold + MinGoldGainTo(runState, player, mapPoint);
        float discount = MerchantDiscount(player);
        int removalBase = AscensionHelper.GetValueIfAscension(AscensionLevel.Inflation, 100, 75);
        int removalStep = AscensionHelper.GetValueIfAscension(AscensionLevel.Inflation, 50, 25);
        int removalCost = Mathf.RoundToInt((removalBase + removalStep * player.ExtraFields.CardShopRemovalsUsed) * discount);
        return string.Join("\n", new[]
        {
            $"{Colored("Common", CommonColorHex)} / {Colored("Uncommon", UncommonColorHex)} / {Colored("Rare", RareColorHex)}",
            $"Cards: {PriceCell(50, 0.05f, discount, gold)} / {PriceCell(75, 0.05f, discount, gold)} / {PriceCell(150, 0.05f, discount, gold)}",
            $"Relics: {PriceCell(175, 0.15f, discount, gold)} / {PriceCell(225, 0.15f, discount, gold)} / {PriceCell(275, 0.15f, discount, gold)}",
            $"Potions: {PriceCell(50, 0.05f, discount, gold)} / {PriceCell(75, 0.05f, discount, gold)} / {PriceCell(100, 0.05f, discount, gold)}",
            $"Card removal: {AffordableValue(removalCost, gold)}",
        });
    }

    // Combined merchant price multiplier from price-modifying relics (the only two that do this).
    private static float MerchantDiscount(Player player)
    {
        float discount = 1f;
        if (player.GetRelic<MembershipCard>() != null)
        {
            discount *= 0.5f;
        }
        if (player.GetRelic<TheCourier>() != null)
        {
            discount *= 0.8f;
        }
        return discount;
    }

    // The least gold the player is guaranteed to pick up before reaching this shop: the minimum,
    // over every route from the current node, of the gold at the nodes in between. Counts the
    // Monster/Elite combat gold minimums and chest gold (all run through the gold relics); chests
    // are assumed to pay out unless Silver Crucible can leave one empty. "?" and event gold isn't
    // counted (they vary), keeping this a safe lower bound on what you'll have when you arrive.
    private static int MinGoldGainTo(IRunState runState, Player player, MapPoint target)
    {
        MapPoint? current = runState.CurrentMapPoint ?? runState.Map?.StartingMapPoint;
        if (current == null)
        {
            return 0;
        }
        HashSet<MapPoint> forward = new() { current };
        Queue<MapPoint> queue = new();
        queue.Enqueue(current);
        while (queue.Count > 0)
        {
            foreach (MapPoint child in queue.Dequeue().Children)
            {
                if (forward.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }
        if (!forward.Contains(target))
        {
            return 0;
        }
        (int monsterMin, _) = GoldRange(10, 20);
        (int eliteMin, _) = GoldRange(35, 45);
        (int treasureMin, _) = GoldRange(42, 52);
        int combatBonus = CombatGoldBonus(runState, player);
        int monsterGold = (int)Hook.ModifyGoldGained(runState, null, monsterMin, player, out _) + combatBonus;
        int eliteGold = (int)Hook.ModifyGoldGained(runState, null, eliteMin, player, out _) + combatBonus;
        // Chests reliably pay out unless Silver Crucible can leave one empty.
        int treasureGold = player.GetRelic<SilverCrucible>() != null
            ? 0
            : (int)Hook.ModifyGoldGained(runState, null, treasureMin, player, out _);
        // Maw Bank pays gold on entering every room until you make a purchase, so it adds to every
        // node on the way (one-time gold relics like Old Coin are already in player.Gold).
        MawBank? mawBank = player.GetRelic<MawBank>();
        int mawBankGold = mawBank != null && !mawBank.HasItemBeenBought ? 12 : 0;
        int GoldAt(MapPoint node) => mawBankGold + node.PointType switch
        {
            MapPointType.Monster => monsterGold,
            MapPointType.Elite => eliteGold,
            MapPointType.Treasure => treasureGold,
            _ => 0,
        };
        // Least gold accumulated from the current node to `node` (node's own gold included, the
        // current node's excluded), recursed over parents on a route from current. Memoized.
        Dictionary<MapPoint, int> memo = new();
        int MinTo(MapPoint node)
        {
            if (node == current)
            {
                return 0;
            }
            if (memo.TryGetValue(node, out int cached))
            {
                return cached;
            }
            memo[node] = 0; // guard against unexpected cycles
            int best = int.MaxValue;
            foreach (MapPoint parent in node.parents)
            {
                if (forward.Contains(parent))
                {
                    best = Math.Min(best, MinTo(parent));
                }
            }
            int result = (best == int.MaxValue ? 0 : best) + GoldAt(node);
            memo[node] = result;
            return result;
        }
        return MinTo(target);
    }

    // Rest site: how much the heal option would restore right now (30% of max HP).
    private static string BuildRest(Player player)
    {
        int heal = (int)(player.Creature.MaxHp * 0.3f);
        return $"{heal} HP (30% of max)";
    }

    // The current combat's potion chance, for CurrentNodeTooltipPatch to append to the
    // current-room history tooltip. Only while a Monster/Elite combat is in progress and its
    // reward (the potion roll) hasn't happened yet; null otherwise. This value is exact, so no
    // asterisk.
    public static string? CurrentRoomPotionLine(IRunState runState)
    {
        if (!ColinsPatchKitConfig.ShowMapNodeInfoTooltips || LocalContext.NetId == null)
        {
            return null;
        }
        if (!IsCurrentRoomPotionPending(runState))
        {
            return null;
        }
        Player? player = runState.Players.FirstOrDefault(p => p.NetId == LocalContext.NetId.Value);
        if (player == null)
        {
            return null;
        }
        RoomType roomType = (runState.CurrentRoom as CombatRoom)?.RoomType ?? RoomType.Monster;
        float chance = PotionChance(runState, player, roomType, out _);
        return PotionLine(chance, showAsterisk: false);
    }

    // Reachable iff there's a forward path (via Children) from the current node to the target.
    // At the start of an act (no current node) or with a free-travel relic, everything's reachable.
    private static bool IsReachable(IRunState runState, MapPoint target)
    {
        MapPoint? current = runState.CurrentMapPoint;
        if (current == null || Hook.ShouldAllowFreeTravel(runState))
        {
            return true;
        }
        HashSet<MapPoint> visited = new() { current };
        Queue<MapPoint> queue = new();
        queue.Enqueue(current);
        while (queue.Count > 0)
        {
            MapPoint node = queue.Dequeue();
            if (node.coord == target.coord)
            {
                return true;
            }
            foreach (MapPoint child in node.Children)
            {
                if (visited.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }
        return false;
    }

    // The act's elite encounters minus the ones already fought this act (map gen never repeats an
    // elite, so a fought elite can't appear again).
    private static List<string> GetUnfoughtEliteNames(IRunState runState)
    {
        return EncounterNames(
            runState.Act?.AllEliteEncounters ?? Enumerable.Empty<EncounterModel>(),
            CollectFoughtIds(runState, RoomType.Elite, allActs: false));
    }

    // The act's normal-monster pool, split into the easy (weak) and hard (regular) sub-pools the
    // game draws from. The first NumberOfWeakEncounters normal combats of an act use the easy
    // pool, the rest the hard pool — so which pool THIS node uses depends on how many normal
    // combats precede it, which varies by route. Only show one pool when every route guarantees
    // it; otherwise show both. "?" nodes count as a maybe-combat, widening the uncertainty.
    private static List<string> GetEnemyBody(IRunState runState, MapPoint target)
    {
        HashSet<string> fought = CollectFoughtIds(runState, RoomType.Monster, allActs: false);
        List<string> easy = EncounterNames(runState.Act?.AllWeakEncounters ?? Enumerable.Empty<EncounterModel>(), fought);
        List<string> hard = EncounterNames(runState.Act?.AllRegularEncounters ?? Enumerable.Empty<EncounterModel>(), fought);

        bool showEasy = true;
        bool showHard = true;
        int? weakCount = GetWeakEncounterCount(runState);
        (int min, int max)? bounds = MonsterDepthBounds(runState, target);
        if (weakCount.HasValue && bounds.HasValue)
        {
            int done = GetNormalsFought(runState);
            int indexMin = done + bounds.Value.min; // earliest this node could be the Nth normal combat
            int indexMax = done + bounds.Value.max; // latest
            if (indexMax <= weakCount.Value)
            {
                showHard = false; // every route keeps it within the easy pool
            }
            else if (indexMin > weakCount.Value)
            {
                showEasy = false; // every route pushes it past the easy pool
            }
        }

        List<string> body = new();
        if (showEasy)
        {
            body.AddRange(NamedList("Easy", easy));
        }
        if (showHard)
        {
            body.AddRange(NamedList("Hard", hard));
        }
        return body;
    }

    // How many normal combats have already been fought this act (the easy/hard cutoff counts from
    // the act's first normal combat).
    private static int GetNormalsFought(IRunState runState)
    {
        RoomSet? rooms = GetRooms(runState);
        if (rooms != null)
        {
            return rooms.normalEncountersVisited;
        }
        int count = 0;
        int act = runState.CurrentActIndex;
        if (act >= 0 && act < runState.MapPointHistory.Count)
        {
            foreach (MapPointHistoryEntry node in runState.MapPointHistory[act])
            {
                count += node.Rooms.Count(r => r.RoomType == RoomType.Monster);
            }
        }
        return count;
    }

    // The min and max number of normal combats on any route from the current node to the target
    // (target inclusive, current exclusive). Definite Monster nodes count 1 on both bounds; "?"
    // nodes count 0 toward the min and 1 toward the max (they might resolve to a combat). Null if
    // the target isn't forward-reachable.
    private static (int min, int max)? MonsterDepthBounds(IRunState runState, MapPoint target)
    {
        MapPoint? current = runState.CurrentMapPoint ?? runState.Map?.StartingMapPoint;
        if (current == null)
        {
            return null;
        }
        // Nodes forward-reachable from the current node.
        HashSet<MapPoint> reachable = new() { current };
        Queue<MapPoint> queue = new();
        queue.Enqueue(current);
        while (queue.Count > 0)
        {
            foreach (MapPoint child in queue.Dequeue().Children)
            {
                if (reachable.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }
        if (!reachable.Contains(target))
        {
            return null;
        }
        // Combat count to a node = its own contribution + the best parent's count, recursed over
        // parents that lie on a route from the current node. Memoized; order-independent.
        Dictionary<MapPoint, int> memoMin = new();
        Dictionary<MapPoint, int> memoMax = new();
        int Bound(MapPoint node, bool wantMax, Dictionary<MapPoint, int> memo)
        {
            if (node == current)
            {
                return 0;
            }
            if (memo.TryGetValue(node, out int cached))
            {
                return cached;
            }
            memo[node] = wantMax ? int.MinValue : int.MaxValue; // guard against unexpected cycles
            int best = wantMax ? int.MinValue : int.MaxValue;
            foreach (MapPoint parent in node.parents)
            {
                if (!reachable.Contains(parent))
                {
                    continue;
                }
                int parentBound = Bound(parent, wantMax, memo);
                best = wantMax ? Math.Max(best, parentBound) : Math.Min(best, parentBound);
            }
            int add = wantMax
                ? (node.PointType is MapPointType.Monster or MapPointType.Unknown ? 1 : 0)
                : (node.PointType == MapPointType.Monster ? 1 : 0);
            int result = best + add;
            memo[node] = result;
            return result;
        }
        return (Bound(target, wantMax: false, memoMin), Bound(target, wantMax: true, memoMax));
    }

    private static List<string> EncounterNames(IEnumerable<EncounterModel> pool, HashSet<string> exclude)
    {
        return pool
            .Where(e => e != null && !exclude.Contains(e.Id.Entry))
            .Select(e => e.Title.GetFormattedText())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Encounter/event ModelIds already fought, from MapPointHistory — the current act only, or
    // run-wide when allActs is set.
    private static HashSet<string> CollectFoughtIds(IRunState runState, RoomType roomType, bool allActs)
    {
        HashSet<string> seen = new();
        for (int i = 0; i < runState.MapPointHistory.Count; i++)
        {
            if (!allActs && i != runState.CurrentActIndex)
            {
                continue;
            }
            foreach (MapPointHistoryEntry node in runState.MapPointHistory[i])
            {
                foreach (MapPointRoomHistoryEntry room in node.Rooms)
                {
                    if (room.RoomType == roomType && room.ModelId != null)
                    {
                        seen.Add(room.ModelId.Entry);
                    }
                }
            }
        }
        return seen;
    }

    // The current act's generated room pools (events / encounters), via reflection on the
    // protected ActModel._rooms field. Null if unavailable (callers fall back to the public pools).
    private static RoomSet? GetRooms(IRunState runState)
    {
        ActModel? act = runState.Act;
        if (act == null)
        {
            return null;
        }
        try
        {
            _roomsField ??= typeof(ActModel).GetField("_rooms", BindingFlags.NonPublic | BindingFlags.Instance);
            return _roomsField?.GetValue(act) as RoomSet;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to read act room pools: {e}");
            return null;
        }
    }

    // How many opening normal combats of the current act draw from the easy pool (varies per act:
    // 2 or 3). Read by reflection from the protected ActModel.NumberOfWeakEncounters; null if
    // unreadable (callers then show both pools).
    private static int? GetWeakEncounterCount(IRunState runState)
    {
        ActModel? act = runState.Act;
        if (act == null)
        {
            return null;
        }
        try
        {
            _weakCountProp ??= typeof(ActModel).GetProperty("NumberOfWeakEncounters", BindingFlags.NonPublic | BindingFlags.Instance);
            return _weakCountProp?.GetValue(act) as int?;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to read weak-encounter count: {e}");
            return null;
        }
    }

    // The displayed potion chance is the live pity value, exact only if no combat rolls a potion
    // before you reach this node. It drifts if a combat is currently mid-fight (reward pending),
    // or if any route from your current node passes through a possible combat (Monster / Elite /
    // "?") strictly before the target. Non-combat rooms (shop, rest, treasure, event) don't roll
    // potions, so they never trigger the asterisk.
    private static bool PotionChanceDrifts(IRunState runState, MapPoint target)
    {
        if (IsCurrentRoomPotionPending(runState))
        {
            return true;
        }
        MapPoint? current = runState.CurrentMapPoint ?? runState.Map?.StartingMapPoint;
        if (current == null)
        {
            return false;
        }
        // Forward-reachable set from the current node (includes current).
        HashSet<MapPoint> forward = new() { current };
        Queue<MapPoint> queue = new();
        queue.Enqueue(current);
        while (queue.Count > 0)
        {
            foreach (MapPoint child in queue.Dequeue().Children)
            {
                if (forward.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }
        // Walk back from the target through parents that are reachable from the current node:
        // every node reached (other than current itself) lies strictly between them on some route.
        HashSet<MapPoint> between = new();
        Queue<MapPoint> back = new();
        back.Enqueue(target);
        while (back.Count > 0)
        {
            foreach (MapPoint parent in back.Dequeue().parents)
            {
                if (forward.Contains(parent) && between.Add(parent))
                {
                    back.Enqueue(parent);
                }
            }
        }
        return between.Any(node => node != current
            && node.PointType is MapPointType.Monster or MapPointType.Elite or MapPointType.Unknown);
    }

    private static bool IsCurrentRoomPotionPending(IRunState runState)
    {
        if (runState.CurrentRoom is not CombatRoom combat)
        {
            return false;
        }
        if (CombatManager.Instance?.IsInProgress != true)
        {
            return false;
        }
        return combat.RoomType is RoomType.Monster or RoomType.Elite;
    }

    // The true displayed potion chance: 100% if a relic forces it (White Beast Statue), otherwise
    // the live pity value plus half the elite bonus on elites.
    private static float PotionChance(IRunState runState, Player player, RoomType roomType, out bool forced)
    {
        forced = Hook.ShouldForcePotionReward(runState, player, roomType);
        if (forced)
        {
            return 1f;
        }
        float bonus = roomType == RoomType.Elite ? PotionRewardOdds.eliteBonus * 0.5f : 0f;
        return player.PlayerOdds.PotionReward.CurrentValue + bonus;
    }

    private static string PotionLine(float chance, bool showAsterisk)
    {
        return $"Potion chance: {Pct(chance)}{(showAsterisk ? "*" : "")}";
    }

    // Gold range with Poverty applied, run through the gold relics (Bowler Hat, Ectoplasm, ...),
    // plus a flat combat-reward bonus (Amethyst Aubergine) passed in for combat nodes.
    private static string GoldText(IRunState runState, Player player, int baseMin, int baseMax, int flatBonus = 0)
    {
        (int min, int max) = GoldRange(baseMin, baseMax);
        min = (int)Hook.ModifyGoldGained(runState, null, min, player, out _) + flatBonus;
        max = (int)Hook.ModifyGoldGained(runState, null, max, player, out _) + flatBonus;
        return $"Gold: {Gold(min == max ? min.ToString() : $"{min}-{max}")}";
    }

    // Extra flat gold added to every combat reward by reward-adding relics (Amethyst Aubergine
    // adds +15 to monster/elite fights), itself run through the gold modifiers.
    private static int CombatGoldBonus(IRunState runState, Player player)
    {
        if (player.GetRelic<AmethystAubergine>() == null)
        {
            return 0;
        }
        return (int)Hook.ModifyGoldGained(runState, null, 15, player, out _);
    }

    private static string Gold(string amount)
    {
        return $"[color=#{GoldColorHex}]{amount}[/color]";
    }

    // A merchant price range (base * NextFloat(1-variance, 1+variance) * relic discount, rounded),
    // gold-colored when the player can afford the top of the range, else greyed as unaffordable.
    private static string PriceCell(int baseCost, float variance, float discount, int gold)
    {
        int lo = Mathf.RoundToInt(baseCost * (1f - variance) * discount);
        int hi = Mathf.RoundToInt(baseCost * (1f + variance) * discount);
        return Colored($"{lo}-{hi}", gold >= hi ? GoldColorHex : DisabledColorHex);
    }

    // A single merchant cost, gold-colored when affordable, else greyed.
    private static string AffordableValue(int cost, int gold)
    {
        return Colored(cost.ToString(), gold >= cost ? GoldColorHex : DisabledColorHex);
    }

    private static string Colored(string text, string hex)
    {
        return $"[color=#{hex}]{text}[/color]";
    }

    // Rebuild the hovered Unknown/Monster node's tooltip when the Cmd/Ctrl state changes (called
    // each frame from MapNodeInfoModifierPatch), expanding/collapsing the possible-list.
    public static void RefreshHoveredExpandableIfModifierChanged()
    {
        if (_hoveredExpandable == null || _hoveredRunState == null || _hoveredScreen == null)
        {
            return;
        }
        bool held = IsModifierHeld();
        if (held == _expandedShown)
        {
            return;
        }
        _expandedShown = held;
        ShowMapNodeInfoTooltip(_hoveredExpandable, _hoveredRunState, _hoveredScreen);
    }

    public static void ClearHoverIf(NMapPoint point)
    {
        if (_hoveredExpandable == point)
        {
            _hoveredExpandable = null;
            _hoveredRunState = null;
            _hoveredScreen = null;
        }
    }

    private static bool IsModifierHeld()
    {
        return IsMac() ? Input.IsKeyPressed(Key.Meta) : Input.IsKeyPressed(Key.Ctrl);
    }

    private static string ModifierName()
    {
        return IsMac() ? "Cmd" : "Ctrl";
    }

    private static bool IsMac()
    {
        return OS.GetName().Contains("macOS");
    }

    private static (int min, int max) GoldRange(int baseMin, int baseMax)
    {
        if (AscensionHelper.HasAscension(AscensionLevel.Poverty))
        {
            return ((int)(baseMin * AscensionHelper.PovertyAscensionGoldMultiplier),
                (int)(baseMax * AscensionHelper.PovertyAscensionGoldMultiplier));
        }
        return (baseMin, baseMax);
    }

    private static string Pct(float value)
    {
        return $"{Mathf.Clamp(value, 0f, 1f) * 100f:0}%";
    }
}

// Clears the hovered-Unknown tracking when the node loses focus, so the modifier poll stops
// rebuilding a tooltip that's no longer shown.
[HarmonyPatch(typeof(NMapPoint), "OnUnfocus")]
public static class MapNodeInfoUnfocusPatch
{
    public static void Postfix(NMapPoint __instance)
    {
        try
        {
            MapNodeInfoTooltipPatch.ClearHoverIf(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to clear map-node-info hover: {e}");
        }
    }
}

// Polls the Cmd/Ctrl modifier each frame while the map screen is up, so holding it expands the
// hovered Unknown node's tooltip to the full possible-events list (and releasing collapses it).
[HarmonyPatch(typeof(NMapScreen), "_Process")]
public static class MapNodeInfoModifierPatch
{
    public static void Postfix()
    {
        try
        {
            MapNodeInfoTooltipPatch.RefreshHoveredExpandableIfModifierChanged();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"map-node-info modifier poll failed: {e}");
        }
    }
}
