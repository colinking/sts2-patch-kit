using BaseLib.Config;
using ColinsPatchKit.ColinsPatchKitCode.Patches;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace ColinsPatchKit.ColinsPatchKitCode;

// You're recommended but not required to keep all your code in this package and all your assets in the ColinsPatchKit folder.
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "ColinsPatchKit"; // At the moment, this is used only for the Logger and harmony names.

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        Harmony harmony = new(ModId);

        harmony.PatchAll();

        ColinsPatchKitConfig config = new();
        ModConfigRegistry.Register(ModId, config);
        config.ConfigChanged += (_, _) =>
        {
            EtherealTransparencyManager.RefreshAllCards();
            RelicReadyPulsesManager.RefreshAll();
            PowerReadyPulsesManager.RefreshAll(force: true);
        };
    }
}
