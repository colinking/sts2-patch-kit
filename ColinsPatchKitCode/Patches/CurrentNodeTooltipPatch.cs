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

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

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
        if (!ColinsPatchKitConfig.ShowCurrentNodeTooltip)
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
        if (DirectionalNavHelper.IsUsingDirectionalNav != false || LocalContext.NetId == null)
        {
            return;
        }
        if (screen.IsTraveling || screen.Drawings.GetLocalDrawingMode() != DrawingMode.None)
        {
            return;
        }
        // While you're actually in this room's combat, the history tooltip's Rewards section is empty
        // (nothing's been earned yet), so we insert our expected rewards into it below. Once the
        // combat ends, the section fills with the real rewards and the insertion is skipped.
        MapPointType? combatType = MapNodeInfoTooltipPatch.InProgressCombatType(runState);
        Player? combatPlayer = combatType.HasValue
            ? runState.Players.FirstOrDefault(p => p.NetId == LocalContext.NetId.Value)
            : null;
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
        NMapPointHistoryHoverTip historyTip = NMapPointHistoryHoverTip.Create(floorNum, LocalContext.NetId.Value, entry);
        NHoverTipSet tip = NHoverTipSet.CreateAndShowMapPointHistory(point, historyTip);
        // Mid-combat, fill the (otherwise empty) Rewards section with the expected rewards. Runs after
        // the tooltip's _Ready has populated the section, so it reveals/overrides it.
        if (combatType.HasValue && combatPlayer != null)
        {
            MapNodeInfoTooltipPatch.RenderExpectedRewardsIntoHistory(historyTip, runState, combatPlayer, point.Point, combatType.Value);
        }
        // The recorded potion-chance line is appended by CombatPotionChanceHistoryTooltipPatch's
        // _Ready postfix, which runs for every history tooltip — this current node and the traveled
        // ones alike (it no-ops on the still-in-progress current combat, whose expected potion chance
        // is shown by the inserted block above instead). Alignment is deferred so the tip is sized
        // (including that line) before it's positioned.
        Callable.From(delegate
        {
            tip.SetAlignment(point, HoverTip.GetHoverTipAlignment(point));
        }).CallDeferred();
    }
}

// Renders the potion outcome onto a previous combat node's run-history tooltip: the chance that
// applied when its reward rolled (reconstructed by replaying the run's potion pity from history —
// see MapNodeInfoTooltipPatch.HistoricalPotionInfo), tagged onto the rolled potion or shown as a
// red "No potion" line — with the guaranteed potion tagged apart when an event added one at the
// node (Punch Off's fight). Covers traveled nodes and the just-completed current node alike (both
// use
// NMapPointHistoryHoverTip); the still-in-combat current node shows its own expected-rewards
// tooltip instead, so the live case never reaches here. ____playerId is the tip's player field.
[HarmonyPatch(typeof(NMapPointHistoryHoverTip), "_Ready")]
public static class CombatPotionChanceHistoryTooltipPatch
{
    public static void Postfix(NMapPointHistoryHoverTip __instance, MapPointHistoryEntry ____entry, ulong ____playerId)
    {
        try
        {
            if (!ColinsPatchKitConfig.ShowPotionChances)
            {
                return;
            }
            IRunState? runState = RunManager.Instance.DebugOnlyGetState();
            if (runState == null)
            {
                return;
            }
            if (MapNodeInfoTooltipPatch.HistoricalPotionInfo(runState, ____entry, ____playerId) is { } info)
            {
                MapNodeInfoTooltipPatch.RenderHistoricalPotion(__instance, info.chance, info.rollHit, info.hasGuaranteedPotion);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to add potion chance to history tooltip: {e}");
        }
    }
}
