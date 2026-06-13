using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// Special card rewards — the card the Thieving Hopper stole and hands back when it dies, and
// the card from the Lantern Key event — call CardCmd.PreviewCardPileAdd(result, 2f) in
// SpecialCardReward.OnSelect. That floats the card dead-center of the screen for two seconds
// before it flies to the deck, hiding whatever is behind it (e.g. the middle card on the card
// reward / selection screen). Normal card rewards don't do this — they swoosh the real reward
// card straight to the deck. This patch reimplements OnSelect with the same behaviour but a 0s
// linger, so the card skips the centered hold and zips straight into the deck the moment you
// take it. The card is added to the deck by CardPileCmd.Add either way; only the cosmetic
// preview's timing changes.
[HarmonyPatch(typeof(SpecialCardReward), "OnSelect")]
public static class SpecialCardRewardNoLingerPatch
{
    private static readonly AccessTools.FieldRef<SpecialCardReward, CardModel?> _card =
        AccessTools.FieldRefAccess<SpecialCardReward, CardModel?>("_card");
    private static readonly AccessTools.FieldRef<SpecialCardReward, bool> _wasTaken =
        AccessTools.FieldRefAccess<SpecialCardReward, bool>("_wasTaken");

    public static bool Prefix(SpecialCardReward __instance, ref Task<bool> __result)
    {
        if (!ColinsPatchKitConfig.SkipReturnedCardPreviewDelay)
        {
            return true; // vanilla two-second centered linger
        }

        __result = TakeWithoutLinger(__instance);
        return false;
    }

    private static async Task<bool> TakeWithoutLinger(SpecialCardReward reward)
    {
        CardPileAddResult result = await CardPileCmd.Add(_card(reward)!, PileType.Deck);
        if (result.success)
        {
            // 0s linger: the card scales in and immediately swooshes to the deck.
            CardCmd.PreviewCardPileAdd(result, 0f);
        }

        _wasTaken(reward) = true;
        return true;
    }
}
