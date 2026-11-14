using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using ColinsPatchKit.ColinsPatchKitCode.Patches;
using GameDevConsole = MegaCrit.Sts2.Core.DevConsole.DevConsole;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Automated assertion harness for the random-character bans (RandomCharacterExclusionPatch) on a
// given screen: the standard character select (both branches) or Blitz's (v0.113.0+). Unlocks
// everything (Random only shows once every character is unlocked), opens the screen, selects
// Random, right-clicks (real MousePressed events) every character but one to ban it, checks the ban
// marks, embarks, and checks the run started as the one unbanned character with the ascension
// left as chosen. Logs a `randomban-e2e: PASS <assertion>` line per check, saves
// /tmp/random_ban_*.png, switches back to the original profile and quits:
//
//   "Slay the Spire 2" --randomban-e2e=<profile> [--randomban-screen=standard|blitz]
//
// The screen defaults to standard. The argument is a scratch profile id: its progress gets
// everything unlocked, and any run in progress on it is abandoned — never pass a real profile.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class RandomBanE2EPatch
{
    private const string Tag = "randomban-e2e";
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
            await E2EHelpers.Shot(tree, "/tmp/random_ban_fail.png", Tag);
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
        bool blitz = CommandLineHelper.TryGetValue("randomban-screen", out string? which) && which == "blitz";
        Control mainMenu = await E2EHelpers.PrepareMainMenu(tree, Tag, ct);

        CmdResult unlock = new GameDevConsole(shouldAllowDebugCommands: true).ProcessCommand("unlock all");
        MainFile.Logger.Info($"{Tag}: unlock all -> {unlock.success} {unlock.msg}");

        Control screen = blitz ? await OpenBlitzScreen(mainMenu, ct) : await OpenStandardScreen(mainMenu, ct);
        await Task.Delay(800);
        Check(RandomCharacterExclusionManager.IsActiveScreen(screen), $"{screen.GetType().Name} is the active ban screen");

        List<NCharacterSelectButton> buttons = UiHelper.FindAll<NCharacterSelectButton>(screen.GetNode("CharSelectButtons/ButtonContainer"));
        NCharacterSelectButton random = buttons.Single(b => b.IsRandom);
        Check(random.Visible && !random.IsLocked, "Random is visible and unlocked");
        List<NCharacterSelectButton> characters = buttons.Where(b => !b.IsRandom && b.Visible && !b.IsLocked).ToList();
        Check(characters.Count >= 2, $"{characters.Count} selectable characters");

        random.Select();
        await Task.Delay(300);
        NCharacterSelectButton keep = characters[1];
        List<NCharacterSelectButton> banned = characters.Where(b => b != keep).ToList();
        foreach (NCharacterSelectButton b in banned)
        {
            b.EmitSignal(NClickableControl.SignalName.MousePressed,
                new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true });
            await Task.Delay(100);
        }
        await Task.Delay(300);
        await E2EHelpers.Shot(tree, $"/tmp/random_ban_{(blitz ? "blitz" : "standard")}.png", Tag);
        Check(banned.All(RandomCharacterExclusionManager.HasActiveBanMark), $"all {banned.Count} banned characters show the ban mark");
        Check(!RandomCharacterExclusionManager.HasActiveBanMark(keep), $"{keep.Character.Id.Entry} (kept) shows no ban mark");
        Check(random.IsSelected, "Random is still the selection");

        StartRunLobby lobby = (StartRunLobby)AccessTools.Field(screen.GetType(), "_lobby").GetValue(screen)!;
        int ascension = lobby.Ascension;
        MainFile.Logger.Info($"{Tag}: embarking as Random at ascension {ascension}");
        await UiHelper.Click(screen.GetNode<NButton>("ConfirmButton"));

        await WaitHelper.Until(() => RunManager.Instance.DebugOnlyGetState() != null, ct, TimeSpan.FromSeconds(60), "run did not start");
        RunState runState = RunManager.Instance.DebugOnlyGetState()!;
        await WaitHelper.Until(() => runState.CurrentRoom != null && runState.CurrentRoom.RoomType != RoomType.Unassigned,
            ct, TimeSpan.FromSeconds(30), "no room assigned");
        string character = runState.Players[0].Character.Id.Entry;
        Check(character == keep.Character.Id.Entry, $"the run's character is the unbanned {keep.Character.Id.Entry} (got {character})");
        Check(BranchCompat.IsBlitz(runState) == blitz, $"the run's mode matches the screen (GameMode={runState.GameMode})");
        Check(runState.AscensionLevel == ascension, $"the run kept the chosen ascension {ascension} (got {runState.AscensionLevel})");
    }

    private static async Task<Control> OpenStandardScreen(Control mainMenu, CancellationToken ct)
    {
        await UiHelper.Click(mainMenu.GetNode<NButton>("MainMenuTextButtons/SingleplayerButton"));
        Control? charSelect = null;
        NButton? standardButton = null;
        await WaitHelper.Until(() =>
        {
            charSelect = mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen");
            Control? submenu = mainMenu.GetNodeOrNull<Control>("Submenus/SingleplayerSubmenu");
            standardButton = submenu?.GetNodeOrNull<NButton>("%StandardButton") ?? submenu?.GetNodeOrNull<NButton>("StandardButton");
            return (charSelect?.Visible ?? false) || (standardButton?.Visible ?? false);
        }, ct, TimeSpan.FromSeconds(15), "no singleplayer submenu or character select");
        if (!(charSelect?.Visible ?? false))
        {
            await UiHelper.Click(standardButton!);
            await WaitHelper.Until(() => mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen")?.Visible ?? false,
                ct, TimeSpan.FromSeconds(15), "character select did not appear");
        }
        return mainMenu.GetNode<Control>("Submenus/CharacterSelectScreen");
    }

    private static async Task<Control> OpenBlitzScreen(Control mainMenu, CancellationToken ct)
    {
        await UiHelper.Click(mainMenu.GetNode<NButton>("MainMenuTextButtons/SingleplayerButton"));
        Control? submenu = null;
        await WaitHelper.Until(() =>
        {
            submenu = mainMenu.GetNodeOrNull<Control>("Submenus/SingleplayerSubmenu");
            return submenu?.Visible ?? false;
        }, ct, TimeSpan.FromSeconds(15), "singleplayer submenu did not appear");
        NButton blitzButton = submenu!.GetNodeOrNull<NButton>("%BlitzButton")
            ?? throw new InvalidOperationException("FAILED: no Blitz button — Blitz needs the v0.113.0+ branch");
        MethodInfo open = AccessTools.Method(submenu.GetType(), "OpenBlitzScreen")
            ?? throw new InvalidOperationException("FAILED: no OpenBlitzScreen");
        open.Invoke(submenu, new object[] { blitzButton });
        Control? blitzScreen = null;
        await WaitHelper.Until(() =>
        {
            blitzScreen = AllNodes(mainMenu).OfType<Control>().FirstOrDefault(n => n.GetType().Name == "NBlitzScreen" && n.Visible);
            return blitzScreen != null;
        }, ct, TimeSpan.FromSeconds(15), "Blitz screen did not appear");
        return blitzScreen!;
    }

    private static IEnumerable<Node> AllNodes(Node root)
    {
        foreach (Node child in root.GetChildren())
        {
            yield return child;
            foreach (Node n in AllNodes(child))
            {
                yield return n;
            }
        }
    }

    private static void Check(bool condition, string assertion)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"FAILED: {assertion}");
        }
        MainFile.Logger.Info($"{Tag}: PASS {assertion}");
    }
}
