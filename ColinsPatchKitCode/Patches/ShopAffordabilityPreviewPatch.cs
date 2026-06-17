using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.addons.mega_text;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// In the shop, hovering an item reds out every OTHER price you couldn't still afford after buying the
// hovered one — the same red the game already uses for items you can't afford at all — so you can see
// at a glance which two purchases you can make together. The recolor fades in/out over ~75ms, and
// hovering away restores the normal colors.
//
// Mechanism: vanilla colors each cost label inside the per-slot UpdateVisual override (cream/green
// when affordable, red when not). We track the hovered slot and the gold you'd have left after buying
// it, then re-run UpdateVisual on every slot so they repaint from scratch. A prefix on each concrete
// override snapshots the on-screen color before vanilla snaps it; the postfix rewinds to that
// snapshot and tweens the label to its real target (red for a no-longer-affordable item, otherwise
// vanilla's color), giving a smooth crossfade in both directions. Tweening only happens during a
// hover/unhover repaint (the _animating gate); every other UpdateVisual (shop open, gold change)
// keeps vanilla's instant behavior.
internal static class ShopAffordabilityPreview
{
    private const double FadeSeconds = 0.075;

    // The slot currently hovered over an affordable item, or null when no preview is active.
    private static NMerchantSlot? _hoveredSlot;

    // Gold left after buying the hovered item (player.Gold - hovered.Cost); only meaningful while
    // _hoveredSlot is non-null.
    private static int _goldAfterPurchase;

    // True only while RepaintAll is driving UpdateVisual, so the postfix knows to fade rather than snap.
    private static bool _animating;

    // The cost label's on-screen color captured by the prefix, just before vanilla overwrites it.
    private static Color _preColor;

    // The in-flight fade per slot, so a new transition can cancel the previous one instead of fighting it.
    private static readonly Dictionary<NMerchantSlot, Tween> _fades = new();

    // NMerchantSlot.UpdateVisual is protected virtual; invoking the cached base MethodInfo still
    // dispatches to the actual subclass override (reflection respects virtual dispatch).
    private static readonly MethodInfo UpdateVisualMethod =
        AccessTools.Method(typeof(NMerchantSlot), "UpdateVisual");

    public static void OnHover(NMerchantSlot slot, NMerchantInventory? rug)
    {
        MerchantEntry entry = slot.Entry;
        // Only preview off an item you can actually buy: if you can't afford the hovered item itself
        // there's nothing to subtract, and its own price is already shown red.
        if (rug?.Inventory?.Player is not { } player || entry is not { IsStocked: true } || !entry.EnoughGold)
        {
            return;
        }

        _hoveredSlot = slot;
        _goldAfterPurchase = player.Gold - entry.Cost;
        RepaintAll(rug);
    }

    public static void OnUnhover(NMerchantSlot slot, NMerchantInventory? rug)
    {
        if (_hoveredSlot != slot)
        {
            return;
        }

        _hoveredSlot = null;
        RepaintAll(rug);
    }

    // Re-run each slot's UpdateVisual so its cost label repaints (vanilla color first, then our fade).
    private static void RepaintAll(NMerchantInventory? rug)
    {
        if (rug is null)
        {
            return;
        }

        _animating = true;
        try
        {
            foreach (NMerchantSlot slot in rug.GetAllSlots())
            {
                UpdateVisualMethod.Invoke(slot, null);
            }
        }
        finally
        {
            _animating = false;
        }
    }

    // Prefix: snapshot the live color before vanilla snaps it, and cancel any fade still running on
    // this slot so it doesn't fight the new one.
    public static void BeforeRepaint(NMerchantSlot slot, MegaLabel? costLabel)
    {
        if (costLabel is null)
        {
            return;
        }

        _preColor = costLabel.Modulate;
        if (_animating && _fades.TryGetValue(slot, out Tween? tween))
        {
            if (tween is not null && tween.IsValid())
            {
                tween.Kill();
            }
            _fades.Remove(slot);
        }
    }

    // Postfix: decide the slot's true target color and either fade to it (during a hover transition)
    // or apply it instantly (any other repaint).
    public static void AfterRepaint(NMerchantSlot slot, MegaLabel? costLabel)
    {
        if (costLabel is null)
        {
            return;
        }

        bool wantRed = _hoveredSlot is not null
                       && slot != _hoveredSlot
                       && slot.Entry is { IsStocked: true } entry
                       && entry.Cost > _goldAfterPurchase;

        if (!_animating)
        {
            // Outside a hover transition (e.g. gold changed): keep vanilla's instant behavior.
            if (wantRed)
            {
                costLabel.Modulate = StsColors.red;
            }
            return;
        }

        // Vanilla just set Modulate to the normal color; that's our fade target unless we want red.
        Color target = wantRed ? StsColors.red : costLabel.Modulate;
        if (_preColor == target)
        {
            return; // already showing the target — nothing to animate.
        }

        costLabel.Modulate = _preColor; // rewind to the pre-snap on-screen color, then fade to the target.
        Tween tween = costLabel.CreateTween();
        tween.TweenProperty(costLabel, "modulate", target, FadeSeconds);
        _fades[slot] = tween;
        tween.Finished += () =>
        {
            if (_fades.TryGetValue(slot, out Tween? finished) && finished == tween)
            {
                _fades.Remove(slot);
            }
        };
    }
}

// Hovering a slot (mouse enter or controller focus) routes through OnFocus/OnUnfocus on the base class.
[HarmonyPatch(typeof(NMerchantSlot), "OnFocus")]
internal static class ShopAffordabilityPreviewHoverPatch
{
    public static void Postfix(NMerchantSlot __instance, NMerchantInventory? ____merchantRug)
    {
        if (ColinsPatchKitConfig.ShowShopAffordabilityPreview)
        {
            ShopAffordabilityPreview.OnHover(__instance, ____merchantRug);
        }
    }
}

[HarmonyPatch(typeof(NMerchantSlot), "OnUnfocus")]
internal static class ShopAffordabilityPreviewUnhoverPatch
{
    public static void Postfix(NMerchantSlot __instance, NMerchantInventory? ____merchantRug)
    {
        if (ColinsPatchKitConfig.ShowShopAffordabilityPreview)
        {
            ShopAffordabilityPreview.OnUnhover(__instance, ____merchantRug);
        }
    }
}

// The base UpdateVisual only sets the cost text; the cream/green/red color is set in each concrete
// override, so patch all four of them. ____costLabel resolves the protected _costLabel field declared
// on the base NMerchantSlot.
[HarmonyPatch]
internal static class ShopAffordabilityPreviewRepaintPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NMerchantCard), "UpdateVisual");
        yield return AccessTools.Method(typeof(NMerchantRelic), "UpdateVisual");
        yield return AccessTools.Method(typeof(NMerchantPotion), "UpdateVisual");
        yield return AccessTools.Method(typeof(NMerchantCardRemoval), "UpdateVisual");
    }

    public static void Prefix(NMerchantSlot __instance, MegaLabel? ____costLabel)
    {
        if (ColinsPatchKitConfig.ShowShopAffordabilityPreview)
        {
            ShopAffordabilityPreview.BeforeRepaint(__instance, ____costLabel);
        }
    }

    public static void Postfix(NMerchantSlot __instance, MegaLabel? ____costLabel)
    {
        if (ColinsPatchKitConfig.ShowShopAffordabilityPreview)
        {
            ShopAffordabilityPreview.AfterRepaint(__instance, ____costLabel);
        }
    }
}
