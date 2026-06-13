using BaseLib.Config;

namespace ColinsPatchKit.ColinsPatchKitCode;

// Registered with BaseLib's ModConfigRegistry in MainFile.Initialize; shows up in-game
// under Settings > Mod Configuration. BaseLib only supports static properties here, and
// persists them to user://mod_configs/ColinsPatchKit.cfg (named after the root namespace).
public class ColinsPatchKitConfig : SimpleModConfig
{
    // Declaration order drives the order of the toggles in the config UI;
    // keep in sync with the patch order in README.md.
    [ConfigHoverTip(true)]
    public static bool ShowCurrentNodeTooltip { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool ShowWellLaidPlansRetainSlots { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool ExcludeCharactersFromRandom { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool ShowRelicReadyPulses { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool ShowPowerReadyPulses { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool SkipIntroLogo { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool MatchCompendiumMultiplayerCardsToRun { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool MoveShopHandAwayFaster { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool ShowCardRewardViewUpgradesToggle { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool SkipReturnedCardPreviewDelay { get; set; } = true;

    // Experimental; off by default (see README).
    [ConfigHoverTip(true)]
    public static bool MakeEtherealCardsTranslucent { get; set; } = false;
}
