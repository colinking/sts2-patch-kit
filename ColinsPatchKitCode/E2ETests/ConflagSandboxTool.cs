using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using GameDevConsole = MegaCrit.Sts2.Core.DevConsole.DevConsole;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Dev sandbox (NOT an automated assertion harness): sets up a specific combat to
// play out by hand, then gets out of the way. Switches to the scratch save profile,
// starts a throwaway Ironclad run, jumps to the Thieving Hopper fight (the bug that
// steals a card), stocks the hand/draw pile with Conflagration and grants 100
// Strength — then stops, leaving you in control of the fight. Unlike the *-e2e
// harnesses it never ends the turn, kills the enemy, restores the profile or quits:
//
//   "Slay the Spire 2" --conflag-sandbox=<profile>
//
// The argument is a scratch profile id. Any run already in progress on that profile
// is abandoned to start the sandbox run — never pass a profile holding a real run.
// When you're done playing, quit to the menu and switch back to your own profile.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class ConflagSandboxPatch
{
    private static bool _started;
    private static int _originalProfileId = -1;

    public static void Postfix(NMainMenu __instance)
    {
        if (!CommandLineHelper.TryGetValue("conflag-sandbox", out string? profileArg)
            || string.IsNullOrEmpty(profileArg) || !int.TryParse(profileArg, out int targetProfile))
        {
            return;
        }
        SceneTree tree = __instance.GetTree();
        if (!E2EHelpers.EnsureProfile(tree, targetProfile, ref _originalProfileId, "conflag-sandbox"))
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
            MainFile.Logger.Info("conflag-sandbox: ready — over to you. Quit to menu and switch back to your own profile when done.");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"conflag-sandbox: setup failed: {e}");
        }
        // No profile restore, no quit: the player takes over from here.
    }

    private static async Task RunInternal(SceneTree tree)
    {
        CancellationToken ct = CancellationToken.None;
        string ironcladId = ModelDb.GetId(typeof(Ironclad)).Entry;
        await E2EHelpers.StartThrowawayRun(tree, "conflag-sandbox", ct, preferredCharacterId: ironcladId);

        GameDevConsole console = new(shouldAllowDebugCommands: true);

        // The bug that steals a card from you.
        string encounterId = ModelDb.GetId(typeof(ThievingHopperWeak)).Entry;
        CmdResult fightResult = console.ProcessCommand($"fight {encounterId}");
        MainFile.Logger.Info($"conflag-sandbox: fight {encounterId} -> {fightResult.success} {fightResult.msg}");
        await WaitHelper.Until(() => CombatManager.Instance.IsInProgress
            && NPlayerHand.Instance != null && NPlayerHand.Instance.ActiveHolders.Count >= 2,
            ct, TimeSpan.FromSeconds(60), "combat hand not ready");
        await Task.Delay(2000);

        // 100 Strength on the player.
        int playerIndex = -1;
        var creatures = CombatManager.Instance.DebugOnlyGetState()!.Creatures;
        for (int i = 0; i < creatures.Count; i++)
        {
            if (creatures[i].IsPlayer)
            {
                playerIndex = i;
                break;
            }
        }
        string strengthId = ModelDb.GetId(typeof(StrengthPower)).Entry;
        CmdResult strResult = console.ProcessCommand($"power {strengthId} 100 {playerIndex}");
        MainFile.Logger.Info($"conflag-sandbox: power {strengthId} 100 {playerIndex} -> {strResult.success} {strResult.msg}");

        // Conflagration: a couple in hand to play now, a few more in the draw pile to
        // keep drawing it as the fight goes on.
        string conflagId = ModelDb.GetId(typeof(Conflagration)).Entry;
        for (int i = 0; i < 2; i++)
        {
            CmdResult r = console.ProcessCommand($"card {conflagId}");
            MainFile.Logger.Info($"conflag-sandbox: card {conflagId} (hand) -> {r.success} {r.msg}");
        }
        for (int i = 0; i < 4; i++)
        {
            CmdResult r = console.ProcessCommand($"card {conflagId} Draw");
            MainFile.Logger.Info($"conflag-sandbox: card {conflagId} Draw -> {r.success} {r.msg}");
        }

        await Task.Delay(500);
    }
}
