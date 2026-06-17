using BaseLib.Config;

namespace ColinsPatchKit.ColinsPatchKitCode;

// Registered with BaseLib's ModConfigRegistry in MainFile.Initialize; shows up in-game
// under Settings > Mod Configuration. BaseLib only supports static properties here, and
// persists them to user://mod_configs/ColinsPatchKit.cfg (named after the root namespace).
public class ColinsPatchKitConfig : SimpleModConfig
{
    // Declaration order drives the order of the toggles in the config UI, and a
    // [ConfigSection] on the first property of each group renders a header above it
    // (loc key COLINSPATCHKIT-<SCREAMING_SNAKE_NAME>.title). Keep the grouping and
    // order in sync with the patch order in README.md.

    [ConfigSection("Map")]
    [ConfigHoverTip(true)]
    public static bool ShowCurrentNodeTooltip { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool ShowMapNodeInfoTooltips { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool ShowPotionChances { get; set; } = true;

    [ConfigSection("Combat")]
    [ConfigHoverTip(true)]
    public static bool ShowWellLaidPlansRetainSlots { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool SortCursesAndStatusesFirst { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool ShowRelicReadyPulses { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool ShowPowerReadyPulses { get; set; } = true;

    [ConfigSection("Menus")]
    [ConfigHoverTip(true)]
    public static bool ExcludeCharactersFromRandom { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool ShowShopAffordabilityPreview { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool ConfirmRestSiteProceedWithTent { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool ShowCardRewardViewUpgradesToggle { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool MatchCompendiumMultiplayerCardsToRun { get; set; } = true;

    [ConfigSection("SpeedUps")]
    [ConfigHoverTip(true)]
    public static bool SkipGameOverAnimations { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool MoveShopHandAwayFaster { get; set; } = true;

    [ConfigHoverTip(true)]
    public static bool SkipReturnedCardPreviewDelay { get; set; } = true;
}
