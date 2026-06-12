using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.ValueProps;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// PowerModel has StartPulsing()/StopPulsing() — a persistent pulse shader on the power icon —
// but only Escape Artist uses it (pulses when the enemy escapes after the next turn). This
// patch drives the pulse for powers whose "will it trigger?" state is otherwise invisible:
//
//   - First-X-each-turn powers (Echo Form, Iteration, Nostalgia, Phantom Blades, Lethality,
//     Unmovable, Pale Blue Dot, Smoggy): pulse while the effect is still armed this turn.
//   - Threshold counters (Juggling, Orbit, Automation, Panache, Outbreak, Withering
//     Presence): pulse when one event away from triggering (Nunchaku-style convention).
//   - Countdowns to a one-shot event (The Bomb, Asleep, Slumber, Hatch, Battleworn Dummy
//     time limit): pulse at 1 stack, i.e. "fires after this turn" (Escape Artist convention).
//   - End-of-turn damage debuffs (Constrict, Disintegration): pulse the whole time as a
//     "don't forget to block" reminder — these trigger unconditionally.
//
// Armed state is recomputed from an exact event mesh instead of patching the powers' own
// (mostly async) hooks: History.Changed fires synchronously the moment a combat-history
// entry is appended (the history-computed gates above read exactly those entries),
// DisplayAmountChanged fires synchronously when a power's internal counter or stack Amount
// changes (SetAmount invokes it too), and the CombatState.RoundNumber/CurrentSide setters
// mark turn boundaries, which flip every HappenedThisTurn() gate. NPower never renders pulse
// state on creation (the shader param is only ever set from the Start/StopPulsing events),
// so a postfix on NPower._Ready re-fires the current state once the icon exists.
public static class PowerReadyPulsesManager
{
    // Armed predicate per tracked power type. Each mirrors the trigger condition in the
    // power's own decompiled code; history-based ones read the same entries the power does.
    private static readonly Dictionary<Type, Func<PowerModel, bool>> _armed = new()
    {
        // --- First X each turn (pulse while still available this turn) ---
        // Duplicates the first Amount card plays each turn.
        [typeof(EchoFormPower)] = p => CountStartedThisTurn(p,
            e => e.Actor == p.Owner && e.CardPlay.IsFirstInSeries) < p.Amount,
        // First Status card drawn each turn draws extra cards.
        [typeof(IterationPower)] = p => !CombatManager.Instance.History.Entries
            .OfType<CardDrawnEntry>().Any(e => e.HappenedThisTurn(p.CombatState)
                && e.Actor == p.Owner && e.Card.Type == CardType.Status),
        // First Amount Attacks/Skills each turn go on top of the draw pile.
        [typeof(NostalgiaPower)] = p => CountStartedThisTurn(p,
            e => e.CardPlay.Card.Owner == p.Owner.Player
                && (e.CardPlay.Card.Type == CardType.Attack || e.CardPlay.Card.Type == CardType.Skill)) < p.Amount,
        // First Shiv each turn deals bonus damage.
        [typeof(PhantomBladesPower)] = p => !CombatManager.Instance.History.CardPlaysFinished
            .Any(e => e.HappenedThisTurn(p.CombatState)
                && e.CardPlay.Card.Tags.Contains(CardTag.Shiv)
                && e.CardPlay.Card.Owner.Creature == p.Owner),
        // First Attack each turn deals bonus damage.
        [typeof(LethalityPower)] = p => CountStartedThisTurn(p,
            e => e.CardPlay.Card.Type == CardType.Attack
                && e.CardPlay.Card.Owner.Creature == p.Owner) == 0,
        // First Amount block gains from cards each turn are doubled.
        [typeof(UnmovablePower)] = p => CombatManager.Instance.History.Entries
            .OfType<BlockGainedEntry>().Count(e => e.HappenedThisTurn(p.CombatState)
                && e.Actor == p.Owner && e.Props.IsCardOrMonsterMove()) < p.Amount,
        // Played 5+ cards this turn -> next hand draw is bigger (warn: it's locked in).
        [typeof(PaleBlueDotPower)] = PaleBlueDotArmed,
        // Debuff: the first Skill played each turn afflicts all Skills (warning pulse).
        [typeof(SmoggyPower)] = p => !CombatManager.Instance.History.CardPlaysStarted
            .Any(e => e.HappenedThisTurn(p.CombatState)
                && e.CardPlay.Card.Type == CardType.Skill
                && e.CardPlay.Card.Owner.Creature == p.Owner),

        // --- Threshold counters (pulse when one event away from triggering) ---
        // 3rd Attack each turn is duplicated.
        [typeof(JugglingPower)] = p => CountStartedThisTurn(p,
            e => e.CardPlay.Card.Type == CardType.Attack
                && e.CardPlay.Card.Owner.Creature == p.Owner) == 2,
        // Every 10 draws gains energy; DisplayAmount is cards left.
        [typeof(AutomationPower)] = p => p.DisplayAmount == 1,
        // Every 4 energy spent gains energy; DisplayAmount is energy left to the trigger.
        [typeof(OrbitPower)] = p => p.DisplayAmount == 1,
        // Every 5 cards played deals damage; DisplayAmount is cards left.
        [typeof(PanachePower)] = p => p.DisplayAmount == 1,
        // Every 3rd poison application hits all enemies; DisplayAmount counts up to 3.
        [typeof(OutbreakPower)] = p => p.DisplayAmount == 2,
        // Enemy power: every 6 of the player's card plays adds a Wither to their hand.
        [typeof(WitheringPresencePower)] = p => p.DisplayAmount == 1,

        // --- Countdowns to a one-shot event (pulse at 1 = fires after this turn) ---
        [typeof(TheBombPower)] = p => p.Amount == 1,
        [typeof(AsleepPower)] = p => p.Amount == 1,
        [typeof(SlumberPower)] = p => p.Amount == 1,
        [typeof(HatchPower)] = p => p.Amount == 1,
        [typeof(BattlewornDummyTimeLimitPower)] = p => p.Amount == 1,

        // --- End-of-turn damage reminders (unconditional, pulse while present) ---
        // (MagicBombPower fits here too but nothing in the game applies it — dead content.
        // DemisePower also fits, but its only source is the player's own Powdered Demise
        // potion, so it only ever sits on enemies where a block reminder is meaningless.)
        [typeof(ConstrictPower)] = _ => true,
        [typeof(DisintegrationPower)] = _ => true,
    };

    // Last pulse state pushed per power, so refreshes only fire events on changes.
    private static readonly ConditionalWeakTable<PowerModel, StrongBox<bool>> _lastState = new();

    // Powers whose DisplayAmountChanged we already subscribed to.
    private static readonly ConditionalWeakTable<PowerModel, object> _subscribed = new();

    private static bool _historyHooked;

    private static int CountStartedThisTurn(PowerModel p, Func<CardPlayStartedEntry, bool> filter)
    {
        return CombatManager.Instance.History.CardPlaysStarted
            .Count(e => e.HappenedThisTurn(p.CombatState) && filter(e));
    }

    private static bool PaleBlueDotArmed(PowerModel p)
    {
        Player? player = p.Owner.Player;
        if (player == null)
        {
            return false;
        }
        IEnumerable<CardPlayFinishedEntry> plays = CombatManager.Instance.History.CardPlaysFinished
            .Where(e => e.CardPlay.Card.Owner == player);
        // During the player's turn the relevant count is this turn's plays (they become
        // "last player turn" by the time the next hand draw checks); afterwards mirror the
        // power's own HappenedLastPlayerTurn check.
        if (p.CombatState.CurrentSide == CombatSide.Player)
        {
            return plays.Count(e => e.HappenedThisTurn(p.CombatState)) >= PaleBlueDotPower.cardPlayThresholdValue;
        }
        return plays.Count(e => e.HappenedLastPlayerTurn(player)) >= PaleBlueDotPower.cardPlayThresholdValue;
    }

    // History.Changed is the backbone refresh signal; it can only be subscribed once a
    // CombatManager exists, so every patch entry point funnels through here first.
    public static void EnsureHistoryHooked()
    {
        if (_historyHooked || CombatManager.Instance == null)
        {
            return;
        }
        CombatManager.Instance.History.Changed += () => RefreshAll(force: false);
        _historyHooked = true;
    }

    public static void Refresh(PowerModel power, bool force)
    {
        try
        {
            if (!_armed.TryGetValue(power.GetType(), out Func<PowerModel, bool>? isArmed) || !power.IsMutable)
            {
                return;
            }
            bool armed = ColinsPatchKitConfig.ShowPowerReadyPulses
                && CombatManager.Instance.IsInProgress
                && isArmed(power);
            StrongBox<bool> last = _lastState.GetOrCreateValue(power);
            if (!force && last.Value == armed)
            {
                return;
            }
            last.Value = armed;
            if (armed)
            {
                power.StartPulsing();
            }
            else
            {
                power.StopPulsing();
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to refresh power ready pulse: {e}");
        }
    }

    public static void RefreshAll(bool force)
    {
        try
        {
            CombatState? state = CombatManager.Instance?.DebugOnlyGetState();
            if (state == null)
            {
                return;
            }
            foreach (Creature creature in state.Creatures)
            {
                foreach (PowerModel power in creature.Powers)
                {
                    EnsureSubscribed(power);
                    Refresh(power, force);
                }
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to refresh power ready pulses: {e}");
        }
    }

    // Counter powers update their internal state (and countdowns their Amount) at moments
    // that don't necessarily append history entries; DisplayAmountChanged fires synchronously
    // at exactly those moments (SetAmount invokes it too).
    private static void EnsureSubscribed(PowerModel power)
    {
        if (!_armed.ContainsKey(power.GetType()) || !power.IsMutable)
        {
            return;
        }
        if (_subscribed.TryGetValue(power, out _))
        {
            return;
        }
        power.DisplayAmountChanged += () => Refresh(power, force: false);
        _subscribed.Add(power, new object());
    }
}

// The pulse shader param is only ever written from the Start/StopPulsing events, so an icon
// created after a pulse started would miss it; re-fire the state once the icon's nodes exist
// (_Ready runs after the Model is assigned and after _EnterTree subscribed to the model).
[HarmonyPatch(typeof(NPower), "_Ready")]
public static class PowerReadyPulsesIconReadyPatch
{
    public static void Postfix(NPower __instance)
    {
        PowerReadyPulsesManager.EnsureHistoryHooked();
        PowerReadyPulsesManager.Refresh(__instance.Model, force: true);
    }
}

// Turn boundaries flip every HappenedThisTurn() gate without writing a history entry.
// RoundNumber and CurrentSide are exactly the two fields those checks compare against.
[HarmonyPatch]
public static class PowerReadyPulsesTurnBoundaryPatch
{
    public static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.PropertySetter(typeof(CombatState), nameof(CombatState.RoundNumber));
        yield return AccessTools.PropertySetter(typeof(CombatState), nameof(CombatState.CurrentSide));
    }

    public static void Postfix()
    {
        PowerReadyPulsesManager.EnsureHistoryHooked();
        PowerReadyPulsesManager.RefreshAll(force: false);
    }
}
