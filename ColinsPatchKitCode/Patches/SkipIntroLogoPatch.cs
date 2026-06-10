using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// Boots straight to the main menu, skipping the Mega Crit logo animation.
// Vanilla already supports this: LaunchMainMenu(skipLogo) is called with
// skipLogo = DebugSettings.DevSkip || SettingsSave.SkipIntroLogo || "fastmp",
// so forcing the flag rides the supported skip path (which also skips
// preloading the logo scene) rather than cancelling the animation mid-flight.
[HarmonyPatch(typeof(NGame), "LaunchMainMenu")]
public static class SkipIntroLogoPatch
{
    public static void Prefix(ref bool skipLogo)
    {
        if (ColinsPatchKitConfig.SkipIntroLogo)
        {
            skipLogo = true;
        }
    }
}
