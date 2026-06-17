using System;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// Miniature Tent lets you take a second rest-site action (it keeps the other options selectable
// after you pick one), but the proceed button lights up the instant you make your first choice —
// so a reflexive click leaves the campfire with a free action still on the table. This makes that
// proceed button require a deliberate hold-to-confirm, the same gesture the game already uses for
// "hold to confirm end turn", but only while leaving would actually waste something: you own Tent
// and at least one shown option can still be selected. Once nothing useful is left (you've used
// both, or the leftover option is disabled — e.g. Smith with no upgradable cards) it's an instant
// click again, exactly like vanilla.
//
// Mechanism: the proceed button's press/release already route through NClickableControl
// (OnPress -> ... -> OnRelease -> Released). We grow a fill bar under the button across a press and
// only fire the real "leave" (NMapScreen.Open) once the hold completes; a tap or an early release
// cancels it. To make sure a tap can't slip through, we also suppress the button's own instant
// Released handler (NRestSiteRoom.OnProceedButtonReleased) while the gate is active — when it's
// active the hold is the *only* way out.
public static class RestSiteProceedHoldManager
{
    // Matches the game's own long-press-to-end-turn threshold (NEndTurnLongPressBar).
    private const double HoldDuration = 0.5;
    private const float BarHeight = 6f;
    private const float BarGap = 4f;
    private static readonly Color BarColor = new("FFCC00"); // the proceed button's gold accent

    // Reads NClickableControl's private "still being held" flag so the completion callback can bail
    // if the press was canceled without an OnRelease (the drag-threshold path just clears _isPressed).
    private static readonly AccessTools.FieldRef<object, bool> IsPressedRef =
        AccessTools.FieldRefAccess<bool>(typeof(NClickableControl), "_isPressed");

    // The bar lives under the current rest-site proceed button (recreated per rest site); _tween
    // drives the fill while held. Only one rest site exists at a time, so single static state is fine.
    private static NProceedButton? _button;
    private static ColorRect? _bar;
    private static Tween? _tween;
    private static float _barWidth;

    // True while leaving the rest site should cost a deliberate hold: feature on, this is the rest
    // site's (enabled) proceed button, the local player owns Tent, and a shown option is still takeable.
    public static bool GateActive(NProceedButton button)
    {
        if (!ColinsPatchKitConfig.ConfirmRestSiteProceedWithTent)
        {
            return false;
        }
        NRestSiteRoom? room = NRestSiteRoom.Instance;
        if (room == null || room.ProceedButton != button || !button.IsEnabled)
        {
            return false;
        }
        if (room.Options.Count == 0 || !room.Options.Any(o => o.IsEnabled))
        {
            return false;
        }
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        return state != null && LocalContext.GetMe(state.Players)?.GetRelic<MiniatureTent>() != null;
    }

    // Press start: begin (or restart) the fill. Fires the real leave when it reaches full.
    public static void BeginHold(NProceedButton button)
    {
        EnsureBar(button);
        if (_bar == null)
        {
            return;
        }
        _tween?.Kill();
        _tween = _bar.CreateTween().SetParallel();
        _tween.TweenProperty(_bar, "size:x", _barWidth, HoldDuration);
        _tween.TweenProperty(_bar, "modulate:a", 1f, HoldDuration * 0.4);
        _tween.Chain().TweenCallback(Callable.From(OnHoldComplete));
    }

    // Released early (or a quick tap): rewind and hide the bar; nothing happens.
    public static void CancelHold(NProceedButton button)
    {
        if (_button != button || _bar == null || !GodotObject.IsInstanceValid(_bar))
        {
            return;
        }
        _tween?.Kill();
        _tween = _bar.CreateTween().SetParallel();
        _tween.TweenProperty(_bar, "size:x", 0f, 0.15);
        _tween.TweenProperty(_bar, "modulate:a", 0f, 0.15);
    }

    private static void OnHoldComplete()
    {
        // Re-check: the press could have been canceled by a drag (clears _isPressed without an
        // OnRelease), or the gate could have lapsed mid-hold. Only leave if it's still a real hold.
        if (_button == null || !GodotObject.IsInstanceValid(_button)
            || !IsPressedRef(_button) || !GateActive(_button))
        {
            ResetBar();
            return;
        }
        ResetBar();
        NMapScreen.Instance?.Open();
    }

    private static void EnsureBar(NProceedButton button)
    {
        if (_button == button && _bar != null && GodotObject.IsInstanceValid(_bar))
        {
            ResetBar();
            return;
        }
        if (_bar != null && GodotObject.IsInstanceValid(_bar))
        {
            _bar.QueueFreeSafelyNoPool();
        }
        _tween?.Kill();
        _tween = null;

        // Anchor the bar to the button's graphic so it tracks the visible artwork, not the (possibly
        // larger/offset) clickable Control rect.
        Control anchor = button.GetNodeOrNull<Control>("%Image") ?? button;
        _button = button;
        _barWidth = anchor.Size.X;
        _bar = new ColorRect
        {
            Color = BarColor,
            Size = new Vector2(0f, BarHeight),
            Position = new Vector2(0f, anchor.Size.Y + BarGap),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1f, 1f, 1f, 0f),
        };
        anchor.AddChildSafely(_bar);
    }

    private static void ResetBar()
    {
        _tween?.Kill();
        _tween = null;
        if (_bar == null || !GodotObject.IsInstanceValid(_bar))
        {
            return;
        }
        _bar.Size = new Vector2(0f, BarHeight);
        _bar.Modulate = new Color(1f, 1f, 1f, 0f);
    }

    // The rest site (and its proceed button, and our child bar) is gone — drop the stale references.
    public static void Clear()
    {
        _tween?.Kill();
        _tween = null;
        _bar = null;
        _button = null;
    }
}

// Every NProceedButton in the game routes through these two overrides; the gate check scopes the
// behavior to the rest site's button only, so other proceed/skip buttons are untouched.
[HarmonyPatch(typeof(NProceedButton), "OnPress")]
public static class RestSiteProceedHoldOnPressPatch
{
    public static void Postfix(NProceedButton __instance)
    {
        try
        {
            if (RestSiteProceedHoldManager.GateActive(__instance))
            {
                RestSiteProceedHoldManager.BeginHold(__instance);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to begin rest-site proceed hold: {e}");
        }
    }
}

[HarmonyPatch(typeof(NProceedButton), "OnRelease")]
public static class RestSiteProceedHoldOnReleasePatch
{
    public static void Postfix(NProceedButton __instance)
    {
        try
        {
            RestSiteProceedHoldManager.CancelHold(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to cancel rest-site proceed hold: {e}");
        }
    }
}

// While the gate is active, the click-release must not leave the rest site — only a completed hold
// (RestSiteProceedHoldManager.OnHoldComplete) calls NMapScreen.Open. Skipping the original here is
// what makes a quick click a no-op.
[HarmonyPatch(typeof(NRestSiteRoom), "OnProceedButtonReleased")]
public static class RestSiteProceedSuppressInstantPatch
{
    public static bool Prefix(NButton _)
    {
        return _ is not NProceedButton proceed || !RestSiteProceedHoldManager.GateActive(proceed);
    }
}

// The bar is parented into the rest-site scene and freed with it; null our static refs so a later
// rest site doesn't reuse a freed button/bar.
[HarmonyPatch(typeof(NRestSiteRoom), "_ExitTree")]
public static class RestSiteProceedHoldCleanupPatch
{
    public static void Postfix()
    {
        RestSiteProceedHoldManager.Clear();
    }
}
