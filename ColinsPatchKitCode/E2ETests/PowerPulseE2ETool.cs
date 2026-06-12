using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using GameDevConsole = MegaCrit.Sts2.Core.DevConsole.DevConsole;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Dev harness: verifies the power ready-pulse patch in a real combat by reading the actual
// `pulse` shader parameter off the NPower icons (the full model -> event -> UI path).
// Switches to the scratch save profile, starts a throwaway run, jumps to a weak fight,
// applies Echo Form (armed first-each-turn gate), Constrict (constant reminder) and
// The Bomb x3 (countdown) to the player, then asserts:
//
//   - Turn 1: Echo Form and Constrict pulse; The Bomb (3 stacks) does not.
//   - Turns 2-3: ending turns counts The Bomb down; at 1 stack it pulses.
//   - Echo Form and Constrict keep pulsing throughout.
//
// Saves /tmp/power_pulse_*.png along the way, switches back to the original profile and
// quits:
//
//   "Slay the Spire 2" --powerpulse-e2e=<profile>
//
// The argument is a scratch profile id. Any run already in progress on that profile is
// treated as disposable test state and gets abandoned — never pass a profile holding a
// real run.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class PowerPulseE2EPatch
{
    private static bool _started;
    private static int _originalProfileId = -1;

    private static readonly FieldInfo _iconField =
        typeof(NPower).GetField("_icon", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(NPower), "_icon");

    public static void Postfix(NMainMenu __instance)
    {
        if (!CommandLineHelper.TryGetValue("powerpulse-e2e", out string? profileArg)
            || string.IsNullOrEmpty(profileArg) || !int.TryParse(profileArg, out int targetProfile))
        {
            return;
        }
        SceneTree tree = __instance.GetTree();
        if (!E2EHelpers.EnsureProfile(tree, targetProfile, ref _originalProfileId, "powerpulse-e2e"))
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
            MainFile.Logger.Info("powerpulse-e2e: complete");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"powerpulse-e2e: failed: {e}");
            await E2EHelpers.Shot(tree, "/tmp/power_pulse_fail.png", "powerpulse-e2e");
        }
        finally
        {
            E2EHelpers.RestoreProfile(_originalProfileId, "powerpulse-e2e");
            await Task.Delay(500);
            tree.Quit();
        }
    }

    private static async Task RunInternal(SceneTree tree)
    {
        CancellationToken ct = CancellationToken.None;
        await E2EHelpers.StartThrowawayRun(tree, "powerpulse-e2e", ct);

        GameDevConsole console = new(shouldAllowDebugCommands: true);
        string encounterId = ModelDb.GetId(typeof(BowlbugsWeak)).Entry;
        CmdResult fightResult = console.ProcessCommand($"fight {encounterId}");
        MainFile.Logger.Info($"powerpulse-e2e: fight {encounterId} -> {fightResult.success} {fightResult.msg}");
        await WaitHelper.Until(() => CombatManager.Instance.IsInProgress
            && NPlayerHand.Instance != null && NPlayerHand.Instance.ActiveHolders.Count >= 2,
            ct, TimeSpan.FromSeconds(60), "combat hand not ready");
        await Task.Delay(2000);

        int playerIndex = -1;
        Creature? player = null;
        var creatures = CombatManager.Instance.DebugOnlyGetState()!.Creatures;
        for (int i = 0; i < creatures.Count; i++)
        {
            if (creatures[i].IsPlayer)
            {
                playerIndex = i;
                player = creatures[i];
                break;
            }
        }

        foreach ((Type type, int amount) in ((Type, int)[])
            [(typeof(EchoFormPower), 1), (typeof(ConstrictPower), 3), (typeof(TheBombPower), 3)])
        {
            string powerId = ModelDb.GetId(type).Entry;
            CmdResult result = console.ProcessCommand($"power {powerId} {amount} {playerIndex}");
            MainFile.Logger.Info($"powerpulse-e2e: power {powerId} {amount} {playerIndex} -> {result.success} {result.msg}");
        }
        await Task.Delay(1500);

        await AssertPulse<EchoFormPower>(tree, player!, expected: true, "Echo Form pulses while armed on turn 1", ct);
        await AssertPulse<ConstrictPower>(tree, player!, expected: true, "Constrict pulses as a constant reminder", ct);
        await AssertPulse<TheBombPower>(tree, player!, expected: false, "The Bomb at 3 stacks does not pulse", ct);
        await E2EHelpers.Shot(tree, "/tmp/power_pulse_1_turn1.png", "powerpulse-e2e");

        // Two end-turns count The Bomb down to 1 stack ("explodes after this turn").
        await EndTurnAndWaitForNext(ct);
        await AssertPulse<TheBombPower>(tree, player!, expected: false, "The Bomb at 2 stacks does not pulse", ct);
        await EndTurnAndWaitForNext(ct);
        await AssertPulse<TheBombPower>(tree, player!, expected: true, "The Bomb at 1 stack pulses (explodes after this turn)", ct);
        await AssertPulse<EchoFormPower>(tree, player!, expected: true, "Echo Form still pulses on turn 3", ct);
        await AssertPulse<ConstrictPower>(tree, player!, expected: true, "Constrict still pulses on turn 3", ct);
        await E2EHelpers.Shot(tree, "/tmp/power_pulse_2_turn3.png", "powerpulse-e2e");

        CmdResult killResult = console.ProcessCommand("kill all");
        MainFile.Logger.Info($"powerpulse-e2e: kill all -> {killResult.success} {killResult.msg}");
        await WaitHelper.Until(() => !CombatManager.Instance.IsInProgress, ct, TimeSpan.FromSeconds(60), "combat did not end");
    }

    private static async Task EndTurnAndWaitForNext(CancellationToken ct)
    {
        NEndTurnButton endTurn = NCombatRoom.Instance!.Ui.EndTurnButton;
        await WaitHelper.Until(() => endTurn.IsEnabled, ct, TimeSpan.FromSeconds(30), "end turn button never enabled");
        // Invoke the button's end-turn action directly: a synthetic click is a no-op when
        // the profile has the long-press-to-end-turn pref enabled.
        endTurn.CallReleaseLogic();
        await Task.Delay(1000);
        await WaitHelper.Until(() => endTurn.IsEnabled, ct, TimeSpan.FromSeconds(60), "next player turn never started");
        await Task.Delay(500);
    }

    // Reads the actual pulse shader parameter off the power's icon node, polling briefly
    // since the refresh runs inside the combat's async flow.
    private static async Task AssertPulse<T>(SceneTree tree, Creature owner, bool expected, string description, CancellationToken ct)
        where T : PowerModel
    {
        await WaitHelper.Until(() => ReadPulse<T>(tree, owner) == expected, ct, TimeSpan.FromSeconds(15),
            $"ASSERT FAILED: {description} (expected pulse={expected}, got {ReadPulse<T>(tree, owner)?.ToString() ?? "no icon"})");
        MainFile.Logger.Info($"powerpulse-e2e: PASS {description}");
    }

    private static bool? ReadPulse<T>(SceneTree tree, Creature owner) where T : PowerModel
    {
        foreach (NPower node in UiHelper.FindAll<NPower>(tree.Root))
        {
            PowerModel? model;
            try
            {
                model = node.Model;
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            if (model is not T || model.Owner != owner)
            {
                continue;
            }
            if (_iconField.GetValue(node) is not TextureRect icon || icon.Material is not ShaderMaterial material)
            {
                return null;
            }
            return material.GetShaderParameter("pulse").AsInt32() == 1;
        }
        return null;
    }
}
