using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Vfx.Utilities;
using MegaCrit.Sts2.Core.Random;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// Lets you ban characters from the "Random" character pick. While the "Random" option is the
// current selection, right-click a character button to toggle it off (it gets a red X);
// right-click again to re-enable it. Embarking as Random then rolls only among the characters
// that are still enabled. Only applies on the standard character select screen — the custom-run
// screen reuses the same button but has no Random option, so the ban semantics are skipped there.
//
// The ban UI is only live while Random is selected: clicking onto a specific character hides the
// red tint (the bans are remembered, and reappear when you click back onto Random). The bans are
// also forgotten each time you re-enter the character select screen — they never persist.
//
// Multiplayer-safe by design. Vanilla never sends the resolved Random character over the wire:
// StartRunLobby.BeginRunLocally runs on every peer and recomputes each Random pick deterministically
// from the shared seed. Narrowing that pool with one player's local bans would make peers disagree
// and desync the lobby. So instead of touching the deterministic roll, we resolve the *local*
// player's Random pick to a concrete character the instant they ready up — before the begin-run
// message is sent — via SetLocalCharacter, which broadcasts the choice through the game's normal
// character-sync message. Every peer then receives a concrete character and never recomputes it.
// Other players' un-banned Random picks still resolve identically across peers, so nothing desyncs.
//
// When you haven't banned anyone the local player keeps RandomCharacter, so the vanilla seeded
// resolution (and its reveal animation) is left completely untouched.
public static class RandomCharacterExclusionManager
{
    // Excluded characters by ModelId entry (stable SCREAMING_SNAKE string). Lives only for the
    // current visit to the character select screen — cleared on every (re)open.
    private static readonly HashSet<string> _excluded = new();

    // Live character buttons, so we can repaint them. Pruned of freed nodes.
    private static readonly List<NCharacterSelectButton> _buttons = new();

    // Whether the "Random" option is the current selection. The ban UI is only active then.
    private static bool _randomSelected;

    // True after we've swapped the local player's RandomCharacter for a concrete pick on ready,
    // so that un-readying (multiplayer) can restore Random and re-roll on the next ready.
    private static bool _resolvedFromRandom;

    // True once the player has committed (pressed embark/ready); suppresses the ban marks so they
    // don't linger over the fade-out / black transition. Cleared on un-ready and on screen open.
    private static bool _committing;

    // The rolled character for this screen visit, cached so a ready/unready cycle keeps the same
    // pick. Re-rolled only when it is no longer allowed (i.e. it got banned). Reset on screen open.
    private static string? _rolledCharacterId;

    // A seed captured once per visit so the roll is deterministic for the session. Standard mode has
    // no run seed until the run begins, so this stands in for it (a real lobby.Seed wins if present).
    private static string? _sessionSeed;

    // Red "banned" frame + X overlaid on excluded characters.
    private const string MarkName = "CpkExclusionMark";
    private const string MarkTexturePath = "res://ColinsPatchKit/assets/disabled_char.png";
    private static Texture2D? _markTexture;

    // Called from the Init postfix once per character button, every time the screen is built.
    public static void OnButtonInitialized(NCharacterSelectButton button)
    {
        if (!_buttons.Contains(button))
        {
            _buttons.Add(button);
            // NClickableControl emits MousePressed for both left and right clicks while enabled.
            button.Connect(NClickableControl.SignalName.MousePressed,
                Callable.From<InputEvent>(e => OnButtonMousePressed(button, e)));
            // The signal is connected on the button itself, so it dies with the node; we only
            // need to stop tracking it for the repaint pass.
            button.Connect(Node.SignalName.TreeExiting, Callable.From(() => _buttons.Remove(button)));
        }
        ApplyVisual(button);
    }

    // Bans never persist across visits — reset them whenever the screen opens.
    public static void OnScreenOpened()
    {
        _excluded.Clear();
        _randomSelected = false;
        _resolvedFromRandom = false;
        _committing = false;
        _rolledCharacterId = null;
        _sessionSeed = null;
        RefreshAll();
    }

    // Track whether the current selection is the Random option, then repaint: the red tint is
    // only shown while Random is selected, but the underlying ban set is preserved either way.
    public static void OnCharacterSelected(NCharacterSelectButton selectedButton)
    {
        _randomSelected = selectedButton != null && selectedButton.IsRandom;
        RefreshAll();
    }

    // Resolve (or, on un-ready, un-resolve) the local player's Random pick around the ready commit.
    public static void OnSetReady(StartRunLobby lobby, bool ready)
    {
        if (lobby == null)
        {
            return;
        }
        bool multiplayer = lobby.NetService.Type.IsMultiplayer();

        if (!ready)
        {
            // Un-ready: drop the embark suppression and hand Random back so the next ready re-rolls
            // (multiplayer only — singleplayer begins immediately on ready and never un-readies).
            _committing = false;
            if (_resolvedFromRandom)
            {
                if (multiplayer)
                {
                    // Re-selecting the Random button restores SetLocalCharacter(Random) via
                    // SelectCharacter and flips _randomSelected back on, bringing the ban marks back.
                    SelectButton(b => b.IsRandom);
                }
                else
                {
                    lobby.SetLocalCharacter(ModelDb.Character<RandomCharacter>());
                }
                _resolvedFromRandom = false;
            }
            RefreshAll();
            return;
        }

        if (!ColinsPatchKitConfig.ExcludeCharactersFromRandom
            || lobby.LocalPlayer.character is not RandomCharacter
            || _excluded.Count == 0)
        {
            return;
        }
        CharacterModel? chosen = ResolveRandomChar(lobby);
        if (chosen == null)
        {
            // Everything banned — leave Random alone and let vanilla resolution handle it.
            return;
        }

        if (multiplayer)
        {
            // Once you ready up in multiplayer the pick is already visible in the lobby, so switch the
            // selector to the chosen character. Selecting its button routes through SelectCharacter ->
            // SetLocalCharacter(chosen) (which syncs the pick to peers) and flips _randomSelected off,
            // which hides the ban marks. Un-readying re-selects Random and brings them back.
            if (SelectButton(b => !b.IsRandom && b.Character?.Id.Entry == chosen.Id.Entry))
            {
                KeepEmbarkDisabled(lobby);
            }
            else
            {
                lobby.SetLocalCharacter(chosen);
            }
        }
        else
        {
            // Singleplayer: keep the selector on Random and play the vanilla reveal. Vanilla resolves a
            // Random pick in BeginRunLocally with isRandomCharacterResolution: true, which makes the
            // screen show the chosen character's background and hold for a beat before the run starts.
            // We resolved early (to keep the choice off the wire), so replay that notification here.
            //
            // SetLocalCharacter on a concrete character resets the Ascension to that character's
            // *preferred* level (SetSingleplayerAscensionAfterCharacterChanged). Vanilla's Random
            // resolution instead keeps the Ascension the player chose, only clamping it down to the
            // rolled character's max — so capture the selection first and re-apply that clamp.
            int chosenAscension = lobby.Ascension;
            lobby.SetLocalCharacter(chosen);
            lobby.SyncAscensionChange(Math.Min(chosenAscension, lobby.MaxAscension));
            lobby.LobbyListener.PlayerChanged(lobby.LocalPlayer, isRandomCharacterResolution: true);
        }
        _resolvedFromRandom = true;
    }

    // Pick a non-banned character, cached for the visit so a ready/unready cycle yields the same one
    // (banning a different character must not change it, so this caches rather than re-deriving from
    // the pool). Re-rolls only when the cached pick itself is no longer allowed.
    private static CharacterModel? ResolveRandomChar(StartRunLobby lobby)
    {
        // Reuse the cached pick while it is still allowed, so a ready/unready cycle keeps the same
        // one. Only when it is missing or has since been banned do we build the pool and re-roll.
        if (_rolledCharacterId != null && !_excluded.Contains(_rolledCharacterId))
        {
            CharacterModel? cached = ModelDb.AllCharacters.FirstOrDefault(c => c.Id.Entry == _rolledCharacterId);
            if (cached != null)
            {
                return cached;
            }
        }
        List<CharacterModel> pool = ModelDb.AllCharacters.Where(c => !_excluded.Contains(c.Id.Entry)).ToList();
        if (pool.Count == 0)
        {
            return null;
        }
        // Seed the roll so it is reproducible: a real lobby seed if one exists, otherwise a value
        // captured once for this visit (standard mode has no run seed until the run actually begins).
        string seed = lobby.Seed ?? (_sessionSeed ??= SeedHelper.GetRandomSeed());
        CharacterModel? chosen = new Rng((uint)StringHelper.GetDeterministicHashCode(seed)).NextItem(pool);
        if (chosen != null)
        {
            _rolledCharacterId = chosen.Id.Entry;
        }
        return chosen;
    }

    // Programmatically select the first live button matching the predicate. Select() routes through
    // the screen's SelectCharacter (updating the lobby character, info panel and ban marks).
    private static bool SelectButton(Func<NCharacterSelectButton, bool> predicate)
    {
        NCharacterSelectButton? button = _buttons.FirstOrDefault(b => GodotObject.IsInstanceValid(b) && predicate(b));
        if (button == null)
        {
            return false;
        }
        button.Select();
        return true;
    }

    // OnEmbarkPressed disables the embark button before we run, but selecting the rolled character
    // routes through SelectCharacter which re-enables it. The player has already committed (the
    // waiting/unready UI is in charge now), so put it back to disabled.
    private static FieldInfo? _embarkButtonField;

    private static void KeepEmbarkDisabled(StartRunLobby lobby)
    {
        if (lobby.LobbyListener is not NCharacterSelectScreen screen)
        {
            return;
        }
        _embarkButtonField ??= AccessTools.Field(typeof(NCharacterSelectScreen), "_embarkButton");
        if (_embarkButtonField?.GetValue(screen) is NClickableControl embark)
        {
            embark.Disable();
        }
    }


    // The run is actually starting (all players ready). Hide the ban marks so they don't linger
    // over the fade-out / black transition. This is deliberately at run-begin rather than ready, so
    // a multiplayer player who readies up still sees the marks while waiting for the others.
    public static void OnRunBeginning()
    {
        _committing = true;
        RefreshAll();
    }

    private static void OnButtonMousePressed(NCharacterSelectButton button, InputEvent inputEvent)
    {
        // Right-click only bans while Random is the active selection.
        if (!ColinsPatchKitConfig.ExcludeCharactersFromRandom || !_randomSelected)
        {
            return;
        }
        if (inputEvent is not InputEventMouseButton mouseButton
            || mouseButton.ButtonIndex != MouseButton.Right
            || !mouseButton.IsPressed())
        {
            return;
        }
        // "Random" can't ban itself, and locked characters aren't in the pool anyway.
        if (button.IsRandom || button.IsLocked || button.Character == null)
        {
            return;
        }
        string id = button.Character.Id.Entry;
        if (!_excluded.Remove(id))
        {
            _excluded.Add(id);
        }
        SfxCmd.Play("event:/sfx/ui/clicks/ui_click");
        ApplyVisual(button);
    }

    private static void ApplyVisual(NCharacterSelectButton button)
    {
        bool excluded = ColinsPatchKitConfig.ExcludeCharactersFromRandom
            && _randomSelected
            && !_committing
            && !button.IsRandom
            && button.Character != null
            && _excluded.Contains(button.Character.Id.Entry);
        SetExclusionMark(button, excluded);
    }

    // Show/hide the red frame + X overlaid on the character's icon. The mark is a TextureRect
    // parented to the icon and explicitly sized to the icon's rect each refresh (anchors alone on a
    // freshly-created node with IgnoreSize don't reliably produce a non-zero rect, so it would draw
    // nothing). Sizing to the icon means it follows the button's hover scale and stretches the frame
    // to the portrait edges; its own (absent) material keeps it bright red even while the icon shader
    // desaturates an unselected character.
    private static void SetExclusionMark(NCharacterSelectButton button, bool show)
    {
        TextureRect? icon = button.GetNodeOrNull<TextureRect>("%Icon");
        if (icon == null)
        {
            return;
        }
        TextureRect? mark = icon.GetNodeOrNull<TextureRect>(MarkName);
        if (mark == null)
        {
            if (!show)
            {
                return;
            }
            _markTexture ??= ResourceLoader.Load<Texture2D>(MarkTexturePath);
            if (_markTexture == null)
            {
                MainFile.Logger.Error($"Random-character exclusion overlay missing at {MarkTexturePath}");
                return;
            }
            mark = new TextureRect
            {
                Name = MarkName,
                Texture = _markTexture,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ZIndex = 2,
            };
            icon.AddChildSafely(mark);
        }
        // Layout is settled by the time a ban can exist (the set is empty on every screen open).
        mark.Position = Vector2.Zero;
        mark.Size = icon.Size;
        mark.Visible = show;
    }

    // Repaint every live button (e.g. on selection change or config toggle).
    public static void RefreshAll()
    {
        _buttons.RemoveAll(b => !GodotObject.IsInstanceValid(b));
        foreach (NCharacterSelectButton button in _buttons)
        {
            ApplyVisual(button);
        }
    }
}

[HarmonyPatch(typeof(NCharacterSelectButton), "Init")]
public static class RandomCharacterExclusionButtonPatch
{
    // `del` is the owning screen. Only the standard character select screen has a Random option,
    // so skip the custom-run screen (which reuses this button but never offers Random).
    public static void Postfix(NCharacterSelectButton __instance, ICharacterSelectButtonDelegate del)
    {
        try
        {
            if (del is NCharacterSelectScreen)
            {
                RandomCharacterExclusionManager.OnButtonInitialized(__instance);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to wire up random-character exclusion on button: {e}");
        }
    }
}

[HarmonyPatch(typeof(NCharacterSelectScreen), "SelectCharacter")]
public static class RandomCharacterExclusionSelectPatch
{
    public static void Postfix(NCharacterSelectButton charSelectButton)
    {
        try
        {
            RandomCharacterExclusionManager.OnCharacterSelected(charSelectButton);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to update random-character exclusion on selection: {e}");
        }
    }
}

[HarmonyPatch(typeof(NCharacterSelectScreen), "OnSubmenuOpened")]
public static class RandomCharacterExclusionScreenOpenPatch
{
    public static void Postfix()
    {
        try
        {
            RandomCharacterExclusionManager.OnScreenOpened();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to reset random-character exclusions on screen open: {e}");
        }
    }
}

[HarmonyPatch(typeof(StartRunLobby), "SetReady")]
public static class RandomCharacterExclusionReadyPatch
{
    // Resolve the local player's Random pick to a concrete character before the ready commit, so
    // the choice is broadcast through SetLocalCharacter and no peer recomputes it. Prefix so it
    // runs before SetReady builds/sends its ready message and (in singleplayer) begins the run.
    public static void Prefix(StartRunLobby __instance, bool ready)
    {
        try
        {
            RandomCharacterExclusionManager.OnSetReady(__instance, ready);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to resolve excluded random character on ready: {e}");
        }
    }
}

[HarmonyPatch(typeof(NRemoteLobbyPlayer), "_Process")]
public static class RandomCharacterExclusionNameplateShakePatch
{
    private static FieldInfo? _shakeField;

    // A lobby nameplate "punches" (shakes) whenever its player's character changes. Vanilla's
    // _Process animates the offset while the punch runs but never restores the resting position once
    // it finishes — it just stops updating, leaving a small residual nudge. Vanilla gets away with it
    // because Random only resolves at the transition; our early ready-time switch (and the matching
    // change arriving for other modded players) makes that punch fire while the lobby lingers on
    // "waiting for players", so the leftover offset becomes visible. Snap the nameplate back as soon
    // as its punch reports done.
    public static void Postfix(NRemoteLobbyPlayer __instance)
    {
        if (!ColinsPatchKitConfig.ExcludeCharactersFromRandom)
        {
            return;
        }
        try
        {
            _shakeField ??= AccessTools.Field(typeof(NRemoteLobbyPlayer), "_shake");
            if (_shakeField?.GetValue(__instance) is ShakeInstance { IsDone: true })
            {
                __instance.CancelShake();
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to settle lobby nameplate shake: {e}");
        }
    }
}

[HarmonyPatch(typeof(StartRunLobby), "BeginRunLocally")]
public static class RandomCharacterExclusionBeginRunPatch
{
    // Runs once the lobby actually begins the run (in multiplayer, only after every player is
    // ready). Hide the ban marks here rather than on ready, so a waiting multiplayer player keeps
    // seeing them until the transition starts.
    public static void Prefix()
    {
        try
        {
            RandomCharacterExclusionManager.OnRunBeginning();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to hide random-character exclusion marks on run begin: {e}");
        }
    }
}
