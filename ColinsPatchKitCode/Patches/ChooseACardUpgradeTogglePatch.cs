using System;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// Adds the "View Upgrades" tickbox (built by ViewUpgradesTickboxHelper, shared config toggle
// with CardRewardUpgradeTogglePatch) to the choose-a-card screen, which relics that grant a
// card choice on pickup (Lead Paperweight, Hefty Tablet, Massive Scroll) show instead of the
// card reward screen. Like the reward screen its cards are NGridCardHolders, which precompute
// an upgraded clone and expose SetIsPreviewingUpgrade, so only the tickbox UI is missing.
// The same screen also serves in-combat card generators (Discovery, the Attack/Skill/Power/
// Colorless Potions, Toolbox), where the pick is a temporary combat copy that never joins the
// deck — an upgrade preview there would suggest a permanence the card doesn't have, so the
// toggle is only added when combat is over or ending (IsOverOrEnding covers both the
// post-combat reward phase, where a relic from a reward can still trigger the choice, and
// non-combat rooms like chests, shops, and events).
public static class ChooseACardUpgradeToggleManager
{
    public static void AddToggle(NChooseACardSelectionScreen screen)
    {
        NTickbox tickbox = ViewUpgradesTickboxHelper.AddTo(screen, show => ApplyPreview(screen, show));
        // The peek button fades out registered UI while held; include the tickbox like the
        // screen's own chrome (banner, card row, skip button).
        screen.GetNodeOrNull<NPeekButton>("%PeekButton")?.AddTargets(tickbox);
    }

    public static void ApplyPreview(NChooseACardSelectionScreen screen, bool show)
    {
        Control? cardRow = screen.GetNodeOrNull<Control>("CardRow");
        if (cardRow == null)
        {
            return;
        }
        foreach (NGridCardHolder holder in cardRow.GetChildren().OfType<NGridCardHolder>())
        {
            // SetIsPreviewingUpgrade throws when asked to preview a card that cannot upgrade
            // (already-upgraded options); same guard as NCardGrid.IsShowingUpgrades.
            if (!holder.IsQueuedForDeletion() && (!show || holder.CardModel.IsUpgradable))
            {
                holder.SetIsPreviewingUpgrade(show);
            }
        }
    }
}

[HarmonyPatch(typeof(NChooseACardSelectionScreen), nameof(NChooseACardSelectionScreen._Ready))]
public static class ChooseACardUpgradeToggleReadyPatch
{
    public static void Postfix(NChooseACardSelectionScreen __instance)
    {
        if (!ColinsPatchKitConfig.ShowCardRewardViewUpgradesToggle ||
            !CombatManager.Instance.IsOverOrEnding)
        {
            return;
        }
        try
        {
            ChooseACardUpgradeToggleManager.AddToggle(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to add the choose-a-card View Upgrades toggle: {e}");
        }
    }
}
