using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using GameDevConsole = MegaCrit.Sts2.Core.DevConsole.DevConsole;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Dev sandbox (NOT an automated assertion harness) for the click-to-skip game-over patch
// (SkipGameOverAnimationsPatch): there's no good way to assert the reveal from a script, so this
// just lands you on the run-summary screen and hands over control. Switches to the scratch save
// profile, starts a throwaway run, jumps to the Bowlbugs (weak) fight, then runs the `die` console
// command to lose — leaving you on the game-over screen. From there click Continue, then click
// (or press confirm) repeatedly to watch each score line snap in and the Return to Main Menu button
// arrive early:
//
//   "Slay the Spire 2" --gameover-skip-sandbox=<profile>
//
// The argument is a scratch profile id. Any run already in progress on that profile is abandoned to
// start the sandbox run — never pass a profile holding a real run. Like the other *-sandbox flag it
// never restores the profile or quits; when you're done, quit to the menu and switch back to your
// own profile. Verify setup via the `gameover-skip-sandbox:` log lines, not a "complete" line.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class GameOverSkipSandboxPatch
{
    private static bool _started;
    private static int _originalProfileId = -1;

    public static void Postfix(NMainMenu __instance)
    {
        if (!CommandLineHelper.TryGetValue("gameover-skip-sandbox", out string? profileArg)
            || string.IsNullOrEmpty(profileArg) || !int.TryParse(profileArg, out int targetProfile))
        {
            return;
        }
        SceneTree tree = __instance.GetTree();
        if (!E2EHelpers.EnsureProfile(tree, targetProfile, ref _originalProfileId, "gameover-skip-sandbox"))
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
            MainFile.Logger.Info("gameover-skip-sandbox: ready — click Continue, then click to skip the reveal. Quit to menu and switch back to your own profile when done.");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"gameover-skip-sandbox: setup failed: {e}");
        }
        // No profile restore, no quit: the player takes over from here.
    }

    private static async Task RunInternal(SceneTree tree)
    {
        CancellationToken ct = CancellationToken.None;
        await E2EHelpers.StartThrowawayRun(tree, "gameover-skip-sandbox", ct);

        GameDevConsole console = new(shouldAllowDebugCommands: true);

        // A real combat so the death lands on the proper game-over flow.
        string encounterId = ModelDb.GetId(typeof(BowlbugsWeak)).Entry;
        CmdResult fightResult = console.ProcessCommand($"fight {encounterId}");
        MainFile.Logger.Info($"gameover-skip-sandbox: fight {encounterId} -> {fightResult.success} {fightResult.msg}");
        await WaitHelper.Until(() => CombatManager.Instance.IsInProgress
            && NPlayerHand.Instance != null && NPlayerHand.Instance.ActiveHolders.Count >= 1,
            ct, TimeSpan.FromSeconds(60), "combat hand not ready");
        await Task.Delay(1500);

        // Lose the run: kills the local player's creature, triggering the game-over screen.
        CmdResult dieResult = console.ProcessCommand("die");
        MainFile.Logger.Info($"gameover-skip-sandbox: die -> {dieResult.success} {dieResult.msg}");

        // Wait for the run-summary screen to mount before handing over.
        await WaitHelper.Until(() => UiHelper.FindAll<NGameOverScreen>(tree.Root).Count > 0,
            ct, TimeSpan.FromSeconds(30), "game-over screen did not appear");
        MainFile.Logger.Info("gameover-skip-sandbox: game-over screen mounted");
        await Task.Delay(500);
    }
}
