using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace StsTools.Game;

/// <summary>
/// Bonus indirects : ce qu'un pouvoir, une relique ou une carte ajoute aux valeurs calculées par le jeu (Block gagné,
/// pioche du tour, énergie, or, PV perdus, pouvoirs donnés, cartes rejouées). Comme pour les dégâts bonus, le jeu
/// calcule ces valeurs en passant ses écouteurs un par un ; on refait le calcul pour savoir ce que chacun a changé,
/// et on le crédite (à la relique, ou aux cartes qui ont donné le pouvoir). Les méthodes Modify… du jeu n'ont pas
/// d'effet de bord (le jeu les appelle aussi pour les aperçus), on peut donc les rappeler.
/// Seulement pour les vrais gains : les aperçus (textes des cartes) passent par les mêmes calculs.
/// </summary>
public sealed partial class CardRunStats
{
    /// <summary>Crédite à chaque écouteur ce qu'il a ajouté (ou, avec <paramref name="sign"/> -1, retiré).</summary>
    private static void CreditChanges(IEnumerable<(AbstractModel Model, decimal Delta)> changes, string name, int sign = 1)
    {
        foreach (var (model, delta) in changes)
            if (delta * sign > 0 && CreditOf(model) is { } credit)
                credit.Add(name, delta * sign);
    }

    private static bool Live(ICombatState? combatState) =>
        combatState != null && combatState.IsLiveCombat() && !CombatManager.Instance.ShouldSkipCombatEffect
        && !PlaySimulation.Active;

    /// <summary>Calcul d'un écouteur : nouvelle valeur à partir de la valeur courante.</summary>
    private delegate decimal Step(AbstractModel model, decimal amount);

    /// <summary>Refait un calcul en chaîne sur les écouteurs, en notant l'écart dû à chacun.</summary>
    private static decimal Replay(IEnumerable<AbstractModel> listeners, decimal amount, Step step, List<(AbstractModel, decimal)> changes)
    {
        foreach (var model in listeners)
        {
            decimal next = step(model, amount);
            if (next != amount)
                changes.Add((model, next - amount));
            amount = next;
        }
        return amount;
    }

    private static void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot record {what}: {e.Message}");
        }
    }

    // ---- Block : Dexterity (Oddly Smooth Stone, Footwork…), Pael's Legion, Frail ----

    private sealed record BlockCalc(ICombatState CombatState, Creature Target, decimal Block, ValueProp Props,
        CardModel? CardSource, CardPlay? CardPlay);

    private static BlockCalc? _lastBlockCalc;

    /// <summary>Calcul du Block (aussi pour les textes des cartes) : on retient seulement les paramètres.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyBlock))]
    public static class ModifyBlockPatch
    {
        public static void Postfix(ICombatState combatState, Creature target, decimal block, ValueProp props,
            CardModel? cardSource, CardPlay? cardPlay) =>
            _lastBlockCalc = PlaySimulation.Active ? null : new BlockCalc(combatState, target, block, props, cardSource, cardPlay);
    }

    /// <summary>Appelé seulement pour un vrai gain de Block, juste après son calcul.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterModifyingBlockAmount))]
    public static class RealBlockPatch
    {
        public static void Prefix(ICombatState combatState, decimal modifiedBlock)
        {
            var calc = _lastBlockCalc;
            _lastBlockCalc = null;
            if (calc == null || calc.CombatState != combatState || modifiedBlock <= 0 || !Live(combatState))
                return;
            Guard("bonus block", () =>
            {
                decimal amount = calc.Block;
                if (calc.CardSource?.Enchantment is { } enchantment)
                {
                    amount += enchantment.EnchantBlockAdditive(amount);
                    amount *= enchantment.EnchantBlockMultiplicative(amount);
                }
                var changes = new List<(AbstractModel, decimal)>();
                amount = Replay(combatState.IterateHookListeners(), amount,
                    (m, a) => a + m.ModifyBlockAdditive(calc.Target, a, calc.Props, calc.CardSource, calc.CardPlay), changes);
                Replay(combatState.IterateHookListeners(), amount,
                    (m, a) => a * m.ModifyBlockMultiplicative(calc.Target, a, calc.Props, calc.CardSource, calc.CardPlay), changes);
                CreditChanges(changes, BonusBlock);
            });
        }
    }

    // ---- Pioche du début de tour (Snecko Eye, Ring of the Serpent…) ----

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyHandDraw))]
    public static class HandDrawPatch
    {
        public static void Postfix(ICombatState combatState, Player player, decimal originalCardCount, decimal __result)
        {
            if (__result == originalCardCount || !Live(combatState))
                return;
            Guard("hand draw", () =>
            {
                var changes = new List<(AbstractModel, decimal)>();
                decimal count = Replay(combatState.IterateHookListeners(), originalCardCount, (m, a) => m.ModifyHandDraw(player, a), changes);
                Replay(combatState.IterateHookListeners(), count, (m, a) => m.ModifyHandDrawLate(player, a), changes);
                CreditChanges(changes, CardsDrawn);
            });
        }
    }

    // ---- Énergie : gains augmentés, énergie de chaque tour (reliques d'énergie) ----

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyEnergyGain))]
    public static class EnergyGainPatch
    {
        public static void Postfix(ICombatState combatState, Player player, decimal originalAmount, decimal __result)
        {
            if (__result == originalAmount || !Live(combatState))
                return;
            Guard("energy gain", () =>
            {
                var changes = new List<(AbstractModel, decimal)>();
                Replay(combatState.IterateHookListeners(), originalAmount, (m, a) => m.ModifyEnergyGain(player, a), changes);
                CreditChanges(changes, EnergyGained);
            });
        }
    }

    /// <summary>Énergie du début de tour : ce que chaque écouteur ajoute à l'énergie max.</summary>
    private static void RecordMaxEnergy(PlayerCombatState state)
    {
        var player = CombatPlayer(state);
        var combatState = player.Creature.CombatState;
        if (!Live(combatState))
            return;
        var changes = new List<(AbstractModel, decimal)>();
        Replay(combatState!.IterateHookListeners(), player.MaxEnergy, (m, a) => m.ModifyMaxEnergy(player, a), changes);
        CreditChanges(changes, EnergyGained);
    }

    [HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.ResetEnergy))]
    public static class ResetEnergyPatch
    {
        public static void Prefix(PlayerCombatState __instance) => Guard("max energy", () => RecordMaxEnergy(__instance));
    }

    [HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.AddMaxEnergyToCurrent))]
    public static class AddMaxEnergyPatch
    {
        public static void Prefix(PlayerCombatState __instance) => Guard("max energy", () => RecordMaxEnergy(__instance));
    }

    // ---- Or gagné en plus ----

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyGoldGained))]
    public static class GoldGainedPatch
    {
        public static void Postfix(IRunState runState, ICombatState? combatState, decimal amount, Player player, decimal __result)
        {
            if (__result == amount || combatState is { } c && !c.IsLiveCombat())
                return;
            Guard("gold gain", () =>
            {
                var changes = new List<(AbstractModel, decimal)>();
                Replay(runState.IterateHookListeners(combatState), amount, (m, a) => m.ModifyGoldGained(player, a), changes);
                CreditChanges(changes, Gold);
            });
        }
    }

    // ---- PV perdus : réduits pour son camp (Tungsten Rod, Intangible…), augmentés pour les ennemis ----

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyHpLost))]
    public static class HpLostPatch
    {
        public static void Postfix(IRunState runState, ICombatState? combatState, Creature target, decimal amount, ValueProp props,
            Creature? dealer, CardModel? cardSource, HpLossHookPhase phases, decimal __result)
        {
            if (__result == amount || !Live(combatState))
                return;
            Guard("hp loss", () =>
            {
                var changes = new List<(AbstractModel, decimal)>();
                decimal a = amount;
                if (phases.HasFlag(HpLossHookPhase.BeforeOsty))
                {
                    a = Replay(runState.IterateHookListeners(combatState), a, (m, x) => m.ModifyHpLostBeforeOsty(target, x, props, dealer, cardSource), changes);
                    a = Replay(runState.IterateHookListeners(combatState), a, (m, x) => m.ModifyHpLostBeforeOstyLate(target, x, props, dealer, cardSource), changes);
                }
                if (phases.HasFlag(HpLossHookPhase.AfterOsty))
                {
                    a = Replay(runState.IterateHookListeners(combatState), a, (m, x) => m.ModifyHpLostAfterOsty(target, x, props, dealer, cardSource), changes);
                    Replay(runState.IterateHookListeners(combatState), a, (m, x) => m.ModifyHpLostAfterOstyLate(target, x, props, dealer, cardSource), changes);
                }
                if (!target.IsEnemy)
                    CreditChanges(changes, Prevented, -1);
                else if (dealer is not { IsEnemy: true })
                    CreditChanges(changes, BonusDamage);
            });
        }
    }

    // ---- Pouvoirs donnés en plus (Poison, Vulnerable… augmentés par une relique ou un pouvoir) ----

    private sealed record PowerCalc(ICombatState CombatState, PowerModel Power, Creature Giver, decimal Amount, Creature? Target,
        CardModel? CardSource);

    private static PowerCalc? _lastPowerCalc;

    /// <summary>Calcul de la quantité donnée (aussi pour les textes des cartes) : on retient seulement les paramètres.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyPowerAmountGiven))]
    public static class PowerGivenPatch
    {
        public static void Postfix(ICombatState combatState, PowerModel power, Creature giver, decimal amount, Creature? target,
            CardModel? cardSource, decimal __result) =>
            _lastPowerCalc = __result != amount && !PlaySimulation.Active ? new PowerCalc(combatState, power, giver, amount, target, cardSource) : null;
    }

    /// <summary>Appelé seulement quand un pouvoir est vraiment donné, juste après le calcul de la quantité donnée.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyPowerAmountReceived))]
    public static class PowerReceivedPatch
    {
        public static void Prefix(ICombatState combatState, PowerModel canonicalPower)
        {
            var calc = _lastPowerCalc;
            _lastPowerCalc = null;
            if (calc == null || calc.CombatState != combatState || calc.Power != canonicalPower || !Live(combatState))
                return;
            Guard("power bonus", () =>
            {
                var changes = new List<(AbstractModel, decimal)>();
                decimal a = Replay(combatState.IterateHookListeners(), calc.Amount,
                    (m, x) => x + m.ModifyPowerAmountGivenAdditive(calc.Power, calc.Giver, x, calc.Target, calc.CardSource), changes);
                Replay(combatState.IterateHookListeners(), a,
                    (m, x) => x * m.ModifyPowerAmountGivenMultiplicative(calc.Power, calc.Giver, x, calc.Target, calc.CardSource), changes);
                var title = calc.Power.Title.GetFormattedText();
                var name = calc.Target is { IsEnemy: true } ? $"{title} applied" : $"{title} gained";
                PowerIcons[name] = calc.Power.IconPath;
                CreditChanges(changes, name);
            });
        }
    }

    // ---- Coût des cartes réduit (énergie, étoiles) ----

    /// <summary>
    /// La carte paie son coût : ce que chaque écouteur a retiré à son coût en énergie et en étoiles, calculé comme le
    /// jeu à partir du coût de la carte elle-même (ses propres réductions comprises).
    /// </summary>
    [HarmonyPatch(typeof(CardModel), nameof(CardModel.SpendResources))]
    public static class SpendResourcesPatch
    {
        public static void Prefix(CardModel __instance)
        {
            var card = __instance;
            var combatState = card.CombatState;
            if (!Live(combatState))
                return;
            Guard("cost reduction", () =>
            {
                if (!card.EnergyCost.CostsX && card.EnergyCost.GetWithModifiers(CostModifiers.Local) is var energy and > 0)
                {
                    var changes = new List<(AbstractModel, decimal)>();
                    decimal cost = Replay(combatState!.IterateHookListeners(), energy,
                        (m, a) => m.TryModifyEnergyCostInCombat(card, a, out var c) ? c : a, changes);
                    Replay(combatState!.IterateHookListeners(), cost,
                        (m, a) => m.TryModifyEnergyCostInCombatLate(card, a, out var c) ? c : a, changes);
                    CreditChanges(Capped(changes, energy), EnergySaved, -1);
                }
                if (!card.HasStarCostX && card.CurrentStarCost is var stars and > 0 && card.Pile is { IsCombatPile: true })
                {
                    var changes = new List<(AbstractModel, decimal)>();
                    Replay(combatState!.IterateHookListeners(), stars,
                        (m, a) => m.TryModifyStarCost(card, a, out var c) ? c : a, changes);
                    CreditChanges(Capped(changes, stars), StarsSaved, -1);
                }
            });
        }

        /// <summary>Un coût ne descend pas sous zéro : les réductions au-delà ne font rien économiser.</summary>
        private static IEnumerable<(AbstractModel, decimal)> Capped(List<(AbstractModel Model, decimal Delta)> changes, decimal cost)
        {
            foreach (var (model, delta) in changes)
            {
                decimal effective = delta < 0 ? -Math.Min(-delta, Math.Max(0, cost)) : delta;
                cost += delta;
                yield return (model, effective);
            }
        }
    }

    // ---- Cartes jouées une fois de plus (pouvoirs et reliques qui rejouent une carte) ----

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyCardPlayCount))]
    public static class PlayCountPatch
    {
        public static void Postfix(ICombatState combatState, CardModel card, int playCount, Creature? target, int __result)
        {
            if (__result == playCount || !Live(combatState))
                return;
            Guard("play count", () =>
            {
                var changes = new List<(AbstractModel, decimal)>();
                Replay(combatState.IterateHookListeners(), playCount, (m, a) => m.ModifyCardPlayCount(card, target, (int)a), changes);
                CreditChanges(changes, CardsPlayed);
            });
        }
    }
}
