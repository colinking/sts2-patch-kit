using System;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Dev sandbox + light assertion harness for the multi-line console patch
// (MultilineConsoleInputPatch). Boots to the main menu, opens the dev console, then drives the
// real input path (events fed through Input.ParseInputEvent, not direct calls) to exercise:
//   - the overlay TextEdit existing (i.e. Setup's reflection succeeded),
//   - pasting a multi-line block and running it sequentially with Enter,
//   - Shift+Enter inserting a newline,
//   - Tab completion on the current line.
// It logs `multilineconsole-sandbox: ...` lines for each step and saves /tmp/ml_console_*.png, then
// leaves the console open so you can keep typing by hand. No run, profile switch, or quit:
//
//   "Slay the Spire 2" --multilineconsole-sandbox=1
//
// (The argument is ignored; any value enables it.) Quit when you're done.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class MultilineConsoleSandboxPatch
{
    private const string Tag = "multilineconsole-sandbox";
    private static bool _started;

    public static void Postfix(NMainMenu __instance)
    {
        if (!CommandLineHelper.TryGetValue("multilineconsole-sandbox", out string? _) || _started)
        {
            return;
        }
        _started = true;
        SceneTree tree = __instance.GetTree();
        tree.CreateTimer(1.0).Timeout += () => TaskHelper.RunSafely(Run(tree));
    }

    private static async Task Run(SceneTree tree)
    {
        try
        {
            await RunInternal(tree);
            MainFile.Logger.Info($"{Tag}: ready — console left open for manual play. Quit when done.");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"{Tag}: setup failed: {e}");
        }
    }

    private static async Task RunInternal(SceneTree tree)
    {
        NDevConsole console = NDevConsole.Instance;
        console.ShowConsole();
        await Task.Delay(500);

        TextEdit? te = console.FindChild("ColinMultilineInput", recursive: true, owned: false) as TextEdit;
        MainFile.Logger.Info($"{Tag}: overlay TextEdit present = {te != null}");
        if (te == null)
        {
            return;
        }

        // 1. Multi-line block (InsertTextAtCaret emits text_changed, like a real paste) — verify it
        // holds 3 lines and renders the prompt gutter.
        te.InsertTextAtCaret("help\nhelp draw\nhelp gold");
        await Task.Delay(200);
        MainFile.Logger.Info($"{Tag}: pasted block line count = {te.GetLineCount()} (expect 3)");
        await E2EHelpers.Shot(tree, "/tmp/ml_console_1_multiline.png", Tag);

        // 2. Enter runs every line in order, then clears the block.
        Dispatch(Key.Enter, shift: false);
        await Task.Delay(400);
        MainFile.Logger.Info($"{Tag}: after Enter, input line count = {te.GetLineCount()} (expect 1, cleared)");
        await E2EHelpers.Shot(tree, "/tmp/ml_console_2_ran.png", Tag);

        // 3. Shift+Enter inserts a newline mid-edit.
        te.InsertTextAtCaret("line one");
        await Task.Delay(100);
        Dispatch(Key.Enter, shift: true);
        await Task.Delay(150);
        te.InsertTextAtCaret("line two");
        await Task.Delay(150);
        MainFile.Logger.Info($"{Tag}: after Shift+Enter, line count = {te.GetLineCount()} (expect 2)");
        await E2EHelpers.Shot(tree, "/tmp/ml_console_3_shiftenter.png", Tag);

        // 4. Tab completion on the current line.
        te.Clear();
        te.InsertTextAtCaret("he");
        await Task.Delay(100);
        Dispatch(Key.Tab, shift: false);
        await Task.Delay(250);
        MainFile.Logger.Info($"{Tag}: after Tab, input = '{te.Text.Replace("\n", "\\n")}'");
        await E2EHelpers.Shot(tree, "/tmp/ml_console_4_tab.png", Tag);

        // Reset to an empty prompt for manual play.
        te.Text = string.Empty;
        te.GrabFocus();
    }

    // Feed a key event through the real input pipeline so the patched NDevConsole._Input runs in a
    // valid input-handling context (direct _Input calls would make SetInputAsHandled complain).
    private static void Dispatch(Key key, bool shift)
    {
        var keyEvent = new InputEventKey
        {
            Keycode = key,
            PhysicalKeycode = key,
            Pressed = true,
            ShiftPressed = shift,
        };
        Input.ParseInputEvent(keyEvent);
    }
}
