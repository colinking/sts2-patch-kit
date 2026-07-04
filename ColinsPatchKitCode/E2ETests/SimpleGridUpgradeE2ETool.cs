using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using ColinsPatchKit.ColinsPatchKitCode.Patches;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Automated assertion harness for SimpleGridUpgradeTogglePatch: verifies the "View Upgrades"
// tickbox on the simple card-select grid (Room Full of Cheese "Gorge", Brain Leech, Sea Glass,
// Sealed Deck). Starts a throwaway run and obtains Sea Glass in code (with a CharacterId set,
// unlike a console grant, so it doesn't hit the relic's missing-character error path) — its
// pickup effect opens the 15-card grid out of combat. Asserts, logging a
// `simplegrid-e2e: PASS <assertion>` line per step:
//   - The tickbox exists on the grid screen.
//   - Ticking it flips NCardGrid.IsShowingUpgrades and every upgradable holder displays its
//     upgraded clone while CardModel keeps reporting the base card.
//   - Unticking reverts the displayed cards.
//   - Selecting a card while previewing and confirming adds the *unupgraded* card to the deck.
// Saves /tmp/simple_grid_*.png along the way, switches back to the original profile and quits:
//
//   "Slay the Spire 2" --simplegrid-e2e=<profile>
//
// The argument is a scratch profile id. Any run already in progress on that profile is treated
// as disposable test state and gets abandoned — never pass a profile holding a real run.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class SimpleGridUpgradeE2EPatch
{
    private static bool _started;
    private static int _originalProfileId = -1;

    public static void Postfix(NMainMenu __instance)
    {
        if (!CommandLineHelper.TryGetValue("simplegrid-e2e", out string? profileArg)
            || string.IsNullOrEmpty(profileArg) || !int.TryParse(profileArg, out int targetProfile))
        {
            return;
        }
        SceneTree tree = __instance.GetTree();
        if (!E2EHelpers.EnsureProfile(tree, targetProfile, ref _originalProfileId, "simplegrid-e2e"))
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
            MainFile.Logger.Info("simplegrid-e2e: complete");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"simplegrid-e2e: failed: {e}");
            await E2EHelpers.Shot(tree, "/tmp/simple_grid_fail.png", "simplegrid-e2e");
        }
        finally
        {
            E2EHelpers.RestoreProfile(_originalProfileId, "simplegrid-e2e");
            await Task.Delay(500);
            tree.Quit();
        }
    }

    private static async Task RunInternal(SceneTree tree)
    {
        CancellationToken ct = CancellationToken.None;
        RunState runState = await E2EHelpers.StartThrowawayRun(tree, "simplegrid-e2e", ct);
        Player player = runState.Players[0];
        int deckSizeBefore = player.Deck.Cards.Count;

        // Obtain Sea Glass in code so CharacterId is assigned up front; its AfterObtained then
        // opens the simple card-select grid with 15 cards of another character's pool.
        SeaGlass seaGlass = (SeaGlass)ModelDb.Relic<SeaGlass>().ToMutable();
        seaGlass.CharacterId = ModelDb.Character<Ironclad>().Id;
        MainFile.Logger.Info("simplegrid-e2e: obtaining Sea Glass (Ironclad pool)");
        // Obtain blocks until the selection screen closes; run it in the background and drive
        // the screen from here (RunSafely observes any exception it throws).
        _ = TaskHelper.RunSafely(RelicCmd.Obtain(seaGlass, player));

        NSimpleCardSelectScreen? screen = null;
        await WaitHelper.Until(() =>
        {
            screen = FindScreen(tree);
            return screen != null;
        }, ct, TimeSpan.FromSeconds(30), "simple card-select grid did not appear");
        await Task.Delay(1000);

        NTickbox tickbox = screen!.GetNodeOrNull<NTickbox>(ViewUpgradesTickboxHelper.TickboxName)
            ?? throw new InvalidOperationException("View Upgrades tickbox not found on the grid screen");
        MainFile.Logger.Info("simplegrid-e2e: PASS tickbox exists on the simple card-select grid");
        await E2EHelpers.Shot(tree, "/tmp/simple_grid_base.png", "simplegrid-e2e");

        NGridCardHolder[] upgradable = FindHolders(screen)
            .Where(h => h.CardModel != null && h.CardModel.IsUpgradable).ToArray();
        if (upgradable.Length == 0)
        {
            throw new InvalidOperationException("expected upgradable options in the grid");
        }

        // Tick: the grid flips into upgrade preview, holders keep reporting the base card.
        SetTicked(tickbox, ticked: true);
        await Task.Delay(250);
        AssertAll(upgradable, h => h.CardNode?.Model?.IsUpgraded == true && !h.CardModel.IsUpgraded,
            "ticking previews the upgraded cards while CardModel stays the base card");
        await E2EHelpers.Shot(tree, "/tmp/simple_grid_previewing.png", "simplegrid-e2e");

        // Untick: displayed cards revert to the base models.
        SetTicked(tickbox, ticked: false);
        await Task.Delay(250);
        AssertAll(upgradable, h => h.CardNode?.Model?.IsUpgraded == false,
            "unticking reverts the preview");

        // Select one card with the preview back on, confirm, and check what joined the deck.
        SetTicked(tickbox, ticked: true);
        await Task.Delay(250);
        NGridCardHolder picked = upgradable[0];
        string pickedId = picked.CardModel.Id.Entry;
        picked.EmitSignal(NCardHolder.SignalName.Pressed, picked);
        await Task.Delay(250);
        NConfirmButton confirm = screen.GetNode<NConfirmButton>("%Confirm");
        confirm.EmitSignal(NClickableControl.SignalName.Released, confirm);
        await WaitHelper.Until(() => player.Deck.Cards.Count == deckSizeBefore + 1,
            ct, TimeSpan.FromSeconds(15), "picked card was not added to the deck");
        if (!player.Deck.Cards.Any(c => c.Id.Entry == pickedId && !c.IsUpgraded))
        {
            throw new InvalidOperationException(
                $"FAILED: deck does not contain an unupgraded {pickedId} after confirming");
        }
        MainFile.Logger.Info("simplegrid-e2e: PASS confirming while previewing adds the unupgraded card to the deck");
        await Task.Delay(1000);
        await E2EHelpers.Shot(tree, "/tmp/simple_grid_picked.png", "simplegrid-e2e");
    }

    private static NSimpleCardSelectScreen? FindScreen(SceneTree tree) =>
        tree.Root.FindChildren("*", nameof(NSimpleCardSelectScreen), recursive: true, owned: false)
            .OfType<NSimpleCardSelectScreen>().FirstOrDefault();

    private static NGridCardHolder[] FindHolders(NSimpleCardSelectScreen screen) =>
        screen.FindChildren("*", nameof(NGridCardHolder), recursive: true, owned: false)
            .OfType<NGridCardHolder>().ToArray();

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
        MainFile.Logger.Info($"simplegrid-e2e: PASS {description}");
    }
}
