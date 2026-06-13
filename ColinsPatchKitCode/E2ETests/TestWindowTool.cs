using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Keeps the launch-flag test/dev harnesses from hijacking the user's active screen.
//
// The game boots fullscreen (SettingsSave.Fullscreen defaults true), and on macOS that
// takes over a whole Space on the active display. Worse, undoing it after the fact
// (WindowSetMode(Windowed) once the window is already in a fullscreen Space) doesn't
// reliably stick. So instead of *undoing* fullscreen we *prevent* it: this patch wraps
// NGame.ApplyDisplaySettings — the one method that calls WindowSetMode(Fullscreen) — and,
// only when one of our harness launch flags is present, forces it down the windowed
// branch onto a chosen display. WindowSetMode(Fullscreen) is therefore never called and
// no macOS Space is ever created.
//
// Target display: `--test-screen=<index>` if given, else the physically smallest screen
// (the laptop built-in on a laptop+external setup — macOS may report it as the *primary*
// "Main" display, so we target by size, not index). The window is parked in that screen's
// top-left corner (a small margin to clear the menu bar), at 1280x800.
//
// SettingsSave is a global, local-only store that NGame.Quit() persists on exit, so the
// prefix snapshots every field it touches (Fullscreen, TargetDisplay, WindowSize,
// WindowPosition) and the postfix restores them — the live window ends up windowed and
// parked aside, but the user's real display preferences are left exactly as they were.
//
// For the automated harnesses the postfix also sets the NoFocus window flag so the game
// never steals keyboard focus from whatever the user is working in (the harnesses drive
// the UI programmatically and never need focus). The interactive --conflag-sandbox is
// left focusable — you're playing that one.
[HarmonyPatch(typeof(NGame), "ApplyDisplaySettings")]
public static class TestWindowPatch
{
    // The launch flags that should run windowed-aside. The bool is grabFocus: true means
    // leave the window focusable (the sandbox you play), false means apply NoFocus.
    private static readonly (string flag, bool grabFocus)[] _testFlags =
    [
        ("conflag-sandbox", true),
        ("relicpulse-e2e", false),
        ("powerpulse-e2e", false),
        ("retainslots-e2e", false),
        ("retainslots", false),
        ("pulsegif", false),
    ];

    private static bool _saved;
    private static bool _savedFullscreen;
    private static int _savedTargetDisplay;
    private static Vector2I _savedWindowSize;
    private static Vector2I _savedWindowPosition;

    private static bool TryGetMode(out bool grabFocus)
    {
        foreach ((string flag, bool focus) in _testFlags)
        {
            if (CommandLineHelper.TryGetValue(flag, out string? _))
            {
                grabFocus = focus;
                return true;
            }
        }
        grabFocus = false;
        return false;
    }

    private static int ResolveTargetScreen()
    {
        int screenCount = DisplayServer.GetScreenCount();
        if (CommandLineHelper.TryGetValue("test-screen", out string? screenArg)
            && int.TryParse(screenArg, out int idx) && idx >= 0 && idx < screenCount)
        {
            return idx;
        }
        // Default to the physically smallest display — on a laptop + external setup that's
        // the built-in screen, keeping the test window off the big monitor used for work.
        // (macOS doesn't necessarily call the laptop the "primary" or "second" screen, so
        // target by size rather than index; override with --test-screen.)
        return Enumerable.Range(0, screenCount)
            .OrderBy(i => { Vector2I s = DisplayServer.ScreenGetSize(i); return (long)s.X * s.Y; })
            .First();
    }

    public static void Prefix()
    {
        if (!TryGetMode(out bool _))
        {
            return;
        }
        SettingsSave s = SaveManager.Instance.SettingsSave;
        _savedFullscreen = s.Fullscreen;
        _savedTargetDisplay = s.TargetDisplay;
        _savedWindowSize = s.WindowSize;
        _savedWindowPosition = s.WindowPosition;
        _saved = true;

        // Force the windowed branch: a 1280x800 window parked in the top-left corner of
        // the target display. (8, 48) clears the macOS menu bar; (-1, -1) would tell the
        // original method to center it instead.
        s.Fullscreen = false;
        s.TargetDisplay = ResolveTargetScreen();
        s.WindowSize = new Vector2I(1280, 800);
        s.WindowPosition = new Vector2I(8, 48);
    }

    public static void Postfix()
    {
        if (!TryGetMode(out bool grabFocus) || !_saved)
        {
            return;
        }
        SettingsSave s = SaveManager.Instance.SettingsSave;
        // The live window is already windowed-aside; put the persisted settings back so
        // nothing leaks into the user's real display preferences on quit.
        s.Fullscreen = _savedFullscreen;
        s.TargetDisplay = _savedTargetDisplay;
        s.WindowSize = _savedWindowSize;
        s.WindowPosition = _savedWindowPosition;

        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, !grabFocus);
        int screen = DisplayServer.WindowGetCurrentScreen();
        MainFile.Logger.Info($"test-window: windowed on screen {screen + 1}/{DisplayServer.GetScreenCount()}"
            + $" (display size {DisplayServer.ScreenGetSize(screen)}), window size {DisplayServer.WindowGetSize()}"
            + $" at {DisplayServer.WindowGetPosition()} (grabFocus={grabFocus})");
    }
}
