using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using ColinsPatchKit.ColinsPatchKitCode.Patches;
using GameDevConsole = MegaCrit.Sts2.Core.DevConsole.DevConsole;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Automated assertion harness for v0.113.0's Evolve (Byrdpip) and Restore (Wuppy) rest-site options
// in the upcoming-rest-site tooltip (MapNodeInfoTooltipPatch.BuildRest). Beta-only: both relics are
// reached by name. Starts a throwaway run, grants Byrdpip and Wuppy, readies Byrdpip to evolve (6
// Swoops) and damages Wuppy to 37/60, then checks the rest-site tooltip body lists "Byrdpip →
// Byrdine" and "Wuppy +23 HP". Jumps into a real rest site to check the game offers both options
// (and that the rest-site pulses left both relics' own Status alone), then heals Wuppy to full and
// checks Restore reads greyed-out with no HP detail. Logs `restoptions-e2e: PASS <assertion>` lines,
// saves /tmp/rest_options.png, switches back to the original profile and quits:
//
//   "Slay the Spire 2" --restoptions-e2e=<profile>
//
// The argument is a scratch profile id; any run in progress on it is abandoned.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class RestOptionsE2EPatch
{
    private const string Tag = "restoptions-e2e";
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
            await E2EHelpers.Shot(tree, "/tmp/rest_options_fail.png", Tag);
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
        Type byrdpipType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Relics.Byrdpip")!;
        Type byrdineType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Relics.Byrdine")
            ?? throw new InvalidOperationException("FAILED: no Byrdine — this harness needs the v0.113.0+ branch");
        Type wuppyType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Relics.Wuppy")!;

        RunState runState = await E2EHelpers.StartThrowawayRun(tree, Tag, ct);
        Player player = runState.Players[0];
        GameDevConsole console = new(shouldAllowDebugCommands: true);
        foreach (Type t in new[] { byrdpipType, wuppyType })
        {
            string id = ModelDb.GetId(t).Entry;
            CmdResult r = console.ProcessCommand($"relic add {id}");
            MainFile.Logger.Info($"{Tag}: relic add {id} -> {r.success} {r.msg}");
        }
        await Task.Delay(1000);

        RelicModel byrdpip = player.Relics.Single(byrdpipType.IsInstanceOfType);
        RelicModel wuppy = player.Relics.Single(wuppyType.IsInstanceOfType);
        AccessTools.Property(byrdpipType, "SwoopsPlayed").SetValue(byrdpip, 6);
        PropertyInfo currentHp = AccessTools.Property(wuppyType, "CurrentHp");
        int maxHp = (int)AccessTools.Property(wuppyType, "MaxHp").GetValue(wuppy)!;
        currentHp.SetValue(wuppy, maxHp - 23);
        MainFile.Logger.Info($"{Tag}: Byrdpip at 6 Swoops, Wuppy at {maxHp - 23}/{maxHp}");

        string byrdpipTitle = byrdpip.Title.GetFormattedText();
        string byrdineTitle = ModelDb.GetById<RelicModel>(ModelDb.GetId(byrdineType)).Title.GetFormattedText();
        string wuppyTitle = wuppy.Title.GetFormattedText();
        string heal23 = MapLoc("REST_HEAL_DETAIL", ("Hp", 23));

        string body = BuildRest(player);
        MainFile.Logger.Info($"{Tag}: rest tooltip body:\n{body}");
        string evolveTitle = OptionTitle(player, "EVOLVE");
        string restoreTitle = OptionTitle(player, "RESTORE");
        Check(body.Contains($"{evolveTitle}") && body.Contains($"{byrdpipTitle} → {byrdineTitle}"),
            $"tooltip lists '{evolveTitle}' as '{byrdpipTitle} → {byrdineTitle}'");
        Check(body.Contains($"{wuppyTitle} {heal23}"), $"tooltip lists '{restoreTitle}' as '{wuppyTitle} {heal23}'");
        string restoreLine = body.Split('\n').Single(l => l.Contains(restoreTitle));
        Check(!restoreLine.Contains("#888888"), "Restore is available (not greyed) while Wuppy is hurt");

        // The real rest site agrees, and the pulses left both relics' own Status alone.
        CmdResult room = console.ProcessCommand("room RestSite");
        MainFile.Logger.Info($"{Tag}: room RestSite -> {room.success} {room.msg}");
        await WaitHelper.Until(() => NRestSiteRoom.Instance != null, ct, TimeSpan.FromSeconds(30), "rest site did not appear");
        await Task.Delay(1500);
        await E2EHelpers.Shot(tree, "/tmp/rest_options.png", Tag);
        var options = NRestSiteRoom.Instance!.Options;
        Check(options.Any(o => o.OptionId == "EVOLVE" && o.IsEnabled), "the real rest site offers Evolve");
        Check(options.Any(o => o.OptionId == "RESTORE" && o.IsEnabled), "the real rest site offers Restore");
        Check(byrdpip.Status == RelicStatus.Active, $"Byrdpip keeps its own Active status (got {byrdpip.Status})");
        Check(wuppy.Status == RelicStatus.Normal, $"Wuppy keeps its own Normal status (got {wuppy.Status})");

        // At full HP Restore is unavailable and shows no HP detail.
        currentHp.SetValue(wuppy, maxHp);
        string fullBody = BuildRest(player);
        string fullLine = fullBody.Split('\n').Single(l => l.Contains(restoreTitle));
        MainFile.Logger.Info($"{Tag}: Restore at full HP: {fullLine}");
        Check(fullLine.Contains("#888888") && !fullLine.Contains(wuppyTitle), "at full HP Restore is greyed out with no HP detail");
    }

    private static string BuildRest(Player player) =>
        (string)AccessTools.Method(typeof(MapNodeInfoTooltipPatch), "BuildRest").Invoke(null, new object[] { player })!;

    private static string OptionTitle(Player player, string optionId) =>
        RestSiteOption.Generate(player).Single(o => o.OptionId == optionId).Title.GetFormattedText();

    private static string MapLoc(string key, params (string name, object value)[] vars) =>
        (string)AccessTools.Method(typeof(MapNodeInfoTooltipPatch), "Loc", new[] { typeof(string), typeof((string, object)[]) })
            .Invoke(null, new object[] { key, vars })!;

    private static void Check(bool condition, string assertion)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"FAILED: {assertion}");
        }
        MainFile.Logger.Info($"{Tag}: PASS {assertion}");
    }
}
