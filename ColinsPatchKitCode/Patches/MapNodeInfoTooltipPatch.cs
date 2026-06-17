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
using MegaCrit.Sts2.Core.Entities.RestSite;
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

    // Disabled light-gray (StsColors.lightGray) for merchant prices the player can't afford.
    private const string DisabledColorHex = "BFBFBF";

    // Red for the "No potion" history line, plus the potion reward icon (full path for rendering,
    // bare filename for matching an existing potion row).
    private const string NoPotionColorHex = "FF5555";
    private const string PotionIconPath = "res://images/packed/sprite_fonts/potion_icon.png";
    private const string PotionIconMarker = "potion_icon.png";

    // The currently-hovered Unknown/Monster node, tracked so MapNodeInfoModifierPatch can rebuild
    // its tooltip (to add/remove the possible-events / possible-enemies list) when Cmd/Ctrl is
    // pressed or released.
    private static NMapPoint? _hoveredExpandable;
    private static IRunState? _hoveredRunState;
    private static NMapScreen? _hoveredScreen;
    private static Player? _hoveredPlayer;
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

        ShowExpectedRewardsTooltip(point, runState, screen, localPlayer);
    }

    // Builds and shows the "Floor N / room type / expected rewards" tooltip for an upcoming `point`.
    // The current in-combat node takes a separate path (CurrentNodeTooltipPatch, which inserts the
    // expected rewards into the vanilla history tooltip), so this only ever runs for upcoming nodes —
    // hence no Cmd-expand or asterisk-suppression handling for the current room here.
    public static void ShowExpectedRewardsTooltip(NMapPoint point, IRunState runState, NMapScreen screen, Player player)
    {
        MapPointType type = point.Point.PointType;
        bool dependsOnUnresolved = PotionChanceDrifts(runState, point.Point);
        (string roomTypeKey, string? body) = BuildTooltipFor(type, point.Point, runState, player, dependsOnUnresolved);
        if (body == null)
        {
            // Boss / Ancient / Unassigned: nothing useful to add.
            return;
        }

        NHoverTipSet.Remove(point);
        // Mirror the historical-node tooltip: a "Floor N" header, the room type in normal text below
        // it, then the expected-reward sections (reusing the same floor-header loc string).
        LocString header = new("run_history", "MAP_POINT_HISTORY.header");
        header.Add("FloorNum", FloorNumberFor(runState, point.Point));
        string roomType = new LocString(LocTable, roomTypeKey).GetFormattedText();
        // Bosses fold their name into the room-type line ("Boss: Waterfall Giant") rather than
        // showing it as a separate body line.
        if (type == MapPointType.Boss && BossEncounterFor(runState, point.Point) is { } bossEncounter)
        {
            roomType = LabelValue(roomType, bossEncounter.Title.GetFormattedText());
        }
        // A single newline keeps the room type tight under the floor header; the body's own section
        // headers provide the visual spacing.
        string description = string.IsNullOrEmpty(body) ? roomType : $"{roomType}\n{body}";
        HoverTip tip = new(header, description);
        // These tooltips are lists of short labels (plus the merchant price table), never wrapped
        // prose, so disable wrapping and let the tooltip grow to fit its widest line rather than
        // wrap at the hover tip's hardcoded 360px width. English labels mostly fit; localized ones
        // overflow and break mid-phrase. The wide-tooltip positioning this relies on is already
        // exercised by the Shop price table and the Cmd/Ctrl-expanded enemy/elite/event lists, so
        // it's a proven path — long encounter names like "Two Gremlins in a Trenchcoat" included.
        tip.ShouldOverrideTextOverflow = true;
        NHoverTipSet? set = NHoverTipSet.CreateAndShow(point, tip, HoverTipAlignment.None);
        if (set == null)
        {
            return;
        }
        // Track expandable nodes so MapNodeInfoModifierPatch can rebuild the tooltip when Cmd/Ctrl
        // is pressed/released (to expand/collapse the possible events / enemies / elites list).
        if (type is MapPointType.Unknown or MapPointType.Monster or MapPointType.Elite)
        {
            _hoveredExpandable = point;
            _hoveredRunState = runState;
            _hoveredScreen = screen;
            _hoveredPlayer = player;
            _expandedShown = IsModifierHeld();
        }
        // Defer alignment a frame so the tooltip is sized before it's positioned (as in
        // CurrentNodeTooltipPatch).
        Callable.From(delegate
        {
            set.SetAlignment(point, HoverTip.GetHoverTipAlignment(point));
        }).CallDeferred();
    }

    // If the local player is in an in-progress Monster/Elite/Boss combat, the map point type to
    // render its expected rewards as; null otherwise (the caller then keeps the history tooltip).
    // The final act's boss still maps to Boss here, but BossRewardLines yields nothing for it (the
    // run ends, no reward), so RenderExpectedRewardsIntoHistory no-ops in that case.
    public static MapPointType? InProgressCombatType(IRunState runState)
    {
        if (runState.CurrentRoom is not CombatRoom combat || CombatManager.Instance?.IsInProgress != true)
        {
            return null;
        }
        return combat.RoomType switch
        {
            RoomType.Monster => MapPointType.Monster,
            RoomType.Elite => MapPointType.Elite,
            RoomType.Boss => MapPointType.Boss,
            _ => null,
        };
    }

    // Inserts the expected rewards into the current room's history tooltip while its combat is still
    // in progress (CurrentNodeTooltipPatch's in-combat path). Vanilla leaves the Rewards section
    // empty mid-fight — nothing's been earned yet — so we reveal it, relabel its header "Expected
    // rewards", and fill the first reward column (exact here, since you're standing in the room).
    // The gold/relic/card estimate is the upcoming-floor info feature (ShowMapNodeInfoTooltips); the
    // potion chance is its own feature (ShowPotionChances, which covers "past, current and upcoming
    // floors"), so honor each toggle independently: the full block when info tooltips are on,
    // otherwise just the expected potion chance. No-op if neither applies, the combat grants no
    // reward (final-act boss), or the tooltip's reward nodes can't be found.
    public static void RenderExpectedRewardsIntoHistory(NMapPointHistoryHoverTip tip, IRunState runState,
        Player player, MapPoint mapPoint, MapPointType type)
    {
        // The current room's chance is exact, so dependsOnUnresolved is false (no drift asterisk).
        List<string> lines = ColinsPatchKitConfig.ShowMapNodeInfoTooltips
            ? type switch
            {
                MapPointType.Elite => EliteRewardLines(mapPoint, runState, player, dependsOnUnresolved: false),
                MapPointType.Monster => MonsterRewardLines(mapPoint, runState, player, dependsOnUnresolved: false),
                MapPointType.Boss => BossRewardLines(mapPoint, runState, player, dependsOnUnresolved: false),
                _ => new List<string>(),
            }
            : ExpectedPotionOnlyLines(runState, player, type);
        if (lines.Count == 0)
        {
            return;
        }
        Control? container = tip.GetNodeOrNull<Control>("%RewardStats");
        RichTextLabel? row = tip.GetNodeOrNull<Control>("%RewardRows")?.GetChildren().OfType<RichTextLabel>().FirstOrDefault();
        if (container == null || row == null)
        {
            return;
        }
        container.Visible = true;
        if (container.GetNodeOrNull<MegaLabel>("Header") is { } headerLabel)
        {
            headerLabel.SetTextAutoSize(Loc("EXPECTED_REWARDS"));
        }
        // Reward rows are tab-indented, one entry per line (matching vanilla's reward formatting).
        row.Text = string.Join("\n", lines.Select(l => $"\t{l}"));
    }

    // Just the expected potion-chance line for the current in-combat floor, used when the upcoming-
    // floor reward info is disabled but potion chances are on. Empty when potion chances are off or
    // the combat rolls no potion (the final act's boss).
    private static List<string> ExpectedPotionOnlyLines(IRunState runState, Player player, MapPointType type)
    {
        if (!ColinsPatchKitConfig.ShowPotionChances)
        {
            return new List<string>();
        }
        RoomType? roomType = type switch
        {
            MapPointType.Monster => RoomType.Monster,
            MapPointType.Elite => RoomType.Elite,
            // The final act's boss rolls no potion (BossRewardLines is empty for it), so skip the line.
            MapPointType.Boss when runState.CurrentActIndex < runState.Acts.Count - 1 => RoomType.Boss,
            _ => null,
        };
        return roomType is { } rt
            ? new List<string> { PotionRewardLine(runState, player, rt, dependsOnUnresolved: false) }
            : new List<string>();
    }

    private static (string titleKey, string? description) BuildTooltipFor(MapPointType type, MapPoint mapPoint,
        IRunState runState, Player player, bool dependsOnUnresolved)
    {
        return type switch
        {
            MapPointType.Unknown => ("LEGEND_UNKNOWN.title", BuildUnknown(runState, mapPoint)),
            MapPointType.Elite => ("LEGEND_ELITE.title", BuildElite(mapPoint, runState, player, dependsOnUnresolved)),
            MapPointType.Treasure => ("LEGEND_TREASURE.title", BuildTreasure(mapPoint, runState, player)),
            MapPointType.Monster => ("LEGEND_ENEMY.title", BuildMonster(mapPoint, runState, player, dependsOnUnresolved)),
            MapPointType.Shop => ("LEGEND_MERCHANT.title", BuildMerchant(mapPoint, runState, player)),
            MapPointType.RestSite => ("LEGEND_REST.title", BuildRest(player)),
            MapPointType.Boss => ("LEGEND_BOSS.title", BuildBoss(mapPoint, runState, player, dependsOnUnresolved)),
            _ => (string.Empty, null),
        };
    }

    // "?" node: the live resolution distribution. Elite is disabled by default (base odds -1),
    // so only show it if a hook has enabled it. Relics and cards that restrict which room types a
    // "?" can resolve to (Juzu Bracelet drops Monster; Golden Compass / Lantern Key force Event on
    // their act) work through ModifyUnknownMapPointRoomTypes — a hook Roll() consults but the odds
    // object itself doesn't bake in. Query it here and drop any removed type, folding its
    // probability into Event exactly as the roll does (a removed type falls through to Event).
    private static string BuildUnknown(IRunState runState, MapPoint target)
    {
        // First-ever run: the game forces the first two "?" rooms of the run to Event and the third
        // to Monster, ignoring the odds (UnknownMapPointOdds.Roll, NumberOfRuns==0). Whenever every
        // route pins this "?" inside that forced window, show the guaranteed outcome(s) instead of the
        // live distribution (which would falsely list Treasure/Shop/Elite chances that can't occur).
        if (runState.UnlockState.NumberOfRuns == 0 && ForcedFirstRunOutcomes(runState, target) is { } forced)
        {
            // A fixed index is a certainty (100%); a route-straddling "?" is one of two outcomes
            // ("Event or Monster"), shown without a misleading per-outcome percentage. The forced
            // list holds stable logic tokens ("Event"/"Monster"); map them to localized labels here.
            string outcomeLine = forced.Count == 1
                ? LabelValue(ForcedOutcomeLabel(forced[0]), Pct(1f))
                : string.Join(Loc("OUTCOME_SEPARATOR"), forced.Select(ForcedOutcomeLabel));
            List<string> forcedLines = Section(Loc("POSSIBLE_OUTCOMES"), new[] { outcomeLine });
            // Event is a possible outcome, so keep the "list events" expansion the other "?" tooltips offer.
            if (forced.Contains("Event"))
            {
                AppendExpandableSection(forcedLines, Loc("NOUN_EVENTS"), runState, rs => Section(Loc("POSSIBLE_EVENTS"), GetPossibleEventNames(rs)));
            }
            return string.Join("\n", forcedLines);
        }

        UnknownMapPointOdds odds = runState.Odds.UnknownMapPoint;
        (RoomType Type, string Label, float Value)[] nonEvent =
        {
            (RoomType.Monster, Loc("ROOM_MONSTER"), odds.MonsterOdds),
            (RoomType.Elite, Loc("ROOM_ELITE"), odds.EliteOdds),
            (RoomType.Treasure, Loc("ROOM_TREASURE"), odds.TreasureOdds),
            (RoomType.Shop, Loc("ROOM_SHOP"), odds.ShopOdds),
        };
        IReadOnlySet<RoomType> allowed = Hook.ModifyUnknownMapPointRoomTypes(
            runState, nonEvent.Select(o => o.Type).Append(RoomType.Event).ToHashSet());

        HashSet<MapPoint> between = NodesStrictlyBetween(runState, target);
        // These odds drift each time a "?" room resolves, so if any route here clears another "?"
        // first the numbers shown can still change — flag them with an asterisk.
        bool drifts = between.Any(node => node.PointType == MapPointType.Unknown);
        string mark = drifts ? "*" : "";

        // The game blacklists Shop from a "?" roll in two cases (RunManager.BuildRoomTypeBlacklist),
        // folding its mass into Event. Case B (every node out of this "?" is a guaranteed shop) is a
        // static property of this node, so we can drop Shop outright. Case A (you arrive here straight
        // from a shop) depends on the route, so we keep Shop but asterisk it: whether it's possible at
        // all hinges on whether you pass through that shop first. The game tests the predecessor with
        // HasRoomOfType(Shop), so a "?" that resolved into a shop counts too — the only such resolved
        // predecessor we can read is the node you're standing on (CurrentMapPointHistoryEntry); an
        // upcoming "?" predecessor that might resolve to a shop is already covered by the drift mark.
        MapPoint? current = runState.CurrentMapPoint ?? runState.Map?.StartingMapPoint;
        bool currentIsShop = runState.CurrentMapPointHistoryEntry?.HasRoomOfType(RoomType.Shop) == true;
        bool shopBlacklisted = target.Children.Count > 0 && target.Children.All(c => c.PointType == MapPointType.Shop);
        bool shopRouteDependent = !shopBlacklisted && target.parents.Any(p =>
            p == current ? currentIsShop : p.PointType == MapPointType.Shop && between.Contains(p));

        List<string> oddsLines = new();
        float nonEventSum = 0f;
        foreach ((RoomType type, string label, float value) in nonEvent)
        {
            if (value <= 0f || !allowed.Contains(type))
            {
                continue;
            }
            // Case B: never a shop here, so omit the line; its probability falls through to Event below.
            if (type == RoomType.Shop && shopBlacklisted)
            {
                continue;
            }
            nonEventSum += value;
            // Case A: the Shop line is route-dependent even when nothing else drifts.
            string lineMark = type == RoomType.Shop && shopRouteDependent ? "*" : mark;
            oddsLines.Add(LabelValue(label, Pct(value)) + lineMark);
        }
        // Event soaks up the leftover probability, including the mass of any type a hook removed.
        if (allowed.Contains(RoomType.Event))
        {
            oddsLines.Add(LabelValue(Loc("ROOM_EVENT"), Pct(Math.Max(0f, 1f - nonEventSum))) + mark);
        }
        // A "?" resolves to one of these rather than handing out rewards, so it's "Possible
        // outcomes" rather than "Expected rewards".
        List<string> lines = Section(Loc("POSSIBLE_OUTCOMES"), oddsLines);
        AppendExpandableSection(lines, Loc("NOUN_EVENTS"), runState, rs => Section(Loc("POSSIBLE_EVENTS"), GetPossibleEventNames(rs)));
        return string.Join("\n", lines);
    }

    // On the player's first-ever run the game pins the first two resolved "?" rooms to Event and the
    // third to Monster (UnknownMapPointOdds.Roll). Returns the guaranteed outcome label(s) for `target`
    // over its possible "?"-indices across every route — a single label when the index is fixed, or
    // both ("Event", "Monster") when the route straddles the Event/Monster boundary. Returns null when
    // some route reaches the target past the forced first three (normal odds apply) or it isn't reachable.
    private static List<string>? ForcedFirstRunOutcomes(IRunState runState, MapPoint target)
    {
        HashSet<MapPoint> forward = ForwardReachable(runState, out MapPoint? current);
        if (current == null || !forward.Contains(target))
        {
            return null;
        }
        int resolved = runState.MapPointHistory
            .SelectMany(act => act)
            .Count(entry => entry.MapPointType == MapPointType.Unknown);
        int UnknownAt(MapPoint node) => node.PointType == MapPointType.Unknown ? 1 : 0;
        // The "?"'s 0-based index among the run's "?" rooms = those already resolved (history) plus
        // those passed through strictly before it on the route (RouteExtremum includes the target).
        int minIndex = resolved + RouteExtremum(current, target, forward, UnknownAt, wantMax: false, 0) - UnknownAt(target);
        int maxIndex = resolved + RouteExtremum(current, target, forward, UnknownAt, wantMax: true, 0) - UnknownAt(target);
        if (maxIndex > 2)
        {
            return null; // some route reaches it past the forced window — its outcome isn't pinned
        }
        // Every route lands within the forced window; collect the guaranteed outcome for each index.
        List<string> outcomes = new();
        for (int index = minIndex; index <= maxIndex; index++)
        {
            string outcome = index <= 1 ? "Event" : "Monster";
            if (!outcomes.Contains(outcome))
            {
                outcomes.Add(outcome);
            }
        }
        return outcomes;
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
            .Where(e => IsEventAllowed(e, runState))
            .Select(e => e.Title.GetFormattedText())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Whether an event passes its own spawn requirements, the same gate the game applies before
    // offering one (RoomSet.EnsureNextEventIsValid → EventModel.IsAllowed). This drops events that
    // can never roll normally (War Historian Repy, only reached via the Lantern Key) as well as ones
    // the player doesn't currently qualify for (act / gold / HP / deck requirements). The latter are
    // evaluated against the live run state, so a gold/HP-gated event reflects your state right now,
    // not what it'll be when you reach the node. Fails open (keeps the event) if a predicate throws,
    // so one misbehaving event can't blank out the whole list.
    private static bool IsEventAllowed(EventModel e, IRunState runState)
    {
        try
        {
            return e.IsAllowed(runState);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"Event {e.Id.Entry} IsAllowed check failed: {ex}");
            return true;
        }
    }

    // "Hold Cmd/Ctrl to list X" hint, expanding to the full body (built by getBody, which already
    // includes any headers) while the modifier is held. Separated from the preceding section by a
    // blank line.
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
            lines.Add("");
            lines.Add($"[color=#888888]{Loc("HOLD_TO_LIST", ("Modifier", ModifierName()), ("Noun", noun))}[/color]");
        }
    }

    // Section/list headers reuse the history tooltip's "Rewards" label styling: gold (StsColors.gold
    // #EFC851), bold, and size 22 (the description label's default is smaller), so they read as
    // native headers above the cream body text.
    private static string Header(string text)
    {
        return $"[font_size=22][color=#{GoldColorHex}][b]{text}[/b][/color][/font_size]";
    }

    // A gold header with each entry indented two spaces beneath it, mirroring the history tooltip's
    // reward block (bullet-free, like vanilla). Empty when there are no entries. Used both for reward
    // blocks ("Expected rewards") and possible-X lists ("Possible events", "Options", ...).
    private static List<string> Section(string title, IEnumerable<string> entries)
    {
        List<string> materialized = entries.ToList();
        List<string> result = new();
        if (materialized.Count > 0)
        {
            result.Add(Header(title));
            result.AddRange(materialized.Select(e => $"  {e}"));
        }
        return result;
    }

    // The cumulative floor number of a node: its row within the current act plus the visited-node
    // counts of all previous acts — the same numbering the history tooltip uses.
    private static int FloorNumberFor(IRunState runState, MapPoint point)
    {
        int floor = point.coord.row + 1;
        for (int i = 0; i < runState.MapPointHistory.Count - 1; i++)
        {
            floor += runState.MapPointHistory[i].Count;
        }
        return floor;
    }

    // Elite node: which (not-yet-fought) elites this act can throw at you, the gold range, and the
    // (elite-boosted) potion chance, plus any reward-count relic bonuses.
    private static string BuildElite(MapPoint mapPoint, IRunState runState, Player player, bool dependsOnUnresolved)
    {
        List<string> lines = Section(Loc("EXPECTED_REWARDS"), EliteRewardLines(mapPoint, runState, player, dependsOnUnresolved));
        // Behind Cmd/Ctrl, like the enemy list, for consistency.
        AppendExpandableSection(lines, Loc("NOUN_ELITES"), runState, rs => Section(Loc("POSSIBLE_ELITES"), GetUnfoughtEliteNames(rs)));
        return string.Join("\n", lines);
    }

    // The plain elite reward lines (no "Expected rewards" header). Order matches the vanilla
    // run-history tooltip we insert into: gold, relic, card, then potion last.
    private static List<string> EliteRewardLines(MapPoint mapPoint, IRunState runState, Player player, bool dependsOnUnresolved)
    {
        List<string> rewards = new() { GoldText(runState, player, RoomType.Elite, CombatGoldBonus(runState, player)) };
        rewards.Add(CombatRelicLine(runState, player, mapPoint, isElite: true)!);
        rewards.Add(CardRewardLine(runState, player, mapPoint));
        if (player.GetRelic<WhiteStar>() is { } whiteStar)
        {
            rewards.Add(Loc("EXTRA_CARD_REWARD", ("Source", whiteStar.Title.GetFormattedText())));
        }
        rewards.Add(PotionRewardLine(runState, player, RoomType.Elite, dependsOnUnresolved));
        return rewards;
    }

    // Treasure node: the relic pick, gold range (Poverty + gold relics), and any Spoils Map bonus.
    // Some run states (e.g. the Silver Crucible relic / Neow choice) leave the first chest empty.
    private static string BuildTreasure(MapPoint mapPoint, IRunState runState, Player player)
    {
        List<string> rewards = new();
        if (!Hook.ShouldGenerateTreasure(runState, player))
        {
            rewards.Add(Loc("EMPTY_CHEST"));
        }
        else
        {
            rewards.Add(GoldText(runState, player, TreasureGoldMin, TreasureGoldMax));
            rewards.Add(RelicLine(1));
            // A Spoils Map quest on this node pays out 600 per Spoils Map still in your deck.
            if (mapPoint.Quests.Any(q => q is SpoilsMap))
            {
                List<SpoilsMap> spoilsCards = player.Deck.Cards.OfType<SpoilsMap>().ToList();
                if (spoilsCards.Count > 0)
                {
                    rewards.Add(Loc("SPOILS_MAP_GOLD",
                        ("Amount", Gold($"{600 * spoilsCards.Count}")),
                        ("Source", spoilsCards[0].TitleLocString.GetFormattedText())));
                }
            }
        }
        return string.Join("\n", Section(Loc("EXPECTED_REWARDS"), rewards));
    }

    // Enemy node: gold range, potion chance, and any reward-count relic bonus.
    private static string BuildMonster(MapPoint mapPoint, IRunState runState, Player player, bool dependsOnUnresolved)
    {
        List<string> lines = Section(Loc("EXPECTED_REWARDS"), MonsterRewardLines(mapPoint, runState, player, dependsOnUnresolved));
        AppendExpandableSection(lines, Loc("NOUN_ENEMIES"), runState, rs => GetEnemyBody(rs, mapPoint));
        return string.Join("\n", lines);
    }

    // The plain enemy reward lines (no "Expected rewards" header). Order matches the vanilla
    // run-history tooltip we insert into: gold, relic, card, then potion last.
    private static List<string> MonsterRewardLines(MapPoint mapPoint, IRunState runState, Player player, bool dependsOnUnresolved)
    {
        List<string> rewards = new() { GoldText(runState, player, RoomType.Monster, CombatGoldBonus(runState, player)) };
        // Monsters give no base relic, but Wongo's Mystery Ticket can drop one here.
        if (CombatRelicLine(runState, player, mapPoint, isElite: false) is { } relicLine)
        {
            rewards.Add(relicLine);
        }
        rewards.Add(CardRewardLine(runState, player, mapPoint));
        if (player.GetRelic<PrayerWheel>() is { } prayerWheel)
        {
            rewards.Add(Loc("EXTRA_CARD_REWARD", ("Source", prayerWheel.Title.GetFormattedText())));
        }
        rewards.Add(PotionRewardLine(runState, player, RoomType.Monster, dependsOnUnresolved));
        return rewards;
    }

    // Boss node: (for non-final-act bosses, which still hand out a reward) the gold, potion chance
    // and card reward. The final act's boss ends the run, so its tooltip is just the "Boss: <name>"
    // header line (the name is folded into the room-type line by the caller). Dual-boss acts have
    // two boss nodes; BossEncounterFor maps each to the right encounter.
    private static string BuildBoss(MapPoint mapPoint, IRunState runState, Player player, bool dependsOnUnresolved)
    {
        return string.Join("\n", Section(Loc("EXPECTED_REWARDS"), BossRewardLines(mapPoint, runState, player, dependsOnUnresolved)));
    }

    // The plain boss reward lines (no "Expected rewards" header). Empty for the final act's boss,
    // which ends the run and hands out no reward. Order matches the vanilla run-history tooltip:
    // gold, relic, card, then potion last.
    private static List<string> BossRewardLines(MapPoint mapPoint, IRunState runState, Player player, bool dependsOnUnresolved)
    {
        if (runState.CurrentActIndex >= runState.Acts.Count - 1)
        {
            return new List<string>();
        }
        List<string> rewards = new() { GoldText(runState, player, RoomType.Boss, CombatGoldBonus(runState, player)) };
        // Bosses give no base relic, but Wongo's Mystery Ticket can drop one here.
        if (CombatRelicLine(runState, player, mapPoint, isElite: false) is { } relicLine)
        {
            rewards.Add(relicLine);
        }
        rewards.Add(CardRewardLine(runState, player, mapPoint));
        rewards.Add(PotionRewardLine(runState, player, RoomType.Boss, dependsOnUnresolved));
        return rewards;
    }

    // The encounter at a boss node — the act's second boss for the second boss map point (dual-boss
    // acts), otherwise the primary boss.
    private static EncounterModel? BossEncounterFor(IRunState runState, MapPoint mapPoint)
    {
        ActModel act = runState.Act;
        if (runState.Map.SecondBossMapPoint is { } second && mapPoint.coord == second.coord)
        {
            return act.SecondBossEncounter ?? act.BossEncounter;
        }
        return act.BossEncounter;
    }

    // Merchant node: approximate price ranges by rarity (prices vary ~±5-15%) laid out as an aligned
    // table so the Common/Uncommon/Rare columns line up, plus the live next card-removal cost (which
    // shares the Common column). Prices reflect price relics (Membership Card, The Courier) and
    // ascension, and are greyed when unaffordable.
    private static string BuildMerchant(MapPoint mapPoint, IRunState runState, Player player)
    {
        // Project the gold you'll have on arrival as a range; the worst case (min) drives the
        // price affordability colouring.
        int goldMin = player.Gold + GoldGainTo(runState, player, mapPoint, max: false);
        int goldMax = player.Gold + GoldGainTo(runState, player, mapPoint, max: true);
        float discount = MerchantDiscount(player);
        int removalBase = AscensionHelper.GetValueIfAscension(AscensionLevel.Inflation, 100, 75);
        int removalStep = AscensionHelper.GetValueIfAscension(AscensionLevel.Inflation, 50, 25);
        int removalCost = Mathf.RoundToInt((removalBase + removalStep * player.ExtraFields.CardShopRemovalsUsed) * discount);
        string goldRange = goldMin == goldMax ? goldMin.ToString() : $"{goldMin}-{goldMax}";
        return MerchantPriceTable(discount, goldMin, removalCost) + "\n\n" + Loc("EXPECTED_GOLD", ("Amount", Gold(goldRange)));
    }

    // A 4-column BBCode table (row label + Common/Uncommon/Rare) — the description label renders
    // BBCode, and a table keeps the price columns aligned for skimming where a "/"-joined line
    // can't. Card removal sits in the Common column.
    private static string MerchantPriceTable(float discount, int gold, int removalCost)
    {
        // Trailing spaces pad the gaps between columns (table cells otherwise butt together).
        static string Row(string label, string common, string uncommon, string rare) =>
            $"[cell]{label}   [/cell][cell]{common}   [/cell][cell]{uncommon}   [/cell][cell]{rare}[/cell]";
        return "[table=4]"
            + Row(string.Empty, Loc("MERCHANT_COMMON"), Loc("MERCHANT_UNCOMMON"), Loc("MERCHANT_RARE"))
            + Row(Loc("MERCHANT_CARDS"), PriceCell(50, 0.05f, discount, gold), PriceCell(75, 0.05f, discount, gold), PriceCell(150, 0.05f, discount, gold))
            + Row(Loc("MERCHANT_RELICS"), PriceCell(175, 0.15f, discount, gold), PriceCell(225, 0.15f, discount, gold), PriceCell(275, 0.15f, discount, gold))
            + Row(Loc("MERCHANT_POTIONS"), PriceCell(50, 0.05f, discount, gold), PriceCell(75, 0.05f, discount, gold), PriceCell(100, 0.05f, discount, gold))
            + Row(Loc("MERCHANT_CARD_REMOVAL"), AffordableValue(removalCost, gold), string.Empty, string.Empty)
            + "[/table]";
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

    // Every node forward-reachable from the current node (following Children), including the current
    // node itself; empty (with current set to null) when there's no current node. `current` is the
    // live node, or the map's starting node before the first move.
    private static HashSet<MapPoint> ForwardReachable(IRunState runState, out MapPoint? current)
    {
        current = runState.CurrentMapPoint ?? runState.Map?.StartingMapPoint;
        HashSet<MapPoint> reachable = new();
        if (current == null)
        {
            return reachable;
        }
        reachable.Add(current);
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
        return reachable;
    }

    // The min (wantMax=false) or max (wantMax=true), over every forward route from `current` to
    // `target`, of the summed `valueAt` over the route's nodes. `current` contributes `currentValue`
    // instead of valueAt(current). `reachable` is the forward set (must contain both endpoints).
    // Memoized over a single call; the in-progress sentinel guards against unexpected cycles.
    private static int RouteExtremum(MapPoint current, MapPoint target, HashSet<MapPoint> reachable,
        Func<MapPoint, int> valueAt, bool wantMax, int currentValue)
    {
        Dictionary<MapPoint, int> memo = new();
        int Best(MapPoint node)
        {
            if (node == current)
            {
                return currentValue;
            }
            if (memo.TryGetValue(node, out int cached))
            {
                return cached;
            }
            memo[node] = wantMax ? int.MinValue : int.MaxValue; // cycle guard
            int best = wantMax ? int.MinValue : int.MaxValue;
            bool any = false;
            foreach (MapPoint parent in node.parents)
            {
                if (reachable.Contains(parent))
                {
                    any = true;
                    int parentValue = Best(parent);
                    best = wantMax ? Math.Max(best, parentValue) : Math.Min(best, parentValue);
                }
            }
            return memo[node] = (any ? best : 0) + valueAt(node);
        }
        return Best(target);
    }

    // The gold the player gains before reaching this shop, as one end of a min/max range over every
    // route from the current node. Counts Monster/Elite combat gold and chest gold (all run through
    // the gold relics); chests are assumed to pay out unless Silver Crucible can leave one empty (min
    // case only). "?" and event gold isn't counted (it varies), keeping the range a safe envelope.
    // The current node is excluded (its reward is already in player.Gold) unless its combat is still
    // in progress, in which case its pending reward is counted. `max` picks the best route + high
    // rolls; otherwise the guaranteed worst route + low rolls.
    private static int GoldGainTo(IRunState runState, Player player, MapPoint target, bool max)
    {
        HashSet<MapPoint> forward = ForwardReachable(runState, out MapPoint? current);
        if (current == null || !forward.Contains(target))
        {
            return 0;
        }
        (int monsterMin, int monsterMax) = CombatGoldRange(RoomType.Monster);
        (int eliteMin, int eliteMax) = CombatGoldRange(RoomType.Elite);
        (int treasureMin, int treasureMax) = GoldRange(TreasureGoldMin, TreasureGoldMax);
        int combatBonus = CombatGoldBonus(runState, player);
        int monsterGold = (int)Hook.ModifyGoldGained(runState, null, max ? monsterMax : monsterMin, player, out _) + combatBonus;
        int eliteGold = (int)Hook.ModifyGoldGained(runState, null, max ? eliteMax : eliteMin, player, out _) + combatBonus;
        // Chests reliably pay out unless Silver Crucible can leave one empty (worst case only).
        int treasureGold = !max && player.GetRelic<SilverCrucible>() != null
            ? 0
            : (int)Hook.ModifyGoldGained(runState, null, max ? treasureMax : treasureMin, player, out _);
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
        // The current node's reward is normally already in player.Gold, so it contributes nothing.
        // But while its combat is still in progress that gold hasn't been paid out yet, so count the
        // pending combat reward (Maw Bank already paid on entering, so only the combat gold).
        int currentPendingGold = InProgressCombatType(runState) switch
        {
            MapPointType.Monster => monsterGold,
            MapPointType.Elite => eliteGold,
            _ => 0,
        };
        // Gold accumulated from the current node to `target` (each node's own gold included, the
        // current node's only its pending in-combat reward), over the best/worst route.
        return RouteExtremum(current, target, forward, GoldAt, max, currentPendingGold);
    }

    // Rest site: the full list of options available here, which depends on the player's relics,
    // quests and party. Built from the game's own RestSiteOption.Generate (Heal + Smith always,
    // Mend in multiplayer, and Dig/Lift/Cook/Kindle/Clone/Hatch added by their relics or cards via
    // the ModifyRestSiteOptions hook), so any option the game would offer here appears, each with a
    // concise value. Generate is side-effect free — the option constructors and every hook listener
    // only build/filter the list. Options that exist but can't be taken right now (Smith with
    // nothing upgradable, Cook with fewer than two removable cards) are greyed, like the rest screen.
    private static string BuildRest(Player player)
    {
        List<string> items = new();
        foreach (RestSiteOption option in RestSiteOption.Generate(player))
        {
            string name = option.Title.GetFormattedText();
            string detail = RestOptionDetail(option, player);
            string item = detail.Length > 0 ? LabelValue(name, detail) : name;
            if (!option.IsEnabled)
            {
                item = $"[color=#888888]{Loc("OPTION_UNAVAILABLE", ("Item", item))}[/color]";
            }
            items.Add(item);
        }
        return string.Join("\n", Section(Loc("OPTIONS"), items));
    }

    // A short, accurate value for each rest-site option. Unknown (e.g. future) options fall back to
    // just their name, so the list stays complete even if the game adds new ones.
    private static string RestOptionDetail(RestSiteOption option, Player player)
    {
        switch (option.OptionId)
        {
            case "HEAL":
                // GetHealAmount already folds in heal-amount relics (Regal Pillow). Relics that
                // grant extras on resting (Tiny Mailbox potions, Dream Catcher card reward, Stone
                // Humidifier max HP, Night Terrors, ...) all describe themselves through the same
                // ModifyExtraRestSiteHealText hook the real Heal option uses, so surface that too.
                string heal = Loc("REST_HEAL_DETAIL", ("Hp", (int)HealRestSiteOption.GetHealAmount(player)));
                IReadOnlyList<LocString> extra =
                    Hook.ModifyExtraRestSiteHealText(player.RunState, player, Array.Empty<LocString>());
                return extra.Count > 0
                    ? $"{heal}, {string.Join(", ", extra.Select(s => s.GetFormattedText()))}"
                    : heal;
            case "MEND":
                return Loc("REST_MEND");
            case "SMITH":
                int smithCount = option is SmithRestSiteOption smith ? smith.SmithCount : 1;
                return smithCount == 1 ? Loc("REST_SMITH_ONE") : Loc("REST_SMITH_MANY", ("Count", smithCount));
            case "COOK":
                return Loc("REST_COOK");
            case "DIG":
            case "HATCH":
                return Loc("REST_GAIN_RELIC");
            case "LIFT":
                int liftsLeft = player.GetRelic<Girya>() is { } girya ? Girya.maxLifts - girya.TimesLifted : 0;
                return Loc("REST_LIFT", ("Left", liftsLeft));
            case "CLONE":
                return Loc("REST_CLONE");
            case "KINDLE":
                return Loc("REST_KINDLE");
            default:
                return string.Empty;
        }
    }

    // The potion chance that was in effect at a past combat node, reconstructed for the history
    // tooltip. The potion pity is a single per-player value that starts at 0.4 and moves ±0.1 each
    // combat (down on a potion, up otherwise; PotionRewardOdds), so replaying every recorded combat
    // in order recovers the value before any node's roll. The pity isn't stored per node, but
    // whether a combat dropped a potion is — a PotionChoices entry on that node — which is exactly
    // the ±0.1 signal. To guard against anything our model doesn't capture (first-run tutorial
    // rewards that skip the roll, a future relic that overrides the pity, ...), we replay the whole
    // run and only trust the result if its end state reproduces the live pity value; otherwise we
    // show nothing rather than a wrong number. Returns null when `target` isn't a recorded combat
    // node or the checksum fails.
    public static (float chance, bool awarded)? HistoricalPotionInfo(IRunState runState, MapPointHistoryEntry target, ulong playerId)
    {
        if (!ColinsPatchKitConfig.ShowPotionChances)
        {
            return null;
        }
        Player? player = runState.Players.FirstOrDefault(p => p.NetId == playerId);
        if (player == null)
        {
            return null;
        }
        // The current, still-pending combat hasn't rolled yet, so it must not advance the pity.
        MapPointHistoryEntry? pending = IsCurrentRoomPotionPending(runState)
            ? runState.GetHistoryEntryFor(runState.MapLocation)
            : null;
        // That same pending combat has no outcome to report yet, so don't render a (necessarily
        // "No potion") historical line for it — its expected potion chance is shown by the inserted
        // expected-rewards block instead (RenderExpectedRewardsIntoHistory). The lone exception is
        // the final act's boss, which rolls no potion and gets no block, so its node simply shows no
        // potion info — correct, since there's nothing to report.
        if (pending != null && ReferenceEquals(target, pending))
        {
            return null;
        }

        const float step = 0.1f;
        const float eliteBonus = PotionRewardOdds.eliteBonus * 0.5f;
        float pity = 0.4f; // PotionRewardOdds base
        float? targetChance = null;
        foreach (IReadOnlyList<MapPointHistoryEntry> act in runState.MapPointHistory)
        {
            foreach (MapPointHistoryEntry entry in act)
            {
                RoomType? combat = CombatRoomType(entry);
                if (combat == null)
                {
                    continue;
                }
                if (entry == target)
                {
                    targetChance = pity + (combat == RoomType.Elite ? eliteBonus : 0f);
                }
                if (entry == pending)
                {
                    continue;
                }
                pity += HasPotionChoice(entry, playerId) ? -step : step;
            }
        }
        if (targetChance == null)
        {
            return null;
        }
        if (Mathf.Abs(pity - player.PlayerOdds.PotionReward.CurrentValue) > 0.001f)
        {
            return null; // our replay disagrees with the live pity — don't show a guess
        }
        return (targetChance.Value, HasPotionChoice(target, playerId));
    }

    // The type of the (first) combat room at a node, or null if it isn't a combat node. Monster,
    // Elite and Boss rooms all roll the potion pity (RewardsSet.RollForPotionAndAddTo).
    private static RoomType? CombatRoomType(MapPointHistoryEntry entry)
    {
        foreach (MapPointRoomHistoryEntry room in entry.Rooms)
        {
            if (room.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss)
            {
                return room.RoomType;
            }
        }
        return null;
    }

    // Whether a potion was rolled (offered) for the player at this node — recorded whether the
    // potion was taken or skipped, so its presence means the combat's potion roll succeeded.
    private static bool HasPotionChoice(MapPointHistoryEntry entry, ulong playerId)
    {
        PlayerMapPointHistoryEntry? stats = entry.PlayerStats.FirstOrDefault(s => s.PlayerId == playerId);
        return stats is { PotionChoices.Count: > 0 };
    }

    // Renders the historical potion outcome into a combat node's history tooltip: if a potion was
    // awarded there, tag its reward row with the chance ("... (40% chance)"); otherwise add a red
    // "No potion (40% chance)" line in the rewards area.
    public static void RenderHistoricalPotion(NMapPointHistoryHoverTip tip, float chance, bool awarded)
    {
        string chancePct = Pct(chance);
        if (awarded)
        {
            if (!TryTagPotionRow(tip, " " + Loc("POTION_CHANCE_SUFFIX", ("Chance", chancePct))))
            {
                // Defensive: the potion row wasn't found — show a plain line instead.
                AppendPotionLineToHistoryTip(tip, Loc("POTION_CHANCE", ("Chance", chancePct)));
            }
        }
        else
        {
            AppendRewardRow(tip, $"[img=top]{PotionIconPath}[/img][color=#{NoPotionColorHex}]{Loc("NO_POTION", ("Chance", chancePct))}[/color]");
        }
    }

    // Appends `suffix` to the potion line in the obtained/skipped reward rows (the row carrying the
    // potion icon). Returns false if no such row exists.
    private static bool TryTagPotionRow(NMapPointHistoryHoverTip tip, string suffix)
    {
        foreach (string container in new[] { "%RewardRows", "%SkippedRows" })
        {
            Control? rows = tip.GetNodeOrNull<Control>(container);
            if (rows == null)
            {
                continue;
            }
            foreach (RichTextLabel label in rows.GetChildren().OfType<RichTextLabel>())
            {
                if (!label.Text.Contains(PotionIconMarker))
                {
                    continue;
                }
                string[] lines = label.Text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(PotionIconMarker))
                    {
                        lines[i] += suffix;
                    }
                }
                label.Text = string.Join("\n", lines);
                return true;
            }
        }
        return false;
    }

    // Adds a line into the obtained-rewards rows (revealing the section if needed); falls back to the
    // combat-stats label.
    private static void AppendRewardRow(NMapPointHistoryHoverTip tip, string text)
    {
        RichTextLabel? row = tip.GetNodeOrNull<Control>("%RewardRows")?.GetChildren().OfType<RichTextLabel>().FirstOrDefault();
        if (row == null)
        {
            AppendPotionLineToHistoryTip(tip, text);
            return;
        }
        if (tip.GetNodeOrNull<Control>("%RewardStats") is { } container)
        {
            container.Visible = true;
        }
        row.Text = string.IsNullOrEmpty(row.Text) ? $"\t{text}" : $"{row.Text}\n\t{text}";
    }

    // Appends a plain line to the history tooltip's combat-stats label (falling back to the player-
    // stats label), making it visible. Used as a defensive fallback by the potion renderer.
    public static void AppendPotionLineToHistoryTip(NMapPointHistoryHoverTip tip, string potionLine)
    {
        RichTextLabel? label = tip.GetNodeOrNull<RichTextLabel>("%CardStats")
            ?? tip.GetNodeOrNull<RichTextLabel>("%PlayerStats");
        if (label == null)
        {
            return;
        }
        label.Text = string.IsNullOrEmpty(label.Text) ? potionLine : label.Text + "\n" + potionLine;
        label.Visible = true;
    }

    // Reachable iff there's a forward path (via Children) from the current node to the target.
    // At the start of an act (no current node) or with a free-travel relic, everything's reachable.
    private static bool IsReachable(IRunState runState, MapPoint target)
    {
        // At the start of an act (no current node) or with a free-travel relic, everything's reachable.
        if (runState.CurrentMapPoint == null || Hook.ShouldAllowFreeTravel(runState))
        {
            return true;
        }
        return ForwardReachable(runState, out _).Contains(target);
    }

    // The act's elite encounters minus the ones already fought this act. The act's generated elite
    // sequence cycles once exhausted (RoomSet.NextEliteEncounter indexes by visited % count), so
    // EncounterNames re-shows the whole pool when every elite has been fought — e.g. after three
    // elites a reachable fourth can be any of them again.
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
            body.AddRange(Section(Loc("EASY_POOL"), easy));
        }
        if (showHard)
        {
            body.AddRange(Section(Loc("HARD_POOL"), hard));
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
        HashSet<MapPoint> reachable = ForwardReachable(runState, out MapPoint? current);
        if (current == null || !reachable.Contains(target))
        {
            return null;
        }
        // Definite Monster nodes count on both bounds; an unresolved "?" might become one (max only).
        static int MonstersMin(MapPoint node) => node.PointType == MapPointType.Monster ? 1 : 0;
        static int MonstersMax(MapPoint node) =>
            node.PointType is MapPointType.Monster or MapPointType.Unknown ? 1 : 0;
        // Target inclusive, current exclusive (RouteExtremum gives current the value 0).
        return (RouteExtremum(current, target, reachable, MonstersMin, wantMax: false, 0),
            RouteExtremum(current, target, reachable, MonstersMax, wantMax: true, 0));
    }

    // The combat card-reward line. Lasting Candy appends an extra Power-card option to the reward
    // every other combat (when its CombatsSeen counter lands even), so when it will fire for this
    // node's combat we show "+ Power". If the parity isn't fixed — the number of combats you clear
    // before this node varies by route or includes an unresolved "?" — the Power gets an asterisk
    // (it may or may not fire).
    private static string CardRewardLine(IRunState runState, Player player, MapPoint target)
    {
        LastingCandy? candy = player.GetRelic<LastingCandy>();
        if (candy == null)
        {
            return Loc("CARD_REWARD");
        }
        if (CombatsBeforeBounds(runState, target) is not { } before || before.min != before.max)
        {
            return Loc("CARD_REWARD_POWER") + "*";
        }
        // Lasting Candy fires when the combat's 1-based index (CombatsSeen after it) is even.
        bool fires = (candy.CombatsSeen + before.min + 1) % 2 == 0;
        return fires ? Loc("CARD_REWARD_POWER") : Loc("CARD_REWARD");
    }

    // (min, max) number of combats (Monster / Elite / Boss; "?" counts 0..1) strictly before
    // `target` on any forward route. (0, 0) when standing on it; null if it isn't forward-reachable.
    private static (int min, int max)? CombatsBeforeBounds(IRunState runState, MapPoint target)
    {
        HashSet<MapPoint> reachable = ForwardReachable(runState, out MapPoint? current);
        if (current == null)
        {
            return null;
        }
        if (target == current)
        {
            return (0, 0); // standing on this combat — nothing precedes it
        }
        if (!reachable.Contains(target))
        {
            return null;
        }
        // Monster/Elite/Boss are always a combat; an unresolved "?" might become one (max case only).
        static int CombatsMin(MapPoint node) =>
            node.PointType is MapPointType.Monster or MapPointType.Elite or MapPointType.Boss ? 1 : 0;
        static int CombatsMax(MapPoint node) =>
            CombatsMin(node) == 1 || node.PointType == MapPointType.Unknown ? 1 : 0;
        // Route bound includes the target's own combat, so subtract it to count only what precedes it.
        return (RouteExtremum(current, target, reachable, CombatsMin, wantMax: false, 0) - CombatsMin(target),
            RouteExtremum(current, target, reachable, CombatsMax, wantMax: true, 0) - CombatsMax(target));
    }

    // Names in `pool` minus those already fought (`exclude`). The act's encounter lists cycle once
    // every entry has been used, so when excluding the fought ones empties the pool we fall back to
    // the full pool — the next combat starts repeating the rotation from the top.
    private static List<string> EncounterNames(IEnumerable<EncounterModel> pool, HashSet<string> exclude)
    {
        List<EncounterModel> all = pool.Where(e => e != null).ToList();
        List<EncounterModel> remaining = all.Where(e => !exclude.Contains(e.Id.Entry)).ToList();
        return (remaining.Count > 0 ? remaining : all)
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
        return NodesStrictlyBetween(runState, target)
            .Any(node => node.PointType is MapPointType.Monster or MapPointType.Elite or MapPointType.Unknown);
    }

    // Nodes that lie strictly between the current node and `target` on some forward route (both the
    // current node and the target excluded). Empty when the target isn't forward-reachable.
    private static HashSet<MapPoint> NodesStrictlyBetween(IRunState runState, MapPoint target)
    {
        HashSet<MapPoint> between = new();
        HashSet<MapPoint> forward = ForwardReachable(runState, out MapPoint? current);
        if (current == null)
        {
            return between;
        }
        // Walk back from the target through parents reachable from the current node; every node
        // reached (other than current itself) lies strictly between them on some route.
        Queue<MapPoint> back = new();
        back.Enqueue(target);
        while (back.Count > 0)
        {
            foreach (MapPoint parent in back.Dequeue().parents)
            {
                if (forward.Contains(parent) && parent != current && between.Add(parent))
                {
                    back.Enqueue(parent);
                }
            }
        }
        return between;
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
        // Boss combats roll the pity too (CombatRoomType counts them), and the roll hasn't happened
        // yet mid-fight, so the in-progress boss node must be treated as pending — otherwise the
        // historical-pity replay advances past a boss the live pity hasn't moved through, the
        // checksum mismatches, and every node's potion line silently blanks out during a boss fight.
        return combat.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss;
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
        return Loc("POTION_CHANCE", ("Chance", Pct(chance))) + (showAsterisk ? "*" : "");
    }

    // The potion reward line for a combat node. With the potion-chances toggle off we still note the
    // potion as a possible reward, just without a number — "Potion" when a relic guarantees it
    // (White Beast Statue), "Potion (possibly)" otherwise.
    private static string PotionRewardLine(IRunState runState, Player player, RoomType roomType, bool dependsOnUnresolved)
    {
        if (!ColinsPatchKitConfig.ShowPotionChances)
        {
            return Hook.ShouldForcePotionReward(runState, player, roomType) ? Loc("POTION") : Loc("POTION_POSSIBLY");
        }
        float chance = PotionChance(runState, player, roomType, out bool forced);
        return PotionLine(chance, dependsOnUnresolved && !forced);
    }

    // "Relic" for a single relic, "N Relics" when more than one can drop (Black Star at elites,
    // Wongo's Mystery Ticket on any combat). An asterisk marks an uncertain count.
    private static string RelicLine(int count, bool uncertain = false)
    {
        string text = count == 1 ? Loc("RELIC_ONE") : Loc("RELIC_MANY", ("Count", count));
        return uncertain ? text + "*" : text;
    }

    // The relic-reward line for a combat node, or null when it grants none. Base is 1 at elites (0
    // at monster/boss); Black Star adds 1 at elites; Wongo's Mystery Ticket adds its relics once, on
    // the first combat after five fights.
    private static string? CombatRelicLine(IRunState runState, Player player, MapPoint target, bool isElite)
    {
        int relics = isElite ? 1 : 0;
        if (isElite && player.GetRelic<BlackStar>() != null)
        {
            relics++;
        }
        (int wongos, bool uncertain) = WongosRelics(runState, player, target);
        relics += wongos;
        return relics > 0 ? RelicLine(relics, uncertain) : null;
    }

    // Relics Wongo's Mystery Ticket would add at `target` (and whether that's uncertain). It fires
    // once, on the combat that reaches its five-fight threshold — i.e. the combat with
    // max(1, 5 - CombatsFinished) - 1 combats before it. Uncertain when the route's combat count
    // straddles that point (so this node may or may not be the one).
    private static (int relics, bool uncertain) WongosRelics(IRunState runState, Player player, MapPoint target)
    {
        WongosMysteryTicket? wongos = player.GetRelic<WongosMysteryTicket>();
        if (wongos == null || wongos.GaveRelic)
        {
            return (0, false);
        }
        if (CombatsBeforeBounds(runState, target) is not { } before)
        {
            return (0, false);
        }
        int trigger = Math.Max(1, WongosMysteryTicket.combatsToActivate - wongos.CombatsFinished) - 1;
        if (before.min == before.max)
        {
            return before.min == trigger ? (WongosMysteryTicket.relicCount, false) : (0, false);
        }
        return before.min <= trigger && trigger <= before.max
            ? (WongosMysteryTicket.relicCount, true)
            : (0, false);
    }

    // Treasure chest gold (rolled from PlayerRng.Rewards, not an EncounterModel reward).
    private const int TreasureGoldMin = 42;
    private const int TreasureGoldMax = 52;

    // Base combat-reward gold range (pre-Poverty, pre-relic) per RoomType, mirroring
    // EncounterModel.MinGoldReward/MaxGoldReward's per-RoomType defaults — the single source for the
    // numbers GoldText/GoldRange then run through Poverty and the gold relics. (An encounter that
    // overrides its gold reward would diverge, but the stock monster/elite/boss encounters use these.)
    private static (int min, int max) CombatGoldBase(RoomType roomType) => roomType switch
    {
        RoomType.Monster => (10, 20),
        RoomType.Elite => (35, 45),
        RoomType.Boss => (100, 100),
        _ => (0, 0),
    };

    // CombatGoldBase with the Poverty multiplier applied (the form GoldGainTo needs before the relics).
    private static (int min, int max) CombatGoldRange(RoomType roomType)
    {
        (int min, int max) = CombatGoldBase(roomType);
        return GoldRange(min, max);
    }

    // Gold reward line for a combat node, from its RoomType's base range.
    private static string GoldText(IRunState runState, Player player, RoomType roomType, int flatBonus = 0)
    {
        (int min, int max) = CombatGoldBase(roomType);
        return GoldText(runState, player, min, max, flatBonus);
    }

    // Gold range with Poverty applied, run through the gold relics (Bowler Hat, Ectoplasm, ...),
    // plus a flat combat-reward bonus (Amethyst Aubergine) passed in for combat nodes.
    private static string GoldText(IRunState runState, Player player, int baseMin, int baseMax, int flatBonus = 0)
    {
        (int min, int max) = GoldRange(baseMin, baseMax);
        min = (int)Hook.ModifyGoldGained(runState, null, min, player, out _) + flatBonus;
        max = (int)Hook.ModifyGoldGained(runState, null, max, player, out _) + flatBonus;
        return Loc("GOLD_REWARD", ("Amount", Gold(min == max ? min.ToString() : $"{min}-{max}")));
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
        if (_hoveredExpandable == null || _hoveredRunState == null || _hoveredScreen == null || _hoveredPlayer == null)
        {
            return;
        }
        bool held = IsModifierHeld();
        if (held == _expandedShown)
        {
            return;
        }
        _expandedShown = held;
        ShowExpectedRewardsTooltip(_hoveredExpandable, _hoveredRunState, _hoveredScreen, _hoveredPlayer);
    }

    public static void ClearHoverIf(NMapPoint point)
    {
        if (_hoveredExpandable == point)
        {
            _hoveredExpandable = null;
            _hoveredRunState = null;
            _hoveredScreen = null;
            _hoveredPlayer = null;
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

    // This patch's own display strings live in the game's `map` loc table under a COLINSPATCHKIT-
    // MAPINFO- prefix. The mod's localization/<lang>/map.json files are merged into that table by
    // the game's mod loader (ModManager.GetModdedLocTables), so these read like native map strings
    // and translate alongside the game. The English source is localization/eng/map.json; every
    // shipped language file must carry the full key set (a missing key has no eng fallback once a
    // non-eng locale is active). Reusing LocTable ("map") keeps these next to the legend keys the
    // tooltip already pulls from the same table.
    private const string CpkLocPrefix = "COLINSPATCHKIT-MAPINFO-";

    private static string Loc(string key)
    {
        return new LocString(LocTable, CpkLocPrefix + key).GetFormattedText();
    }

    // SmartFormat lookup with {Name} placeholders filled from the passed (name, value) pairs.
    private static string Loc(string key, params (string name, object value)[] vars)
    {
        LocString locString = new(LocTable, CpkLocPrefix + key);
        foreach ((string name, object value) in vars)
        {
            locString.AddObj(name, value);
        }
        return locString.GetFormattedText();
    }

    // "Label: Value" line (e.g. "Monster: 30%", "Boss: Waterfall Giant"), routed through a loc key
    // so locales that punctuate differently (French's space-before-colon, etc.) can adjust it.
    private static string LabelValue(string label, string value)
    {
        return Loc("LABEL_VALUE", ("Label", label), ("Value", value));
    }

    // Localized label for a first-run forced-outcome token ("Event"/"Monster" — kept as stable
    // strings for the logic in ForcedFirstRunOutcomes, mapped to display text only here).
    private static string ForcedOutcomeLabel(string token)
    {
        return Loc(token == "Event" ? "ROOM_EVENT" : "ROOM_MONSTER");
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
