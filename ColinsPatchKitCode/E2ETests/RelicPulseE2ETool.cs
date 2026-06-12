using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using GameDevConsole = MegaCrit.Sts2.Core.DevConsole.DevConsole;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Dev harness: verifies the relic ready-pulse patch in a real combat. Switches
// to the scratch save profile, starts a throwaway run, grants Permafrost,
// Music Box and Pael's Tears, jumps to a weak fight, and asserts the Status
// transitions driven by RelicReadyPulsesManager:
//
//   - Permafrost / Music Box pulse (Active) once combat starts.
//   - Flash() stops the pulse (consumption path).
//   - Pael's Tears is deliberately untracked; it is granted as a negative
//     control and must never pulse, even across the turn boundary where its
//     own gate arms and its real trigger Flash()es.
//   - Everything is back to Normal after combat ends.
//
// Saves /tmp/relic_pulse_*.png along the way, switches back to the original
// profile and quits:
//
//   "Slay the Spire 2" --relicpulse-e2e=<profile>
//
// The argument is a scratch profile id. Any run already in progress on that
// profile is treated as disposable test state and gets abandoned — never pass
// a profile holding a real run.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class RelicPulseE2EPatch
{
    private static bool _started;
    private static int _originalProfileId = -1;

    public static void Postfix(NMainMenu __instance)
    {
        if (!CommandLineHelper.TryGetValue("relicpulse-e2e", out string? profileArg)
            || string.IsNullOrEmpty(profileArg) || !int.TryParse(profileArg, out int targetProfile))
        {
            return;
        }
        SceneTree tree = __instance.GetTree();
        if (!E2EHelpers.EnsureProfile(tree, targetProfile, ref _originalProfileId, "relicpulse-e2e"))
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
            MainFile.Logger.Info("relicpulse-e2e: complete");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"relicpulse-e2e: failed: {e}");
            await E2EHelpers.Shot(tree, "/tmp/relic_pulse_fail.png", "relicpulse-e2e");
        }
        finally
        {
            E2EHelpers.RestoreProfile(_originalProfileId, "relicpulse-e2e");
            await Task.Delay(500);
            tree.Quit();
        }
    }

    private static async Task RunInternal(SceneTree tree)
    {
        CancellationToken ct = CancellationToken.None;
        RunState runState = await E2EHelpers.StartThrowawayRun(tree, "relicpulse-e2e", ct);
        Player player = runState.Players[0];

        GameDevConsole console = new(shouldAllowDebugCommands: true);
        foreach (Type relicType in (Type[])[typeof(Permafrost), typeof(MusicBox), typeof(PaelsTears)])
        {
            string relicId = ModelDb.GetId(relicType).Entry;
            CmdResult result = console.ProcessCommand($"relic {relicId}");
            MainFile.Logger.Info($"relicpulse-e2e: relic {relicId} -> {result.success} {result.msg}");
        }
        await WaitHelper.Until(() => GetRelic<Permafrost>(player) != null
            && GetRelic<MusicBox>(player) != null && GetRelic<PaelsTears>(player) != null,
            ct, TimeSpan.FromSeconds(15), "relics were not granted");
        RelicModel permafrost = GetRelic<Permafrost>(player)!;
        RelicModel musicBox = GetRelic<MusicBox>(player)!;
        RelicModel paelsTears = GetRelic<PaelsTears>(player)!;

        string encounterId = ModelDb.GetId(typeof(BowlbugsWeak)).Entry;
        CmdResult fightResult = console.ProcessCommand($"fight {encounterId}");
        MainFile.Logger.Info($"relicpulse-e2e: fight {encounterId} -> {fightResult.success} {fightResult.msg}");
        await WaitHelper.Until(() => CombatManager.Instance.IsInProgress
            && NPlayerHand.Instance != null && NPlayerHand.Instance.ActiveHolders.Count >= 2,
            ct, TimeSpan.FromSeconds(60), "combat hand not ready");
        await Task.Delay(2000);

        await AssertStatus(permafrost, RelicStatus.Active, "Permafrost armed at combat start", ct);
        await AssertStatus(musicBox, RelicStatus.Active, "Music Box armed at combat start", ct);
        await AssertStatus(paelsTears, RelicStatus.Normal, "Pael's Tears (untracked) idle at combat start", ct);
        await E2EHelpers.Shot(tree, "/tmp/relic_pulse_1_armed.png", "relicpulse-e2e");

        // Consumption path: every tracked relic's trigger funnels through
        // Flash(), so a synthetic Flash must stop the pulse.
        permafrost.Flash();
        await AssertStatus(permafrost, RelicStatus.Normal, "Permafrost pulse stops on Flash", ct);

        // End the turn without spending energy: Pael's Tears' own gate arms at
        // turn end and its real trigger (energy gain + Flash) fires at the next
        // turn start — being untracked, its Status must never leave Normal.
        // Watch through the turn boundary, where it would pulse if tracked.
        NEndTurnButton endTurn = NCombatRoom.Instance!.Ui.EndTurnButton;
        await WaitHelper.Until(() => endTurn.IsEnabled, ct, TimeSpan.FromSeconds(30), "end turn button never enabled");
        // Invoke the button's end-turn action directly: a synthetic click is a
        // no-op when the profile has the long-press-to-end-turn pref enabled.
        endTurn.CallReleaseLogic();
        for (int i = 0; i < 30; i++)
        {
            if (paelsTears.Status != RelicStatus.Normal)
            {
                throw new Exception($"ASSERT FAILED: Pael's Tears (untracked) must never pulse (got {paelsTears.Status})");
            }
            await Task.Delay(100);
        }
        MainFile.Logger.Info("relicpulse-e2e: PASS Pael's Tears (untracked) never pulsed across the turn boundary");

        await WaitHelper.Until(() => endTurn.IsEnabled, ct, TimeSpan.FromSeconds(60), "player turn 2 never started");
        await AssertStatus(musicBox, RelicStatus.Active, "Music Box still armed on turn 2", ct);
        await AssertStatus(permafrost, RelicStatus.Normal, "Permafrost still consumed on turn 2", ct);
        await E2EHelpers.Shot(tree, "/tmp/relic_pulse_2_turn2.png", "relicpulse-e2e");

        CmdResult killResult = console.ProcessCommand("kill all");
        MainFile.Logger.Info($"relicpulse-e2e: kill all -> {killResult.success} {killResult.msg}");
        await WaitHelper.Until(() => !CombatManager.Instance.IsInProgress, ct, TimeSpan.FromSeconds(60), "combat did not end");
        await AssertStatus(permafrost, RelicStatus.Normal, "Permafrost cleared after combat", ct);
        await AssertStatus(musicBox, RelicStatus.Normal, "Music Box cleared after combat", ct);
        await AssertStatus(paelsTears, RelicStatus.Normal, "Pael's Tears (untracked) still idle after combat", ct);
    }

    private static RelicModel? GetRelic<T>(Player player) where T : RelicModel
    {
        return player.GetRelicById(ModelDb.GetId(typeof(T)));
    }

    // The hooks that drive Status run inside the combat's async flow, so poll
    // briefly instead of asserting instantly.
    private static async Task AssertStatus(RelicModel relic, RelicStatus expected, string description, CancellationToken ct)
    {
        await WaitHelper.Until(() => relic.Status == expected, ct, TimeSpan.FromSeconds(15),
            $"ASSERT FAILED: {description} (expected {expected}, got {relic.Status})");
        MainFile.Logger.Info($"relicpulse-e2e: PASS {description}");
    }
}
