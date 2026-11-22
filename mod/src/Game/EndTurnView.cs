using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;

namespace StsTools.Game;

/// <summary>
/// Sous-onglet End turn de l'onglet Combat : ce que donnerait la fin du tour maintenant, d'après la même simulation
/// que le bouton de fin de tour et les têtes de mort (<see cref="PlaySimulation.PredictTurnEndDetail"/>). PV perdus
/// et PV restants (Osty aussi), tour supplémentaire, attaque de chaque ennemi au moment d'attaquer (mort avant
/// d'agir, après), effets de la fin de tour sur chaque créature (Block du Plating, Burn, poison…). Seulement pendant
/// le tour des joueurs. Si le hasard intervient, moyenne de tirages qui ne sont pas ceux du jeu.
/// </summary>
internal static class EndTurnView
{
    public const string Name = "End turn";

    private const string AttackIcon = "res://images/packed/intents/attack/intent_attack_1.png";
    private const string HpLost = "HP lost";
    private const string HpLeft = "HP left";
    private const string ExtraTurn = "Extra turn";

    public static void Fill(VBoxContainer content, IReadOnlyList<Player> players)
    {
        var combat = players[0].Creature.CombatState;
        if (!CombatManager.Instance.IsInProgress || CombatManager.Instance.IsOverOrEnding || combat == null)
        {
            content.AddChild(RunPanel.Label("No combat in progress.", 18, StsColors.halfTransparentCream));
            return;
        }
        if (combat.CurrentSide != CombatSide.Player)
        {
            content.AddChild(RunPanel.Label("Shown during your turn.", 18, StsColors.halfTransparentCream));
            return;
        }
        // Le joueur local en dernier : sa fin de tour reste gardée (bouton de fin de tour, têtes de mort)
        var ordered = players.OrderBy(p => p == CardRunStats.LocalPlayer ? 1 : 0).ToList();
        var details = new Dictionary<Player, PlaySimulation.TurnEndDetail?>();
        foreach (var player in ordered)
            details[player] = player.Creature.IsDead ? null : Predict(player);
        var names = CreatureNames(combat);
        var rows = new List<string[]> { new[] { "If you end your turn now", "" } };
        var icons = new Dictionary<string, Texture2D?>
        {
            [HpLost] = CardRunStats.IconFor(CardRunStats.SelfDamage),
            [HpLeft] = CardRunStats.IconFor(CardRunStats.Healed),
        };
        foreach (var player in players)
        {
            if (players.Count > 1)
                rows.Add([RunPanel.PlayerName(player)]);
            if (player.Creature.IsDead)
                rows.Add(["Dead", ""]);
            else if (details[player] is not { } detail)
                rows.Add(["Cannot predict the end of the turn", ""]);
            else
                AddSummary(rows, icons, player, detail);
        }
        // Ennemis et effets de fin de tour : ceux de la simulation du joueur local (les mêmes pour tous)
        var main = details.GetValueOrDefault(CardRunStats.LocalPlayer ?? players[0]) ?? details.Values.FirstOrDefault(d => d != null);
        if (main != null)
        {
            AddEnemies(rows, icons, combat, main, names);
            AddEffects(rows, main, players, names);
        }
        content.AddChild(StatsTable.Create(rows,
            name => icons.TryGetValue(name, out var icon) ? icon : main != null ? TargetPreview.IconOf(name, main.End) : null,
            highlight: true));
        if (main is { Random: true })
            content.AddChild(RunPanel.Label($"Random effects: average of {ModOptions.RandomDraws} draws (not the game's own).", 16,
                StsColors.halfTransparentCream));
        if (main is { End.Incomplete: true })
            content.AddChild(RunPanel.Label("Part of the end of the turn could not be simulated.", 16, StsColors.halfTransparentCream));
    }

    private static PlaySimulation.TurnEndDetail? Predict(Player player)
    {
        try
        {
            return PlaySimulation.PredictTurnEndDetail(player);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot predict the end of the turn: {e.Message}");
            return null;
        }
    }

    /// <summary>PV perdus et restants (mort, mort évitée par Lizard Tail…), Osty, tour supplémentaire.</summary>
    private static void AddSummary(List<string[]> rows, Dictionary<string, Texture2D?> icons, Player player,
        PlaySimulation.TurnEndDetail detail)
    {
        rows.Add([HpLost, detail.Taken.ToString()]);
        string left;
        if (detail.Taken < detail.Hp)
            left = $"{detail.Hp - detail.Taken} / {player.Creature.MaxHp}";
        else
        {
            bool dies = detail.Forced || player.Creature.CombatState is CombatState state
                && Hook.ShouldDie(state.RunState, state, player.Creature, out _);
            left = dies ? "0 (dies)" : "0 (death prevented)";
        }
        rows.Add([HpLeft, left]);
        if (detail.PetTaken is { } petTaken)
        {
            const string pet = "Osty's HP lost";
            icons[pet] = CardRunStats.IconFor(CardRunStats.SelfDamage);
            rows.Add([pet, petTaken + (detail.PetDies ? " (dies)" : "")]);
        }
        if (detail.ExtraTurn is { } source)
        {
            icons[ExtraTurn] = null;
            rows.Add([ExtraTurn, Title(source)]);
        }
    }

    /// <summary>Attaque de chaque ennemi en vie au moment d'attaquer, et s'il meurt avant ou après avoir agi.</summary>
    private static void AddEnemies(List<string[]> rows, Dictionary<string, Texture2D?> icons, ICombatState combat,
        PlaySimulation.TurnEndDetail detail, Dictionary<Creature, string> names)
    {
        var enemies = combat.Enemies.Where(e => !e.IsDead).ToList();
        if (enemies.Count == 0 || detail.ExtraTurn != null)
            return;
        rows.Add(["Enemy attacks"]);
        var attack = StatsTable.LoadIcon(AttackIcon);
        foreach (var enemy in enemies)
        {
            var name = names[enemy];
            icons[name] = attack;
            string value = detail.DeadEnemies.Contains(enemy) ? "dies first"
                : detail.Incoming.GetValueOrDefault(enemy).ToString() + (detail.LateDeadEnemies.Contains(enemy) ? " (then dies)" : "");
            rows.Add([name, value]);
        }
    }

    /// <summary>Ce que la fin du tour change à chaque créature, dans l'ordre des joueurs puis des ennemis.</summary>
    private static void AddEffects(List<string[]> rows, PlaySimulation.TurnEndDetail detail, IReadOnlyList<Player> players,
        Dictionary<Creature, string> names)
    {
        var order = players.Select(p => p.Creature).Concat(names.Keys).Distinct();
        foreach (var creature in order)
        {
            if (!detail.End.Creatures.TryGetValue(creature, out var stats))
                continue;
            var effects = TargetPreview.Rows(creature, stats, detail.End).ToList();
            if (effects.Count == 0)
                continue;
            rows.Add([$"End of turn: {names.GetValueOrDefault(creature) ?? Safe(() => creature.Name)}"]);
            rows.AddRange(effects);
        }
    }

    /// <summary>
    /// Nom affiché de chaque créature du combat : « You » pour le joueur local, numéroté si plusieurs ennemis
    /// portent le même (« Louse 1 », « Louse 2 »).
    /// </summary>
    private static Dictionary<Creature, string> CreatureNames(ICombatState combat)
    {
        var names = new Dictionary<Creature, string>();
        foreach (var player in combat.Players)
        {
            names[player.Creature] = RunPanel.PlayerName(player);
            if (player.Osty is { } osty)
                names[osty] = player == CardRunStats.LocalPlayer ? "Osty" : $"Osty ({RunPanel.PlayerName(player)})";
        }
        var enemies = combat.Enemies.ToList();
        var counts = enemies.GroupBy(e => Safe(() => e.Name)).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<string, int>();
        foreach (var enemy in enemies)
        {
            var name = Safe(() => enemy.Name);
            seen[name] = seen.GetValueOrDefault(name) + 1;
            names[enemy] = counts[name] > 1 ? $"{name} {seen[name]}" : name;
        }
        return names;
    }

    private static string Title(MegaCrit.Sts2.Core.Models.AbstractModel model) => model switch
    {
        MegaCrit.Sts2.Core.Models.RelicModel relic => Safe(() => relic.Title.GetFormattedText()),
        MegaCrit.Sts2.Core.Models.PowerModel power => Safe(() => power.Title.GetFormattedText()),
        MegaCrit.Sts2.Core.Models.CardModel card => Safe(() => card.Title),
        _ => model.Id.Entry,
    };

    private static string Safe(Func<string> text)
    {
        try
        {
            return text();
        }
        catch (Exception)
        {
            return "?";
        }
    }
}
