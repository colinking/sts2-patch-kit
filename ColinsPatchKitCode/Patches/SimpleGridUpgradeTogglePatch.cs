using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// Adds the "View Upgrades" tickbox (built by ViewUpgradesTickboxHelper, shared config toggle
// with CardRewardUpgradeTogglePatch) to the simple card-select grid, which every remaining
// out-of-combat "pick new cards for your deck" flow uses: the Room Full of Cheese "Gorge"
// option and Brain Leech's "Share Knowledge" (EventModel.SelectCardsToAddToDeckFromGrid),
// the Sea Glass relic's 15-card pick, and the Sealed Deck modifier's 10-of-30 draft. Unlike
// the reward and choose-a-card screens there is no per-holder work to do here: the screen's
// NCardGrid is the same grid the deck view drives, so the tickbox just sets
// NCardGrid.IsShowingUpgrades, which previews existing holders (with its own can-upgrade
// guards) and is honored by holders the grid materializes later while scrolling.
// The same screen also serves in-combat picks (Choices Paradox's turn-1 choice), where the
// pick is a temporary combat card that never joins the deck, so like
// ChooseACardUpgradeTogglePatch the toggle is only added when combat is over or ending.
public static class SimpleGridUpgradeToggleManager
{
    // _grid is protected on NCardGridSelectionScreen; present in both supported game branches.
    private static readonly AccessTools.FieldRef<NCardGridSelectionScreen, NCardGrid> GridRef =
        AccessTools.FieldRefAccess<NCardGridSelectionScreen, NCardGrid>("_grid");

    public static void AddToggle(NSimpleCardSelectScreen screen)
    {
        NCardGrid grid = GridRef(screen);
        NTickbox tickbox = ViewUpgradesTickboxHelper.AddTo(screen,
            show => grid.IsShowingUpgrades = show);
        // The peek button fades out registered UI while held; include the tickbox like the
        // screen's own chrome (the bottom prompt text).
        screen.GetNodeOrNull<NPeekButton>("%PeekButton")?.AddTargets(tickbox);
    }
}

[HarmonyPatch(typeof(NSimpleCardSelectScreen), nameof(NSimpleCardSelectScreen._Ready))]
public static class SimpleGridUpgradeToggleReadyPatch
{
    public static void Postfix(NSimpleCardSelectScreen __instance)
    {
        if (!ColinsPatchKitConfig.ShowCardRewardViewUpgradesToggle ||
            !CombatManager.Instance.IsOverOrEnding)
        {
            return;
        }
        try
        {
            SimpleGridUpgradeToggleManager.AddToggle(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to add the simple grid View Upgrades toggle: {e}");
        }
    }
}
