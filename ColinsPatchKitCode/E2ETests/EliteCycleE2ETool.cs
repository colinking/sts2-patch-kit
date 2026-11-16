using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using ColinsPatchKit.ColinsPatchKitCode.Patches;
using GameDevConsole = MegaCrit.Sts2.Core.DevConsole.DevConsole;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Automated assertion harness for the elite-node tooltip's grab-bag deductions
// (MapNodeInfoTooltipPatch.GetPossibleEliteNames). Starts a seeded custom run (default seed
// DBJKEQ8KBEYK, Overgrowth/Hive/Glory at Ascension 2 — a run whose act 3 had four elites), jumps
// to act 3, and walks the route with the most elites, winning every fight via `win`. Before each
// elite it stands on the elite's parent node (so the elite is guaranteed the next one) and checks
// the possible-elites list against the cycle rules, logging `elitecycle-e2e: PASS <assertion>`:
//   - 1st elite: the whole pool; 2nd: excludes the 1st; 3rd: only the one left in the cycle;
//   - 4th (first of a new cycle): excludes the 3rd, which the game's no-repeat draw rules out.
// Each elite actually fought must be one the tooltip listed. For the 4th it also hovers the real
// map node with Cmd held and checks the rendered tooltip, saving /tmp/elite_cycle_4th.png.
// Switches back to the original profile and quits:
//
//   "Slay the Spire 2" --elitecycle-e2e=<profile> [--elitecycle-seed=<seed>]
//
// The argument is a scratch profile id; it runs `unlock all` there. Any run already in progress
// on that profile is treated as disposable test state and gets abandoned — never pass a profile
// holding a real run.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class EliteCycleE2EPatch
{
    private const string Tag = "elitecycle-e2e";
    private static bool _started;
    private static int _originalProfileId = -1;

    public static void Postfix(NMainMenu __instance)
    {
        if (!CommandLineHelper.TryGetValue(Tag, out string? profileArg)
            || string.IsNullOrEmpty(profileArg) || !int.TryParse(profileArg, out int targetProfile))
        {
            return;
        }
        SceneTree tree = __instance.GetTree();
        if (!E2EHelpers.EnsureProfile(tree, targetProfile, ref _originalProfileId, Tag))
        {
            return;
        }
        if (_started)
        {
            return;
        }
        _started = true;
        tree.CreateTimer(1.0).Timeout += () => TaskHelper.RunSafely(Run(tree));
    }

    private static async Task Run(SceneTree tree)
    {
        try
        {
            await RunInternal(tree);
            MainFile.Logger.Info($"{Tag}: complete");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"{Tag}: failed: {e}");
            await E2EHelpers.Shot(tree, "/tmp/elite_cycle_fail.png", Tag);
        }
        finally
        {
            await E2EHelpers.RestoreProfile(_originalProfileId, Tag);
            await Task.Delay(500);
            tree.Quit();
        }
    }

    private static async Task RunInternal(SceneTree tree)
    {
        CancellationToken ct = CancellationToken.None;
        string seed = CommandLineHelper.TryGetValue("elitecycle-seed", out string? s) && !string.IsNullOrEmpty(s)
            ? s! : "DBJKEQ8KBEYK";
        await E2EHelpers.PrepareMainMenu(tree, Tag, ct);
        CmdResult unlock = Console().ProcessCommand("unlock all");
        MainFile.Logger.Info($"{Tag}: unlock all -> {unlock.success} {unlock.msg}");

        RunState runState = await StartSeededRun(seed, ct);
        ActModel act3 = runState.Acts[2];
        List<string> sequence = act3.GetRoomsDebug().eliteEncounters.Select(e => e.Id.Entry).ToList();
        MainFile.Logger.Info($"{Tag}: act 3 ({act3.Id.Entry}) generated elite sequence: {string.Join(", ", sequence)}");

        CmdResult act = Console().ProcessCommand("act 3");
        MainFile.Logger.Info($"{Tag}: act 3 -> {act.success} {act.msg}");
        await WaitHelper.Until(() => runState.CurrentActIndex == 2 && runState.Map?.StartingMapPoint != null,
            ct, TimeSpan.FromSeconds(30), "did not reach act 3");
        await Task.Delay(2500);
        // The act jump leaves you on the map before the act's starting (Ancient) node.
        if (runState.CurrentMapPoint == null)
        {
            await Enter(runState, runState.Map!.StartingMapPoint, ct);
        }

        List<MapPoint> route = MostElitesRoute(runState.CurrentMapPoint!);
        int routeElites = route.Count(p => p.PointType == MapPointType.Elite);
        MainFile.Logger.Info($"{Tag}: route ({routeElites} elites): "
            + string.Join(" > ", route.Select(p => $"{p.PointType}@{p.coord}")));
        Check(routeElites >= 4, $"act 3 has a route with at least four elites ({routeElites})");

        List<string> pool = act3.AllEliteEncounters.Select(e => e.Id.Entry).Distinct().ToList();
        List<string> fought = new();
        foreach (MapPoint next in route)
        {
            await FinishCurrentRoom(runState, ct);
            if (next.PointType == MapPointType.Elite)
            {
                List<string> predicted = PredictedEliteIds(runState, next, act3);
                MainFile.Logger.Info($"{Tag}: elite #{fought.Count + 1} at {next.coord} predicted: {string.Join(", ", predicted)}");
                CheckPrediction(fought, pool, predicted);
                if (fought.Count == 2)
                {
                    await CheckBeforeThird(tree, runState, route, next, act3, pool, fought, predicted);
                }
                if (fought.Count == 3)
                {
                    await CheckRenderedTooltip(tree, next, act3, predicted, new[] { fought[2] },
                        "4th elite (from its parent)", "/tmp/elite_cycle_4th.png");
                }
                await Enter(runState, next, ct);
                string actual = (runState.CurrentRoom as CombatRoom)?.Encounter.Id.Entry ?? "?";
                Check(predicted.Contains(actual), $"elite #{fought.Count + 1} is {actual}, which the tooltip listed");
                fought.Add(actual);
                if (fought.Count == 4)
                {
                    Check(actual != fought[2], $"the game's 4th elite ({actual}) is not the 3rd ({fought[2]})");
                    return;
                }
            }
            else
            {
                await Enter(runState, next, ct);
            }
        }
        throw new InvalidOperationException("FAILED: the route ended before a 4th elite");
    }

    private static void CheckPrediction(List<string> fought, List<string> pool, List<string> predicted)
    {
        switch (fought.Count)
        {
            case 0:
                Check(predicted.Count == pool.Count, "1st elite: the whole pool is possible");
                break;
            case 1:
                Check(!predicted.Contains(fought[0]) && predicted.Count == pool.Count - 1,
                    $"2nd elite: excludes the 1st ({fought[0]})");
                break;
            case 2:
                Check(predicted.Count == 1 && !fought.Contains(predicted[0]),
                    $"3rd elite: only the one left in the cycle ({string.Join(", ", predicted)})");
                break;
            case 3:
                Check(!predicted.Contains(fought[2]) && predicted.Count == pool.Count - 1,
                    $"4th elite: excludes the 3rd ({fought[2]}), the new cycle's no-repeat rule");
                break;
        }
    }

    // Standing on the 3rd elite's parent: the 3rd shows only the elite left in the cycle, and the
    // 4th (next elite on the route) shows the other two — provided every route to the 4th passes
    // exactly one elite first, so it's guaranteed to open the new cycle. Otherwise (a route could
    // skip the 3rd, or pass two) the 4th could be another position, and the whole pool is right.
    private static async Task CheckBeforeThird(SceneTree tree, RunState runState, List<MapPoint> route, MapPoint third,
        ActModel act, List<string> pool, List<string> fought, List<string> thirdPredicted)
    {
        await CheckRenderedTooltip(tree, third, act, thirdPredicted, fought,
            "3rd elite (from its parent)", "/tmp/elite_cycle_3rd_from_parent.png");

        MapPoint fourth = route.Skip(route.IndexOf(third) + 1).First(p => p.PointType == MapPointType.Elite);
        (int min, int max) between = ElitesBetween(runState.CurrentMapPoint!, fourth);
        MainFile.Logger.Info($"{Tag}: 4th elite at {fourth.coord}: elites strictly before it on routes from "
            + $"{runState.CurrentMapPoint!.coord}: min {between.min}, max {between.max}");
        List<string> fourthPredicted = PredictedEliteIds(runState, fourth, act);
        MainFile.Logger.Info($"{Tag}: 4th elite from the 3rd's parent predicted: {string.Join(", ", fourthPredicted)}");
        if (between is (1, 1))
        {
            List<string> others = pool.Where(id => id != thirdPredicted.Single()).ToList();
            Check(fourthPredicted.Count == others.Count && others.All(fourthPredicted.Contains),
                $"4th elite (two ahead): the other two ({string.Join(", ", others)})");
            await CheckRenderedTooltip(tree, fourth, act, others, thirdPredicted,
                "4th elite (from the 3rd's parent)", "/tmp/elite_cycle_4th_from_3rd_parent.png");
        }
        else
        {
            Check(fourthPredicted.Count == pool.Count,
                "4th elite (two ahead, not guaranteed to open the new cycle): the whole pool");
            await CheckRenderedTooltip(tree, fourth, act, pool, Array.Empty<string>(),
                "4th elite (from the 3rd's parent)", "/tmp/elite_cycle_4th_from_3rd_parent.png");
        }
    }

    // Min/max number of Elite nodes strictly between `from` and `to` over every forward route.
    private static (int min, int max) ElitesBetween(MapPoint from, MapPoint to)
    {
        int min = int.MaxValue, max = -1;
        void Walk(MapPoint p, int count)
        {
            foreach (MapPoint child in p.Children)
            {
                if (child == to)
                {
                    min = Math.Min(min, count);
                    max = Math.Max(max, count);
                }
                else if (child.coord.row < to.coord.row)
                {
                    Walk(child, count + (child.PointType == MapPointType.Elite ? 1 : 0));
                }
            }
        }
        Walk(from, 0);
        return (min, max);
    }

    // Hovers the real NMapPoint with the expand modifier held and checks the rendered list.
    private static async Task CheckRenderedTooltip(SceneTree tree, MapPoint target, ActModel act,
        IEnumerable<string> expectedIds, IEnumerable<string> excludedIds, string label, string shot)
    {
        NMapScreen screen = NMapScreen.Instance ?? throw new InvalidOperationException("FAILED: no map screen");
        screen.Open();
        await Task.Delay(1500);
        Dictionary<MapCoord, NMapPoint> points = (Dictionary<MapCoord, NMapPoint>)
            AccessTools.Field(typeof(NMapScreen), "_mapPointDictionary").GetValue(screen)!;
        NMapPoint node = points[target.coord];
        Key modifier = OS.GetName() == "macOS" ? Key.Meta : Key.Ctrl;
        Input.ParseInputEvent(new InputEventKey { Keycode = modifier, PhysicalKeycode = modifier, Pressed = true });
        await Task.Delay(200);
        AccessTools.Method(typeof(NMapPoint), "OnFocus").Invoke(node, null);
        await Task.Delay(600);
        NHoverTipSet? set = ActiveTip(node);
        string text = set == null ? "" : TipText(set);
        MainFile.Logger.Info($"{Tag}: rendered {label} tooltip at {target.coord}: {text.Replace("\n", " | ")}");
        await E2EHelpers.Shot(tree, shot, Tag);
        AccessTools.Method(typeof(NMapPoint), "OnUnfocus").Invoke(node, null);
        Input.ParseInputEvent(new InputEventKey { Keycode = modifier, PhysicalKeycode = modifier, Pressed = false });
        await Task.Delay(200);

        List<string> lines = text.Split('\n').Select(l => l.Trim()).ToList();
        int header = lines.FindIndex(l => l.Contains("elites", StringComparison.OrdinalIgnoreCase));
        Check(header >= 0, $"{label}: tooltip has a possible-elites section");
        List<string> listed = lines.Skip(header + 1).Where(l => act.AllEliteEncounters.Any(e => e.Title.GetFormattedText() == l)).ToList();
        List<string> expected = expectedIds.Select(id => Title(act, id)).OrderBy(t => t).ToList();
        Check(listed.OrderBy(t => t).SequenceEqual(expected),
            $"{label}: rendered list is exactly [{string.Join(", ", expected)}] (got [{string.Join(", ", listed)}])");
        foreach (string id in excludedIds)
        {
            Check(!listed.Contains(Title(act, id)), $"{label}: rendered tooltip omits {Title(act, id)}");
        }
        screen.Close();
        await Task.Delay(500);
    }

    private static List<string> PredictedEliteIds(IRunState runState, MapPoint target, ActModel act)
    {
        MethodInfo method = AccessTools.Method(typeof(MapNodeInfoTooltipPatch), "GetPossibleEliteNames")
            ?? throw new InvalidOperationException("FAILED: no GetPossibleEliteNames");
        List<string> names = (List<string>)method.Invoke(null, new object[] { runState, target })!;
        return act.AllEliteEncounters.Where(e => names.Contains(e.Title.GetFormattedText()))
            .Select(e => e.Id.Entry).Distinct().ToList();
    }

    private static string Title(ActModel act, string id) =>
        act.AllEliteEncounters.First(e => e.Id.Entry == id).Title.GetFormattedText();

    // Starts the run directly (as the custom-run screen would) so the seed and ascension are exact.
    private static async Task<RunState> StartSeededRun(string seed, CancellationToken ct)
    {
        CharacterModel character = ModelDb.AllCharacters.FirstOrDefault(c => c.Id.Entry == "FOOL")
            ?? ModelDb.AllCharacters.First();
        List<ActModel> acts = new[] { "OVERGROWTH", "HIVE", "GLORY" }
            .Select(id => ModelDb.Acts.First(a => a.Id.Entry == id)).ToList();
        Player player = Player.CreateForNewRun(character, new UnlockState(SaveManager.Instance.Progress), 1uL);
        MainFile.Logger.Info($"{Tag}: starting custom run seed={seed} character={character.Id.Entry} ascension=2");
        _ = TaskHelper.RunSafely(NGame.Instance!.StartNewSingleplayerRun(player, shouldSave: true, acts,
            Array.Empty<ModifierModel>(), seed, GameMode.Standard, isCustomGame: true, ascensionLevel: 2));
        await WaitHelper.Until(() => RunManager.Instance.DebugOnlyGetState() != null, ct, TimeSpan.FromSeconds(60), "run did not start");
        RunState runState = RunManager.Instance.DebugOnlyGetState()!;
        await WaitHelper.Until(() => runState.CurrentRoom != null && runState.CurrentRoom.RoomType != RoomType.Unassigned,
            ct, TimeSpan.FromSeconds(30), "no room assigned");
        await Task.Delay(2500);
        return runState;
    }

    // The forward route from `start` to the act's last row with the most Elite nodes (ties: the
    // fewest "?" nodes, which are the least predictable rooms to walk through).
    private static List<MapPoint> MostElitesRoute(MapPoint start)
    {
        Dictionary<MapPoint, (int elites, int unknowns, List<MapPoint> route)> memo = new();
        (int, int, List<MapPoint>) Best(MapPoint p)
        {
            if (memo.TryGetValue(p, out var hit))
            {
                return hit;
            }
            (int e, int u, List<MapPoint> r) best = (0, 0, new List<MapPoint>());
            bool any = false;
            foreach (MapPoint child in p.Children)
            {
                (int e, int u, List<MapPoint> r) = Best(child);
                e += child.PointType == MapPointType.Elite ? 1 : 0;
                u += child.PointType == MapPointType.Unknown ? 1 : 0;
                if (!any || e > best.e || (e == best.e && u < best.u))
                {
                    best = (e, u, new List<MapPoint> { child }.Concat(r).ToList());
                    any = true;
                }
            }
            memo[p] = best;
            return best;
        }
        return Best(start).Item3;
    }

    // Wins a fight in progress; anything else (event, shop, rest, chest, rewards) is left as is —
    // EnterMapCoord clears screens and exits the current room itself.
    private static async Task FinishCurrentRoom(RunState runState, CancellationToken ct)
    {
        if (CombatManager.Instance?.IsInProgress == true)
        {
            CmdResult win = Console().ProcessCommand("win");
            MainFile.Logger.Info($"{Tag}: win -> {win.success} {win.msg}");
            await WaitHelper.Until(() => CombatManager.Instance?.IsInProgress != true, ct, TimeSpan.FromSeconds(30), "combat did not end");
            await Task.Delay(2000);
        }
    }

    private static async Task Enter(RunState runState, MapPoint point, CancellationToken ct)
    {
        MainFile.Logger.Info($"{Tag}: entering {point.PointType} at {point.coord}");
        _ = TaskHelper.RunSafely(RunManager.Instance.EnterMapCoord(point.coord));
        await WaitHelper.Until(() => runState.CurrentMapPoint?.coord == point.coord && runState.CurrentRoom != null
                && runState.CurrentRoom.RoomType != RoomType.Unassigned,
            ct, TimeSpan.FromSeconds(30), $"did not enter {point.coord}");
        await Task.Delay(2500);
        MainFile.Logger.Info($"{Tag}: in {runState.CurrentRoom!.RoomType} at {point.coord}"
            + (runState.CurrentRoom is CombatRoom c ? $" ({c.Encounter.Id.Entry})" : ""));
    }

    private static GameDevConsole Console() => new(shouldAllowDebugCommands: true);

    private static NHoverTipSet? ActiveTip(Control owner)
    {
        Dictionary<Control, NHoverTipSet>? active =
            AccessTools.Field(typeof(NHoverTipSet), "_activeHoverTips")?.GetValue(null) as Dictionary<Control, NHoverTipSet>;
        return active != null && active.TryGetValue(owner, out NHoverTipSet? set) ? set : null;
    }

    private static string TipText(Node root)
    {
        IEnumerable<string> texts = AllNodes(root).Where(n => n is not CanvasItem c || c.IsVisibleInTree()).Select(n => n switch
        {
            RichTextLabel r => r.Text,
            Label l => l.Text,
            _ => null,
        }).Where(t => !string.IsNullOrWhiteSpace(t))!;
        return Regex.Replace(string.Join("\n", texts), @"\[[^\]]*\]", "");
    }

    private static IEnumerable<Node> AllNodes(Node root)
    {
        Stack<Node> stack = new();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Node node = stack.Pop();
            yield return node;
            foreach (Node child in node.GetChildren())
            {
                stack.Push(child);
            }
        }
    }

    private static void Check(bool ok, string description)
    {
        if (!ok)
        {
            throw new InvalidOperationException($"FAILED: {description}");
        }
        MainFile.Logger.Info($"{Tag}: PASS {description}");
    }
}
