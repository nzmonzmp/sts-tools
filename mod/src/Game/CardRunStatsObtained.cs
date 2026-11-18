using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Random;

namespace StsTools.Game;

/// <summary>
/// Ce qu'une relique fait quand on la prend (PV max, or, cartes ajoutées, retirées, améliorées…), hors des écouteurs
/// du jeu, et les tours joués avec chaque relique. Les mêmes stats comptent aussi pour les cartes qui font ces
/// actions en combat (amélioration d'une carte, PV max gagnés…).
/// </summary>
public sealed partial class CardRunStats
{
    /// <summary>
    /// Effet de prise d'une relique : elle est « ce qui agit » jusqu'à la fin de son effet, même s'il attend un choix
    /// du joueur (carte à retirer, à améliorer…).
    /// </summary>
    [HarmonyPatch]
    internal static class AfterObtainedPatch
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
            typeof(RelicModel).Assembly.GetTypes()
                .Where(t => t.IsSubclassOf(typeof(RelicModel)) && !t.IsAbstract)
                .Select(t => AccessTools.DeclaredMethod(t, nameof(RelicModel.AfterObtained), Type.EmptyTypes))
                .Where(m => m != null)
                .Cast<MethodBase>();

        public static void Prefix(RelicModel __instance, out Frame __state)
        {
            __state = new Frame { Listener = __instance };
            _frames.Add(__state);
        }

        public static void Postfix(Task __result, Frame __state) => OnFinished(__result, () => _frames.Remove(__state));
    }

    /// <summary>Matérialise une liste de cartes passée au jeu, pour la compter sans la parcourir deux fois.</summary>
    private static int CountOnce<T>(ref IEnumerable<T> items)
    {
        if (!items.TryGetNonEnumeratedCount(out int count))
        {
            var list = items.ToList();
            items = list;
            count = list.Count;
        }
        return count;
    }

    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.GainMaxHp))]
    public static class GainMaxHpPatch
    {
        public static void Prefix(Creature creature, decimal amount)
        {
            if (creature is { IsEnemy: false } && amount > 0)
                Current?.Add(MaxHpGained, amount);
        }
    }

    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.LoseMaxHp))]
    public static class LoseMaxHpPatch
    {
        public static void Prefix(PlayerChoiceContext choiceContext, Creature creature, decimal amount)
        {
            if (creature is { IsEnemy: false } && amount > 0)
                Current?.Add(MaxHpLost, amount);
        }
    }

    /// <summary>Toutes les façons d'ajouter des cartes à une pile finissent ici ; seulement celles qui vont dans le deck.</summary>
    [HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.Add),
        [typeof(IEnumerable<CardModel>), typeof(CardPile), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool), typeof(bool)])]
    public static class DeckAddPatch
    {
        public static void Prefix(ref IEnumerable<CardModel> cards, CardPile newPile)
        {
            if (newPile.Type == PileType.Deck && Current is { } credit)
                credit.Add(DeckAdded, CountOnce(ref cards));
        }
    }

    [HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.RemoveFromDeck), [typeof(IReadOnlyList<CardModel>), typeof(bool)])]
    public static class DeckRemovePatch
    {
        public static void Prefix(IReadOnlyList<CardModel> cards) => Current?.Add(DeckRemoved, cards.Count);
    }

    [HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Upgrade), [typeof(IEnumerable<CardModel>), typeof(CardPreviewStyle)])]
    public static class UpgradePatch
    {
        public static void Prefix(ref IEnumerable<CardModel> cards)
        {
            if (Current is not { } credit)
                return;
            var list = cards.ToList();
            cards = list;
            // Pour le reste de la partie (carte du deck), ou seulement pour ce combat (carte d'une pile de combat).
            // Une carte hors de toute pile (Astrolabe améliore la carte qu'il crée) : comptée par sa transformation
            var upgradable = list.Where(c => c.IsUpgradable).ToList();
            credit.Add(Upgraded, upgradable.Count(c => c.Pile?.Type == PileType.Deck));
            credit.Add(UpgradedInCombat, upgradable.Count(c => c.Pile is { IsCombatPile: true }));
        }
    }

    [HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Transform), [typeof(IEnumerable<CardTransformation>), typeof(Rng), typeof(CardPreviewStyle)])]
    public static class TransformPatch
    {
        public static void Prefix(ref IEnumerable<CardTransformation> transformations)
        {
            if (Current is { } credit)
                credit.Add(Transformed, CountOnce(ref transformations));
        }
    }

    [HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Enchant), [typeof(EnchantmentModel), typeof(CardModel), typeof(decimal)])]
    public static class EnchantPatch
    {
        /// <summary>
        /// Seulement une carte du joueur (deck, piles de combat) : pas les copies enchantées pour être proposées
        /// (Glitter sur les récompenses), comptées si la carte est prise (<see cref="CreationModifiedAddedPatch"/>).
        /// </summary>
        public static void Prefix(CardModel card)
        {
            if (card.Pile != null)
                Current?.Add(Enchanted, 1);
        }
    }

    /// <summary>Potion donnée : comptée seulement si elle a trouvé une place.</summary>
    [HarmonyPatch(typeof(PotionCmd), nameof(PotionCmd.TryToProcure), [typeof(PotionModel), typeof(Player), typeof(int)])]
    public static class PotionPatch
    {
        public static void Postfix(Task<PotionProcureResult> __result)
        {
            if (Current is { } credit)
                OnFinished(__result, () =>
                {
                    if (__result.IsCompletedSuccessfully && __result.Result.success)
                        credit.Add(Potions, 1);
                });
        }
    }

    [HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.GainMaxPotionCount))]
    public static class PotionSlotsPatch
    {
        public static void Prefix(int amount)
        {
            if (amount > 0)
                Current?.Add(PotionSlots, amount);
        }
    }

    // ---- Combat : ennemis étourdis, orbes, forge ----

    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Stun), [typeof(Creature), typeof(Func<IReadOnlyList<Creature>, Task>), typeof(string)])]
    public static class StunPatch
    {
        public static void Prefix(Creature creature)
        {
            if (creature.IsEnemy)
                Current?.Add(Stunned, 1);
        }
    }

    /// <summary>Toutes les évocations (EvokeNext, EvokeLast, orbe chassée par une nouvelle) passent par là.</summary>
    [HarmonyPatch(typeof(OrbCmd), "Evoke")]
    public static class EvokePatch
    {
        public static void Prefix(OrbModel evokedOrb) => Current?.Add(OrbStat(evokedOrb, Evoked), 1);
    }

    [HarmonyPatch(typeof(OrbCmd), nameof(OrbCmd.AddSlots))]
    public static class OrbSlotsPatch
    {
        public static void Prefix(int amount)
        {
            if (amount > 0)
                Current?.Add(OrbSlots, amount);
        }
    }

    // ---- Flèches (The Fool) ----

    /// <summary>Flèche tirée (s'il en reste une) : pour la carte ou la relique qui la tire.</summary>
    [HarmonyPatch(typeof(ArrowCmd), nameof(ArrowCmd.TryShootArrow))]
    public static class ArrowShotPatch
    {
        public static void Prefix(Player player)
        {
            if (ArrowCmd.PlayerHasAnyArrows(player))
                Current?.Add(ArrowsShot, 1);
        }
    }

    /// <summary>Flèche retirée d'un ennemi (et rendue au joueur, sauf détruite).</summary>
    [HarmonyPatch(typeof(ArrowCmd), nameof(ArrowCmd.DislodgeArrow), [typeof(ICombatState), typeof(MegaCrit.Sts2.Core.Models.Powers.ImpaledPower),
        typeof(PlayerChoiceContext), typeof(CardModel)])]
    public static class ArrowDislodgedPatch
    {
        public static void Prefix(MegaCrit.Sts2.Core.Models.Powers.ImpaledPower impaledArrow)
        {
            if (impaledArrow.Arrows.FirstOrDefault() is { DestroyAfterImpaled: false })
                Current?.Add(ArrowsRetrieved, 1);
        }
    }

    [HarmonyPatch(typeof(ArrowCmd), nameof(ArrowCmd.GainArrowSlot))]
    public static class ArrowSlotPatch
    {
        public static void Prefix() => Current?.Add(ArrowSlots, 1);
    }

    /// <summary>Forge (Regent) : dégâts ajoutés à Sovereign Blade.</summary>
    [HarmonyPatch(typeof(ForgeCmd), nameof(ForgeCmd.Forge))]
    public static class ForgePatch
    {
        public static void Prefix(decimal amount)
        {
            if (amount > 0)
                Current?.Add(Forged, amount);
        }
    }

    // ---- Block gardé, mort évitée ----

    /// <summary>
    /// Ce qui a empêché le Block d'être retiré en début de tour (Barricade, Calipers) est « ce qui agit » pendant
    /// son effet (Calipers retire alors une partie du Block) ; ensuite, le Block resté est crédité comme gardé.
    /// Seulement pour le camp du joueur.
    /// </summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterPreventingBlockClear))]
    internal static class BlockRetainedPatch
    {
        public static void Prefix(AbstractModel preventer, out Frame __state)
        {
            __state = new Frame { Listener = preventer };
            _frames.Add(__state);
        }

        public static void Postfix(Task __result, ICombatState combatState, AbstractModel preventer, Creature creature, Frame __state) =>
            OnFinished(__result, () =>
            {
                _frames.Remove(__state);
                if (!creature.IsEnemy && combatState.IsLiveCombat() && CreditOf(preventer) is { } credit)
                    credit.Add(BlockRetained, creature.Block);
            });
    }

    /// <summary>
    /// Ce qui a empêché une mort (Lizard Tail) est « ce qui agit » pendant son effet (PV rendus), et compte une mort
    /// évitée. Seulement pour le camp du joueur.
    /// </summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterPreventingDeath))]
    internal static class DeathPreventedPatch
    {
        public static void Prefix(AbstractModel preventer, Creature creature, out Frame __state)
        {
            __state = new Frame { Listener = preventer };
            _frames.Add(__state);
            if (!creature.IsEnemy)
                CreditOf(preventer)?.Add(DeathsPrevented, 1);
        }

        public static void Postfix(Task __result, Frame __state) => OnFinished(__result, () => _frames.Remove(__state));
    }

    /// <summary>
    /// Fait <paramref name="then"/> à la fin d'un effet du jeu, sans remplacer sa tâche : le jeu attend toujours la
    /// sienne, sans étape d'attente en plus. En multijoueur, chaque joueur simule la partie et le jeu vérifie que
    /// tous obtiennent le même état : le mod ne doit rien y changer, pas même l'ordre des attentes. La suite est
    /// faite tout de suite à la fin de l'effet, avant que le jeu ne reprenne.
    /// </summary>
    private static void OnFinished(Task effect, Action then)
    {
        void Run()
        {
            try
            {
                then();
            }
            catch (Exception e)
            {
                MegaCrit.Sts2.Core.Logging.Log.Warn($"[{ModEntry.Id}] cannot finish recording an effect: {e.Message}");
            }
        }
        if (effect.IsCompleted)
            Run();
        else
            effect.ContinueWith(_ => Run(), TaskContinuationOptions.ExecuteSynchronously);
    }

    // ---- Tours joués avec chaque relique ----

    private static readonly AccessTools.FieldRef<PlayerCombatState, Player> CombatPlayer =
        AccessTools.FieldRefAccess<PlayerCombatState, Player>("_player");

    [HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.IncrementTurnNumber))]
    public static class TurnPatch
    {
        public static void Postfix(PlayerCombatState __instance) => Guard("relic turns", () =>
        {
            var player = CombatPlayer(__instance);
            if (!Live(player.Creature.CombatState))
                return;
            foreach (var relic in player.Relics)
                RelicStats.GetOrCreateValue(relic).Turns++;
        });
    }
}
