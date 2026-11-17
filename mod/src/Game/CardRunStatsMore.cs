using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace StsTools.Game;

/// <summary>
/// Effets des reliques et pouvoirs qui changent une règle du jeu plutôt qu'une valeur : cartes gardées en main,
/// tour en plus, X augmenté, coups et déclenchements d'orbe en plus, debuffs évités ; et hors combat : cartes
/// modifiées en entrant dans le deck, or économisé en boutique, soins en plus au feu de camp, choix et récompenses
/// en plus, options de feu de camp, trésors sautés. Chaque effet est crédité à ce qui l'a produit (relique, ou
/// cartes qui ont donné le pouvoir). Comme pour les bonus indirects, les calculs du jeu sont refaits écouteur par
/// écouteur quand il faut savoir lequel a changé quoi (ils n'ont pas d'effet de bord).
/// </summary>
public sealed partial class CardRunStats
{
    /// <summary>Un seul responsable : il prend tout ; plusieurs : chacun compte pour 1.</summary>
    private static void CreditEach(IEnumerable<AbstractModel> models, string name, decimal total)
    {
        var list = models.Distinct().ToList();
        if (total <= 0)
            return;
        foreach (var model in list)
            CreditOf(model)?.Add(name, list.Count == 1 ? total : 1);
    }

    // ---- Cartes gardées en main (Runic Pyramid) ----

    /// <summary>
    /// Fin de tour sans défausse de la main : ce qui l'a empêchée garde les cartes qui auraient été défaussées
    /// (pas celles qui restent de toute façon, Retain).
    /// </summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.ShouldFlush))]
    public static class FlushPatch
    {
        public static void Postfix(ICombatState combatState, Player player, bool __result)
        {
            if (__result || !Live(combatState))
                return;
            Guard("cards retained", () =>
            {
                var preventer = combatState.IterateHookListeners().FirstOrDefault(m => !m.ShouldFlush(player));
                int kept = PileType.Hand.GetPile(player).Cards.Count(c => !c.ShouldRetainThisTurn);
                if (preventer != null)
                    CreditOf(preventer)?.Add(CardsRetained, kept);
            });
        }
    }

    // ---- Tour en plus ----

    [HarmonyPatch(typeof(Hook), nameof(Hook.ShouldTakeExtraTurn))]
    public static class ExtraTurnPatch
    {
        public static void Postfix(ICombatState combatState, bool __result, ref AbstractModel? source)
        {
            if (__result && source is { } model && Live(combatState))
                CreditOf(model)?.Add(ExtraTurns, 1);
        }
    }

    // ---- X augmenté (Chemical X) ----

    /// <summary>Dernière carte dont le X a été compté, et le jeu en cours à ce moment : une seule fois par jeu.</summary>
    private static (CardModel Card, Frame? Play)? _lastX;

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyXValue))]
    public static class XValuePatch
    {
        public static void Postfix(ICombatState combatState, CardModel card, int originalValue, int __result)
        {
            if (__result == originalValue || !Live(combatState))
                return;
            var play = _frames.LastOrDefault(f => f.PlayedCard == card);
            if (_lastX is { } last && last.Card == card && last.Play == play)
                return;
            _lastX = (card, play);
            Guard("x bonus", () =>
            {
                var changes = new List<(AbstractModel, decimal)>();
                Replay(combatState.IterateHookListeners(), originalValue, (m, a) => m.ModifyXValue(card, (int)a), changes);
                CreditChanges(changes, XBonus);
            });
        }
    }

    // ---- Coups en plus ----

    /// <summary>Seulement une vraie attaque (avec ses cibles) : les textes des cartes calculent aussi le nombre de coups.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyAttackHitCount))]
    public static class HitCountPatch
    {
        public static void Postfix(ICombatState combatState, AttackCommand attackCommand, int originalHitCount, decimal __result)
        {
            if (__result == originalHitCount || !Live(combatState)
                || !(attackCommand.IsSingleTargeted || attackCommand.IsMultiTargeted))
                return;
            Guard("extra hits", () =>
            {
                var changes = new List<(AbstractModel, decimal)>();
                Replay(combatState.IterateHookListeners(), originalHitCount, (m, a) => m.ModifyAttackHitCount(attackCommand, (int)a), changes);
                CreditChanges(changes, ExtraHits);
            });
        }
    }

    // ---- Déclenchements d'orbe en plus ----

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyOrbPassiveTriggerCount))]
    public static class OrbTriggerPatch
    {
        public static void Postfix(ICombatState combatState, OrbModel orb, int triggerCount, int __result)
        {
            if (__result == triggerCount || !Live(combatState))
                return;
            Guard("orb triggers", () =>
            {
                var changes = new List<(AbstractModel, decimal)>();
                Replay(combatState.IterateHookListeners(), triggerCount, (m, a) => m.ModifyOrbPassiveTriggerCounts(orb, (int)a), changes);
                CreditChanges(changes, OrbTriggers);
            });
        }
    }

    // ---- Debuffs évités (Artifact) ----

    /// <summary>Debuff entièrement annulé sur le camp du joueur : un debuff évité pour ce qui l'a annulé.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyPowerAmountReceived))]
    public static class DebuffPreventedPatch
    {
        public static void Postfix(ICombatState combatState, PowerModel canonicalPower, Creature target, decimal amount,
            decimal __result, ref IEnumerable<AbstractModel> modifiers)
        {
            if (amount <= 0 || __result > 0 || target.IsEnemy || canonicalPower.Type != PowerType.Debuff || !Live(combatState))
                return;
            var models = modifiers;
            Guard("debuff prevented", () => CreditEach(models, DebuffsPrevented, 1));
        }
    }

    // ---- Cartes modifiées en entrant dans le deck (œufs, Fresnel Lens) ----

    private static (int Upgrades, string? Enchantment) CardState(CardModel card) =>
        (card.CurrentUpgradeLevel, card.Enchantment?.Id.ToString());

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyCardBeingAddedToDeck))]
    public static class DeckCardPatch
    {
        public static void Prefix(CardModel card, out (int Upgrades, string? Enchantment) __state) => __state = CardState(card);

        public static void Postfix(CardModel __result, ref List<AbstractModel> modifyingModels, (int Upgrades, string? Enchantment) __state)
        {
            if (modifyingModels.Count == 0)
                return;
            var models = modifyingModels;
            Guard("deck card modified", () =>
            {
                var after = CardState(__result);
                foreach (var model in models.Distinct())
                {
                    // Ce que fait chaque responsable, d'après son code : améliorer ou enchanter
                    var commands = CardCode.Of(model.GetType()).Commands;
                    if (after.Upgrades > __state.Upgrades && commands.Any(c => c.Contains("Upgrade")))
                        CreditOf(model)?.Add(Upgraded, 1);
                    if (after.Enchantment != __state.Enchantment && commands.Any(c => c.Contains("Enchant")))
                        CreditOf(model)?.Add(Enchanted, 1);
                }
            });
        }
    }

    // ---- Or économisé en boutique ----

    /// <summary>Dernier prix calculé (affichage compris) : celui payé juste après, à l'achat.</summary>
    private static (IRunState RunState, Player Player, MerchantEntry Entry, decimal Base, decimal Price)? _lastPrice;

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyMerchantPrice))]
    public static class MerchantPricePatch
    {
        public static void Prefix(decimal result, out decimal __state) => __state = result;

        public static void Postfix(IRunState runState, Player player, MerchantEntry entry, decimal __result, decimal __state) =>
            _lastPrice = (runState, player, entry, __state, __result);
    }

    [HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.LoseGold))]
    public static class GoldSpentPatch
    {
        public static void Prefix(decimal amount, Player player, GoldLossType goldLossType)
        {
            if (goldLossType != GoldLossType.Spent || _lastPrice is not { } last || last.Player != player
                || (int)last.Price != (int)amount || last.Base <= last.Price)
                return;
            _lastPrice = null;
            Guard("gold saved", () =>
            {
                var changes = new List<(AbstractModel, decimal)>();
                Replay(last.RunState.IterateHookListeners(null), last.Base, (m, a) => m.ModifyMerchantPrice(player, last.Entry, a), changes);
                CreditChanges(changes, GoldSaved, -1);
            });
        }
    }

    // ---- Soins en plus au feu de camp ----

    private static (IRunState RunState, Creature Creature, decimal Base, decimal Amount)? _lastRestHeal;

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyRestSiteHealAmount))]
    public static class RestHealPatch
    {
        public static void Postfix(IRunState runState, Creature creature, decimal amount, decimal __result) =>
            _lastRestHeal = (runState, creature, amount, __result);
    }

    /// <summary>
    /// Soin du feu de camp (appelé juste après le calcul de son montant) : ce que chaque relique a ajouté, dans la
    /// limite des PV qui manquaient.
    /// </summary>
    private static void RecordRestHeal(Creature creature, decimal amount)
    {
        if (_lastRestHeal is not { } last || last.Creature != creature || last.Amount != amount || last.Amount <= last.Base)
            return;
        _lastRestHeal = null;
        decimal missing = creature.MaxHp - creature.CurrentHp;
        decimal bonus = Math.Max(0, Math.Min(amount, missing) - Math.Min(last.Base, missing));
        if (bonus <= 0)
            return;
        var changes = new List<(AbstractModel Model, decimal Delta)>();
        Replay(last.RunState.IterateHookListeners(null), last.Base, (m, a) => m.ModifyRestSiteHealAmount(creature, a), changes);
        decimal added = changes.Where(c => c.Delta > 0).Sum(c => c.Delta);
        foreach (var (model, delta) in changes)
            if (delta > 0)
                CreditOf(model)?.Add(Healed, Math.Round(bonus * delta / added, 2));
    }

    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Heal))]
    public static class RestSiteHealPatch
    {
        public static void Prefix(Creature creature, decimal amount) => Guard("rest heal", () => RecordRestHeal(creature, amount));
    }

    // ---- Choix de cartes, récompenses et options en plus ----

    [HarmonyPatch(typeof(Hook), nameof(Hook.TryModifyCardRewardOptions))]
    public static class CardChoicesPatch
    {
        public static void Prefix(List<CardCreationResult> cardRewardOptions, out int __state) => __state = cardRewardOptions.Count;

        public static void Postfix(List<CardCreationResult> cardRewardOptions, ref List<AbstractModel> modifiers, int __state)
        {
            var models = modifiers;
            Guard("card choices", () => CreditEach(models, CardChoices, cardRewardOptions.Count - __state));
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyCardRewardAlternatives))]
    public static class CardAlternativesPatch
    {
        public static void Prefix(List<CardRewardAlternative> alternatives, out int __state) => __state = alternatives.Count;

        public static void Postfix(List<CardRewardAlternative> alternatives, IEnumerable<AbstractModel> __result, int __state) =>
            Guard("card alternatives", () => CreditEach(__result, CardChoices, alternatives.Count - __state));
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyRewards))]
    public static class RewardsPatch
    {
        public static void Prefix(List<Reward> rewards, out int __state) => __state = rewards.Count;

        public static void Postfix(List<Reward> rewards, IEnumerable<AbstractModel> __result, int __state) =>
            Guard("rewards", () => CreditEach(__result, RewardsAdded, rewards.Count - __state));
    }

    /// <summary>Potion imposée dans les récompenses : à la première relique qui l'impose.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.ShouldForcePotionReward))]
    public static class PotionRewardPatch
    {
        public static void Postfix(IRunState runState, Player player, RoomType roomType, bool __result)
        {
            if (!__result)
                return;
            Guard("potion reward", () =>
            {
                if (runState.IterateHookListeners(null).FirstOrDefault(m => m.ShouldForcePotionReward(player, roomType)) is { } model)
                    CreditOf(model)?.Add(PotionRewards, 1);
            });
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyRestSiteOptions))]
    public static class RestOptionsPatch
    {
        public static void Prefix(ICollection<RestSiteOption> options, out int __state) => __state = options.Count;

        public static void Postfix(ICollection<RestSiteOption> options, IEnumerable<AbstractModel> __result, int __state) =>
            Guard("rest options", () => CreditEach(__result, RestOptions, options.Count - __state));
    }

    // ---- Cartes modifiées à leur création (vitrine de la boutique, récompenses) ----

    /// <summary>
    /// Carte modifiée par une relique au moment où elle est proposée (œufs, Fresnel Lens en boutique) → reliques et
    /// ce qu'elles lui ont fait. Crédité seulement si la carte entre dans le deck (achetée, choisie).
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CardModel, List<(RelicModel Relic, string Stat)>>
        PendingCardChanges = new();

    /// <summary>Résultats déjà comptés (une même proposition peut repasser par les calculs du jeu).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CardCreationResult, object> SeenResults = new();

    /// <summary>
    /// Cartes proposées après passage des reliques : le jeu note sur chacune les reliques qui l'ont modifiée. On
    /// compare la carte d'origine et la carte proposée (amélioration, enchantement), et on crédite ce que chaque
    /// relique a fait d'après son code ; la carte est retenue pour être comptée si elle entre dans le deck.
    /// (Pas sur CardCreationResult.ModifyCard : trop courte, le moteur la recopie dans les appelants.)
    /// </summary>
    private static void RecordModifiedOffers(IEnumerable<CardCreationResult> results)
    {
        foreach (var result in results)
        {
            if (!result.HasBeenModified || SeenResults.TryGetValue(result, out _))
                continue;
            SeenResults.Add(result, new object());
            var before = result.originalCard;
            var after = result.Card;
            bool upgraded = after.CurrentUpgradeLevel > before.CurrentUpgradeLevel;
            bool enchanted = after.Enchantment?.Id.ToString() != before.Enchantment?.Id.ToString();
            var changes = new List<(RelicModel, string)>();
            foreach (var relic in result.ModifyingRelics.Distinct())
            {
                var commands = CardCode.Of(relic.GetType()).Commands;
                bool canUpgrade = commands.Any(c => c.Contains("Upgrade"));
                bool canEnchant = commands.Any(c => c.Contains("Enchant"));
                // Code illisible ou qui passe par une autre classe (œufs : EggRelicHelper) : seul responsable, tout lui revient
                bool alone = result.ModifyingRelics.Distinct().Count() == 1;
                if (upgraded && (canUpgrade || alone && !canEnchant))
                {
                    changes.Add((relic, Upgraded));
                    CreditOf(relic)?.Add(OfferedUpgraded, 1);
                }
                if (enchanted && (canEnchant || alone && !canUpgrade))
                {
                    changes.Add((relic, Enchanted));
                    CreditOf(relic)?.Add(OfferedEnchanted, 1);
                }
            }
            if (changes.Count > 0)
                PendingCardChanges.AddOrUpdate(after, changes);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.TryModifyCardRewardOptions))]
    public static class RewardOffersPatch
    {
        public static void Postfix(List<CardCreationResult> cardRewardOptions) =>
            Guard("modified reward cards", () => RecordModifiedOffers(cardRewardOptions));
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyMerchantCardCreationResults))]
    public static class MerchantOffersPatch
    {
        public static void Postfix(List<CardCreationResult> cards) =>
            Guard("modified merchant cards", () => RecordModifiedOffers(cards));
    }

    /// <summary>Carte modifiée à sa création qui entre dans le deck : chaque relique est créditée de ce qu'elle a fait.</summary>
    [HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.Add),
        [typeof(IEnumerable<CardModel>), typeof(CardPile), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool), typeof(bool)])]
    public static class CreationModifiedAddedPatch
    {
        public static void Prefix(ref IEnumerable<CardModel> cards, CardPile newPile)
        {
            if (newPile.Type != PileType.Deck)
                return;
            CountOnce(ref cards);
            var list = cards;
            Guard("card modified on creation", () =>
            {
                foreach (var card in list)
                    if (PendingCardChanges.TryGetValue(card, out var changes))
                    {
                        PendingCardChanges.Remove(card);
                        foreach (var (relic, stat) in changes)
                            CreditOf(relic)?.Add(stat, 1);
                    }
            });
        }
    }

    // ---- Articles remis en stock (The Courier) ----

    [HarmonyPatch(typeof(Hook), nameof(Hook.ShouldRefillMerchantEntry))]
    public static class RestockPatch
    {
        public static void Postfix(IRunState runState, MerchantEntry entry, Player player, bool __result)
        {
            if (!__result)
                return;
            Guard("restock", () =>
            {
                if (runState.IterateHookListeners(null).FirstOrDefault(m => m.ShouldRefillMerchantEntry(entry, player)) is { } model)
                    CreditOf(model)?.Add(ItemsRestocked, 1);
            });
        }
    }

    // ---- Carte du jeu ----

    /// <summary>
    /// Salle inconnue tirée sans combat possible (Juzu Bracelet, Golden Compass) : à chaque relique qui a retiré les
    /// combats des salles possibles.
    /// </summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyUnknownMapPointRoomTypes))]
    public static class UnknownRoomPatch
    {
        private static bool HasCombat(IReadOnlySet<RoomType> types) => types.Contains(RoomType.Monster) || types.Contains(RoomType.Elite);

        public static void Postfix(IRunState runState, IReadOnlySet<RoomType> roomTypes, IReadOnlySet<RoomType> __result)
        {
            if (!HasCombat(roomTypes) || HasCombat(__result))
                return;
            Guard("unknown room", () =>
            {
                var types = roomTypes;
                foreach (var model in runState.IterateHookListeners(null))
                {
                    var next = model.ModifyUnknownMapPointRoomTypes(types);
                    if (HasCombat(types) && !HasCombat(next))
                        CreditOf(model)?.Add(SafeUnknownRooms, 1);
                    types = next;
                }
            });
        }
    }

    /// <summary>
    /// Déplacement libre sur la carte (Winged Boots) : la relique compte ses utilisations en entrant dans la salle.
    /// Seulement pendant son effet d'entrée dans une salle (pas au chargement de la partie, qui remet le compteur).
    /// </summary>
    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Models.Relics.WingedBoots), nameof(MegaCrit.Sts2.Core.Models.Relics.WingedBoots.TimesUsed), MethodType.Setter)]
    public static class FreeTravelPatch
    {
        public static void Prefix(MegaCrit.Sts2.Core.Models.Relics.WingedBoots __instance, out int __state) => __state = __instance.TimesUsed;

        public static void Postfix(MegaCrit.Sts2.Core.Models.Relics.WingedBoots __instance, int value, int __state)
        {
            if (value > __state && _frames.Count > 0 && _frames[^1].Listener == __instance)
                CreditOf(__instance)?.Add(FreeTravels, value - __state);
        }
    }

    /// <summary>PV d'un ennemi ramenés directement à une valeur plus basse (Fur Coat : 1 PV au début des combats marqués).</summary>
    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.SetCurrentHp))]
    public static class SetHpPatch
    {
        public static void Prefix(Creature creature, decimal amount)
        {
            if (creature.IsEnemy && amount < creature.CurrentHp && Live(creature.CombatState))
                Current?.Add(EnemyHpRemoved, creature.CurrentHp - amount);
        }
    }

    // ---- Trésors sautés (Silver Crucible) ----

    /// <summary>Étage du dernier trésor sauté compté : le jeu pose la question plusieurs fois par salle.</summary>
    private static int _lastSkippedTreasureFloor = -1;

    [HarmonyPatch(typeof(Hook), nameof(Hook.ShouldGenerateTreasure))]
    public static class TreasurePatch
    {
        public static void Postfix(IRunState runState, Player player, bool __result)
        {
            if (__result || runState.TotalFloor == _lastSkippedTreasureFloor)
                return;
            _lastSkippedTreasureFloor = runState.TotalFloor;
            Guard("treasure skipped", () =>
            {
                if (runState.IterateHookListeners(null).FirstOrDefault(m => !m.ShouldGenerateTreasure(player)) is { } model)
                    CreditOf(model)?.Add(TreasuresSkipped, 1);
            });
        }
    }
}
