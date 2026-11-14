using System;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Runs;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// Blitz (v0.113.0 beta) plays its fixed line of rooms without opening the map screen: the act is
// drawn as a strip of NBlitzTopBarMapPoint icons in the top bar, each with its own OnFocus hover
// (vanilla: the history tooltip on traveled rooms, the map-legend blurb everywhere else — the room
// you're standing in included). This gives those icons the same tooltips the map screen's nodes get:
// expected rewards on upcoming rooms (MapNodeInfoTooltipPatch) and the live history tooltip on the
// current room (CurrentNodeTooltipPatch). Traveled rooms keep vanilla's history tooltip.
//
// NBlitzTopBarMapPoint exists only on the beta, so the patch target and its members are resolved
// by name, and Prepare skips the patch on the stable branch.
[HarmonyPatch]
public static class BlitzTopBarMapPointTooltipPatch
{
    internal static readonly Type? PointType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.CommonUi.NBlitzTopBarMapPoint");

    private static readonly PropertyInfo? PointProperty =
        PointType == null ? null : AccessTools.Property(PointType, "Point");

    private static readonly FieldInfo? RunStateField =
        PointType == null ? null : AccessTools.Field(PointType, "_runState");

    public static bool Prepare()
    {
        return PointType != null && PointProperty != null && RunStateField != null
            && AccessTools.Method(PointType, "OnFocus") != null;
    }

    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(PointType, "OnFocus");
    }

    public static void Postfix(Control __instance)
    {
        try
        {
            ShowTooltip(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to show Blitz top-bar map tooltip: {e}");
        }
    }

    private static void ShowTooltip(Control anchor)
    {
        if (LocalContext.NetId == null
            || PointProperty!.GetValue(anchor) is not MapPoint mapPoint
            || RunStateField!.GetValue(anchor) is not IRunState runState)
        {
            return;
        }
        if (runState.MapLocation.coord == mapPoint.coord)
        {
            // The room you're in: vanilla shows the legend blurb, so swap in its live history tooltip.
            if (ColinsPatchKitConfig.ShowCurrentNodeTooltip)
            {
                CurrentNodeTooltipPatch.ShowHistoryTooltip(anchor, mapPoint, runState, PlaceBelow);
            }
            return;
        }
        // Traveled rooms already show vanilla's history tooltip.
        if (runState.GetHistoryEntryFor(new MapLocation(mapPoint.coord, runState.CurrentActIndex)) != null)
        {
            return;
        }
        if (!ColinsPatchKitConfig.ShowMapNodeInfoTooltips)
        {
            return;
        }
        Player? player = runState.Players.FirstOrDefault(p => p.NetId == LocalContext.NetId.Value);
        if (player != null)
        {
            MapNodeInfoTooltipPatch.ShowExpectedRewardsTooltip(anchor, mapPoint, runState, player, PlaceBelow);
        }
    }

    // Vanilla's top-bar placement — centered under the icon, 20px below it — clamped to the window
    // horizontally, since these tooltips can be much wider than the legend blurb and the strip runs
    // to the screen's right edge. Applied now and again once the tip has been laid out (its size is
    // only final a frame later).
    private static void PlaceBelow(Control anchor, NHoverTipSet set)
    {
        void Place()
        {
            if (!GodotObject.IsInstanceValid(set) || !GodotObject.IsInstanceValid(anchor))
            {
                return;
            }
            const float margin = 8f;
            float width = set.Size.X;
            float viewportWidth = anchor.GetViewportRect().Size.X;
            float x = anchor.GlobalPosition.X + anchor.Size.X * 0.5f - width * 0.5f;
            x = Math.Max(margin, Math.Min(x, viewportWidth - width - margin));
            set.GlobalPosition = new Vector2(x, anchor.GlobalPosition.Y + anchor.Size.Y + 20f);
        }
        Place();
        Callable.From(Place).CallDeferred();
    }
}

// Clears MapNodeInfoTooltipPatch's hovered-node tracking when a Blitz top-bar icon loses focus (the
// map-screen counterpart is MapNodeInfoUnfocusPatch).
[HarmonyPatch]
public static class BlitzTopBarMapPointUnfocusPatch
{
    public static bool Prepare()
    {
        return BlitzTopBarMapPointTooltipPatch.PointType != null
            && AccessTools.Method(BlitzTopBarMapPointTooltipPatch.PointType, "OnUnfocus") != null;
    }

    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(BlitzTopBarMapPointTooltipPatch.PointType, "OnUnfocus");
    }

    public static void Postfix(Control __instance)
    {
        try
        {
            MapNodeInfoTooltipPatch.ClearHoverIf(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to clear Blitz top-bar map hover: {e}");
        }
    }
}
