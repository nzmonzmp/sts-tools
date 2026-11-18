using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace StsTools.Game;

/// <summary>
/// Onglet Support (multijoueur) : ce que chaque joueur a donné à ses alliés (énergie, cartes, pioche, Block, buffs,
/// PV rendus), par ses cartes, ses reliques, ses potions et les pouvoirs qu'il a donnés. Ce qu'un joueur se donne à
/// lui-même n'y compte pas.
/// </summary>
public sealed partial class CardRunStats
{
    public const string EnergyGiven = "Energy given";
    public const string CardsGiven = "Cards given";
    public const string DrawGiven = "Cards drawn by allies";
    public const string BlockGiven = "Block given";
    public const string HpRestored = "HP restored";

    /// <summary>Ordre d'affichage de l'onglet Support ; les buffs donnés (« Strength given »…) viennent après.</summary>
    public static readonly string[] SupportOrder = [EnergyGiven, CardsGiven, DrawGiven, BlockGiven, HpRestored];

    private static readonly Dictionary<string, string> SupportIcons = new()
    {
        [EnergyGiven] = "res://images/packed/sprite_fonts/colorless_energy_icon.png",
        [CardsGiven] = "res://images/packed/sprite_fonts/card_icon.png",
        [DrawGiven] = "res://images/packed/combat_ui/draw_pile.png",
        [BlockGiven] = "res://images/ui/combat/block.png",
        [HpRestored] = "res://images/packed/intents/intent_heal.png",
    };

    /// <summary>Stats de l'onglet Support non nulles, dans l'ordre d'affichage.</summary>
    public IEnumerable<string> SupportNames()
    {
        var powers = Values.Keys.Except(SupportOrder).OrderBy(name => name);
        return SupportOrder.Concat(powers).Where(name => this[name] != 0);
    }

    /// <summary>Ce que chaque joueur a donné à ses alliés : combat, tour, partie (sauvegardée avec la partie).</summary>
    private static readonly Dictionary<ulong, PlayerTotals> SupportTotals = new();

    public static PlayerTotals SupportOf(Player player)
    {
        if (!SupportTotals.TryGetValue(player.NetId, out var totals) || totals.Player != player)
            SupportTotals[player.NetId] = totals = new PlayerTotals(player);
        return totals;
    }

    /// <summary>
    /// Compte un don de <paramref name="giver"/> à <paramref name="receiver"/>, seulement entre deux joueurs
    /// différents (ce qu'on se donne à soi-même est dans l'onglet Combat).
    /// </summary>
    private static void Give(Player? giver, Player? receiver, string name, decimal amount)
    {
        if (giver == null || receiver == null || giver == receiver || amount <= 0 || PlaySimulation.Active)
            return;
        SupportOf(giver).Add(name, amount);
        Changed?.Invoke();
    }

    /// <summary>Joueur d'une créature alliée : le joueur lui-même, ou le maître de son familier (Osty).</summary>
    private static Player? PlayerOf(Creature? creature) => creature?.Player ?? creature?.PetOwner;

    /// <summary>Joueur qui a donné chaque pouvoir : ses effets suivants (énergie chaque tour…) sont ses dons.</summary>
    private static readonly ConditionalWeakTable<PowerModel, Player> PowerGivers = new();

    /// <summary>Joueurs dont une carte ou une potion fait son effet en ce moment, le plus récent en dernier.</summary>
    private static readonly List<Player> _effectOwners = [];

    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.BeginCardOrPotionEffect))]
    public static class EffectBeginPatch
    {
        public static void Prefix(Player player)
        {
            if (!PlaySimulation.Active)
                _effectOwners.Add(player);
        }
    }

    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.EndCardOrPotionEffect))]
    public static class EffectEndPatch
    {
        public static void Prefix(Player player)
        {
            if (PlaySimulation.Active)
                return;
            int index = _effectOwners.LastIndexOf(player);
            if (index >= 0)
                _effectOwners.RemoveAt(index);
        }
    }

    /// <summary>
    /// Joueur qui agit en ce moment : celui de la carte jouée ou de l'effet en cours (relique, pouvoir, carte), sinon
    /// celui de la carte ou de la potion en train de faire son effet ; null pour un effet ennemi.
    /// </summary>
    private static Player? Giver()
    {
        try
        {
            if (_frames.Count > 0 && _frames[^1] is { } frame && (frame.PlayedCard != null || frame.Listener != null))
                return frame.PlayedCard is { } card ? card.Owner : OwnerOf(frame.Listener!);
            return _effectOwners.Count > 0 ? _effectOwners[^1] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Player? OwnerOf(AbstractModel model) => model switch
    {
        CardModel card => card.Owner,
        RelicModel relic => relic.Owner,
        PotionModel potion => potion.Owner,
        PowerModel power => PowerGivers.TryGetValue(power, out var giver) ? giver : PlayerOf(power.Owner),
        _ => null,
    };

    /// <summary>Événements de l'historique du combat : Block, buffs, cartes et pioche donnés à un allié.</summary>
    [HarmonyPatch(typeof(CombatHistory), "Add")]
    public static class SupportHistoryPatch
    {
        public static void Postfix(ICombatState combatState, CombatHistoryEntry entry)
        {
            try
            {
                if (combatState.IsLiveCombat() && !PlaySimulation.Active)
                    RecordSupport(entry);
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] cannot record support stats: {e.Message}");
            }
        }
    }

    private static void RecordSupport(CombatHistoryEntry entry)
    {
        switch (entry)
        {
            case BlockGainedEntry block:
                Give(block.CardPlay?.Card.Owner ?? Giver(), PlayerOf(block.Actor), BlockGiven, block.Amount);
                return;
            case PowerReceivedEntry { Amount: > 0 } power when !power.Power.Owner.IsEnemy:
                var giver = PlayerOf(power.Applier) ?? Giver();
                if (giver == null)
                    return;
                // Les effets futurs du pouvoir (énergie, pioche chaque tour…) seront des dons du même joueur
                PowerGivers.AddOrUpdate(power.Power, giver);
                if (power.Power.Type != PowerType.Buff)
                    return;
                var name = $"{power.Power.Title.GetFormattedText()} given";
                PowerIcons[name] = power.Power.IconPath;
                Give(giver, PlayerOf(power.Power.Owner), name, power.Amount);
                return;
            // Pas les Statuts ni les Malédictions mis dans le deck d'un allié
            case CardGeneratedEntry generated when generated.Card.Type is not (CardType.Status or CardType.Curse):
                Give(generated.Creator ?? Giver(), generated.Card.Owner, CardsGiven, 1);
                return;
            // Pas la pioche du début de tour : seulement celle qu'un effet fait faire
            case CardDrawnEntry { FromHandDraw: false } drawn:
                Give(Giver(), drawn.Card.Owner, DrawGiven, 1);
                return;
        }
    }

    [HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.GainEnergy))]
    public static class SupportEnergyPatch
    {
        public static void Prefix(decimal amount, Player player) => Give(Giver(), player, EnergyGiven, amount);
    }

    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Heal))]
    public static class SupportHealPatch
    {
        public static void Prefix(Creature creature, decimal amount)
        {
            if (creature is not { IsEnemy: false } || !CombatManager.Instance.IsInProgress
                || !(creature.CombatState?.IsLiveCombat() ?? false))
                return;
            decimal healed = Math.Max(0, Math.Min(amount, creature.MaxHp - creature.CurrentHp));
            Give(Giver(), PlayerOf(creature), HpRestored, healed);
        }
    }

    /// <summary>Début d'un combat : un combat de plus pour chacun, plus aucun effet en cours.</summary>
    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
    public static class SupportCombatStartPatch
    {
        public static void Postfix(CombatState state)
        {
            _effectOwners.Clear();
            foreach (var player in state.Players)
                SupportOf(player).StartCombat();
        }
    }
}
