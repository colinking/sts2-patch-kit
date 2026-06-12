using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// When the cursor leaves a shop item, vanilla NMerchantSlot.OnMerchantHandUnhovered calls
// NMerchantHand.StopPointing(2f), so the merchant's hand keeps hovering over the wares for two
// seconds before drifting back to its resting spot. Re-issuing StopPointing with a much shorter
// linger (each call cancels the previous one's token) sends the hand home almost immediately —
// the 0.3s grace keeps the hand from twitching back during the brief unhover gap when the cursor
// moves between adjacent items (hovering the next item cancels the pending return).
// The post-purchase flourish (TriggerMerchantHandToPointHere) keeps its vanilla 2s linger.
[HarmonyPatch(typeof(NMerchantSlot), "OnMerchantHandUnhovered")]
public static class ShopHandLingerPatch
{
    internal const float LingerSeconds = 0.3f;

    public static void Postfix(NMerchantInventory? ____merchantRug)
    {
        if (ColinsPatchKitConfig.MoveShopHandAwayFaster)
        {
            ____merchantRug?.MerchantHand.StopPointing(LingerSeconds);
        }
    }
}
