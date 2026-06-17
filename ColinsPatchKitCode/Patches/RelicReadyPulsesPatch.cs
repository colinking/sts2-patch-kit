using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// Vanilla relics with a limited "armed" effect (Vambrace, Burning Sticks, Throwing Axe, ...)
// pulse their inventory icon via RelicModel.Status = RelicStatus.Active while the effect is
// still available, and stop once it is consumed. Seven relics with the same kind of
// once-per-turn / once-per-combat gate never touch Status, so the player cannot tell whether
// the effect is still pending. This patch drives Status for them from their private gate
// fields, following the Vambrace convention (Active while armed, Normal once consumed or
// when combat ends).
//
// Deliberately not covered: Orichalcum / Fake Orichalcum (their gate only exists for an
// instant inside end-of-turn resolution, and the armed condition — 0 Block — is already
// visible), Lasting Candy (already shows a counter), and Pael's Tears (whether it will
// trigger is just "do I have energy left", which the energy orb already shows).
//
// All gated relics except Lava Lamp call the non-virtual RelicModel.Flash() at the exact
// moment their effect is consumed, so a single postfix there handles "armed -> consumed".
// The gate fields cannot be read at that point: several triggers are async methods that set
// the field only after their first await, while a Harmony postfix on an async method runs
// after just the synchronous prelude. Arming instead recomputes from the gate field in
// synchronous reset hooks where the field has already settled.
public static class RelicReadyPulsesManager
{
    private sealed record TrackedRelic(FieldInfo Gate, bool ArmedWhenTrue);

    // Gate field per relic, and the field value that means "effect still available".
    private static readonly Dictionary<Type, TrackedRelic> _tracked = new()
    {
        // Once per combat: first Power card played grants Block.
        [typeof(Permafrost)] = Track<Permafrost>("_activatedThisCombat", armedWhenTrue: false),
        // Once per combat: first unblocked damage draws cards.
        [typeof(CentennialPuzzle)] = Track<CentennialPuzzle>("_usedThisCombat", armedWhenTrue: false),
        // Once per combat: first Strength gain is doubled.
        [typeof(RuinedHelmet)] = Track<RuinedHelmet>("_usedThisCombat", armedWhenTrue: false),
        // Per combat: card rewards are upgraded unless unblocked damage was taken.
        [typeof(LavaLamp)] = Track<LavaLamp>("_tookDamageThisCombat", armedWhenTrue: false),
        // Once per turn: first unblocked damage during your turn heals.
        [typeof(DemonTongue)] = Track<DemonTongue>("_triggeredThisTurn", armedWhenTrue: false),
        // Once per turn: first stars spent grant Strength.
        [typeof(MiniRegent)] = Track<MiniRegent>("_usedThisTurn", armedWhenTrue: false),
        // Once per turn: first Attack played is duplicated.
        [typeof(MusicBox)] = Track<MusicBox>("_wasUsedThisTurn", armedWhenTrue: false),
    };

    private static TrackedRelic Track<T>(string fieldName, bool armedWhenTrue) where T : RelicModel
    {
        FieldInfo field = typeof(T).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(typeof(T).Name, fieldName);
        return new TrackedRelic(field, armedWhenTrue);
    }

    // Recomputes Status from the relic's gate field. Only valid at points where the field
    // has already settled (synchronous hooks); never call this from a Flash postfix.
    public static void Refresh(RelicModel relic)
    {
        try
        {
            if (!_tracked.TryGetValue(relic.GetType(), out TrackedRelic? tracked) || !relic.IsMutable)
            {
                return;
            }
            bool armed = ColinsPatchKitConfig.ShowRelicReadyPulses
                && CombatManager.Instance.IsInProgress
                && (bool)tracked.Gate.GetValue(relic)! == tracked.ArmedWhenTrue;
            relic.Status = armed ? RelicStatus.Active : RelicStatus.Normal;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to refresh relic ready pulse: {e}");
        }
    }

    // Stops the pulse without consulting the gate field (it may not be written yet when the
    // relic Flash()es from an async trigger). A no-op when the status is already Normal.
    public static void StopPulse(RelicModel relic)
    {
        try
        {
            if (!_tracked.ContainsKey(relic.GetType()) || !relic.IsMutable)
            {
                return;
            }
            relic.Status = RelicStatus.Normal;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to stop relic ready pulse: {e}");
        }
    }

    // Re-evaluates every tracked relic in the current run; used when the config toggle
    // changes so pulses appear/disappear immediately instead of at the next combat hook.
    public static void RefreshAll()
    {
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
        {
            return;
        }
        foreach (Player player in runState.Players)
        {
            foreach (RelicModel relic in player.Relics)
            {
                Refresh(relic);
                if (relic is RainbowRing rainbowRing)
                {
                    RainbowRingReadyPulseManager.Refresh(rainbowRing);
                }
            }
        }
        // Rest-site relics live on the same toggle; re-evaluate them against the current rest site
        // (or clear them when not at one) so the config change takes effect immediately.
        RestSiteReadyPulsesManager.RefreshAll(NRestSiteRoom.Instance?.Options);
    }
}

// Vanilla already pulses some rest-site relics while their benefit is pending: Regal Pillow lights
// up on entering a rest site and goes quiet once you rest, and the Venerable Tea Sets pulse from the
// campfire through their next-combat energy. Several other relics that grant or modify a rest-site
// action never touch Status, so there's no hint their campfire effect is still unspent. This drives
// Status for them the same way — Active while you're at a rest site and the relic's action is still
// takeable, Normal once it's used or you leave — under the same ShowRelicReadyPulses toggle.
//
// Deliberately not covered: Regal Pillow and the Venerable Tea Sets (vanilla already pulses them),
// Eternal Feather (its heal fires instantly on entry, so nothing is left pending), and Pumpkin
// Candle (it already drives its own Status — a charge counter that grays out when empty — which a
// pulse would clobber).
public static class RestSiteReadyPulsesManager
{
    // Each tracked relic and the rest-site option whose presence means "still takeable". A null
    // option type is Miniature Tent: its benefit is the extra action itself, so it pulses while any
    // option is still selectable. The three heal-reward relics key off Heal, the option that
    // triggers them.
    private static readonly Dictionary<Type, Type?> _tracked = new()
    {
        [typeof(Shovel)] = typeof(DigRestSiteOption),
        [typeof(Girya)] = typeof(LiftRestSiteOption),
        [typeof(MeatCleaver)] = typeof(CookRestSiteOption),
        [typeof(PaelsGrowth)] = typeof(CloneRestSiteOption),
        [typeof(DreamCatcher)] = typeof(HealRestSiteOption),
        [typeof(TinyMailbox)] = typeof(HealRestSiteOption),
        [typeof(StoneHumidifier)] = typeof(HealRestSiteOption),
        [typeof(MiniatureTent)] = null,
    };

    // Recomputes Status for every tracked relic the local player owns. Pass the rest site's current
    // options while at a campfire, or null when leaving (every tracked relic settles on Normal).
    // Scoped to the local player because the options list (and the relic icons we light up) are the
    // local client's; a relic only ever adds its option to its own owner's list.
    public static void RefreshAll(IReadOnlyList<RestSiteOption>? options)
    {
        try
        {
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            Player? me = runState == null ? null : LocalContext.GetMe(runState.Players);
            if (me == null)
            {
                return;
            }
            foreach (RelicModel relic in me.Relics)
            {
                Refresh(relic, options);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to refresh rest-site relic pulses: {e}");
        }
    }

    private static void Refresh(RelicModel relic, IReadOnlyList<RestSiteOption>? options)
    {
        if (!_tracked.TryGetValue(relic.GetType(), out Type? optionType) || !relic.IsMutable)
        {
            return;
        }
        bool armed = options != null
            && ColinsPatchKitConfig.ShowRelicReadyPulses
            && (optionType == null
                ? options.Any(o => o.IsEnabled)
                : options.Any(o => o.GetType() == optionType && o.IsEnabled));
        relic.Status = armed ? RelicStatus.Active : RelicStatus.Normal;
    }
}

// NRestSiteRoom rebuilds its options at mount and again after each choice (UpdateRestSiteOptions),
// so this single postfix re-evaluates the pulses every time the takeable set changes — arming on
// arrival and clearing each relic the moment its option is spent.
[HarmonyPatch(typeof(NRestSiteRoom), "UpdateRestSiteOptions")]
public static class RestSiteReadyPulsesUpdatePatch
{
    public static void Postfix(NRestSiteRoom __instance)
    {
        RestSiteReadyPulsesManager.RefreshAll(__instance.Options);
    }
}

// Leaving the rest site clears every rest-site pulse so nothing keeps pulsing on the map.
[HarmonyPatch(typeof(NRestSiteRoom), "_ExitTree")]
public static class RestSiteReadyPulsesCleanupPatch
{
    public static void Postfix()
    {
        RestSiteReadyPulsesManager.RefreshAll(null);
    }
}

// Rainbow Ring is the one vanilla relic that already drives its own pulse — but backwards. Its
// ActivationCountThisTurn setter lights the inventory icon (RelicStatus.Active) once the relic has
// *already* granted its Strength/Dexterity this turn (count > 0), i.e. exactly when nothing more
// can happen this turn. We invert it: pulse while the relic can still trigger (no activation yet,
// combat in progress) and go quiet the instant it fires, matching the "still pending" hint the
// other ready-pulses give. Unlike the tracked relics above it isn't in _tracked — vanilla owns
// its Status, we only override the value the setter writes.
public static class RainbowRingReadyPulseManager
{
    private static readonly FieldInfo ActivationCountField =
        typeof(RainbowRing).GetField("_activationCountThisTurn",
            BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(RainbowRing), "_activationCountThisTurn");

    // Re-derives Status from the relic's activation count. When the pulse is disabled this
    // reproduces vanilla's own (post-trigger) logic so toggling the setting off restores the
    // stock behavior exactly.
    public static void Apply(RainbowRing relic, int activationCount)
    {
        try
        {
            if (!relic.IsMutable)
            {
                return;
            }
            if (!ColinsPatchKitConfig.ShowRelicReadyPulses)
            {
                relic.Status = activationCount > 0 ? RelicStatus.Active : RelicStatus.Normal;
                return;
            }
            // CombatManager clears IsInProgress before AfterCombatEnd runs (which resets the count
            // to 0), so the end-of-combat reset naturally settles on Normal instead of re-arming.
            bool armed = CombatManager.Instance.IsInProgress && activationCount < 1;
            relic.Status = armed ? RelicStatus.Active : RelicStatus.Normal;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to refresh Rainbow Ring ready pulse: {e}");
        }
    }

    public static void Refresh(RainbowRing relic) =>
        Apply(relic, (int)ActivationCountField.GetValue(relic)!);
}

// The relic routes every state change — the per-turn reset, the post-activation increment, and the
// end-of-combat reset — through ActivationCountThisTurn, so a single postfix on its setter covers
// all of them. It runs after vanilla's own (inverted) Status write and overrides it.
[HarmonyPatch(typeof(RainbowRing), "set_ActivationCountThisTurn")]
public static class RainbowRingReadyPulseSetterPatch
{
    public static void Postfix(RainbowRing __instance, int value) =>
        RainbowRingReadyPulseManager.Apply(__instance, value);
}

// Every tracked relic except Lava Lamp consumes its effect exactly when it Flash()es. The
// parameterless Flash() funnels into this overload.
[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.Flash), typeof(IEnumerable<Creature>))]
public static class RelicReadyPulsesFlashPatch
{
    public static void Postfix(RelicModel __instance)
    {
        RelicReadyPulsesManager.StopPulse(__instance);
    }
}

// Arms the per-combat relics. BeforeCombatStart fires after the room-entry hooks that reset
// their gate fields, and none of the tracked relics override it, so the base no-op body is
// what executes for them.
[HarmonyPatch(typeof(AbstractModel), nameof(AbstractModel.BeforeCombatStart))]
public static class RelicReadyPulsesCombatStartPatch
{
    public static void Postfix(AbstractModel __instance)
    {
        if (__instance is RelicModel relic)
        {
            RelicReadyPulsesManager.Refresh(relic);
        }
    }
}

// Re-arming and non-Flash consumption points: each target is a synchronous override on the
// relic itself, so its gate field has settled by the time the postfix runs.
[HarmonyPatch]
public static class RelicReadyPulsesRefreshPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        // Per-turn relics reset their gate at the start of their owner's turn.
        yield return AccessTools.DeclaredMethod(typeof(DemonTongue), "BeforeSideTurnStart");
        yield return AccessTools.DeclaredMethod(typeof(MiniRegent), "BeforeSideTurnStart");
        yield return AccessTools.DeclaredMethod(typeof(MusicBox), "BeforeSideTurnStart");
        // Lava Lamp never Flash()es; taking unblocked damage consumes it.
        yield return AccessTools.DeclaredMethod(typeof(LavaLamp), "AfterDamageReceived");
    }

    public static void Postfix(AbstractModel __instance)
    {
        RelicReadyPulsesManager.Refresh((RelicModel)__instance);
    }
}

// Clears any leftover pulse when combat ends, mirroring Vambrace's AfterCombatEnd reset so
// nothing keeps pulsing on the map. Tracked relics that don't override AfterCombatEnd run
// the base no-op body; the rest need their override patched.
[HarmonyPatch]
public static class RelicReadyPulsesCombatEndPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        // Covers Permafrost, DemonTongue and LavaLamp, which don't override AfterCombatEnd.
        yield return AccessTools.DeclaredMethod(typeof(AbstractModel), "AfterCombatEnd");
        yield return AccessTools.DeclaredMethod(typeof(CentennialPuzzle), "AfterCombatEnd");
        yield return AccessTools.DeclaredMethod(typeof(MiniRegent), "AfterCombatEnd");
        yield return AccessTools.DeclaredMethod(typeof(MusicBox), "AfterCombatEnd");
        yield return AccessTools.DeclaredMethod(typeof(RuinedHelmet), "AfterCombatEnd");
    }

    public static void Postfix(AbstractModel __instance)
    {
        if (__instance is RelicModel relic)
        {
            RelicReadyPulsesManager.StopPulse(relic);
        }
    }
}
