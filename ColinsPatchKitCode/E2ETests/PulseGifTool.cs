using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using System.Linq;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using GameDevConsole = MegaCrit.Sts2.Core.DevConsole.DevConsole;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Dev harness: captures a frame sequence of the relic and power ready-pulses for the README
// GIFs. Switches to the scratch save profile, starts a throwaway run, grants Centennial
// Puzzle, jumps to a weak fight, applies Juggling and plays two Attacks (arming Juggling's
// one-attack-away pulse), then saves 30 frames to /tmp/pulse_gif/frame_NNN.png and quits.
// Frames are assembled into GIFs externally with ffmpeg:
//
//   "Slay the Spire 2" --pulsegif=<profile>
//
// The argument is a scratch profile id. Any run already in progress on that profile is
// treated as disposable test state and gets abandoned — never pass a profile holding a
// real run.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class PulseGifPatch
{
    private const int FrameCount = 30;
    private const int FrameIntervalMs = 100;

    private static bool _started;
    private static int _originalProfileId = -1;

    public static void Postfix(NMainMenu __instance)
    {
        if (!CommandLineHelper.TryGetValue("pulsegif", out string? profileArg)
            || string.IsNullOrEmpty(profileArg) || !int.TryParse(profileArg, out int targetProfile))
        {
            return;
        }
        SceneTree tree = __instance.GetTree();
        if (!E2EHelpers.EnsureProfile(tree, targetProfile, ref _originalProfileId, "pulsegif"))
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
            MainFile.Logger.Info("pulsegif: complete");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"pulsegif: failed: {e}");
        }
        finally
        {
            E2EHelpers.RestoreProfile(_originalProfileId, "pulsegif");
            await Task.Delay(500);
            tree.Quit();
        }
    }

    private static async Task RunInternal(SceneTree tree)
    {
        CancellationToken ct = CancellationToken.None;
        await E2EHelpers.StartThrowawayRun(tree, "pulsegif", ct);

        GameDevConsole console = new(shouldAllowDebugCommands: true);
        string relicId = ModelDb.GetId(typeof(CentennialPuzzle)).Entry;
        CmdResult relicResult = console.ProcessCommand($"relic {relicId}");
        MainFile.Logger.Info($"pulsegif: relic {relicId} -> {relicResult.success} {relicResult.msg}");

        string encounterId = ModelDb.GetId(typeof(BowlbugsWeak)).Entry;
        CmdResult fightResult = console.ProcessCommand($"fight {encounterId}");
        MainFile.Logger.Info($"pulsegif: fight {encounterId} -> {fightResult.success} {fightResult.msg}");
        await WaitHelper.Until(() => CombatManager.Instance.IsInProgress
            && NPlayerHand.Instance != null && NPlayerHand.Instance.ActiveHolders.Count >= 2,
            ct, TimeSpan.FromSeconds(60), "combat hand not ready");

        int playerIndex = 0;
        var creatures = CombatManager.Instance.DebugOnlyGetState()!.Creatures;
        for (int i = 0; i < creatures.Count; i++)
        {
            if (creatures[i].IsPlayer)
            {
                playerIndex = i;
                break;
            }
        }
        string powerId = ModelDb.GetId(typeof(JugglingPower)).Entry;
        CmdResult powerResult = console.ProcessCommand($"power {powerId} 1 {playerIndex}");
        MainFile.Logger.Info($"pulsegif: power {powerId} 1 {playerIndex} -> {powerResult.success} {powerResult.msg}");
        await Task.Delay(1500);

        // Juggling pulses when one attack away from its 3rd-attack trigger: play two.
        Creature target = CombatManager.Instance.DebugOnlyGetState()!.HittableEnemies.First();
        Player player = creatures[playerIndex].Player!;
        for (int i = 0; i < 2; i++)
        {
            CardModel attack = PileType.Hand.GetPile(player).Cards
                .First(c => c.Type == CardType.Attack && c.CanPlay(out UnplayableReason _, out AbstractModel? _));
            MainFile.Logger.Info($"pulsegif: playing {attack.Id.Entry}");
            await CardCmd.AutoPlay(new BlockingPlayerChoiceContext(), attack, target);
            await Task.Delay(800);
        }

        // Let the attack VFX clear before recording.
        await Task.Delay(2500);

        // macOS stops redrawing occluded windows, which freezes the viewport texture and
        // makes every captured frame identical. Don't steal focus (stray keystrokes would
        // leak into the game); instead disable the engine's own render loop so ONLY the
        // forced draws below render — each advances the shader clock by exactly one frame
        // interval, so the assembled GIF plays at real game speed. (Without disabling the
        // loop, natural frames advance the clock too and the pulse plays ~2x fast.)
        DirAccess.MakeDirRecursiveAbsolute("/tmp/pulse_gif");
        RenderingServer.RenderLoopEnabled = false;
        try
        {
            for (int frame = 0; frame < FrameCount; frame++)
            {
                RenderingServer.ForceDraw(swapBuffers: true, frameStep: FrameIntervalMs / 1000.0);
                Image image = tree.Root.GetTexture().GetImage();
                Error err = image.SavePng($"/tmp/pulse_gif/frame_{frame:D3}.png");
                if (err != Error.Ok)
                {
                    throw new InvalidOperationException($"frame {frame} save failed: {err}");
                }
                await Task.Delay(10);
            }
        }
        finally
        {
            RenderingServer.RenderLoopEnabled = true;
        }
        MainFile.Logger.Info($"pulsegif: saved {FrameCount} frames to /tmp/pulse_gif");
    }
}
