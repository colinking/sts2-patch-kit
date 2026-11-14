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
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using ColinsPatchKit.ColinsPatchKitCode.Patches;
using GameDevConsole = MegaCrit.Sts2.Core.DevConsole.DevConsole;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Automated assertion harness for the Blitz map tooltips (MapNodeInfoTooltipPatch's Blitz rules,
// shown on Blitz's top-bar map by BlitzTopBarMapPointTooltipPatch). Beta-only (v0.113.0+): Blitz
// doesn't exist on the stable branch, so every Blitz type is reached by name. Starts a throwaway
// singleplayer Blitz run and, standing in act 1's Ancient room, hovers every top-bar room and
// checks its tooltip, logging a `blitzmap-e2e: PASS <assertion>` line per check:
//   - The current room (the Ancient) shows the room-history tooltip, not the legend blurb.
//   - First Monster (the act's weak fight): Common card reward, no potion; second Monster: potion.
//   - Shop: vanilla's Merchant description, untouched; Elite: 2 relics + Uncommon card reward, no potion;
//     Treasure: 2 relics; Rest site: options; Boss: Rare card reward + potion. No gold anywhere.
// Then walks into the first Monster room and checks the in-combat current-room tooltip (expected
// rewards inserted, no potion), wins the fight, and checks the real reward screen agrees (a card
// reward and no potion). Saves /tmp/blitz_map_*.png, switches back to the original profile, quits:
//
//   "Slay the Spire 2" --blitzmap-e2e=<profile>
//
// The argument is a scratch profile id. Any run already in progress on that profile is treated
// as disposable test state and gets abandoned — never pass a profile holding a real run.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class BlitzMapTooltipE2EPatch
{
    private const string Tag = "blitzmap-e2e";
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
            await E2EHelpers.Shot(tree, "/tmp/blitz_map_fail.png", Tag);
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
        Type pointType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.CommonUi.NBlitzTopBarMapPoint")
            ?? throw new InvalidOperationException("FAILED: no NBlitzTopBarMapPoint — this harness needs the Blitz (v0.113.0+) branch");
        RunState runState = await StartBlitzRun(tree, ct);
        Check(BranchCompat.IsBlitz(runState), $"the run is in Blitz mode (GameMode={runState.GameMode})");

        List<Control> points = FindTopBarPoints(tree, pointType);
        Check(points.Count >= 8, $"the top-bar map has the act's rooms ({points.Count} points)");
        foreach (Control p in points)
        {
            MainFile.Logger.Info($"{Tag}: top-bar room row {PointOf(p).coord.row}: {PointOf(p).PointType}");
        }

        string potion = MapLoc("POTION");
        string goldPrefix = MapLoc("GOLD_REWARD", ("Amount", "")).Trim();
        string common = CardRarity.Common.ToLocString().GetFormattedText();
        string uncommon = CardRarity.Uncommon.ToLocString().GetFormattedText();
        string rare = CardRarity.Rare.ToLocString().GetFormattedText();
        string commonReward = MapLoc("CARD_REWARD_RARITY", ("Rarity", common));
        string uncommonReward = MapLoc("CARD_REWARD_RARITY", ("Rarity", uncommon));
        string rareReward = MapLoc("CARD_REWARD_RARITY", ("Rarity", rare));
        string twoRelics = MapLoc("RELIC_MANY", ("Count", 2));

        // --- Standing in the Ancient room: hover every room in the strip. ---
        Control ancient = points.First(p => PointOf(p).PointType == MapPointType.Ancient);
        (string ancientText, bool ancientIsHistory) = await Hover(ancient, pointType);
        Check(ancientIsHistory, "the current room (Ancient) shows the room-history tooltip");

        List<Control> monsters = points.Where(p => PointOf(p).PointType == MapPointType.Monster)
            .OrderBy(p => PointOf(p).coord.row).ToList();
        Check(monsters.Count == 2, "the act has two Monster rooms");

        string weak = (await Hover(monsters[0], pointType, "/tmp/blitz_map_monster1.png")).text;
        Check(weak.Contains(commonReward), $"first Monster lists '{commonReward}'");
        Check(!weak.Contains(potion), "first Monster (the weak fight) lists no potion");
        Check(!weak.Contains(goldPrefix), "first Monster lists no gold");

        string normal = (await Hover(monsters[1], pointType)).text;
        Check(normal.Contains(potion) && normal.Contains(commonReward), "second Monster lists a Common card reward and a potion");

        string shop = (await Hover(Single(points, MapPointType.Shop), pointType, "/tmp/blitz_map_shop.png")).text;
        string vanillaShop = new LocString("map", "LEGEND_MERCHANT.hoverTip.description").GetFormattedText();
        Check(shop.Contains(Regex.Replace(vanillaShop, @"\[[^\]]*\]", "")) && !shop.Contains(MapLoc("EXPECTED_REWARDS")),
            "Shop keeps vanilla's Merchant description");

        string elite = (await Hover(Single(points, MapPointType.Elite), pointType, "/tmp/blitz_map_elite.png")).text;
        Check(elite.Contains(twoRelics) && elite.Contains(uncommonReward), $"Elite lists 2 relics and '{uncommonReward}'");
        Check(!elite.Contains(potion) && !elite.Contains(goldPrefix), "Elite lists no potion and no gold");

        string treasure = (await Hover(Single(points, MapPointType.Treasure), pointType)).text;
        Check(treasure.Contains(twoRelics) && !treasure.Contains(goldPrefix), "Treasure lists 2 relics (singleplayer) and no gold");

        string rest = (await Hover(Single(points, MapPointType.RestSite), pointType)).text;
        Check(rest.Contains(MapLoc("OPTIONS")), "Rest site lists its options");

        string boss = (await Hover(points.First(p => PointOf(p).PointType == MapPointType.Boss), pointType, "/tmp/blitz_map_boss.png")).text;
        Check(boss.Contains(rareReward) && boss.Contains(potion) && !boss.Contains(goldPrefix), "act 1 Boss lists a Rare card reward and a potion, no gold");

        // --- Walk into the first (weak) Monster room. ---
        MapCoord firstFight = PointOf(monsters[0]).coord;
        MainFile.Logger.Info($"{Tag}: entering the first Monster room at {firstFight}");
        // Not awaited: room entry finishes only once the fight is set up, so poll for that instead.
        _ = TaskHelper.RunSafely(RunManager.Instance.EnterMapCoord(firstFight));
        await WaitHelper.Until(() => CombatManager.Instance?.IsInProgress == true, ct, TimeSpan.FromSeconds(30), "combat did not start");
        await Task.Delay(2500);
        Check(runState.CurrentRoom is CombatRoom { Encounter.IsWeak: true }, "the first Monster room is the weak fight");

        points = FindTopBarPoints(tree, pointType); // the strip may rebuild on room change
        Control current = points.First(p => PointOf(p).coord == firstFight);
        (string inCombat, bool inCombatIsHistory) = await Hover(current, pointType, "/tmp/blitz_map_in_combat.png");
        Check(inCombatIsHistory && inCombat.Contains(MapLoc("EXPECTED_REWARDS")),
            "mid-combat, the current room's history tooltip shows expected rewards");
        Check(inCombat.Contains(commonReward) && !inCombat.Contains(potion), "mid-combat expected rewards: Common card reward, no potion");

        Control next = points.Where(p => PointOf(p).PointType == MapPointType.Monster && PointOf(p).coord != firstFight).First();
        string nextText = (await Hover(next, pointType)).text;
        Check(nextText.Contains(potion), "mid-combat, the next Monster still lists a potion");

        // --- Win and compare against the real reward screen. ---
        CmdResult win = new GameDevConsole(shouldAllowDebugCommands: true).ProcessCommand("win");
        MainFile.Logger.Info($"{Tag}: win -> {win.success} {win.msg}");
        await WaitHelper.Until(() => CombatManager.Instance?.IsInProgress != true, ct, TimeSpan.FromSeconds(30), "combat did not end");
        List<NRewardButton> rewards = new();
        await WaitHelper.Until(() =>
        {
            rewards = AllNodes(tree.Root).OfType<NRewardButton>().Where(b => b.Reward != null && b.IsVisibleInTree()).ToList();
            return rewards.Count > 0;
        }, ct, TimeSpan.FromSeconds(30), "reward screen did not appear");
        await Task.Delay(1000);
        await E2EHelpers.Shot(tree, "/tmp/blitz_map_rewards.png", Tag);
        List<string> rewardTypes = rewards.Select(b => b.Reward!.GetType().Name).ToList();
        MainFile.Logger.Info($"{Tag}: real rewards: {string.Join(", ", rewardTypes)}");
        Check(rewardTypes.Contains("CardReward") && !rewardTypes.Contains("PotionReward") && !rewardTypes.Contains("GoldReward"),
            "the weak fight's real rewards match the tooltip (card reward, no potion, no gold)");

        // MapNodeInfoTooltipPatch.RenderExpectedRewardsIntoHistory only fills the section while the
        // fight is on; afterwards the current room's tooltip is the vanilla history (real rewards).
        (string after, bool afterIsHistory) = await Hover(current, pointType, "/tmp/blitz_map_after_combat.png");
        Check(afterIsHistory && !after.Contains(MapLoc("EXPECTED_REWARDS")),
            "after the fight, the current room shows its recorded history instead of expected rewards");
    }

    // Main menu → Singleplayer → Blitz → first unlocked character → Embark. The Blitz submenu is
    // opened through its handler (OpenBlitzScreen), which is what the button calls, so a profile
    // that hasn't revealed the Blitz epoch yet still works.
    private static async Task<RunState> StartBlitzRun(SceneTree tree, CancellationToken ct)
    {
        Control mainMenu = await E2EHelpers.PrepareMainMenu(tree, Tag, ct);
        MainFile.Logger.Info($"{Tag}: starting Blitz run");
        await UiHelper.Click(mainMenu.GetNode<NButton>("MainMenuTextButtons/SingleplayerButton"));
        Control? submenu = null;
        await WaitHelper.Until(() =>
        {
            submenu = mainMenu.GetNodeOrNull<Control>("Submenus/SingleplayerSubmenu");
            return submenu?.Visible ?? false;
        }, ct, TimeSpan.FromSeconds(15), "singleplayer submenu did not appear");
        NButton blitzButton = submenu!.GetNode<NButton>("%BlitzButton");
        MethodInfo open = AccessTools.Method(submenu.GetType(), "OpenBlitzScreen")
            ?? throw new InvalidOperationException("FAILED: no OpenBlitzScreen");
        open.Invoke(submenu, new object[] { blitzButton });

        Control? blitzScreen = null;
        await WaitHelper.Until(() =>
        {
            blitzScreen = AllNodes(mainMenu).OfType<Control>().FirstOrDefault(n => n.GetType().Name == "NBlitzScreen" && n.Visible);
            return blitzScreen != null;
        }, ct, TimeSpan.FromSeconds(15), "Blitz screen did not appear");
        await Task.Delay(500);

        List<NCharacterSelectButton> characters = UiHelper.FindAll<NCharacterSelectButton>(blitzScreen!.GetNode("CharSelectButtons/ButtonContainer"));
        foreach (NCharacterSelectButton button in characters)
        {
            button.UnlockIfPossible();
        }
        NCharacterSelectButton chosen = characters.First(b => !b.IsLocked && !b.IsRandom && b.Visible);
        MainFile.Logger.Info($"{Tag}: selecting {chosen.Character.Id}");
        chosen.Select();
        await Task.Delay(300);
        await UiHelper.Click(blitzScreen.GetNode<NButton>("ConfirmButton"));

        await WaitHelper.Until(() => RunManager.Instance.DebugOnlyGetState() != null, ct, TimeSpan.FromSeconds(60), "run did not start");
        RunState runState = RunManager.Instance.DebugOnlyGetState()!;
        await WaitHelper.Until(() => runState.CurrentRoom != null && runState.CurrentRoom.RoomType != RoomType.Unassigned,
            ct, TimeSpan.FromSeconds(30), "no room assigned");
        await Task.Delay(2500);
        return runState;
    }

    private static List<Control> FindTopBarPoints(SceneTree tree, Type pointType) =>
        AllNodes(tree.Root).OfType<Control>().Where(n => pointType.IsInstanceOfType(n) && n.IsInsideTree())
            .OrderBy(n => PointOf(n).coord.row).ToList();

    private static MapPoint PointOf(Control point) =>
        (MapPoint)AccessTools.Property(point.GetType(), "Point").GetValue(point)!;

    private static Control Single(List<Control> points, MapPointType type) =>
        points.Single(p => PointOf(p).PointType == type);

    // Focuses a top-bar room (its OnFocus, as a mouse hover does), waits for the tooltip to lay
    // out, and returns its plain text plus whether it is the room-history tooltip; then unfocuses.
    private static async Task<(string text, bool isHistory)> Hover(Control point, Type pointType, string? shot = null)
    {
        AccessTools.Method(pointType, "OnFocus").Invoke(point, null);
        await Task.Delay(400);
        NHoverTipSet? set = ActiveTip(point);
        string text = set == null ? "" : TipText(set);
        bool isHistory = set != null && AllNodes(set).Any(n => n is NMapPointHistoryHoverTip);
        MapPoint mp = PointOf(point);
        MainFile.Logger.Info($"{Tag}: tooltip row {mp.coord.row} {mp.PointType} (history={isHistory}): {text.Replace("\n", " | ")}");
        if (shot != null)
        {
            await E2EHelpers.Shot(point.GetTree(), shot, Tag);
        }
        AccessTools.Method(pointType, "OnUnfocus").Invoke(point, null);
        await Task.Delay(100);
        return (text, isHistory);
    }

    private static NHoverTipSet? ActiveTip(Control owner)
    {
        Dictionary<Control, NHoverTipSet>? active =
            AccessTools.Field(typeof(NHoverTipSet), "_activeHoverTips")?.GetValue(null) as Dictionary<Control, NHoverTipSet>;
        return active != null && active.TryGetValue(owner, out NHoverTipSet? set) ? set : null;
    }

    // Visible text of every label under the tip, BBCode stripped.
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

    private static string MapLoc(string key, params (string name, object value)[] vars)
    {
        LocString loc = new("map", "COLINSPATCHKIT-MAPINFO-" + key);
        foreach ((string name, object value) in vars)
        {
            loc.AddObj(name, value);
        }
        return Regex.Replace(loc.GetFormattedText(), @"\[[^\]]*\]", "");
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
