using System.Threading;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// The game-over/run-summary screen reveals its score lines and badges one at a time via a chain of
// awaited Tweens (NGameOverScreen.AnimateRunSummary -> AnimateScoreLines/AnimateBadges/
// AnimateScoreBar/AnimateDiscoveries), with fixed Cmd.Wait gaps between phases, then shows the
// Return-to-Main-Menu button. Vanilla has no way to hurry it along. While the screen is open we
// poll input each frame; a click/confirm while the summary is animating (a) cranks the speed scale
// of every running Tween so the current line snaps to its final state within a frame (firing its
// `finished` signal and advancing the await to the next line), and (b) flips SkipRequested, after
// which a Cmd.Wait prefix collapses the remaining inter-phase gaps to zero. One click ~ one line;
// spam-clicking jumps straight to the menu button with no leftover pauses.
//
// We bump speed scale (rather than Kill()) so each line lands fully revealed; we poll Input from
// the SceneTree's ProcessFrame signal (rather than the screen's _GuiInput) so full-screen backstop
// ColorRects can't swallow the click, and rather than a mod-defined Node subclass whose runtime
// Godot bindings don't register cleanly; and we never cancel the screen's CancellationToken, so the
// save/unlock bookkeeping in AnimateScoreBar still runs (just faster). Gap suppression is gated on
// both Active (a game-over screen is open) and SkipRequested (the user actually clicked), so a
// player who never clicks keeps vanilla pacing and nothing outside this screen is affected.
public static class GameOverSkipManager
{
    private static readonly System.Reflection.FieldInfo IsAnimatingField =
        AccessTools.Field(typeof(NGameOverScreen), "_isAnimatingSummary");

    // Active while an NGameOverScreen is open; SkipRequested latches once the user clicks to skip.
    public static bool Active;
    public static bool SkipRequested;

    private static NGameOverScreen? _screen;
    private static SceneTree? _tree;
    private static Callable _frameCallable;
    private static bool _connected;
    private static bool _wasPressed;

    public static void Begin(NGameOverScreen screen)
    {
        End(); // Defensive: drop any stale connection before re-arming.
        _screen = screen;
        _tree = screen.GetTree();
        Active = true;
        SkipRequested = false;
        _wasPressed = false;
        _frameCallable = Callable.From(OnProcessFrame);
        _tree.Connect(SceneTree.SignalName.ProcessFrame, _frameCallable);
        _connected = true;
    }

    public static void End()
    {
        if (_connected && _tree != null && GodotObject.IsInstanceValid(_tree))
        {
            _tree.Disconnect(SceneTree.SignalName.ProcessFrame, _frameCallable);
        }
        _connected = false;
        _screen = null;
        _tree = null;
        Active = false;
        SkipRequested = false;
    }

    private static void OnProcessFrame()
    {
        if (_tree == null || _screen == null || !GodotObject.IsInstanceValid(_screen))
        {
            End();
            return;
        }

        // Edge-detect a left click (or controller/keyboard confirm) so a held button is one skip.
        bool pressed = Input.IsMouseButtonPressed(MouseButton.Left)
            || (InputMap.HasAction("ui_accept") && Input.IsActionPressed("ui_accept"));
        bool justPressed = pressed && !_wasPressed;
        _wasPressed = pressed;
        if (!justPressed)
        {
            return;
        }

        // Only while the run-summary reveal is actually running.
        if (IsAnimatingField.GetValue(_screen) is not true)
        {
            return;
        }

        SkipStep(_tree);
    }

    private static void SkipStep(SceneTree tree)
    {
        SkipRequested = true;
        foreach (Tween tween in tree.GetProcessedTweens())
        {
            if (tween.IsValid() && tween.IsRunning())
            {
                // The game's own fast-forward (TweenHelper.FastForwardToCompletion): jump the
                // tween to its end, firing `Finished` this frame so the awaited reveal advances
                // to the next line immediately.
                tween.CustomStep(999999999.0);
            }
        }
    }
}

[HarmonyPatch(typeof(NGameOverScreen), "_Ready")]
public static class SkipGameOverAnimationsPatch
{
    public static void Postfix(NGameOverScreen __instance)
    {
        if (!ColinsPatchKitConfig.SkipGameOverAnimations)
        {
            return;
        }

        GameOverSkipManager.Begin(__instance);
    }
}

// Tear down the frame poll when the screen leaves the tree so it is inert everywhere else.
[HarmonyPatch(typeof(NGameOverScreen), "_ExitTree")]
public static class SkipGameOverAnimationsExitPatch
{
    public static void Postfix()
    {
        GameOverSkipManager.End();
    }
}

// Collapse the inter-phase reveal gaps once the player has started skipping. Scoped tightly: only
// fires while a game-over screen is open AND the user has clicked. seconds <= 0 makes Cmd.Wait
// return an already-completed task (see its guard), so this is a clean no-op delay.
[HarmonyPatch(typeof(Cmd), nameof(Cmd.Wait),
    new[] { typeof(float), typeof(CancellationToken), typeof(bool) })]
public static class SkipGameOverWaitPatch
{
    public static void Prefix(ref float seconds)
    {
        if (ColinsPatchKitConfig.SkipGameOverAnimations &&
            GameOverSkipManager.Active && GameOverSkipManager.SkipRequested)
        {
            seconds = 0f;
        }
    }
}
