using BaseLib.Config;

namespace MapMod.MapModCode;

// Registered with BaseLib's ModConfigRegistry in MainFile.Initialize; shows up in-game
// under Settings > Mod Configuration. BaseLib only supports static properties here, and
// persists them to user://mod_configs/MapMod.cfg (named after the root namespace).
public class MapModConfig : SimpleModConfig
{
    [ConfigHoverTip(true)]
    public static bool ShowCurrentNodeTooltip { get; set; } = true;
}
