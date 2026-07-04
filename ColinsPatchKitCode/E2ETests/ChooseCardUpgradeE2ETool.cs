using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using ColinsPatchKit.ColinsPatchKitCode.Patches;
using GameDevConsole = MegaCrit.Sts2.Core.DevConsole.DevConsole;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Automated assertion harness for ChooseACardUpgradeTogglePatch: verifies the "View Upgrades"
// tickbox on the choose-a-card screen shown by relics that offer a card choice on pickup.
// Starts a throwaway run, grants Lead Paperweight via the console (its AfterObtained opens
// the choose-a-card screen out of combat), then asserts, logging a
// `choosecard-e2e: PASS <assertion>` line per step:
//   - The tickbox exists on the screen.
//   - Ticking it swaps every upgradable option's displayed card to the upgraded clone while
//     the holder's CardModel keeps reporting the base card (what a pick would grant).
//   - Unticking reverts the displayed cards.
//   - Picking an option while the preview is on adds the *unupgraded* base card to the deck.
// Saves /tmp/choose_card_*.png along the way, switches back to the original profile and quits:
//
//   "Slay the Spire 2" --choosecard-e2e=<profile>
//
// The argument is a scratch profile id. Any run already in progress on that profile is treated
// as disposable test state and gets abandoned — never pass a profile holding a real run.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class ChooseCardUpgradeE2EPatch
{
    private static bool _started;
    private static int _originalProfileId = -1;

    public static void Postfix(NMainMenu __instance)
    {
        if (!CommandLineHelper.TryGetValue("choosecard-e2e", out string? profileArg)
            || string.IsNullOrEmpty(profileArg) || !int.TryParse(profileArg, out int targetProfile))
        {
            return;
        }
        SceneTree tree = __instance.GetTree();
        if (!E2EHelpers.EnsureProfile(tree, targetProfile, ref _originalProfileId, "choosecard-e2e"))
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
            MainFile.Logger.Info("choosecard-e2e: complete");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"choosecard-e2e: failed: {e}");
            await E2EHelpers.Shot(tree, "/tmp/choose_card_fail.png", "choosecard-e2e");
        }
        finally
        {
            E2EHelpers.RestoreProfile(_originalProfileId, "choosecard-e2e");
            await Task.Delay(500);
            tree.Quit();
        }
    }

    private static async Task RunInternal(SceneTree tree)
    {
        CancellationToken ct = CancellationToken.None;
        RunState runState = await E2EHelpers.StartThrowawayRun(tree, "choosecard-e2e", ct);
        Player player = runState.Players[0];
        int deckSizeBefore = player.Deck.Cards.Count;

        GameDevConsole console = new(shouldAllowDebugCommands: true);
        string relicId = ModelDb.GetId(typeof(LeadPaperweight)).Entry;
        CmdResult result = console.ProcessCommand($"relic add {relicId}");
        MainFile.Logger.Info($"choosecard-e2e: relic add {relicId} -> {result.success} {result.msg}");

        NChooseACardSelectionScreen? screen = null;
        await WaitHelper.Until(() =>
        {
            screen = tree.Root.FindChild("NChooseACardSelectionScreen", recursive: true, owned: false)
                as NChooseACardSelectionScreen;
            return screen != null;
        }, ct, TimeSpan.FromSeconds(30), "choose-a-card screen did not appear");
        // Let the card-row tween land and clear the screen's 350ms input-guard before picking.
        await Task.Delay(1000);

        NTickbox tickbox = screen!.GetNodeOrNull<NTickbox>(ViewUpgradesTickboxHelper.TickboxName)
            ?? throw new InvalidOperationException("View Upgrades tickbox not found on the choose-a-card screen");
        MainFile.Logger.Info("choosecard-e2e: PASS tickbox exists on the choose-a-card screen");
        await E2EHelpers.Shot(tree, "/tmp/choose_card_base.png", "choosecard-e2e");

        Control cardRow = screen.GetNode<Control>("CardRow");
        NGridCardHolder[] holders = cardRow.GetChildren().OfType<NGridCardHolder>().ToArray();
        NGridCardHolder[] upgradable = holders.Where(h => h.CardModel.IsUpgradable).ToArray();
        if (holders.Length == 0 || upgradable.Length == 0)
        {
            throw new InvalidOperationException(
                $"expected upgradable options, got {holders.Length} holders / {upgradable.Length} upgradable");
        }

        // Tick: every upgradable option must display its upgraded clone, while CardModel (what a
        // pick grants) keeps reporting the base card.
        SetTicked(tickbox, ticked: true);
        await Task.Delay(250);
        AssertAll(upgradable, h => h.CardNode?.Model?.IsUpgraded == true && !h.CardModel.IsUpgraded,
            "ticking previews the upgraded card while CardModel stays the base card");
        await E2EHelpers.Shot(tree, "/tmp/choose_card_previewing.png", "choosecard-e2e");

        // Untick: displayed cards revert to the base models.
        SetTicked(tickbox, ticked: false);
        await Task.Delay(250);
        AssertAll(upgradable, h => h.CardNode?.Model?.IsUpgraded == false,
            "unticking reverts the preview");

        // Pick an option with the preview back on: the deck must gain the unupgraded base card.
        SetTicked(tickbox, ticked: true);
        await Task.Delay(250);
        NGridCardHolder picked = upgradable[0];
        string pickedId = picked.CardModel.Id.Entry;
        picked.EmitSignal(NCardHolder.SignalName.Pressed, picked);
        await WaitHelper.Until(() => !GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree(),
            ct, TimeSpan.FromSeconds(15), "choose-a-card screen did not close after picking");
        await WaitHelper.Until(() => player.Deck.Cards.Count == deckSizeBefore + 1,
            ct, TimeSpan.FromSeconds(15), "picked card was not added to the deck");
        CardModelAssert(player, pickedId);
        MainFile.Logger.Info("choosecard-e2e: PASS picking while previewing adds the unupgraded base card to the deck");
        await Task.Delay(1000);
        await E2EHelpers.Shot(tree, "/tmp/choose_card_picked.png", "choosecard-e2e");
    }

    // Mirrors a user click: the IsTicked setter only updates the art, the Toggled signal drives
    // the preview callback.
    private static void SetTicked(NTickbox tickbox, bool ticked)
    {
        tickbox.IsTicked = ticked;
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
    }

    private static void AssertAll(NGridCardHolder[] holders, Func<NGridCardHolder, bool> check,
        string description)
    {
        if (!holders.All(check))
        {
            throw new InvalidOperationException($"FAILED: {description}");
        }
        MainFile.Logger.Info($"choosecard-e2e: PASS {description}");
    }

    private static void CardModelAssert(Player player, string pickedId)
    {
        bool added = player.Deck.Cards.Any(c => c.Id.Entry == pickedId && !c.IsUpgraded);
        if (!added)
        {
            throw new InvalidOperationException(
                $"FAILED: deck does not contain an unupgraded {pickedId} after picking");
        }
    }
}
