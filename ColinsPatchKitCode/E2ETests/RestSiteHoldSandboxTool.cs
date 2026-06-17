using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using GameDevConsole = MegaCrit.Sts2.Core.DevConsole.DevConsole;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Dev sandbox (NOT an automated assertion harness) for the rest-site hold-to-confirm patch
// (RestSiteProceedHoldPatch): the hold gesture is a manual feel test, so this just sets the scene
// and hands over control. Switches to the scratch save profile, starts a throwaway run, grants
// Miniature Tent, then jumps to a rest site — leaving you at the campfire with Tent, so both options
// stay selectable after your first pick:
//
//   "Slay the Spire 2" --restsitehold-sandbox=<profile>
//
// Pick one option (e.g. heal); the other stays takeable and the proceed button lights up. Then try
// to leave: a quick click does nothing, and the proceed button now fills a gold bar as you hold —
// release early to cancel, or hold ~0.5s to leave. Use the second option and try again with nothing
// selectable left, and proceed becomes an instant click as in vanilla.
//
// The argument is a scratch profile id. Any run already in progress on that profile is abandoned to
// start the sandbox run — never pass a profile holding a real run. Like the other *-sandbox flag it
// never restores the profile or quits; when you're done, quit to the menu and switch back to your
// own profile. Verify setup via the `restsitehold-sandbox:` log lines, not a "complete" line.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class RestSiteHoldSandboxPatch
{
    private static bool _started;
    private static int _originalProfileId = -1;

    public static void Postfix(NMainMenu __instance)
    {
        if (!CommandLineHelper.TryGetValue("restsitehold-sandbox", out string? profileArg)
            || string.IsNullOrEmpty(profileArg) || !int.TryParse(profileArg, out int targetProfile))
        {
            return;
        }
        SceneTree tree = __instance.GetTree();
        if (!E2EHelpers.EnsureProfile(tree, targetProfile, ref _originalProfileId, "restsitehold-sandbox"))
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
            MainFile.Logger.Info("restsitehold-sandbox: ready — pick an option, then hold the proceed button to leave. Quit to menu and switch back to your own profile when done.");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"restsitehold-sandbox: setup failed: {e}");
        }
        // No profile restore, no quit: the player takes over from here.
    }

    private static async Task RunInternal(SceneTree tree)
    {
        CancellationToken ct = CancellationToken.None;
        await E2EHelpers.StartThrowawayRun(tree, "restsitehold-sandbox", ct);

        GameDevConsole console = new(shouldAllowDebugCommands: true);

        // Grant Tent so the rest site keeps its other option selectable after the first pick — the
        // condition the patch gates on.
        string tentId = ModelDb.GetId(typeof(MiniatureTent)).Entry;
        CmdResult relicResult = console.ProcessCommand($"relic add {tentId}");
        MainFile.Logger.Info($"restsitehold-sandbox: relic add {tentId} -> {relicResult.success} {relicResult.msg}");

        // Jump to a rest site.
        CmdResult roomResult = console.ProcessCommand("room RestSite");
        MainFile.Logger.Info($"restsitehold-sandbox: room RestSite -> {roomResult.success} {roomResult.msg}");

        await WaitHelper.Until(() => NRestSiteRoom.Instance != null,
            ct, TimeSpan.FromSeconds(30), "rest site did not appear");
        MainFile.Logger.Info("restsitehold-sandbox: rest site mounted");
        await Task.Delay(500);
    }
}
