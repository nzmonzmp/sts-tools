using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace StsTools.Game;

/// <summary>
/// Composition du deck (onglet Deck > Stats) : nombre de cartes par type et par effet, d'après ce que le jeu sait
/// de chaque carte (valeurs de dégâts, de Block, de pioche, pouvoirs donnés, mots-clés, coût) ; pour les effets et
/// pouvoirs, aussi le nombre de reliques du joueur qui les ont.
/// </summary>
public static class DeckStats
{
    /// <summary>
    /// Une ligne : groupe, nom, nombre de cartes du deck, nombre de reliques et total de l'effet sur les cartes du
    /// deck (ces deux-là pour les effets et pouvoirs seulement), chemin de l'icône dans les images du jeu.
    /// </summary>
    public sealed record Row(string Group, string Name, int Count, int? Relics, decimal? Total, string? Icon);

    private const string Sprites = "res://images/packed/sprite_fonts/";
    private const string Powers = "res://images/atlases/power_atlas.sprites/";
    private const string Relics = "res://images/atlases/relic_atlas.sprites/";
    private const string Potions = "res://images/atlases/potion_atlas.sprites/";

    /// <summary>Pouvoirs toujours affichés, même à 0 ; les autres pouvoirs donnés par des cartes du deck s'y ajoutent.</summary>
    private static readonly string[] CommonPowers = ["Strength", "Dexterity", "Vulnerable", "Weak", "Poison"];

    public static List<Row> Compute(IReadOnlyList<CardModel> cards, IReadOnlyList<RelicModel> relics)
    {
        var rows = new List<Row>();
        string group = "";
        void Add(string name, Func<CardModel, bool> test, string? icon) =>
            rows.Add(new Row(group, name, cards.Count(test), null, null, icon));

        // Effet d'une valeur de la carte : nombre de cartes qui l'ont, nombre de reliques qui l'ont, et somme de
        // cette valeur sur les cartes du deck (dégâts multipliés par le nombre de coups). Plusieurs types possibles,
        // par ordre de préférence.
        void AddEffect(string name, Type[] types, string? icon, bool timesHits = false)
        {
            var values = cards.Select(c => Value(c.DynamicVars, types) * (timesHits ? Hits(c.DynamicVars) : 1))
                .Where(v => v != null).ToList();
            int relicCount = relics.Count(r => Value(r.DynamicVars, types) != null);
            rows.Add(new Row(group, name, values.Count, relicCount, values.Sum(v => v!.Value), icon));
        }

        group = "Card types";
        Add("Attacks", c => c.Type == CardType.Attack, "res://images/packed/card_library/type_sort_attack.png");
        Add("Skills", c => c.Type == CardType.Skill, "res://images/packed/card_library/type_sort_skill.png");
        Add("Powers", c => c.Type == CardType.Power, "res://images/packed/card_library/type_sort_power.png");
        Add("Statuses", c => c.Type == CardType.Status, "res://images/packed/intents/intent_status_card.png");
        Add("Curses", c => c.Type == CardType.Curse, "res://images/ui/game_over_screen/badge_curses.png");

        // Mots-clés et coût
        group = "Keywords & cost";
        Add("Exhaust", c => c.Keywords.Contains(CardKeyword.Exhaust), "res://images/packed/combat_ui/exhaust_pile.png");
        // Pas d'icône de mot-clé dans le jeu : des images qui l'évoquent (Intangible pour Ethereal, Bag of
        // Preparation pour Innate, Chemical X pour X cost…)
        Add("Ethereal", c => c.Keywords.Contains(CardKeyword.Ethereal), Powers + "intangible_power.tres");
        Add("Innate", c => c.Keywords.Contains(CardKeyword.Innate), Relics + "bag_of_preparation.tres");
        Add("Retain", c => c.Keywords.Contains(CardKeyword.Retain), Powers + "retain_hand_power.tres");
        Add("Unplayable", c => c.Keywords.Contains(CardKeyword.Unplayable), "res://images/packed/common_ui/card/card_unplayable_icon.png");
        Add("X cost", c => c.EnergyCost.CostsX, Relics + "chemical_x.tres");

        // Effets, d'après les valeurs de la carte
        group = "Effects";
        AddEffect("Damage", [typeof(DamageVar), typeof(OstyDamageVar), typeof(CalculatedDamageVar), typeof(ExtraDamageVar)],
            "res://images/packed/intents/attack/intent_attack_1.png", timesHits: true);
        AddEffect("Block", [typeof(BlockVar), typeof(CalculatedBlockVar)], "res://images/ui/combat/block.png");
        // Valeur « Cards » : pioche le plus souvent (parfois cartes à défausser, à créer…)
        AddEffect("Draw", [typeof(CardsVar)], "res://images/packed/combat_ui/draw_pile.png");
        AddEffect("Energy", [typeof(EnergyVar)], Sprites + "colorless_energy_icon.png");
        AddEffect("Stars", [typeof(StarsVar)], Sprites + "star_icon.png");
        AddEffect("Heal", [typeof(HealVar)], "res://images/packed/intents/intent_heal.png");
        AddEffect("HP loss", [typeof(HpLossVar)], "res://images/packed/common_ui/top_bar/top_bar_heart.png");
        AddEffect("Summon", [typeof(SummonVar)], "res://images/packed/intents/intent_summon.png");
        AddEffect("Forge", [typeof(ForgeVar)], Potions + "blessing_of_the_forge.tres");

        // Pouvoirs donnés (Strength, Vulnerable, Poison…) : les courants, puis tous ceux du deck ;
        // total : la somme des quantités données
        group = "Powers applied";
        var powers = cards.SelectMany(c => PowerValues(c.DynamicVars).GroupBy(p => p.Name).Select(g => g.First())).ToList();
        var relicPowers = relics.SelectMany(r => PowerValues(r.DynamicVars).Select(p => p.Name).Distinct()).ToList();
        var names = powers.Select(p => p.Name).Concat(relicPowers).Distinct().Except(CommonPowers).Order();
        foreach (var power in CommonPowers.Concat(names))
        {
            var given = powers.Where(p => p.Name == power).ToList();
            rows.Add(new Row(group, power, given.Count, relicPowers.Count(p => p == power), given.Sum(p => p.Amount),
                Powers + SnakeCase(power) + "_power.tres"));
        }
        return rows;
    }

    /// <summary>Valeur de la carte du premier type trouvé parmi <paramref name="types"/>, ou null si elle n'en a aucun.</summary>
    private static decimal? Value(DynamicVarSet vars, Type[] types)
    {
        foreach (var type in types)
            if (vars.Values.FirstOrDefault(v => v.GetType() == type) is { } v)
                return v.BaseValue;
        return null;
    }

    /// <summary>Nombre de coups d'une attaque (Twin Strike : 2), 1 par défaut.</summary>
    private static decimal Hits(DynamicVarSet vars) =>
        vars.Values.FirstOrDefault(v => v is HitCountVar or CalculatedHitCountVar) is { BaseValue: > 0 } v ? v.BaseValue : 1;

    /// <summary>Pouvoirs que la carte donne et leur quantité : « PowerVar&lt;StrengthPower&gt; 2 » → (« Strength », 2).</summary>
    private static IEnumerable<(string Name, decimal Amount)> PowerValues(DynamicVarSet vars) =>
        vars.Values
            .Where(v => v.GetType() is { IsGenericType: true } t && t.GetGenericTypeDefinition() == typeof(PowerVar<>))
            .Select(v => (v.GetType().GetGenericArguments()[0].Name, v.BaseValue))
            .Select(p => (p.Name.EndsWith("Power") ? p.Name[..^"Power".Length] : p.Name, p.BaseValue));

    /// <summary>« StrengthPower » → « strength_power », comme les noms des icônes de pouvoirs du jeu.</summary>
    private static string SnakeCase(string name) => Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();

    public static Texture2D? IconFor(IEnumerable<Row> rows, string name) =>
        StatsTable.LoadIcon(rows.FirstOrDefault(r => r.Name == name)?.Icon);
}
