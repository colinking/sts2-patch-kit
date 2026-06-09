using System;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace MapMod.MapModCode;

// Vanilla NMapPoint.OnFocus only shows the room-history tooltip on previously
// traveled nodes, explicitly skipping the node the player is currently on.
// This postfix handles that skipped case and shows the same tooltip there.
[HarmonyPatch(typeof(NMapPoint), "OnFocus")]
public static class CurrentNodeTooltipPatch
{
    public static void Postfix(NMapPoint __instance, IRunState ____runState, NMapScreen ____screen)
    {
        try
        {
            ShowCurrentNodeTooltip(__instance, ____runState, ____screen);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to show current-node tooltip: {e}");
        }
    }

    private static void ShowCurrentNodeTooltip(NMapPoint point, IRunState runState, NMapScreen screen)
    {
        if (!MapModConfig.ShowCurrentNodeTooltip)
        {
            return;
        }
        // Only the case vanilla OnFocus skips: the node the player is standing on.
        // Everything else (other traveled nodes) is already handled by the original.
        if (point.State != MapPointState.Traveled || runState.MapLocation.coord != point.Point.coord)
        {
            return;
        }
        // Mirror vanilla guards: no tooltips on controller, while traveling, or while map drawing.
        if (NControllerManager.Instance?.IsUsingController != false || LocalContext.NetId == null)
        {
            return;
        }
        if (screen.IsTraveling || screen.Drawings.GetLocalDrawingMode() != DrawingMode.None)
        {
            return;
        }
        MapPointHistoryEntry? entry = runState.GetHistoryEntryFor(new MapLocation(point.Point.coord, runState.CurrentActIndex));
        if (entry == null)
        {
            return;
        }
        // NMapPointHistoryHoverTip._Ready throws if the local player has no stats
        // entry (possible if they weren't in the run when this room was entered).
        if (entry.PlayerStats.All(s => s.PlayerId != LocalContext.NetId.Value))
        {
            return;
        }
        // The game only snapshots HP/gold into the history entry when leaving a room
        // (RunManager.UpdatePlayerStatsInMapPointHistory), so for the current room they
        // still hold defaults. Fill in live values; they're overwritten on room exit anyway.
        foreach (Player player in runState.Players)
        {
            PlayerMapPointHistoryEntry? stats = entry.PlayerStats.FirstOrDefault(s => s.PlayerId == player.NetId);
            if (stats != null)
            {
                stats.CurrentGold = player.Gold;
                stats.CurrentHp = player.Creature.CurrentHp;
                stats.MaxHp = player.Creature.MaxHp;
            }
        }
        // TurnsTaken is likewise only written when combat ends (CombatManager), so
        // mid-combat hovers would show a stale count. Fill in the live turn number
        // the same way RunManager.OnEnded does.
        if (runState.CurrentRoom is CombatRoom combatRoom && entry.Rooms.Count > 0)
        {
            Player? me = runState.Players.FirstOrDefault(p => p.NetId == LocalContext.NetId.Value);
            entry.Rooms.Last().TurnsTaken = me?.PlayerCombatState?.TurnNumber ?? combatRoom.CombatState.RoundNumber;
        }
        int floorNum = point.Point.coord.row + 1;
        for (int i = 0; i < runState.MapPointHistory.Count - 1; i++)
        {
            floorNum += runState.MapPointHistory[i].Count;
        }
        NHoverTipSet.Remove(point);
        NHoverTipSet tip = NHoverTipSet.CreateAndShowMapPointHistory(point, NMapPointHistoryHoverTip.Create(floorNum, LocalContext.NetId.Value, entry));
        Callable.From(delegate
        {
            tip.SetAlignment(point, HoverTip.GetHoverTipAlignment(point));
        }).CallDeferred();
    }
}
