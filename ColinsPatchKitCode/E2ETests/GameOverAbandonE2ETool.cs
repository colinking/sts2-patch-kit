using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using ColinsPatchKit.ColinsPatchKitCode.Patches;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Regression harness for the click-to-skip game-over patch (SkipGameOverAnimationsPatch) against the
// "abandon at Neow freezes the game" bug. Neow is an Ancient-dialogue event whose option arrows run
// infinitely-looping tweens (NAncientDialogueHitbox._loopTween); the game-over screen is an overlay,
// so abandoning at Neow leaves that event room — and its loop tweens — alive in the tree behind the
// summary. The patch used to fast-forward EVERY processed tween by ~1e9s, and stepping an infinite
// loop tween that far hangs Godot in a single synchronous call. The fix bounds the step
// (GameOverSkipManager.FastForwardSeconds); this harness asserts the skip completes instead of
// freezing:
//
//   "Slay the Spire 2" --gameover-abandon-e2e=<profile>
//
// Flow: switch to the scratch profile, start a throwaway run (which lands at Neow), confirm the
// Ancient-dialogue loop tweens are live, abandon to reach the game-over screen, open the summary,
// request the skip, and assert the reveal finishes (_isAnimatingSummary clears and the Return to
// Main Menu button arrives) within a few seconds. If the bug regresses the game freezes here and no
// `gameover-abandon-e2e: PASS` line is logged — kill the process and treat the missing PASS as a
// failure. Saves /tmp/gameover_abandon_*.png, then restores the profile and quits.
//
// The argument is a scratch profile id. Any run already in progress on that profile is abandoned to
// start the harness run — never pass a profile holding a real run.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class GameOverAbandonE2EPatch
{
    private static readonly FieldInfo IsAnimatingField =
        AccessTools.Field(typeof(NGameOverScreen), "_isAnimatingSummary");
    private static readonly FieldInfo MainMenuButtonField =
        AccessTools.Field(typeof(NGameOverScreen), "_mainMenuButton");
    private static readonly MethodInfo OpenSummaryScreenMethod =
        AccessTools.Method(typeof(NGameOverScreen), "OpenSummaryScreen");

    private static bool _started;
    private static int _originalProfileId = -1;

    public static void Postfix(NMainMenu __instance)
    {
        if (!CommandLineHelper.TryGetValue("gameover-abandon-e2e", out string? profileArg)
            || string.IsNullOrEmpty(profileArg) || !int.TryParse(profileArg, out int targetProfile))
        {
            return;
        }
        SceneTree tree = __instance.GetTree();
        if (!E2EHelpers.EnsureProfile(tree, targetProfile, ref _originalProfileId, "gameover-abandon-e2e"))
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
            MainFile.Logger.Info("gameover-abandon-e2e: complete");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"gameover-abandon-e2e: failed: {e}");
            await E2EHelpers.Shot(tree, "/tmp/gameover_abandon_fail.png", "gameover-abandon-e2e");
        }
        finally
        {
            E2EHelpers.RestoreProfile(_originalProfileId, "gameover-abandon-e2e");
            await Task.Delay(500);
            tree.Quit();
        }
    }

    private static async Task RunInternal(SceneTree tree)
    {
        CancellationToken ct = CancellationToken.None;
        await E2EHelpers.StartThrowawayRun(tree, "gameover-abandon-e2e", ct);

        // A fresh run opens on Neow. Confirm we're in the event room and that its Ancient-dialogue
        // option arrows (the infinite-loop tweens that used to hang the skip) are live — that's the
        // precondition the bug needs.
        if (NEventRoom.Instance == null)
        {
            throw new Exception("ASSERT FAILED: expected to be in the Neow event room after run start");
        }
        int hitboxes = UiHelper.FindAll<NAncientDialogueHitbox>(tree.Root).Count;
        if (hitboxes == 0)
        {
            throw new Exception("ASSERT FAILED: no Ancient-dialogue hitboxes at Neow — cannot reproduce the loop-tween hang");
        }
        MainFile.Logger.Info($"gameover-abandon-e2e: at Neow with {hitboxes} Ancient-dialogue loop tween(s) live");
        await E2EHelpers.Shot(tree, "/tmp/gameover_abandon_1_neow.png", "gameover-abandon-e2e");

        // Abandon straight through (the production path past the confirm popup) to open the
        // game-over overlay; the Neow room and its loop tweens stay alive behind it.
        RunManager.Instance.Abandon();
        await WaitHelper.Until(() => UiHelper.FindAll<NGameOverScreen>(tree.Root).Count > 0,
            ct, TimeSpan.FromSeconds(30), "game-over screen did not appear");
        NGameOverScreen screen = UiHelper.FindAll<NGameOverScreen>(tree.Root).First();
        MainFile.Logger.Info($"gameover-abandon-e2e: game-over screen mounted ({UiHelper.FindAll<NAncientDialogueHitbox>(tree.Root).Count} loop tween(s) still live behind it)");
        await E2EHelpers.Shot(tree, "/tmp/gameover_abandon_2_gameover.png", "gameover-abandon-e2e");

        // Continue to the summary (the exact handler the Continue button fires), then wait for the
        // reveal to actually be animating before requesting the skip.
        await Task.Delay(2000); // let AnimateIn settle so the screen is ready to continue
        OpenSummaryScreenMethod.Invoke(screen, new object?[] { null });
        await WaitHelper.Until(() => IsAnimating(screen), ct, TimeSpan.FromSeconds(10), "summary never started animating");
        MainFile.Logger.Info("gameover-abandon-e2e: summary animating — requesting skip");

        // Simulate the skip click. This drives GameOverSkipManager.OnProcessFrame to fast-forward
        // every processed tween — including the live Neow loop tweens. With the bug this synchronous
        // CustomStep never returns and the line below hangs; with the fix it completes promptly.
        GameOverSkipManager.SkipRequested = true;

        await WaitHelper.Until(() => !IsAnimating(screen), ct, TimeSpan.FromSeconds(10),
            "ASSERT FAILED: reveal did not finish after skip (loop-tween hang regressed?)");
        MainFile.Logger.Info("gameover-abandon-e2e: PASS skip completed without freezing");

        await WaitHelper.Until(() => MainMenuButtonField.GetValue(screen) is Control { Visible: true },
            ct, TimeSpan.FromSeconds(10), "ASSERT FAILED: Return to Main Menu button never appeared after skip");
        MainFile.Logger.Info("gameover-abandon-e2e: PASS Return to Main Menu button revealed");
        await E2EHelpers.Shot(tree, "/tmp/gameover_abandon_3_skipped.png", "gameover-abandon-e2e");
    }

    private static bool IsAnimating(NGameOverScreen screen)
    {
        return IsAnimatingField.GetValue(screen) is true;
    }
}
