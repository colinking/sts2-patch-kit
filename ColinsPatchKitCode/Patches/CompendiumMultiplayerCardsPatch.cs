using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Runs;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// During a run, defaults the Compendium card library's "Multiplayer cards" toggle to whether
// the current run is multiplayer, instead of vanilla's always-on. Outside a run (main menu
// compendium) the vanilla default is kept.
//
// OnSubmenuOpened resets all filters every time the library opens (it sets
// _viewMultiplayerCards.IsTicked = true and then calls UpdateFilter), so a postfix there can
// override the default without fighting any other code path; the player can still flip the
// tickbox freely afterwards.
[HarmonyPatch(typeof(NCardLibrary), "OnSubmenuOpened")]
public static class CompendiumMultiplayerCardsPatch
{
    // _runState is only set on the in-run path (NPauseMenu -> NCompendiumSubmenu ->
    // NCardLibrary.Initialize); the main-menu compendium never calls Initialize, so a
    // non-null _runState is exactly "opened from within a run".
    private static readonly FieldInfo _runStateField =
        typeof(NCardLibrary).GetField("_runState", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(NCardLibrary), "_runState");

    private static readonly FieldInfo _viewMultiplayerCardsField =
        typeof(NCardLibrary).GetField("_viewMultiplayerCards", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(NCardLibrary), "_viewMultiplayerCards");

    private static readonly MethodInfo _updateFilterMethod =
        typeof(NCardLibrary).GetMethod("UpdateFilter", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException(nameof(NCardLibrary), "UpdateFilter");

    public static void Postfix(NCardLibrary __instance)
    {
        try
        {
            ApplyRunDefault(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to default the multiplayer cards toggle: {e}");
        }
    }

    private static void ApplyRunDefault(NCardLibrary library)
    {
        if (!ColinsPatchKitConfig.MatchCompendiumMultiplayerCardsToRun
            || _runStateField.GetValue(library) == null)
        {
            return;
        }
        bool isMultiplayer = RunManager.Instance.NetService?.Type.IsMultiplayer() == true;
        NTickbox tickbox = (NTickbox)_viewMultiplayerCardsField.GetValue(library)!;
        if (tickbox.IsTicked == isMultiplayer)
        {
            return;
        }
        // IsTicked's setter only updates the visuals (no Toggled signal), so re-run the
        // filter the same way OnSubmenuOpened does after setting the vanilla default.
        tickbox.IsTicked = isMultiplayer;
        _updateFilterMethod.Invoke(library, [false]);
    }
}
