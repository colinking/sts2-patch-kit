using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// Adds the "View Upgrades" tickbox (built by ViewUpgradesTickboxHelper, shared config toggle
// with CardRewardUpgradeTogglePatch) to the choose-a-bundle screen used by the Scroll Boxes
// relic. Unlike the card reward screen, bundle cards are raw NCards without the
// NGridCardHolder machinery that precomputes an upgraded clone, so this manager keeps its own
// base->upgraded mapping per NCard and swaps Model the same way
// NGridCardHolder.SetIsPreviewingUpgrade does. The vanilla screen also resets card visuals to
// CardPreviewMode.Normal whenever cards move between the stacked-bundle and spread-out
// preview states (OnBundleClicked, and CancelSelection via ReAddCardNodes), so those
// transitions re-apply an active preview afterwards.
public static class BundleUpgradeToggleManager
{
    private sealed class PreviewState
    {
        public CardModel BaseCard = null!;
        public CardModel? UpgradedCard;
    }

    // Keyed weakly: the NCards are freed with the screen and the entries die with them.
    private static readonly ConditionalWeakTable<NCard, PreviewState> _states = new();

    public static void AddToggle(NChooseABundleSelectionScreen screen)
    {
        NTickbox tickbox = ViewUpgradesTickboxHelper.AddTo(screen, show => ApplyPreview(screen, show));
        // The peek button fades out registered UI while held; include the tickbox like the
        // screen's own chrome (banner, bundle row, preview container).
        screen.GetNodeOrNull<NPeekButton>("%PeekButton")?.AddTargets(tickbox);
    }

    public static NTickbox? FindToggle(NChooseABundleSelectionScreen screen) =>
        screen.GetNodeOrNull<NTickbox>(ViewUpgradesTickboxHelper.TickboxName);

    public static void ReapplyIfTicked(NChooseABundleSelectionScreen screen)
    {
        if (FindToggle(screen) is { IsTicked: true })
        {
            ApplyPreview(screen, show: true);
        }
    }

    public static void ApplyPreview(NChooseABundleSelectionScreen screen, bool show)
    {
        Control? bundleRow = screen.GetNodeOrNull<Control>("%BundleRow");
        if (bundleRow == null)
        {
            return;
        }
        foreach (NCardBundle bundle in bundleRow.GetChildren().OfType<NCardBundle>())
        {
            // CardNodes also covers the spread-out preview state: the selected bundle's NCards
            // stay in its list after being reparented into the preview holders.
            foreach (NCard card in bundle.CardNodes)
            {
                SetPreview(card, show);
            }
        }
    }

    // NCard-level equivalent of NGridCardHolder.SetIsPreviewingUpgrade. Re-showing is
    // deliberately not guarded by "already previewing" so it can restore the Upgrade visuals
    // after vanilla resets them to Normal behind our back.
    private static void SetPreview(NCard card, bool show)
    {
        if (!GodotObject.IsInstanceValid(card) || card.Model == null)
        {
            return;
        }
        PreviewState state = _states.GetValue(card, c => new PreviewState { BaseCard = c.Model! });
        if (show)
        {
            if (!state.BaseCard.IsUpgradable)
            {
                return;
            }
            if (state.UpgradedCard == null)
            {
                state.UpgradedCard = (CardModel)state.BaseCard.MutableClone();
                state.UpgradedCard.UpgradeInternal();
            }
            card.Model = state.UpgradedCard;
            card.ShowUpgradePreview();
        }
        else if (card.Model != state.BaseCard)
        {
            card.Model = state.BaseCard;
            card.UpdateVisuals(card.DisplayingPile, CardPreviewMode.Normal);
        }
    }
}

[HarmonyPatch(typeof(NChooseABundleSelectionScreen), nameof(NChooseABundleSelectionScreen._Ready))]
public static class BundleUpgradeToggleReadyPatch
{
    public static void Postfix(NChooseABundleSelectionScreen __instance)
    {
        if (!ColinsPatchKitConfig.ShowCardRewardViewUpgradesToggle)
        {
            return;
        }
        try
        {
            BundleUpgradeToggleManager.AddToggle(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to add the bundle View Upgrades toggle: {e}");
        }
    }
}

// Clicking a bundle spreads its cards into preview holders and resets their visuals to
// CardPreviewMode.Normal; restore the upgrade preview when the toggle is on.
[HarmonyPatch(typeof(NChooseABundleSelectionScreen), "OnBundleClicked")]
public static class BundleUpgradeToggleBundleClickedPatch
{
    public static void Postfix(NChooseABundleSelectionScreen __instance)
    {
        try
        {
            BundleUpgradeToggleManager.ReapplyIfTicked(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to re-apply the bundle upgrade preview: {e}");
        }
    }
}

// Backing out re-stacks the spread cards via ReAddCardNodes, which also resets their visuals
// to Normal; restore the upgrade preview when the toggle is on.
[HarmonyPatch(typeof(NChooseABundleSelectionScreen), "CancelSelection")]
public static class BundleUpgradeToggleCancelPatch
{
    public static void Postfix(NChooseABundleSelectionScreen __instance)
    {
        try
        {
            BundleUpgradeToggleManager.ReapplyIfTicked(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to re-apply the bundle upgrade preview: {e}");
        }
    }
}

// Confirming reparents the selected bundle's NCards into the fly-to-deck VFX; restore the
// base models first so the flying cards show what was actually obtained (the bundle always
// grants the unupgraded cards).
[HarmonyPatch(typeof(NChooseABundleSelectionScreen), "ConfirmSelection")]
public static class BundleUpgradeToggleConfirmPatch
{
    public static void Prefix(NChooseABundleSelectionScreen __instance)
    {
        try
        {
            BundleUpgradeToggleManager.ApplyPreview(__instance, show: false);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to clear the bundle upgrade preview: {e}");
        }
    }
}
