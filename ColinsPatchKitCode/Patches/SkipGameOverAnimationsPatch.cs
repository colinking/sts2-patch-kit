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
// poll input each frame; the first click/confirm while the summary is animating latches
// SkipRequested, which (a) flips a Cmd.Wait prefix that collapses the remaining inter-phase gaps to
// zero, and (b) makes us snap every running reveal Tween to its final state each frame from then on
// (firing each `finished` signal so the awaited chain advances). With the gaps gone the whole chain
// resolves within a few frames of that single click — a full resolve, no per-line clicking.
//
// We fast-forward via Tween.CustomStep (the game's own TweenHelper.FastForwardToCompletion) rather
// than Kill() so each line lands fully revealed; we poll Input from the SceneTree's ProcessFrame
// signal (rather than the screen's _GuiInput) so full-screen backstop ColorRects can't swallow the
// click, and rather than a mod-defined Node subclass whose runtime Godot bindings don't register
// cleanly; and we never cancel the screen's CancellationToken, so the save/unlock bookkeeping in
// AnimateScoreBar still runs (just faster). Both the fast-forward and the gap suppression require
// SkipRequested and stop once _isAnimatingSummary clears, so a player who never clicks keeps vanilla
// pacing and nothing outside this screen (the menu button fade, etc.) is touched.
public static class GameOverSkipManager
{
    private static readonly System.Reflection.FieldInfo IsAnimatingField =
        AccessTools.Field(typeof(NGameOverScreen), "_isAnimatingSummary");

    // How far to step each reveal tween when skipping. Must comfortably exceed the longest one-shot
    // reveal (a few seconds) so it snaps to its end and fires `Finished`, but must NOT be the
    // game's own ~1e9 (TweenHelper.FastForwardToCompletion): that helper is only ever called on known
    // one-shot tweens, whereas we step every processed tween in the tree — and stepping an
    // *infinitely looping* tween (e.g. Neow's Ancient-dialogue option arrow, whose room stays alive
    // behind this overlay when you abandon at Neow) by 1e9s makes Godot iterate ~billions of loop
    // cycles in one synchronous call and hangs the game. 100s completes every reveal here while
    // advancing a loop tween only ~100 cheap cycles.
    private const double FastForwardSeconds = 100.0;

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

        // Latch the skip on the first click/confirm, but only while the reveal is actually running
        // (edge-detected so a held button counts once). Until then, do nothing — vanilla pacing.
        if (!SkipRequested)
        {
            bool pressed = Input.IsMouseButtonPressed(MouseButton.Left)
                || (InputMap.HasAction("ui_accept") && Input.IsActionPressed("ui_accept"));
            bool justPressed = pressed && !_wasPressed;
            _wasPressed = pressed;
            if (!justPressed || IsAnimatingField.GetValue(_screen) is not true)
            {
                return;
            }
            SkipRequested = true;
        }

        // Once skipping, snap every running reveal tween to its end each frame until the summary
        // animation finishes — turning that one click into a full resolve. Stop once it's done so
        // unrelated tweens (e.g. the menu button fade) keep their normal timing.
        if (IsAnimatingField.GetValue(_screen) is not true)
        {
            return;
        }
        foreach (Tween tween in _tree.GetProcessedTweens())
        {
            if (tween.IsValid() && tween.IsRunning())
            {
                // Snap the tween to its end (bounded — see FastForwardSeconds), firing `Finished`
                // this frame so the awaited reveal advances.
                tween.CustomStep(FastForwardSeconds);
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
