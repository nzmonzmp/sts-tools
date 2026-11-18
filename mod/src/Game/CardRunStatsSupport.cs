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
    public const string BonusDamageGiven = "Bonus damage for allies";
    public const string PreventedForAllies = "Damage prevented for allies";
    public const string OrbsGiven = "Orbs given";
    public const string OrbDamageGiven = "Damage by orbs given";

    /// <summary>Ordre d'affichage de l'onglet Support ; les buffs donnés (« Strength given »…) viennent après.</summary>
    public static readonly string[] SupportOrder =
        [BonusDamageGiven, PreventedForAllies, OrbsGiven, OrbDamageGiven, EnergyGiven, CardsGiven, DrawGiven, BlockGiven, HpRestored];

    private static readonly Dictionary<string, string> SupportIcons = new()
    {
        [EnergyGiven] = "res://images/packed/sprite_fonts/colorless_energy_icon.png",
        [CardsGiven] = "res://images/packed/sprite_fonts/card_icon.png",
        [DrawGiven] = "res://images/packed/combat_ui/draw_pile.png",
        [BlockGiven] = "res://images/ui/combat/block.png",
        [HpRestored] = "res://images/packed/intents/intent_heal.png",
        [BonusDamageGiven] = "res://images/atlases/power_atlas.sprites/strength_power.tres",
        [PreventedForAllies] = "res://images/packed/intents/intent_defend.png",
        [OrbsGiven] = "res://images/orbs/empty_orb.png",
        [OrbDamageGiven] = "res://images/orbs/lightning_orb.png",
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

    /// <summary>Joueurs à qui revient un effet, avec leur part (somme 1).</summary>
    private sealed record Givers(IReadOnlyList<(Player Player, decimal Share)> Shares)
    {
        public static readonly Givers None = new([]);

        public static Givers One(Player? player) => player == null ? None : new([(player, 1m)]);
    }

    /// <summary>
    /// Compte un don à <paramref name="receiver"/>, partagé entre les joueurs qui le font, seulement entre deux joueurs
    /// différents (ce qu'on se donne à soi-même est dans l'onglet Combat).
    /// </summary>
    private static void Give(Givers givers, Player? receiver, string name, decimal amount)
    {
        if (receiver == null || amount <= 0 || PlaySimulation.Active)
            return;
        bool changed = false;
        foreach (var (giver, share) in givers.Shares)
            if (giver != receiver && share > 0)
            {
                SupportOf(giver).Add(name, Math.Round(amount * share, 2));
                changed = true;
            }
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Joueur d'une créature alliée : le joueur lui-même, ou le maître de son familier (Osty).</summary>
    private static Player? PlayerOf(Creature? creature) => creature?.Player ?? creature?.PetOwner;

    /// <summary>
    /// Joueurs qui ont donné chaque pouvoir, avec la quantité donnée par chacun (Vulnerable posé par deux joueurs,
    /// Force donnée à un allié qui en avait déjà) : ses effets suivants (dégâts en plus, énergie chaque tour…) sont
    /// leurs dons, au prorata.
    /// </summary>
    private static readonly ConditionalWeakTable<PowerModel, Dictionary<Player, decimal>> PowerGivers = new();

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
    /// Joueurs qui agissent en ce moment : celui de la carte jouée ou de l'effet en cours (relique, carte, ou ceux qui
    /// ont donné le pouvoir), sinon celui de la carte ou de la potion en train de faire son effet ; aucun pour un
    /// effet ennemi.
    /// </summary>
    private static Givers CurrentGivers()
    {
        try
        {
            // Orbe placée chez un allié (Ignition) qui fait son effet : c'est un don de celui qui l'a placée
            if (_givingOrb is { } orb && OrbGivers.TryGetValue(orb, out var orbGivers))
                return orbGivers;
            if (_frames.Count > 0 && _frames[^1] is { } frame && (frame.PlayedCard != null || frame.Listener != null))
                return frame.PlayedCard is { } card ? Givers.One(card.Owner) : GiversOf(frame.Listener!);
            return Givers.One(_effectOwners.Count > 0 ? _effectOwners[^1] : null);
        }
        catch (Exception)
        {
            return Givers.None;
        }
    }

    /// <summary>Joueurs à qui reviennent les effets d'un écouteur du jeu (carte, relique, potion, pouvoir).</summary>
    private static Givers GiversOf(AbstractModel model)
    {
        try
        {
            switch (model)
            {
                case CardModel card:
                    return Givers.One(card.Owner);
                case RelicModel relic:
                    return Givers.One(relic.Owner);
                case PotionModel potion:
                    return Givers.One(potion.Owner);
                case PowerModel power:
                    if (PowerGivers.TryGetValue(power, out var givers) && givers.Values.Sum() is var total and > 0)
                        return new Givers(givers.Select(g => (g.Key, g.Value / total)).ToList());
                    return Givers.One(PlayerOf(power.Owner));
                default:
                    return Givers.None;
            }
        }
        catch (Exception)
        {
            return Givers.None;
        }
    }

    // ---- Orbes placées chez un allié (Ignition) ----

    /// <summary>Joueurs qui ont placé chaque orbe : ses effets (énergie du Plasma, dégâts, Block) sont leurs dons.</summary>
    private static readonly ConditionalWeakTable<OrbModel, Givers> OrbGivers = new();

    /// <summary>
    /// Orbe dont l'effet (passif ou évocation) est en train de se faire, sinon null. À part de celle des stats de
    /// Focus (CardRunStatsMore), effacée dès que la valeur de l'orbe est calculée, avant ses effets.
    /// </summary>
    private static OrbModel? _givingOrb;

    /// <summary>
    /// Effet de chaque orbe (Passive, Evoke, redéfinis par chaque orbe) : l'orbe agit pendant la partie synchrone de
    /// l'effet, où le jeu donne l'énergie, le Block ou les dégâts.
    /// </summary>
    [HarmonyPatch]
    public static class OrbGiftPatch
    {
        public static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            typeof(OrbModel).Assembly.GetTypes()
                .Where(t => t.IsSubclassOf(typeof(OrbModel)) && !t.IsAbstract)
                .SelectMany(t => new[] { nameof(OrbModel.Passive), nameof(OrbModel.Evoke) }
                    .Select(name => AccessTools.DeclaredMethod(t, name)))
                .Where(m => m != null)
                .Cast<System.Reflection.MethodBase>();

        public static void Prefix(OrbModel __instance, out OrbModel? __state)
        {
            __state = _givingOrb;
            _givingOrb = __instance;
        }

        public static void Finalizer(OrbModel? __state) => _givingOrb = __state;
    }

    /// <summary>
    /// Pendant un coup : ce qu'un pouvoir donné par d'autres joueurs a ajouté aux dégâts d'un allié (Vulnerable sur
    /// l'ennemi, Force donnée à l'allié) ou retiré à une attaque ennemie contre un allié (Weak sur l'ennemi).
    /// </summary>
    private static void GiveDamageChange(AbstractModel model, Creature? ally, string name, decimal amount) =>
        Give(GiversOf(model), PlayerOf(ally), name, amount);

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
                Give(block.CardPlay?.Card.Owner is { } owner ? Givers.One(owner) : CurrentGivers(), PlayerOf(block.Actor), BlockGiven, block.Amount);
                return;
            case PowerReceivedEntry { Amount: > 0 } power when power.Applier is not { IsEnemy: true }:
                var givers = PlayerOf(power.Applier) is { } applier ? Givers.One(applier) : CurrentGivers();
                // Les effets futurs du pouvoir (dégâts en plus, énergie chaque tour…) seront des dons de ces
                // joueurs, au prorata de ce que chacun a donné ; aussi les debuffs posés sur les ennemis
                var shares = PowerGivers.GetOrCreateValue(power.Power);
                foreach (var (giver, share) in givers.Shares)
                    shares[giver] = shares.GetValueOrDefault(giver) + power.Amount * share;
                if (power.Power.Owner.IsEnemy || power.Power.Type != PowerType.Buff)
                    return;
                var name = $"{power.Power.Title.GetFormattedText()} given";
                PowerIcons[name] = power.Power.IconPath;
                Give(givers, PlayerOf(power.Power.Owner), name, power.Amount);
                return;
            // Pas les Statuts ni les Malédictions mis dans le deck d'un allié
            case CardGeneratedEntry generated when generated.Card.Type is not (CardType.Status or CardType.Curse):
                Give(generated.Creator is { } creator ? Givers.One(creator) : CurrentGivers(), generated.Card.Owner, CardsGiven, 1);
                return;
            // Pas la pioche du début de tour : seulement celle qu'un effet fait faire
            case OrbChanneledEntry channeled:
                var orbGivers = CurrentGivers();
                OrbGivers.AddOrUpdate(channeled.Orb, orbGivers);
                Give(orbGivers, channeled.Orb.Owner, OrbsGiven, 1);
                return;
            // Dégâts faits aux ennemis par une orbe placée chez un allié
            case DamageReceivedEntry damage when damage.Receiver.IsEnemy && _givingOrb is { } orb && OrbGivers.TryGetValue(orb, out var givenBy):
                Give(givenBy, orb.Owner, OrbDamageGiven, damage.Result.TotalDamage);
                return;
            case CardDrawnEntry { FromHandDraw: false } drawn:
                Give(CurrentGivers(), drawn.Card.Owner, DrawGiven, 1);
                return;
        }
    }

    [HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.GainEnergy))]
    public static class SupportEnergyPatch
    {
        public static void Prefix(decimal amount, Player player) => Give(CurrentGivers(), player, EnergyGiven, amount);
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
            Give(CurrentGivers(), PlayerOf(creature), HpRestored, healed);
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
