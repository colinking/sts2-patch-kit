using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Nodes.Debug;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// The dev console's input field (NDevConsole._inputBuffer) is a Godot LineEdit, which physically
// cannot hold newlines — so you can't enter or paste a multi-line block of commands. This patch
// overlays a real multi-line TextEdit on top of it so you can:
//   - press Shift+Enter to start a new line and keep typing,
//   - paste a block of commands (TextEdit keeps the newlines),
//   - press Enter to run every non-blank line in order (stopping at the first failure),
// while completions, command history, and BaseLib's nicer autocomplete keep working.
//
// Approach: the original LineEdit stays in the tree but hidden and is reused as a single-line
// "scratch buffer" representing the line the caret is on. The TextEdit is the real editor. For
// completion we marshal the caret's line in and out of that scratch buffer around invoking the
// game's own (unmodified) AutocompleteCommand / AcceptSelection / OnInputTextChanged — so the
// vanilla completion + selection menu and BaseLib's BetterConsoleAutocompletePatch /
// UpdateGhostTextPatch all run exactly as they do normally, just scoped to the current line.
// Only ProcessCommand (multi-line, sequential, fail-stop) and the up/down history recall are
// reimplemented, because they need to target the TextEdit instead of the hidden LineEdit.
//
// Inline ghost-text preview is intentionally dropped in multi-line mode (the ghost Label lives in
// the hidden InputBufferContainer, so it simply never shows); the Tab completion menu is the
// must-have and still works on any line. The "> " prompt is rendered per line in a TextEdit gutter
// (bright on the first line, dim on continuations), replacing the single PromptLabel.
//
// Toggle is read at console-creation time (_Ready), so changing it takes effect on the next launch.
public static class MultilineConsoleManager
{
    // Visible input lines before the input box stops growing and scrolls internally.
    private const int MaxVisibleLines = 10;
    private const int GutterWidthPx = 28;

    // Matches NDevConsole.UpdatePromptStyle's cyan; continuations are the same hue, dimmed.
    private static readonly Color PromptColor = new(0f, 0.831f, 1f);
    private static readonly Color ContinuationColor = new(0f, 0.831f, 1f, 0.4f);

    private sealed class ConsoleState
    {
        public TextEdit Te = null!;
        public int Gutter;

        // Set true around our own programmatic edits to the TextEdit so the TextChanged handler
        // skips the selection-mode re-filter (which would otherwise recurse) while still letting it
        // refresh the gutter and layout.
        public bool Suppress;
    }

    private static readonly ConditionalWeakTable<NDevConsole, ConsoleState> _state = new();

    // --- Reflected access to NDevConsole's private members (the class is mostly private). ---
    private static readonly FieldInfo InputBufferField = Field("_inputBuffer");
    private static readonly FieldInfo OutputBufferField = Field("_outputBuffer");
    private static readonly FieldInfo TabBufferField = Field("_tabBuffer");
    private static readonly FieldInfo PromptLabelField = Field("_promptLabel");
    private static readonly FieldInfo IsFullscreenField = Field("_isFullscreen");
    private static readonly FieldInfo SymbolPromptField = Field("_symbolPrompt");
    private static readonly FieldInfo SymbolWarningField = Field("_symbolWarning");
    private static readonly FieldInfo DevConsoleField = Field("_devConsole");
    private static readonly FieldInfo TabCompletionField = Field("_tabCompletion");

    private static readonly MethodInfo AutocompleteMethod = Method("AutocompleteCommand");
    private static readonly MethodInfo AcceptSelectionMethod = Method("AcceptSelection");
    private static readonly MethodInfo OnInputTextChangedMethod = Method("OnInputTextChanged");
    private static readonly MethodInfo NavigateSelectionMethod = Method("NavigateSelection");
    private static readonly MethodInfo ExitSelectionModeMethod = Method("ExitSelectionMode");

    private static FieldInfo Field(string name) =>
        AccessTools.Field(typeof(NDevConsole), name)
        ?? throw new MissingFieldException(nameof(NDevConsole), name);

    private static MethodInfo Method(string name) =>
        AccessTools.Method(typeof(NDevConsole), name)
        ?? throw new MissingMethodException(nameof(NDevConsole), name);

    private static LineEdit Scratch(NDevConsole c) => (LineEdit)InputBufferField.GetValue(c)!;
    private static RichTextLabel OutputBuffer(NDevConsole c) => (RichTextLabel)OutputBufferField.GetValue(c)!;
    private static RichTextLabel TabBuffer(NDevConsole c) => (RichTextLabel)TabBufferField.GetValue(c)!;
    private static Label PromptLabel(NDevConsole c) => (Label)PromptLabelField.GetValue(c)!;
    private static bool IsFullscreen(NDevConsole c) => (bool)IsFullscreenField.GetValue(c)!;
    private static string SymbolPrompt(NDevConsole c) => (string)SymbolPromptField.GetValue(c)!;
    private static string SymbolWarning(NDevConsole c) => (string)SymbolWarningField.GetValue(c)!;
    private static DevConsole DevConsoleOf(NDevConsole c) => (DevConsole)DevConsoleField.GetValue(c)!;
    private static TabCompletionState TabCompletion(NDevConsole c) => (TabCompletionState)TabCompletionField.GetValue(c)!;

    // Builds and wires the overlay TextEdit. Called from a postfix on NDevConsole._Ready (after the
    // game has fetched all its nodes and created _devConsole), only when the feature is enabled.
    public static void Setup(NDevConsole console)
    {
        if (!ColinsPatchKitConfig.AllowMultilineConsoleInput || _state.TryGetValue(console, out _))
        {
            return;
        }

        try
        {
            LineEdit scratch = Scratch(console);
            Control? bufferContainer = console.GetNodeOrNull<Control>("InputContainer/InputBufferContainer");

            var te = new TextEdit
            {
                Name = "ColinMultilineInput",
                WrapMode = TextEdit.LineWrappingMode.None,
                ScrollFitContentHeight = false,
                CaretBlink = true,
                Editable = true,
                FocusMode = Control.FocusModeEnum.All,
            };

            // Match the console font so the overlay is visually identical to the original LineEdit,
            // and strip the default TextEdit background so the console panel shows through.
            Font font = scratch.GetThemeFont("font");
            int fontSize = scratch.GetThemeFontSize("font_size");
            if (font != null)
            {
                te.AddThemeFontOverride("font", font);
            }
            if (fontSize > 0)
            {
                te.AddThemeFontSizeOverride("font_size", fontSize);
            }
            te.AddThemeColorOverride("font_color", scratch.GetThemeColor("font_color"));
            te.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
            te.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());

            // Per-line "> " prompt lives in a string gutter (so it's never part of the command text).
            te.AddGutter();
            int gutter = te.GetGutterCount() - 1;
            te.SetGutterType(gutter, TextEdit.GutterType.String);
            te.SetGutterWidth(gutter, GutterWidthPx);
            te.SetGutterClickable(gutter, false);

            // Parent the overlay to the console Panel itself, not InputContainer: that node is a
            // Godot Container and would reset the TextEdit's width to its minimum (0) on every
            // layout pass. On the plain Panel we drive position/size explicitly in Relayout.
            console.AddChild(te);

            // The original LineEdit + ghost label and the single PromptLabel are now redundant.
            if (bufferContainer != null)
            {
                bufferContainer.Visible = false;
            }
            PromptLabel(console).Visible = false;

            var state = new ConsoleState { Te = te, Gutter = gutter };
            _state.Add(console, state);
            te.TextChanged += () => OnTextChanged(console, state);

            UpdateGutter(console, state);
            Relayout(console, state);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to set up multi-line console input: {e}");
        }
    }

    // Drives the overlay from NDevConsole._Input (prefix). Returns false to skip the original method
    // (we fully handle the key), true to let the original run (toggle keys, Escape, F11, Ctrl-binds,
    // and ordinary typing — which flows through to the focused TextEdit).
    public static bool HandleInput(NDevConsole console, InputEvent inputEvent)
    {
        try
        {
            if (!_state.TryGetValue(console, out ConsoleState? state)
                || inputEvent is not InputEventKey { Pressed: true } key
                || !console.Visible)
            {
                return true;
            }

            TextEdit te = state.Te;
            bool inSelection = TabCompletion(console).InSelectionMode;

            switch (key.Keycode)
            {
                case Key.Enter:
                case Key.KpEnter:
                    if (key.IsShiftPressed() && !inSelection)
                    {
                        // Insert the newline ourselves and consume, so exactly one is added
                        // regardless of how the TextEdit's own newline action would resolve Shift.
                        te.InsertTextAtCaret("\n");
                    }
                    else if (inSelection)
                    {
                        RunOnCurrentLine(console, state, AcceptSelectionMethod);
                    }
                    else
                    {
                        RunBlock(console, state);
                    }
                    Consume(console);
                    return false;

                case Key.Tab:
                    if (inSelection)
                    {
                        NavigateSelectionMethod.Invoke(console, new object[] { 1 });
                    }
                    else
                    {
                        RunOnCurrentLine(console, state, AutocompleteMethod);
                    }
                    Consume(console);
                    return false;

                case Key.Up:
                    if (inSelection)
                    {
                        NavigateSelectionMethod.Invoke(console, new object[] { -1 });
                    }
                    else if (te.GetCaretLine() == 0)
                    {
                        HistoryRecall(console, state, forward: false);
                    }
                    else
                    {
                        return false; // Not consumed: let the TextEdit move the caret up a line.
                    }
                    Consume(console);
                    return false;

                case Key.Down:
                    if (inSelection)
                    {
                        NavigateSelectionMethod.Invoke(console, new object[] { 1 });
                    }
                    else if (te.GetCaretLine() == te.GetLineCount() - 1)
                    {
                        HistoryRecall(console, state, forward: true);
                    }
                    else
                    {
                        return false; // Not consumed: let the TextEdit move the caret down a line.
                    }
                    Consume(console);
                    return false;

                default:
                    return true;
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Multi-line console input error: {e}");
            return true;
        }
    }

    // Focus the overlay instead of the hidden LineEdit when the console opens.
    public static void FocusOverlay(NDevConsole console)
    {
        if (_state.TryGetValue(console, out ConsoleState? state))
        {
            state.Te.CallDeferred(Control.MethodName.GrabFocus);
        }
    }

    // Re-apply our dynamic input height after the game's half/full-screen relayout.
    public static void RelayoutFor(NDevConsole console)
    {
        if (_state.TryGetValue(console, out ConsoleState? state))
        {
            Relayout(console, state);
        }
    }

    private static void OnTextChanged(NDevConsole console, ConsoleState state)
    {
        try
        {
            bool suppressed = state.Suppress;
            state.Suppress = false;

            UpdateGutter(console, state);
            Relayout(console, state);

            // Re-filter an open completion menu as the user narrows it by typing.
            if (!suppressed && TabCompletion(console).InSelectionMode)
            {
                RunOnCurrentLine(console, state, OnInputTextChangedMethod, passCurrentLine: true);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Multi-line console text-changed error: {e}");
        }
    }

    // Runs each non-blank line of the buffer as its own command, in order, echoing it with the "> "
    // prompt and stopping at the first failure (or exception). The whole block is cleared afterward
    // regardless of success or failure.
    private static void RunBlock(NDevConsole console, ConsoleState state)
    {
        TextEdit te = state.Te;
        RichTextLabel output = OutputBuffer(console);
        DevConsole devConsole = DevConsoleOf(console);
        string prompt = SymbolPrompt(console);
        string warning = SymbolWarning(console);

        foreach (string rawLine in te.Text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            output.Text += $"[color=#00ff00]{prompt}[/color] {line}\n";

            // Built-ins handled by NDevConsole.ProcessCommand itself, mirrored per line.
            if (line.Equals("clear"))
            {
                output.Text = string.Empty;
                continue;
            }
            if (line.Equals("exit"))
            {
                console.HideConsole();
                break;
            }

            CmdResult result;
            try
            {
                result = devConsole.ProcessCommand(line);
            }
            catch (Exception ex)
            {
                output.Text += $"[color=#ff5555]{warning} An exception occurred: {ex}[/color]\n";
                MainFile.Logger.Error($"Console command '{line}' threw: {ex}");
                break; // Fail-stop.
            }

            if (result.success)
            {
                output.Text += result.msg + "\n";
            }
            else
            {
                output.Text += $"[color=#ff5555]{warning} {result.msg}[/color]\n";
                break; // Fail-stop.
            }
        }

        state.Suppress = true;
        te.Text = string.Empty;
        te.SetCaretLine(0);
        te.SetCaretColumn(0);
        ExitSelectionModeMethod.Invoke(console, null);
        RefreshView(console, state);
    }

    // Marshals the caret's line into the hidden LineEdit, invokes one of the game's own completion
    // methods (which operate on that LineEdit), then splices the result back onto the same line of
    // the TextEdit. This reuses vanilla + BaseLib completion behavior unchanged, scoped to one line.
    private static void RunOnCurrentLine(NDevConsole console, ConsoleState state, MethodInfo method, bool passCurrentLine = false)
    {
        TextEdit te = state.Te;
        LineEdit scratch = Scratch(console);
        TabCompletionState tabCompletion = TabCompletion(console);

        int line = te.GetCaretLine();
        string current = te.GetLine(line);
        int column = Math.Min(te.GetCaretColumn(), current.Length);

        // ProgrammaticTextChange suppresses the scratch LineEdit's own TextChanged side effects when
        // we seed it; the explicit Invoke below is what actually does the completion work.
        tabCompletion.ProgrammaticTextChange = true;
        scratch.Text = current;
        scratch.CaretColumn = column;

        method.Invoke(console, passCurrentLine ? new object[] { current } : null);

        string updated = scratch.Text;
        int updatedColumn = scratch.CaretColumn;
        if (updated != current)
        {
            state.Suppress = true;
            te.SetLine(line, updated);
        }
        te.SetCaretLine(line);
        te.SetCaretColumn(Math.Min(updatedColumn, updated.Length));
    }

    // Reproduces NDevConsole's up/down history recall against the TextEdit. Up at the top line steps
    // back through history; Down at the bottom line steps forward. Recalled commands replace the
    // whole buffer (they are single-line). Mirrors the vanilla duplicate-skipping logic.
    private static void HistoryRecall(NDevConsole console, ConsoleState state, bool forward)
    {
        DevConsole devConsole = DevConsoleOf(console);
        TextEdit te = state.Te;

        if (devConsole.historyIndex >= devConsole.history.Count)
        {
            return;
        }

        string text = devConsole.history[devConsole.historyIndex];
        state.Suppress = true;
        te.Text = text;

        if (!forward && devConsole.historyIndex < devConsole.history.Count - 1)
        {
            devConsole.historyIndex++;
            while (devConsole.historyIndex < devConsole.history.Count - 1
                && devConsole.history[devConsole.historyIndex] == text)
            {
                devConsole.historyIndex++;
            }
        }
        else if (forward && devConsole.historyIndex > 0)
        {
            devConsole.historyIndex--;
            while (devConsole.historyIndex > 0 && devConsole.history[devConsole.historyIndex] == text)
            {
                devConsole.historyIndex--;
            }
        }

        int last = te.GetLineCount() - 1;
        te.SetCaretLine(last);
        te.SetCaretColumn(te.GetLine(last).Length);
        RefreshView(console, state);
    }

    private static void UpdateGutter(NDevConsole console, ConsoleState state)
    {
        TextEdit te = state.Te;
        string prompt = SymbolPrompt(console);
        int lines = te.GetLineCount();
        for (int i = 0; i < lines; i++)
        {
            te.SetLineGutterText(i, state.Gutter, prompt);
            te.SetLineGutterItemColor(i, state.Gutter, i == 0 ? PromptColor : ContinuationColor);
        }
    }

    // Grows the input strip upward to fit the typed lines (capped), shrinking the output area to
    // match, in both half-screen and full-screen layouts. Falls back to the vanilla 40px baseline
    // for a single line.
    private static void Relayout(NDevConsole console, ConsoleState state)
    {
        TextEdit te = state.Te;
        Vector2 viewport = console.GetViewportRect().Size;
        int lineHeight = te.GetLineHeight();
        int lines = Math.Clamp(te.GetLineCount(), 1, MaxVisibleLines);

        float inputHeight = Math.Max(40f, lines * lineHeight + 12f);
        float panelHeight = IsFullscreen(console) ? viewport.Y : viewport.Y * 0.5f;
        // Never let the input box eat the entire panel; always leave room for some output.
        inputHeight = Math.Min(inputHeight, Math.Max(40f, panelHeight - 40f));

        // te is a direct child of the console Panel, so these are panel-local coordinates: park the
        // input strip at the bottom of the half/full panel and let the output area fill above it.
        te.Position = new Vector2(0f, panelHeight - inputHeight);
        te.Size = new Vector2(viewport.X, inputHeight);
        OutputBuffer(console).Size = new Vector2(viewport.X, panelHeight - inputHeight);
        TabBuffer(console).Size = new Vector2(viewport.X, panelHeight - inputHeight);
    }

    // Refresh the gutter and layout after a programmatic edit to the TextEdit. Setting the Text
    // property (history recall, clearing the block) doesn't emit text_changed, so OnTextChanged
    // won't fire for those — call this directly instead.
    private static void RefreshView(NDevConsole console, ConsoleState state)
    {
        UpdateGutter(console, state);
        Relayout(console, state);
    }

    private static void Consume(NDevConsole console) => console.GetViewport().SetInputAsHandled();
}

// Swaps in the multi-line overlay once the console has finished building its nodes.
[HarmonyPatch(typeof(NDevConsole), "_Ready")]
public static class MultilineConsoleReadyPatch
{
    public static void Postfix(NDevConsole __instance)
    {
        MultilineConsoleManager.Setup(__instance);
    }
}

// Routes console key input through the overlay. Returning false skips the original handler.
[HarmonyPatch(typeof(NDevConsole), "_Input")]
public static class MultilineConsoleInputHandlerPatch
{
    public static bool Prefix(NDevConsole __instance, InputEvent inputEvent)
    {
        return MultilineConsoleManager.HandleInput(__instance, inputEvent);
    }
}

// The console grabs focus on the hidden LineEdit when shown; focus the overlay instead.
[HarmonyPatch(typeof(NDevConsole), "ShowConsole")]
public static class MultilineConsoleShowPatch
{
    public static void Postfix(NDevConsole __instance)
    {
        MultilineConsoleManager.FocusOverlay(__instance);
    }
}

// Re-apply the dynamic input height after the game's own half/full-screen relayout.
[HarmonyPatch(typeof(NDevConsole), "MakeHalfScreen")]
public static class MultilineConsoleHalfScreenPatch
{
    public static void Postfix(NDevConsole __instance)
    {
        MultilineConsoleManager.RelayoutFor(__instance);
    }
}

[HarmonyPatch(typeof(NDevConsole), "MakeFullScreen")]
public static class MultilineConsoleFullScreenPatch
{
    public static void Postfix(NDevConsole __instance)
    {
        MultilineConsoleManager.RelayoutFor(__instance);
    }
}
