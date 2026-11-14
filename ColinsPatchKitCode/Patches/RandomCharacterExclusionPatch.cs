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
// that are still enabled. Applies on every screen with a Random option: the standard character
// select and v0.113.0's Blitz screen (see RandomCharacterScreens). The custom-run screen reuses the
// same button but has no Random option, and v0.113.0's Dread screen only has a deterministic
// "Rotating" pick, so the ban semantics are skipped on both.
//
// Right-clicking a character when Random is *not* the current selection jumps the selection to
// Random and bans that character in one gesture (it never toggles in this case — an already-banned
// character stays banned), so you can ban straight from a concrete character without first clicking
// Random. Once Random is selected, right-click reverts to the toggle behavior above.
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

    // Live character buttons, so we can repaint them, each mapped to the screen that owns it (the
    // standard and Blitz screens both stay alive as main-menu submenus, so their buttons coexist).
    // Pruned of freed nodes.
    private static readonly Dictionary<NCharacterSelectButton, Node> _buttons = new();

    // The Random-offering screen currently open; only its buttons take part in bans.
    private static Node? _activeScreen;

    // Whether the "Random" option is the current selection. The ban UI is only active then.
    private static bool _randomSelected;

    // True after we've swapped the local player's RandomCharacter for a concrete pick on ready,
    // so that un-readying (multiplayer) can restore Random and re-roll on the next ready.
    private static bool _resolvedFromRandom;

    public static bool ResolvedFromRandom => _resolvedFromRandom;

    // True once the player has committed (pressed embark/ready); suppresses the ban marks so they
    // don't linger over the fade-out / black transition. Cleared on un-ready and on screen open.
    private static bool _committing;

    // The rolled character for this screen visit, cached so a ready/unready cycle keeps the same
    // pick. Re-rolled only when it is no longer allowed (i.e. it got banned). Reset on screen open.
    private static string? _rolledCharacterId;

    // A seed captured once per visit so the roll is deterministic for the session. Standard mode has
    // no run seed until the run begins, so this stands in for it (a real lobby.Seed wins if present).
    private static string? _sessionSeed;

    // Red "banned" X overlaid on excluded characters. A soft dark drop shadow hugging the X strokes
    // plus a uniform 20% black scrim (dimming the portrait behind the X) are baked into this image, so
    // the X renders as-authored (no shader). On top of the baked scrim, the portrait's own hsv is held
    // at its resting (dim) state on hover so banning keeps it from lighting up (see the portrait-hold
    // logic below).
    private const string MarkName = "CpkExclusionMark";
    private const string MarkTexturePath = "res://ColinsPatchKit/assets/disabled_char.png";
    private static Texture2D? _markTexture;

    // Resting zoom: scale the mark up 10% (centered) so the X arms reach slightly farther toward the
    // portrait edges; the ragged icon mask clips the overage. The mark sits at MarkIdleScale whenever
    // it's shown — the animate-out fades it without touching scale.
    private static readonly Vector2 MarkIdleScale = Vector2.One * 1.1f;

    // Animate-out: a straight 100ms modulate:a fade to 0 (no scale pop), so an un-banned X fades out
    // instead of vanishing instantly. A plain TextureRect honors modulate natively, so the fade lands.
    private const float MarkAnimOutDuration = 0.1f;
    private const string MarkAnimMeta = "CpkMarkAnimTween";

    // Called once per character button, every time a screen is built (the Init postfix), and for
    // each screen's Random button on open.
    public static void OnButtonInitialized(NCharacterSelectButton button, Node screen)
    {
        if (!_buttons.ContainsKey(button))
        {
            // NClickableControl emits MousePressed for both left and right clicks while enabled.
            button.Connect(NClickableControl.SignalName.MousePressed,
                Callable.From<InputEvent>(e => OnButtonMousePressed(button, e)));
            // The signal is connected on the button itself, so it dies with the node; we only
            // need to stop tracking it for the repaint pass.
            button.Connect(Node.SignalName.TreeExiting, Callable.From(() => _buttons.Remove(button)));
        }
        _buttons[button] = screen;
        ApplyVisual(button);
    }

    // Bans never persist across visits — reset them whenever a Random-offering screen opens.
    public static void OnScreenOpened(Node screen)
    {
        _activeScreen = screen;
        _excluded.Clear();
        _randomSelected = false;
        _resolvedFromRandom = false;
        _committing = false;
        _rolledCharacterId = null;
        _sessionSeed = null;
        RefreshAll();
    }

    public static bool IsActiveScreen(object? screen) => screen != null && screen == _activeScreen;

    private static bool IsOnActiveScreen(NCharacterSelectButton button) =>
        _activeScreen != null && _buttons.TryGetValue(button, out Node? screen) && screen == _activeScreen;

    private static IEnumerable<NCharacterSelectButton> ActiveButtons =>
        _buttons.Keys.Where(b => GodotObject.IsInstanceValid(b) && IsOnActiveScreen(b));

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
        // Only the open Random-offering screen's lobby has bans. Every other lobby (custom/daily
        // runs, v0.113.0's Dread screen) is left alone so stale bans or resolve state from an
        // earlier visit never leak into it.
        if (lobby == null || !IsActiveScreen(lobby.LobbyListener))
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
                    BranchCompat.SetLocalCharacter(lobby, ModelDb.Character<RandomCharacter>());
                }
                _resolvedFromRandom = false;
            }
            RefreshAll();
            return;
        }

        if (!ColinsPatchKitConfig.ExcludeCharactersFromRandom
            || LocalPlayerCharacter(lobby) is not RandomCharacter
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
                BranchCompat.SetLocalCharacter(lobby, chosen);
            }
        }
        else
        {
            // Singleplayer: keep the selector on Random and play the vanilla reveal. On v0.111.0
            // vanilla resolves a Random pick in BeginRunLocally with isRandomCharacterResolution: true,
            // which makes the screen show the chosen character's background and hold for a beat before
            // the run starts; we resolved early (to keep the choice off the wire), so replay that
            // notification here. v0.113.0 dropped that parameter and instead plays the reveal itself
            // whenever the Random button is still selected at embark — which it is — so there's
            // nothing to replay.
            //
            // SetLocalCharacter on a concrete character resets the Ascension to that character's
            // *preferred* level (SetSingleplayerAscensionAfterCharacterChanged). Vanilla's Random
            // resolution instead keeps the Ascension the player chose, only clamping it down to the
            // rolled character's max — so capture the selection first and re-apply that clamp.
            // (Blitz shares one ascension across characters, so there SetLocalCharacter leaves it
            // alone — re-sync only if it actually moved.)
            int chosenAscension = lobby.Ascension;
            BranchCompat.SetLocalCharacter(lobby, chosen);
            if (lobby.Ascension != chosenAscension)
            {
                lobby.SyncAscensionChange(Math.Min(chosenAscension, lobby.MaxAscension));
            }
            if (PlayerChangedMethod.GetParameters().Length == 2)
            {
                PlayerChangedMethod.Invoke(lobby.LobbyListener,
                    new[] { LocalPlayerProp.GetValue(lobby), (object)true /* isRandomCharacterResolution */ });
            }
        }
        _resolvedFromRandom = true;
    }

    // v0.110.0 renamed the lobby-player type (LobbyPlayer -> StartRunLobbyPlayer), and that type
    // is baked into the IL of StartRunLobby.LocalPlayer's getter and
    // IStartRunLobbyListener.PlayerChanged's parameter list — a compile-time bind to either would
    // throw MissingMethodException on the other branch. Bind both by name, and read the player's
    // `character` field off whichever type the loaded assembly has. PlayerChanged also lost its
    // `isRandomCharacterResolution` parameter in v0.113.0, so it is invoked only where it still
    // takes two.
    private static readonly PropertyInfo LocalPlayerProp =
        AccessTools.Property(typeof(StartRunLobby), "LocalPlayer")
        ?? throw new MissingMemberException(nameof(StartRunLobby), "LocalPlayer");

    private static readonly FieldInfo LobbyPlayerCharacterField =
        AccessTools.Field(LocalPlayerProp.PropertyType, "character")
        ?? throw new MissingFieldException(LocalPlayerProp.PropertyType.Name, "character");

    private static readonly MethodInfo PlayerChangedMethod =
        AccessTools.Method(typeof(IStartRunLobbyListener), "PlayerChanged")
        ?? throw new MissingMethodException(nameof(IStartRunLobbyListener), "PlayerChanged");

    private static CharacterModel? LocalPlayerCharacter(StartRunLobby lobby) =>
        LocalPlayerProp.GetValue(lobby) is { } player
            ? LobbyPlayerCharacterField.GetValue(player) as CharacterModel
            : null;

    // v0.109.0 widened run seeds: StringHelper.GetDeterministicHashCode now returns ulong (was
    // int) and the seed ctor is Rng(ulong) (was Rng(uint, int counter)). One shipped dll runs on
    // both game branches, and a compile-time bind to either shape throws MissingMethodException
    // on the other — so resolve both by reflection and mirror whichever pair the loaded assembly
    // has (same seed -> same roll as that branch's own seeded-Rng call sites).
    private static readonly MethodInfo SeedHashMethod =
        AccessTools.Method(typeof(StringHelper), "GetDeterministicHashCode")
        ?? throw new MissingMethodException(nameof(StringHelper), "GetDeterministicHashCode");

    private static readonly ConstructorInfo SeededRngCtor =
        AccessTools.Constructor(typeof(Rng), new[] { typeof(ulong) })
        ?? AccessTools.Constructor(typeof(Rng), new[] { typeof(uint), typeof(int) })
        ?? throw new MissingMethodException(nameof(Rng), ".ctor");

    private static Rng CreateSeededRng(string seed)
    {
        object rawHash = SeedHashMethod.Invoke(null, new object[] { seed })!;
        // The v0.107 hash is a possibly-negative int that the game truncates with an unchecked
        // uint cast; mirror that here (zero-extended — the uint ctor path keeps the same 32 bits).
        ulong hash = rawHash as ulong? ?? unchecked((uint)(int)rawHash);
        return SeededRngCtor.GetParameters()[0].ParameterType == typeof(ulong)
            ? (Rng)SeededRngCtor.Invoke(new object[] { hash })
            : (Rng)SeededRngCtor.Invoke(new object[] { (uint)hash, 0 });
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
        CharacterModel? chosen = CreateSeededRng(seed).NextItem(pool);
        if (chosen != null)
        {
            _rolledCharacterId = chosen.Id.Entry;
        }
        return chosen;
    }

    // Programmatically select the first live button matching the predicate. Select() routes through
    // the screen's SelectCharacter (updating the lobby character, info panel and ban marks).
    //
    // grabFocus moves OS keyboard focus onto the chosen button. The screen wires selection to each
    // button's FocusEntered signal, so a real click selects only because clicking moves focus. When
    // we switch selection programmatically the previously-clicked button keeps OS focus, and a later
    // click back onto it fires no FocusEntered (it is already focused) — so it would not re-select.
    // Grabbing focus here mirrors what a click would do and keeps the previous button re-selectable.
    private static bool SelectButton(Func<NCharacterSelectButton, bool> predicate, bool grabFocus = false)
    {
        NCharacterSelectButton? button = ActiveButtons.FirstOrDefault(predicate);
        if (button == null)
        {
            return false;
        }
        button.Select();
        if (grabFocus)
        {
            button.GrabFocus();
        }
        return true;
    }

    // OnEmbarkPressed disables the embark button before we run, but selecting the rolled character
    // routes through SelectCharacter which re-enables it. The player has already committed (the
    // waiting/unready UI is in charge now), so put it back to disabled.
    private static void KeepEmbarkDisabled(StartRunLobby lobby)
    {
        if (lobby.LobbyListener is Node screen
            && AccessTools.Field(screen.GetType(), "_embarkButton")?.GetValue(screen) is NClickableControl embark)
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
        if (!ColinsPatchKitConfig.ExcludeCharactersFromRandom)
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
        // Bans only mean anything through the Random option, which vanilla hides until someone has
        // unlocked every character and locks until you have (v0.113.0's Fool resets that for most
        // players). Selecting a hidden/locked Random would leave the screen un-embarkable.
        if (!IsOnActiveScreen(button) || !ActiveButtons.Any(b => b.IsRandom && b.Visible && !b.IsLocked))
        {
            return;
        }
        string id = button.Character.Id.Entry;
        if (!_randomSelected)
        {
            // Random isn't the current selection: right-clicking a character jumps to Random and
            // bans it, rather than toggling. A character that was already banned stays banned. Add
            // the ban first, then select Random so its RefreshAll paints this mark together with any
            // previously-banned characters reappearing.
            _excluded.Add(id);
            SelectButton(b => b.IsRandom, grabFocus: true);
        }
        else if (!_excluded.Remove(id))
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
            && IsOnActiveScreen(button)
            && !button.IsRandom
            && button.Character != null
            && _excluded.Contains(button.Character.Id.Entry);
        SetExclusionMark(button, excluded);
        SyncHoveredPortrait(button, excluded);
    }

    // Show/hide the red frame + X overlaid on the character's icon. The mark is a TextureRect
    // parented to the icon and explicitly sized to the icon's rect each refresh (anchors alone on a
    // freshly-created node with IgnoreSize don't reliably produce a non-zero rect, so it would draw
    // nothing). Sizing to the icon means it follows the button's hover scale and stretches the frame
    // to the portrait edges.
    //
    // The game gives every portrait its ragged edge by nesting the icon under a "Mask" node whose
    // ClipChildren=Only clips the whole subtree to char_select_button_mask.png. Our mark lives inside
    // that subtree (it's a child of %Icon), so it inherits the same ragged clip for free — as long as
    // it does NOT set a ZIndex. A z override would draw the mark in a separate pass that escapes the
    // mask, which is what left the overlay with clean rectangular edges poking past the torn portrait.
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
            };
            icon.AddChildSafely(mark);
        }
        // Layout is settled by the time a ban can exist (the set is empty on every screen open).
        mark.Position = Vector2.Zero;
        mark.Size = icon.Size;
        mark.PivotOffset = icon.Size / 2f; // scale (resting zoom + animate-out) pivots on the center

        if (show)
        {
            // Snap in — vanilla animates the dismiss, not the appear. A re-ban while a previous
            // fade-out is still running must win over it, so kill it and reset the fade/scale state.
            KillTween(mark, MarkAnimMeta);
            mark.Scale = MarkIdleScale;
            mark.Modulate = Colors.White;
            mark.Visible = true;
            return;
        }

        // Hiding. Already gone, or a fade-out already running (let it finish) — nothing to start.
        if (!mark.Visible || mark.HasMeta(MarkAnimMeta))
        {
            return;
        }

        // Fade out: a straight modulate:a fade to 0, then hide and reset modulate so the next show
        // starts clean (scale stays at MarkIdleScale throughout).
        TextureRect closeMark = mark;
        Tween anim = mark.CreateTween();
        anim.TweenProperty(mark, "modulate:a", 0f, MarkAnimOutDuration);
        anim.TweenCallback(Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(closeMark))
            {
                return;
            }
            closeMark.Visible = false;
            closeMark.Modulate = Colors.White;
            closeMark.RemoveMeta(MarkAnimMeta);
        }));
        mark.SetMeta(MarkAnimMeta, anim);
    }

    // Kill and forget a tween parked in a node meta slot (hover-v or animate-out), if still live.
    private static void KillTween(Node node, string metaKey)
    {
        if (node.HasMeta(metaKey)
            && node.GetMeta(metaKey).As<Tween>() is { } running
            && GodotObject.IsInstanceValid(running))
        {
            running.Kill();
        }
        node.RemoveMeta(metaKey);
    }

    // --- Portrait hover suppression for banned characters ---------------------------------------
    // Vanilla NCharacterSelectButton.OnFocus snaps the portrait's hsv to s=1, v=1.1 (saturate +
    // brighten) and OnUnfocus eases it back to the resting state. For a banned character we hold the
    // portrait at its resting (dim) state so hovering it doesn't light it up — only the red X marks it.
    private const float PortraitHoverSaturation = 1.0f; // mirrors OnFocus's hard-set values
    private const float PortraitHoverValue = 1.1f;

    private static MethodInfo? _getRestingSaturation;
    private static MethodInfo? _getRestingValue;
    private static PropertyInfo? _isFocusedProp;

    // True when the button currently wears a shown ban mark — the single source of truth for "this
    // character is disabled," kept in lockstep with SetExclusionMark (which hides the mark when a
    // character is un-banned).
    public static bool HasActiveBanMark(NCharacterSelectButton button) =>
        button.GetNodeOrNull<TextureRect>("%Icon")?.GetNodeOrNull<TextureRect>(MarkName)
            is { Visible: true };

    // The portrait's hsv ShaderMaterial (lives on %Icon — the game sets it up in NCharacterSelectButton).
    private static ShaderMaterial? GetPortraitHsv(NCharacterSelectButton button) =>
        button.GetNodeOrNull<TextureRect>("%Icon")?.Material as ShaderMaterial;

    // Whether the button is currently focused/hovered (NClickableControl.IsFocused is true while the
    // cursor or controller is on it). Reflected because the property is protected.
    private static bool IsFocused(NCharacterSelectButton button)
    {
        _isFocusedProp ??= AccessTools.Property(typeof(NClickableControl), "IsFocused");
        return _isFocusedProp?.GetValue(button) is true;
    }

    // Snap the portrait to its resting (not-hovered) hsv, reading the game's own state-aware getters
    // so it lands on the right dim level (not-selected / remotely-selected) without us hardcoding it.
    public static void HoldPortraitResting(NCharacterSelectButton button)
    {
        if (GetPortraitHsv(button) is not { } hsv)
        {
            return;
        }
        _getRestingSaturation ??= AccessTools.Method(typeof(NCharacterSelectButton), "GetSaturationForCurrentState");
        _getRestingValue ??= AccessTools.Method(typeof(NCharacterSelectButton), "GetValueForCurrentState");
        hsv.SetShaderParameter("s", (float)_getRestingSaturation.Invoke(button, null)!);
        hsv.SetShaderParameter("v", (float)_getRestingValue.Invoke(button, null)!);
    }

    // Snap the portrait to the vanilla hover hsv — used when a character is un-banned while the cursor
    // is still over it, so it lights up immediately instead of waiting for the next focus event.
    private static void RestorePortraitHover(NCharacterSelectButton button)
    {
        if (GetPortraitHsv(button) is { } hsv)
        {
            hsv.SetShaderParameter("s", PortraitHoverSaturation);
            hsv.SetShaderParameter("v", PortraitHoverValue);
        }
    }

    // Keep a hovered portrait's saturation in sync the instant its ban state toggles. OnFocus/OnUnfocus
    // only fire on navigation, so toggling the ban while the cursor sits on the button wouldn't
    // otherwise update its brightness until you moved away and back.
    private static void SyncHoveredPortrait(NCharacterSelectButton button, bool banned)
    {
        if (!IsFocused(button))
        {
            return;
        }
        if (banned)
        {
            HoldPortraitResting(button);
        }
        else
        {
            RestorePortraitHover(button);
        }
    }

    // Repaint every live button (e.g. on selection change or config toggle).
    public static void RefreshAll()
    {
        foreach (NCharacterSelectButton dead in _buttons.Keys.Where(b => !GodotObject.IsInstanceValid(b)).ToList())
        {
            _buttons.Remove(dead);
        }
        foreach (NCharacterSelectButton button in _buttons.Keys)
        {
            ApplyVisual(button);
        }
    }
}

// The character select screens that offer a Random option, and the per-screen members the
// patches below hook. The standard screen exists on both branches; the Blitz screen only on
// v0.113.0+, so it is resolved by name. Both declare the same private members.
public static class RandomCharacterScreens
{
    public static readonly Type? Blitz =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.Blitz.NBlitzScreen");

    public static IEnumerable<Type> All =>
        Blitz == null ? new[] { typeof(NCharacterSelectScreen) } : new[] { typeof(NCharacterSelectScreen), Blitz };

    public static bool Is(object? screen) =>
        screen is NCharacterSelectScreen || (screen != null && Blitz?.IsInstanceOfType(screen) == true);

    public static IEnumerable<MethodBase> Methods(string name) =>
        All.Select(t => AccessTools.DeclaredMethod(t, name)).Where(m => m != null).Cast<MethodBase>();

    public static NCharacterSelectButton? RandomButton(Node screen) =>
        AccessTools.Field(screen.GetType(), "_randomCharacterButton")?.GetValue(screen) as NCharacterSelectButton;
}

[HarmonyPatch(typeof(NCharacterSelectButton), "Init")]
public static class RandomCharacterExclusionButtonPatch
{
    // `del` is the owning screen. Only screens with a Random option take part, so skip the
    // custom-run and Dread screens (which reuse this button but never offer Random).
    public static void Postfix(NCharacterSelectButton __instance, ICharacterSelectButtonDelegate del)
    {
        try
        {
            if (RandomCharacterScreens.Is(del))
            {
                RandomCharacterExclusionManager.OnButtonInitialized(__instance, (Node)del);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to wire up random-character exclusion on button: {e}");
        }
    }
}

// Suppress the game's portrait hover saturate/brighten on banned characters. OnFocus snaps the
// portrait's hsv to s=1, v=1.1 (when not selected); for a disabled character we instead hold it at
// the resting (dim) state in a postfix, so hovering it doesn't light it up — only the red X marks
// it. The rest of the hover (scale pop, sfx, hover tip) is left intact, and non-banned buttons are
// untouched. OnUnfocus needs no patch: vanilla already eases back to the resting state we held.
[HarmonyPatch(typeof(NCharacterSelectButton), "OnFocus")]
public static class RandomCharacterExclusionPortraitHoldPatch
{
    public static void Postfix(NCharacterSelectButton __instance)
    {
        try
        {
            if (RandomCharacterExclusionManager.HasActiveBanMark(__instance))
            {
                RandomCharacterExclusionManager.HoldPortraitResting(__instance);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to hold banned portrait saturation on focus: {e}");
        }
    }
}

[HarmonyPatch]
public static class RandomCharacterExclusionSelectPatch
{
    public static IEnumerable<MethodBase> TargetMethods() => RandomCharacterScreens.Methods("SelectCharacter");

    public static void Postfix(Node __instance, NCharacterSelectButton charSelectButton)
    {
        try
        {
            if (RandomCharacterExclusionManager.IsActiveScreen(__instance))
            {
                RandomCharacterExclusionManager.OnCharacterSelected(charSelectButton);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to update random-character exclusion on selection: {e}");
        }
    }
}

// A prefix, so the reset lands before vanilla selects the opening button: v0.113.0 reopens on the
// character you last embarked with (Random included), and that Select() must be what sets
// _randomSelected. It also registers the Random button, which v0.113.0 builds with InitAsRandom
// rather than Init, so the Init postfix above never sees it (on v0.111.0 it already has; the
// registration is idempotent). The field is created in _Ready, before the screen first opens.
[HarmonyPatch]
public static class RandomCharacterExclusionScreenOpenPatch
{
    public static IEnumerable<MethodBase> TargetMethods() => RandomCharacterScreens.Methods("OnSubmenuOpened");

    public static void Prefix(Node __instance)
    {
        try
        {
            RandomCharacterExclusionManager.OnScreenOpened(__instance);
            if (RandomCharacterScreens.RandomButton(__instance) is { } random && GodotObject.IsInstanceValid(random))
            {
                RandomCharacterExclusionManager.OnButtonInitialized(random, __instance);
            }
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

// v0.113.0 remembers the character you embark with (UpdatePreferredStandardCharacter /
// UpdatePreferredBlitzCharacter, called from BeginRun) by reading the selected button — and when
// you picked Random it's meant to remember Random. Our multiplayer resolve switches the selection
// to the rolled character's button, so point the selection back at Random for the duration of the
// save. Absent (and skipped) on v0.111.0.
[HarmonyPatch]
public static class RandomCharacterExclusionPreferredCharacterPatch
{
    private static IEnumerable<MethodBase> Targets() =>
        RandomCharacterScreens.Methods("UpdatePreferredStandardCharacter")
            .Concat(RandomCharacterScreens.Methods("UpdatePreferredBlitzCharacter"));

    public static bool Prepare() => Targets().Any();

    public static IEnumerable<MethodBase> TargetMethods() => Targets();

    public static void Prefix(Node __instance, out NCharacterSelectButton? __state)
    {
        __state = null;
        try
        {
            FieldInfo? selectedField = AccessTools.Field(__instance.GetType(), "_selectedButton");
            if (RandomCharacterExclusionManager.ResolvedFromRandom
                && RandomCharacterExclusionManager.IsActiveScreen(__instance)
                && selectedField != null
                && RandomCharacterScreens.RandomButton(__instance) is { } random
                && selectedField.GetValue(__instance) is NCharacterSelectButton selected
                && selected != random)
            {
                __state = selected;
                selectedField.SetValue(__instance, random);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to remember Random as the preferred character: {e}");
        }
    }

    public static void Postfix(Node __instance, NCharacterSelectButton? __state)
    {
        if (__state != null)
        {
            AccessTools.Field(__instance.GetType(), "_selectedButton")?.SetValue(__instance, __state);
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
