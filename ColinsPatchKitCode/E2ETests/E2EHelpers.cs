using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Shared plumbing for the launch-flag e2e harnesses: scratch-profile switching
// and driving the main menu into a throwaway singleplayer run.
internal static class E2EHelpers
{
    // Handles the scratch-profile switch dance from a NMainMenu._Ready postfix.
    // Returns true once the menu is running on the target profile; otherwise
    // requests the switch (at most once, tracked via originalProfileId) and
    // returns false — the menu reload re-enters the caller's postfix.
    public static bool EnsureProfile(SceneTree tree, int targetProfile, ref int originalProfileId, string tag)
    {
        if (SaveManager.Instance.CurrentProfileId == targetProfile)
        {
            return true;
        }
        if (originalProfileId >= 0)
        {
            return false;
        }
        originalProfileId = SaveManager.Instance.CurrentProfileId;
        int from = originalProfileId;
        tree.CreateTimer(1.0).Timeout += () =>
        {
            MainFile.Logger.Info($"{tag}: switching profile {from} -> {targetProfile}");
            SaveManager.Instance.SwitchProfileId(targetProfile);
            SaveManager.Instance.InitPrefsData();
            SaveManager.Instance.InitProgressData();
            NGame.Instance!.ReloadMainMenu();
        };
        return false;
    }

    public static void RestoreProfile(int originalProfileId, string tag)
    {
        if (originalProfileId < 0)
        {
            return;
        }
        MainFile.Logger.Info($"{tag}: switching back to profile {originalProfileId}");
        SaveManager.Instance.SwitchProfileId(originalProfileId);
        SaveManager.Instance.InitPrefsData();
        SaveManager.Instance.InitProgressData();
    }

    // Abandons any leftover test run on the current (scratch) profile, then
    // clicks through the main menu and character select into a fresh
    // singleplayer run. Returns once a room is assigned and settled.
    // preferredCharacterId (a SCREAMING_SNAKE model id entry, e.g. "IRONCLAD") selects that
    // character when it is unlocked; null keeps the default of the first unlocked character.
    public static async Task<RunState> StartThrowawayRun(SceneTree tree, string tag, CancellationToken ct,
        string? preferredCharacterId = null)
    {
        SaveManager.Instance.SetFtuesEnabled(enabled: false);
        Control mainMenu = await WaitHelper.ForNode<Control>(tree.Root, "/root/Game/RootSceneContainer/MainMenu", ct, TimeSpan.FromSeconds(30));

        NButton? abandon = mainMenu.GetNodeOrNull<NButton>("MainMenuTextButtons/AbandonRunButton");
        if (abandon != null && abandon.Visible)
        {
            MainFile.Logger.Info($"{tag}: abandoning leftover test run on the scratch profile");
            await UiHelper.Click(abandon);
            await WaitHelper.Until(() => NModalContainer.Instance?.OpenModal != null,
                ct, TimeSpan.FromSeconds(15), "abandon confirmation did not appear");
            Node modal = (Node)NModalContainer.Instance!.OpenModal!;
            await UiHelper.Click(modal.GetNode<NButton>("VerticalPopup/YesButton"));
            await WaitHelper.Until(() => NModalContainer.Instance.OpenModal == null,
                ct, TimeSpan.FromSeconds(15), "abandon confirmation did not close");
        }

        MainFile.Logger.Info($"{tag}: starting run");
        await UiHelper.Click(mainMenu.GetNode<NButton>("MainMenuTextButtons/SingleplayerButton"));
        Control? charSelect = null;
        NButton? standardButton = null;
        await WaitHelper.Until(() =>
        {
            charSelect = mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen");
            standardButton = mainMenu.GetNodeOrNull<NButton>("Submenus/SingleplayerSubmenu/StandardButton");
            return (charSelect?.Visible ?? false) || (standardButton?.Visible ?? false);
        }, ct, TimeSpan.FromSeconds(15), "no singleplayer submenu or character select");
        if ((standardButton?.Visible ?? false) && !(charSelect?.Visible ?? false))
        {
            await UiHelper.Click(standardButton!);
            await WaitHelper.Until(() => mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen")?.Visible ?? false,
                ct, TimeSpan.FromSeconds(15), "character select did not appear");
            charSelect = mainMenu.GetNode<Control>("Submenus/CharacterSelectScreen");
        }

        Node buttonContainer = charSelect!.GetNode("CharSelectButtons/ButtonContainer");
        List<NCharacterSelectButton> characters = UiHelper.FindAll<NCharacterSelectButton>(buttonContainer);
        foreach (NCharacterSelectButton button in characters)
        {
            button.UnlockIfPossible();
        }
        NCharacterSelectButton chosen =
            (preferredCharacterId != null
                ? characters.FirstOrDefault(b => !b.IsLocked && b.Character.Id.Entry == preferredCharacterId)
                : null)
            ?? characters.First(b => !b.IsLocked);
        if (preferredCharacterId != null && chosen.Character.Id.Entry != preferredCharacterId)
        {
            MainFile.Logger.Info($"{tag}: '{preferredCharacterId}' not available, falling back to {chosen.Character.Id.Entry}");
        }
        MainFile.Logger.Info($"{tag}: selecting {chosen.Character.Id}");
        chosen.Select();
        await Task.Delay(200);
        await UiHelper.Click(await WaitHelper.ForNode<NButton>(mainMenu, "Submenus/CharacterSelectScreen/ConfirmButton", ct));

        await WaitHelper.Until(() => RunManager.Instance.DebugOnlyGetState() != null, ct, TimeSpan.FromSeconds(60), "run did not start");
        RunState runState = RunManager.Instance.DebugOnlyGetState()!;
        await WaitHelper.Until(() => runState.CurrentRoom != null && runState.CurrentRoom.RoomType != RoomType.Unassigned,
            ct, TimeSpan.FromSeconds(30), "no room assigned");
        await Task.Delay(1500);
        return runState;
    }

    public static async Task Shot(SceneTree tree, string path, string tag)
    {
        await Task.Delay(100);
        Image image = tree.Root.GetTexture().GetImage();
        Error err = image.SavePng(path);
        MainFile.Logger.Info($"{tag}: shot '{path}' ({err})");
    }
}
