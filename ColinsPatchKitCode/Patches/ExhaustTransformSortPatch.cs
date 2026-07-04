using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// When you pick a card to *exhaust* (Cleanse) or *transform into a specific card* (Charge, Séance)
// out of your draw pile, the chosen card is usually a curse, status, or quest card you want gone —
// but the draw-pile selection screen sorts those to the very bottom (NCombatPileCardSelectScreen
// sorts the draw pile RarityAscending then alphabetically, and GetCardRarityComparisonValue maps
// Status->6, Curse->7, Quest->9, behind every normal rarity). This patch floats curses, then
// statuses, then quest cards to the front of that list so the cards you came to remove are right
// where you look first.
//
// Scoping: the draw-pile selection screen is shared by other effects that pull a card to *play*
// (Wish, Secret Weapon, Secret Technique), where surfacing curses would be actively wrong, so we
// only act when the selector's prompt is TO_EXHAUST or TO_TRANSFORM (the prompts Cleanse / Charge /
// Séance use; the play-a-card effects use the card's own prompt). Rather than reimplement
// UpdatePileContents (its SetCards call kicks off an async grid layout that double-calling would
// race), we let the vanilla rarity sort run and only remap the comparison value for curses/statuses
// while the flag is set — keeping the existing alphabetical-within-group tiebreak intact.
internal static class ExhaustTransformSort
{
    // True only while NCombatPileCardSelectScreen.UpdatePileContents is sorting a draw-pile
    // exhaust/transform selection. The vanilla sort runs synchronously inside that call, so the
    // flag reliably brackets every GetCardRarityComparisonValue invocation we care about.
    internal static bool Active;
}

[HarmonyPatch(typeof(NCombatPileCardSelectScreen), "UpdatePileContents")]
internal static class ExhaustTransformSortScopePatch
{
    private static void Prefix(CardPile ____pile, CardSelectorPrefs ____prefs)
    {
        if (!ColinsPatchKitConfig.SortCursesAndStatusesFirst || ____pile.Type != PileType.Draw)
        {
            return;
        }

        string? prompt = ____prefs.Prompt?.LocEntryKey;
        ExhaustTransformSort.Active = prompt == "TO_EXHAUST" || prompt == "TO_TRANSFORM";
    }

    // Finalizer (not Postfix) so the flag is always cleared, even if the sort throws — a leaked
    // flag would silently re-sort unrelated rarity-sorted grids (e.g. the deck view).
    private static void Finalizer()
    {
        ExhaustTransformSort.Active = false;
    }
}

[HarmonyPatch(typeof(NCardGrid), "GetCardRarityComparisonValue")]
internal static class ExhaustTransformSortRarityPatch
{
    // Curses sort ahead of statuses, then quest cards, all ahead of every normal rarity (>= 0).
    // Leaving every other card's value untouched preserves the vanilla rarity ordering, and the
    // screen's secondary AlphabetAscending key still breaks ties within each group.
    private static void Postfix(CardModel a, ref int __result)
    {
        if (!ExhaustTransformSort.Active)
        {
            return;
        }

        if (a.Rarity == CardRarity.Curse)
        {
            __result = -3;
        }
        else if (a.Rarity == CardRarity.Status)
        {
            __result = -2;
        }
        else if (a.Rarity == CardRarity.Quest)
        {
            __result = -1;
        }
    }
}
