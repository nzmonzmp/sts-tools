using MegaCrit.Sts2.Core.Models;
using static StsTools.Game.CardRunStats;

namespace StsTools.Game;

/// <summary>
/// Stats logiquement liées à une carte ou une relique, d'après son code (<see cref="CardCode"/>) : une carte
/// qui attaque a des dégâts, une relique qui redéfinit le calcul du Block a du Block bonus… Le code de son
/// enchantement et des pouvoirs qu'elle donne compte aussi (leurs effets lui reviennent).
/// Seules les stats non nulles sont affichées : les liées d'abord, les autres à part (la carte est sortie de son
/// comportement habituel, ou le jeu l'a créditée d'un effet imprévu).
/// </summary>
public sealed class StatRelevance
{
    /// <summary>Stats de toute carte, quoi qu'elle fasse.</summary>
    private static readonly string[] CardBasics = [Played, AutoPlayed, Drawn, Exhausted, Energy, StarsSpent];

    /// <summary>Commande du jeu appelée (« Classe.Méthode », ou « Classe.* ») → stats qu'elle produit.</summary>
    private static readonly (string Command, string[] Stats)[] Commands =
    [
        ("DamageCmd.Attack", [Damage, Kills]),
        ("CreatureCmd.Damage", [Damage, Kills, SelfDamage]),
        ("CreatureCmd.Kill", [Kills]),
        ("ArrowCmd.ShootArrowAtTarget", [Damage, Kills]),
        ("CreatureCmd.GainBlock", [Block]),
        ("CardPileCmd.Draw", [CardsDrawn]),
        ("CardCmd.Discard", [CardsDiscarded]),
        ("CardSelectCmd.FromHandForDiscard", [CardsDiscarded]),
        ("CardCmd.Exhaust", [CardsExhausted]),
        ("CardPileCmd.AddGeneratedCardToCombat", [CardsCreated]),
        ("CardPileCmd.AddGeneratedCardsToCombat", [CardsCreated]),
        ("CardPileCmd.AddToCombatAndPreview", [CardsCreated]),
        ("CardCmd.AutoPlay", [CardsPlayed]),
        ("CardPileCmd.AutoPlayFromDrawPile", [CardsPlayed]),
        ("PlayerCmd.GainEnergy", [EnergyGained]),
        ("PlayerCmd.GainStars", [StarsGained]),
        ("PlayerCmd.GainGold", [Gold]),
        ("CreatureCmd.Heal", [Healed]),
        ("CreatureCmd.GainMaxHp", [MaxHpGained, Healed]),
        ("CreatureCmd.LoseMaxHp", [MaxHpLost]),
        ("OrbCmd.Channel", [Orbs, Damage, Block]),
        ("OstyCmd.Summon", [Summoned]),
        ("CardPileCmd.AddCurseToDeck", [DeckAdded]),
        ("CardPileCmd.AddCursesToDeck", [DeckAdded]),
        ("CardPileCmd.RemoveFromDeck", [DeckRemoved]),
        ("CardSelectCmd.FromDeckForRemoval", [DeckRemoved]),
        ("CardSelectCmd.FromDeckForUpgrade", [Upgraded]),
        ("CardCmd.Transform", [Transformed]),
        ("CardCmd.TransformTo", [Transformed]),
        ("CardCmd.TransformToRandom", [Transformed]),
        ("CardCmd.Enchant", [Enchanted]),
        ("CardSelectCmd.FromDeckForEnchantment", [Enchanted]),
        ("PotionCmd.TryToProcure", [Potions]),
        ("PlayerCmd.GainMaxPotionCount", [PotionSlots]),
        ("CreatureCmd.Stun", [Stunned]),
        ("OrbCmd.EvokeNext", [OrbsEvoked]),
        ("OrbCmd.EvokeLast", [OrbsEvoked]),
        ("OrbCmd.AddSlots", [OrbSlots]),
        ("ForgeCmd.Forge", [Forged]),
        ("CreatureCmd.SetCurrentHp", [EnemyHpRemoved]),
        ("CardPileCmd.Add", [DeckAdded]),
        ("CardCmd.Upgrade", [Upgraded, UpgradedInCombat]),
    ];

    /// <summary>Calcul du jeu redéfini (méthode déclarée) → stats que ce bonus indirect produit.</summary>
    private static readonly (string Method, string[] Stats)[] Methods =
    [
        ("ModifyBlockAdditive", [BonusBlock]),
        ("ModifyBlockMultiplicative", [BonusBlock]),
        ("ModifyDamageAdditive", [BonusDamage, Prevented]),
        ("ModifyDamageMultiplicative", [BonusDamage, Prevented]),
        ("ModifyDamageMultiplicativeLate", [BonusDamage, Prevented]),
        ("ModifyHpLostBeforeOsty", [Prevented, BonusDamage]),
        ("ModifyHpLostBeforeOstyLate", [Prevented, BonusDamage]),
        ("ModifyHpLostAfterOsty", [Prevented, BonusDamage]),
        ("ModifyHpLostAfterOstyLate", [Prevented, BonusDamage]),
        ("ModifyHandDraw", [CardsDrawn]),
        ("ModifyHandDrawLate", [CardsDrawn]),
        ("ModifyEnergyGain", [EnergyGained]),
        ("ModifyMaxEnergy", [EnergyGained]),
        ("ModifyGoldGained", [Gold]),
        ("ModifyCardPlayCount", [CardsPlayed]),
        ("ModifySummonAmount", [Summoned]),
        ("TryModifyEnergyCostInCombat", [EnergySaved]),
        ("TryModifyEnergyCostInCombatLate", [EnergySaved]),
        ("TryModifyStarCost", [StarsSaved]),
        ("ShouldClearBlock", [BlockRetained]),
        ("ShouldDie", [DeathsPrevented, Healed]),
        ("ShouldDieLate", [DeathsPrevented, Healed]),
        ("ShouldFlush", [CardsRetained]),
        ("ShouldTakeExtraTurn", [ExtraTurns]),
        ("ShouldTakeExtraTurnEarly", [ExtraTurns]),
        ("ModifyXValue", [XBonus]),
        ("ModifyAttackHitCount", [ExtraHits]),
        ("ModifyOrbPassiveTriggerCounts", [OrbTriggers]),
        ("TryModifyPowerAmountReceived", [DebuffsPrevented]),
        ("TryModifyCardBeingAddedToDeck", [Upgraded, Enchanted]),
        ("TryModifyCardBeingAddedToDeckLate", [Upgraded, Enchanted]),
        ("ModifyMerchantPrice", [GoldSaved]),
        ("ModifyRestSiteHealAmount", [Healed]),
        ("TryModifyCardRewardOptions", [CardChoices, Upgraded, Enchanted, OfferedUpgraded, OfferedEnchanted]),
        ("TryModifyCardRewardOptionsLate", [CardChoices, Upgraded, Enchanted, OfferedUpgraded, OfferedEnchanted]),
        ("TryModifyCardRewardAlternatives", [CardChoices]),
        ("TryModifyRewards", [RewardsAdded]),
        ("TryModifyRewardsLate", [RewardsAdded]),
        ("ShouldForcePotionReward", [PotionRewards]),
        ("TryModifyRestSiteOptions", [RestOptions]),
        ("ShouldGenerateTreasure", [TreasuresSkipped]),
        ("ModifyMerchantCardCreationResults", [Upgraded, Enchanted, OfferedUpgraded, OfferedEnchanted]),
        ("ShouldRefillMerchantEntry", [ItemsRestocked]),
        ("ModifyUnknownMapPointRoomTypes", [SafeUnknownRooms]),
        ("ShouldAllowFreeTravel", [FreeTravels]),
        ("ModifyOrbValue", [OrbBonus]),
        ("ModifyGeneratedMap", [GoldenPathRooms]),
    ];

    private readonly HashSet<string> _stats = [];

    /// <summary>Noms des pouvoirs donnés (« Strength »), pour leurs lignes « Strength gained » / « … applied ».</summary>
    private readonly HashSet<string> _powers = [];

    /// <summary>Code illisible, ou bonus sur tous les pouvoirs donnés : tous les pouvoirs sont liés.</summary>
    private bool _allPowers;

    /// <summary>Code illisible : on ne peut rien écarter.</summary>
    private bool _everything;

    private static readonly Dictionary<string, StatRelevance> Cache = new();

    public static StatRelevance For(CardModel card)
    {
        var enchantment = card.Enchantment?.GetType();
        var key = $"card:{card.GetType().FullName}:{enchantment?.FullName}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        var r = new StatRelevance();
        r._stats.UnionWith(CardBasics);
        r.AddCode(card.GetType(), typeof(CardModel));
        // Enchantement : ce qu'il ajoute à la carte (Block, pioche…) est fait au nom de la carte
        if (enchantment != null)
            r.AddCode(enchantment, typeof(EnchantmentModel));
        return Cache[key] = r;
    }

    public static StatRelevance For(RelicModel relic)
    {
        var key = $"relic:{relic.GetType().FullName}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        var r = new StatRelevance();
        r.AddCode(relic.GetType(), typeof(RelicModel));
        return Cache[key] = r;
    }

    /// <summary>Code de la classe et de ses classes parentes propres au contenu (jusqu'à la classe de base du jeu).</summary>
    private void AddCode(Type type, Type root, int depth = 0, HashSet<Type>? seen = null)
    {
        seen ??= [];
        for (var t = type; t != null && t != root && t != typeof(AbstractModel); t = t.BaseType)
        {
            if (!seen.Add(t))
                continue;
            var effects = CardCode.Of(t);
            if (!effects.Readable)
                _everything = true;
            foreach (var (command, stats) in Commands)
                if (effects.Commands.Contains(command))
                    _stats.UnionWith(stats);
            foreach (var (method, stats) in Methods)
                if (effects.Methods.Contains(method))
                    _stats.UnionWith(stats);
            if (effects.Methods.Any(m => m.StartsWith("ModifyPowerAmountGiven")))
                _allPowers = true;
            // Pouvoir donné : sa ligne, et ce qu'il fait ensuite (Strength → dégâts bonus, Poison → dégâts…)
            foreach (var power in effects.PowerTypes)
            {
                if (PowerTitle(power) is { } title)
                    _powers.Add(title);
                if (depth < 2)
                    AddCode(power, typeof(PowerModel), depth + 1, seen);
            }
        }
    }

    private static string? PowerTitle(Type type)
    {
        try
        {
            return ModelDb.AllPowers.FirstOrDefault(p => p.GetType() == type)?.Title.GetFormattedText();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool IsRelated(string name)
    {
        if (_everything || _stats.Contains(name))
            return true;
        foreach (var suffix in new[] { " applied", " gained" })
            if (name.EndsWith(suffix))
                return _allPowers || _powers.Contains(name[..^suffix.Length]);
        return false;
    }

    /// <summary>Lignes à afficher, non nulles : les stats liées dans l'ordre habituel, puis les autres, à part.</summary>
    public (List<string> Related, List<string> Others) Arrange(CardRunStats s)
    {
        var shown = s.DisplayNames().ToList();
        return (shown.Where(IsRelated).ToList(), shown.Where(name => !IsRelated(name)).ToList());
    }
}
