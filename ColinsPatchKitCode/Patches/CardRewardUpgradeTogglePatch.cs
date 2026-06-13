using System;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// Adds the deck view's "View Upgrades" tickbox (built by ViewUpgradesTickboxHelper) to the
// card reward screen, so reward cards can be previewed in their upgraded form before picking
// one. The reward screen already builds its cards as NGridCardHolders, which precompute an
// upgraded clone of their card and expose SetIsPreviewingUpgrade — the same machinery the
// deck view's toggle drives via NCardGrid.IsShowingUpgrades — so only the tickbox UI itself
// is missing. See BundleUpgradeTogglePatch for the same toggle on the choose-a-bundle screen.
public static class CardRewardUpgradeToggleManager
{
    public static void AddToggle(NCardRewardSelectionScreen screen)
    {
        Control ui = screen.GetNode<Control>("UI");
        ViewUpgradesTickboxHelper.AddTo(ui, show => ApplyPreview(screen, show));
    }

    public static NTickbox? FindToggle(NCardRewardSelectionScreen screen) =>
        screen.GetNodeOrNull<NTickbox>("UI/" + ViewUpgradesTickboxHelper.TickboxName);

    public static void ApplyPreview(NCardRewardSelectionScreen screen, bool show)
    {
        Control? cardRow = screen.GetNodeOrNull<Control>("UI/CardRow");
        if (cardRow == null)
        {
            return;
        }
        foreach (NGridCardHolder holder in cardRow.GetChildren().OfType<NGridCardHolder>())
        {
            // SetIsPreviewingUpgrade throws when asked to preview a card that cannot upgrade
            // (curses, already-upgraded rewards); same guard as NCardGrid.IsShowingUpgrades.
            if (!holder.IsQueuedForDeletion() && (!show || holder.CardModel.IsUpgradable))
            {
                holder.SetIsPreviewingUpgrade(show);
            }
        }
    }
}

[HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen._Ready))]
public static class CardRewardUpgradeToggleReadyPatch
{
    public static void Postfix(NCardRewardSelectionScreen __instance)
    {
        if (!ColinsPatchKitConfig.ShowCardRewardViewUpgradesToggle)
        {
            return;
        }
        try
        {
            CardRewardUpgradeToggleManager.AddToggle(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to add the card reward View Upgrades toggle: {e}");
        }
    }
}

// RefreshOptions rebuilds the card holders (it also runs on multiplayer reward updates), and
// fresh holders always start in non-preview state — re-apply the toggle if it is on.
[HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.RefreshOptions))]
public static class CardRewardUpgradeToggleRefreshPatch
{
    public static void Postfix(NCardRewardSelectionScreen __instance)
    {
        try
        {
            if (CardRewardUpgradeToggleManager.FindToggle(__instance) is { IsTicked: true })
            {
                CardRewardUpgradeToggleManager.ApplyPreview(__instance, show: true);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to re-apply the card reward upgrade preview: {e}");
        }
    }
}
