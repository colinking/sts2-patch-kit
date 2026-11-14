using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// Reflection shims for game APIs whose *binary* shape differs between the two live branches
// (stable, beta v0.113.0). Each of these compiles fine against either build but binds the
// dll to only one shape (an added optional parameter, a setter turned init-only), failing at
// runtime with MissingMethodException on the other branch. See CLAUDE.md "Dual game-branch
// support"; `tools/BinaryCompat` lists such references for a built dll.
public static class BranchCompat
{
    // v0.113.0 added `bool isRotatingDread = false`; v0.111.0 takes just the character.
    private static readonly MethodInfo SetLocalCharacterMethod =
        AccessTools.Method(typeof(StartRunLobby), nameof(StartRunLobby.SetLocalCharacter))
        ?? throw new MissingMethodException(nameof(StartRunLobby), nameof(StartRunLobby.SetLocalCharacter));

    public static void SetLocalCharacter(StartRunLobby lobby, CharacterModel character)
    {
        InvokeWithDefaults(SetLocalCharacterMethod, lobby, character);
    }

    // v0.113.0 added a trailing `GameMode gameMode` (Blitz hides gold in the tip).
    private static readonly MethodInfo HistoryTipCreateMethod =
        AccessTools.Method(typeof(NMapPointHistoryHoverTip), nameof(NMapPointHistoryHoverTip.Create))
        ?? throw new MissingMethodException(nameof(NMapPointHistoryHoverTip), nameof(NMapPointHistoryHoverTip.Create));

    public static NMapPointHistoryHoverTip CreateMapPointHistoryHoverTip(
        int floorNum, ulong playerId, MapPointHistoryEntry entry, IRunState runState)
    {
        object[] args = HistoryTipCreateMethod.GetParameters().Length == 4
            ? new object[] { floorNum, playerId, entry, runState.GameMode }
            : new object[] { floorNum, playerId, entry };
        return (NMapPointHistoryHoverTip)HistoryTipCreateMethod.Invoke(null, args)!;
    }

    // v0.113.0 made the setter init-only (modreq(IsExternalInit)), so a compiled assignment or
    // object initializer only binds on one branch. HoverTip is a record struct: set it on a box.
    private static readonly MethodInfo OverflowSetter =
        AccessTools.PropertySetter(typeof(HoverTip), nameof(HoverTip.ShouldOverrideTextOverflow))
        ?? throw new MissingMethodException(nameof(HoverTip), nameof(HoverTip.ShouldOverrideTextOverflow));

    public static HoverTip WithTextOverflowOverride(HoverTip tip, bool value = true)
    {
        object boxed = tip;
        OverflowSetter.Invoke(boxed, new object[] { value });
        return (HoverTip)boxed;
    }

    // GameMode was renumbered in v0.113.0 (v0.111.0: Custom = 3; v0.113.0: Dread = 3, Blitz = 4),
    // and enum constants compile to ints, so never compare against a GameMode member that differs
    // across branches — compare by name instead.
    public static bool IsGameMode(GameMode mode, string name)
    {
        return mode.ToString() == name;
    }

    // v0.113.0's Blitz mode (absent on v0.111.0).
    public static bool IsBlitz(IRunState? runState)
    {
        return runState != null && IsGameMode(runState.GameMode, "Blitz");
    }

    // Invokes `method` with the given leading arguments, filling any remaining optional parameters
    // with their declared defaults.
    private static object? InvokeWithDefaults(MethodInfo method, object? target, params object?[] leading)
    {
        ParameterInfo[] ps = method.GetParameters();
        object?[] args = ps.Select((p, i) => i < leading.Length ? leading[i] : p.DefaultValue).ToArray();
        return method.Invoke(target, args);
    }
}
