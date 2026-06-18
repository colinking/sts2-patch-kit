using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ColinsPatchKit.ColinsPatchKitCode.Patches;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using GameDevConsole = MegaCrit.Sts2.Core.DevConsole.DevConsole;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Dev harness: renders the retain-slot outlines (with a real card occupying
// one slot) on an overlay at the main menu, so the slot visuals can be
// screenshot-verified without starting a run:
//
//   "Slay the Spire 2" --retainslots=3:1 \
//       --retainslots-shot=/tmp/retain_slots.png --retainslots-quit
//
// (3 slots, slot index 1 filled by a card.)
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class RetainSlotsPreviewPatch
{
    private static bool _ran;

    public static void Postfix(NMainMenu __instance)
    {
        if (_ran || !CommandLineHelper.TryGetValue("retainslots", out string? spec) || string.IsNullOrEmpty(spec))
        {
            return;
        }
        _ran = true;
        SceneTree tree = __instance.GetTree();
        tree.CreateTimer(1.0).Timeout += () => Run(spec, tree);
    }

    private static void Run(string spec, SceneTree tree)
    {
        try
        {
            string[] parts = spec.Split(':', 2);
            int slotCount = int.Parse(parts[0]);
            HashSet<int> filled = new();
            if (parts.Length == 2 && parts[1].Length > 0)
            {
                filled = parts[1].Split(',').Select(int.Parse).ToHashSet();
            }

            CanvasLayer layer = new() { Layer = 90 };
            ColorRect backdrop = new() { Color = new Color(0.13f, 0.14f, 0.16f) };
            backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            layer.AddChild(backdrop);
            Control center = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
            center.SetAnchorsPreset(Control.LayoutPreset.Center);
            backdrop.AddChild(center);
            NGame.Instance!.AddChildSafely(layer);

            float spacing = RetainSlotsManager.MeasureSlotSpacing();
            MainFile.Logger.Info($"retainslots: spacing={spacing}.");
            center.Draw += () => RetainSlotsManager.DrawSlots(center, slotCount, spacing, filled);
            center.QueueRedraw();

            // Fill the requested slots with a real card to check size/alignment.
            foreach (int slot in filled)
            {
                CardModel? model = ModelDb.AllCards.FirstOrDefault(c => c.Id.Entry == "DEFILE");
                NCard? card = model == null ? null : NCard.Create(model);
                if (card == null)
                {
                    continue;
                }
                center.AddChildSafely(card);
                card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
                card.Position = RetainSlotsManager.SlotPosition(slot, slotCount, spacing);
                card.Scale = NCardHolder.smallScale;
            }

            if (CommandLineHelper.TryGetValue("retainslots-shot", out string? shotPath) && !string.IsNullOrEmpty(shotPath))
            {
                tree.CreateTimer(1.5).Timeout += () =>
                {
                    Image image = tree.Root.GetTexture().GetImage();
                    Error err = image.SavePng(shotPath);
                    MainFile.Logger.Info($"retainslots: screenshot to '{shotPath}' ({err}).");
                    if (CommandLineHelper.HasArg("retainslots-quit"))
                    {
                        tree.Quit();
                    }
                };
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"retainslots: preview failed: {e}");
        }
    }
}

// Dev harness: side-by-side sizing calibration. Renders three columns at the
// main menu — an empty slot, a card sitting OVER its slot (slot behind, the live
// look), and a card sitting UNDER its slot (slot drawn on top, so the outline is
// fully visible against the card edges) — so the SlotScale can be eyeballed and
// tweaked. Pair with --test-window-size for a high-res capture:
//
//   "Slay the Spire 2" --retainslots-sizecheck=/tmp/sizecheck.png \
//       --test-window-size=2560x1600
//
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class RetainSlotsSizeCheckPatch
{
    private static bool _ran;

    public static void Postfix(NMainMenu __instance)
    {
        if (_ran || !CommandLineHelper.TryGetValue("retainslots-sizecheck", out string? shotPath)
            || string.IsNullOrEmpty(shotPath))
        {
            return;
        }
        _ran = true;
        SceneTree tree = __instance.GetTree();
        tree.CreateTimer(1.0).Timeout += () => Run(shotPath, tree);
    }

    private static void Run(string shotPath, SceneTree tree)
    {
        try
        {
            CanvasLayer layer = new() { Layer = 90 };
            ColorRect backdrop = new() { Color = new Color(0.13f, 0.14f, 0.16f) };
            backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            layer.AddChild(backdrop);
            Control center = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
            center.SetAnchorsPreset(Control.LayoutPreset.Center);
            backdrop.AddChild(center);
            NGame.Instance!.AddChildSafely(layer);

            CardModel? model = ModelDb.AllCards.FirstOrDefault(c => c.Id.Entry == "DEFILE");

            // Columns: 0 = empty slot, 1 = card over slot (live look), 2 = slot over card.
            float colSpacing = 360f;
            for (int col = 0; col < 3; col++)
            {
                var colPos = new Vector2(colSpacing * (col - 1), 0f);

                Control outline = new() { Position = colPos, MouseFilter = Control.MouseFilterEnum.Ignore };
                outline.Draw += () => RetainSlotsManager.DrawSlots(outline, 1, 0f, new HashSet<int>());

                NCard? card = (col == 0 || model == null) ? null : NCard.Create(model);
                if (card != null)
                {
                    card.Position = colPos;
                    card.Scale = NCardHolder.smallScale;
                }

                // Child order sets z-order: later child renders on top.
                if (col == 2)
                {
                    if (card != null) { center.AddChildSafely(card); card.UpdateVisuals(PileType.None, CardPreviewMode.Normal); }
                    center.AddChildSafely(outline);   // slot OVER card
                }
                else
                {
                    center.AddChildSafely(outline);   // slot UNDER card (or empty)
                    if (card != null) { center.AddChildSafely(card); card.UpdateVisuals(PileType.None, CardPreviewMode.Normal); }
                }
                outline.QueueRedraw();
            }

            tree.CreateTimer(1.5).Timeout += () =>
            {
                Image image = tree.Root.GetTexture().GetImage();
                Error err = image.SavePng(shotPath);
                MainFile.Logger.Info($"retainslots-sizecheck: screenshot to '{shotPath}' ({err}).");
                tree.Quit();
            };
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"retainslots-sizecheck: failed: {e}");
            tree.Quit();
        }
    }
}

// Dev harness: full end-to-end verification of the retain slots in a real
// combat. Switches to the scratch save profile, starts a throwaway run, jumps
// to a weak fight, grants Well Laid Plans x2, ends the turn, and then walks
// the selection flow (pick, pick, deselect leftmost, re-pick), saving a
// screenshot at each step. Switches back to the original profile and quits.
//
//   "Slay the Spire 2" --retainslots-e2e=2
//
// The argument is a scratch profile id. Any run already in progress on that
// profile is treated as disposable test state and gets abandoned — never pass
// a profile holding a real run.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class RetainSlotsE2EPatch
{
    private static bool _started;
    private static int _originalProfileId = -1;

    public static void Postfix(NMainMenu __instance)
    {
        if (!CommandLineHelper.TryGetValue("retainslots-e2e", out string? profileArg)
            || string.IsNullOrEmpty(profileArg) || !int.TryParse(profileArg, out int targetProfile))
        {
            return;
        }
        SceneTree tree = __instance.GetTree();
        if (!E2EHelpers.EnsureProfile(tree, targetProfile, ref _originalProfileId, "retainslots-e2e"))
        {
            return;
        }
        if (_started)
        {
            return;
        }
        _started = true;
        tree.CreateTimer(1.0).Timeout += () => TaskHelper.RunSafely(Run(tree));
    }

    private static async Task Run(SceneTree tree)
    {
        try
        {
            await RunInternal(tree);
            MainFile.Logger.Info("retainslots-e2e: complete");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"retainslots-e2e: failed: {e}");
            await Shot(tree, "/tmp/retain_e2e_fail.png");
        }
        finally
        {
            E2EHelpers.RestoreProfile(_originalProfileId, "retainslots-e2e");
            await Task.Delay(500);
            tree.Quit();
        }
    }

    private static async Task RunInternal(SceneTree tree)
    {
        CancellationToken ct = CancellationToken.None;
        await E2EHelpers.StartThrowawayRun(tree, "retainslots-e2e", ct);

        GameDevConsole console = new(shouldAllowDebugCommands: true);
        string encounterId = ModelDb.GetId(typeof(BowlbugsWeak)).Entry;
        CmdResult fightResult = console.ProcessCommand($"fight {encounterId}");
        MainFile.Logger.Info($"retainslots-e2e: fight {encounterId} -> {fightResult.success} {fightResult.msg}");
        await WaitHelper.Until(() => CombatManager.Instance.IsInProgress
            && NPlayerHand.Instance != null && NPlayerHand.Instance.ActiveHolders.Count >= 2,
            ct, TimeSpan.FromSeconds(60), "combat hand not ready");
        await Task.Delay(2000);

        int playerIndex = -1;
        IReadOnlyList<MegaCrit.Sts2.Core.Entities.Creatures.Creature> creatures = CombatManager.Instance.DebugOnlyGetState()!.Creatures;
        for (int i = 0; i < creatures.Count; i++)
        {
            if (creatures[i].IsPlayer)
            {
                playerIndex = i;
                break;
            }
        }
        string powerId = ModelDb.GetId(typeof(WellLaidPlansPower)).Entry;
        CmdResult powerResult = console.ProcessCommand($"power {powerId} 2 {playerIndex}");
        MainFile.Logger.Info($"retainslots-e2e: power {powerId} 2 {playerIndex} -> {powerResult.success} {powerResult.msg}");
        await Task.Delay(1000);

        MainFile.Logger.Info("retainslots-e2e: ending turn");
        NEndTurnButton endTurn = NCombatRoom.Instance!.Ui.EndTurnButton;
        NPlayerHand hand = NPlayerHand.Instance!;
        await WaitHelper.Until(() => endTurn.IsEnabled, ct, TimeSpan.FromSeconds(30), "end turn button never enabled");
        // Invoke the button's end-turn action directly: a synthetic click is a
        // no-op when the profile has the long-press-to-end-turn pref enabled.
        endTurn.CallReleaseLogic();
        await WaitHelper.Until(() => hand.CurrentMode == NPlayerHand.Mode.SimpleSelect,
            ct, TimeSpan.FromSeconds(30), "retain selection did not start");
        await Task.Delay(1000);
        await Shot(tree, "/tmp/retain_e2e_1_empty.png");

        // ForceClick only emits Released, which holders ignore (they listen to
        // MousePressed/MouseReleased), so drive selection through the same
        // methods the input handlers call.
        SelectCard(hand);
        await Task.Delay(800);
        await Shot(tree, "/tmp/retain_e2e_2_one.png");

        SelectCard(hand);
        await Task.Delay(800);
        await Shot(tree, "/tmp/retain_e2e_3_two.png");

        // Deselect the card in the LEFT slot; expect an empty left slot with
        // the right card staying put.
        NSelectedHandCardContainer container = (NSelectedHandCardContainer)AccessTools
            .Field(typeof(NPlayerHand), "_selectedHandCardContainer").GetValue(hand)!;
        List<NSelectedHandCardHolder> selected = container.Holders.OrderBy(h => h.Position.X).ToList();
        MainFile.Logger.Info($"retainslots-e2e: {selected.Count} selected holders at x=[{string.Join(",", selected.Select(h => h.Position.X))}]");
        container.DeselectCard(selected.First().CardNode!.Model!);
        await Task.Delay(800);
        await Shot(tree, "/tmp/retain_e2e_4_gap.png");

        // Re-pick: the new card should land in the empty LEFT slot.
        SelectCard(hand);
        await Task.Delay(800);
        await Shot(tree, "/tmp/retain_e2e_5_refilled.png");
    }

    private static void SelectCard(NPlayerHand hand)
    {
        NHandCardHolder holder = hand.ActiveHolders[0];
        MainFile.Logger.Info($"retainslots-e2e: selecting card {holder.CardNode?.Model?.Id.Entry}");
        AccessTools.Method(typeof(NPlayerHand), "SelectCardInSimpleMode").Invoke(hand, new object[] { holder });
    }

    private static Task Shot(SceneTree tree, string path)
    {
        return E2EHelpers.Shot(tree, path, "retainslots-e2e");
    }
}
