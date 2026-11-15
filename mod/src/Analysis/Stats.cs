namespace StsTools.Analysis;

/// <summary>Les quatre analyses : winrate, cartes, reliques, défaites. Portage de sts_tools/cli.py.</summary>
public static class Stats
{
    public const string NoRows = "(aucune ligne, essayer un minimum plus petit)";

    public static string Pct(int w, int n) =>
        n == 0 ? "-" : $"{Math.Round(100.0 * w / n, MidpointRounding.ToEven):0}%";

    public static string Summary(IReadOnlyCollection<Run> runs)
    {
        var wins = runs.Count(r => r.Win);
        return $"{runs.Count} parties, {wins}W ({Pct(wins, runs.Count)})";
    }

    public static string AscensionLabel(int? asc) => asc is { } a ? $"A{a}" : "toutes ascensions";

    private static bool MatchesMode(Run r, Filters f) =>
        (f.Ascension == null || r.Ascension == f.Ascension) && (f.GameMode == null || r.GameMode == f.GameMode);

    /// <summary>Parties retenues par les filtres, et un libellé qui les décrit.</summary>
    public static (List<Run> Runs, string Label) Select(IEnumerable<Run> all, Filters f, IGameInfo game)
    {
        var runs = all.Where(r =>
            r.Coop == f.Coop && MatchesMode(r, f)
            && (f.Character == null || string.Equals(r.Character, f.Character, StringComparison.OrdinalIgnoreCase))).ToList();
        var parts = new List<string?>
        {
            f.Coop ? "Coop" : "Solo",
            AscensionLabel(f.Ascension),
            f.GameMode,
            f.Character == null ? null : game.CharacterName(f.Character),
        };
        return (runs, $"{string.Join(", ", parts.Where(p => p != null))} - {Summary(runs)}");
    }

    private static IEnumerable<(string Character, List<Run> Runs)> ByCharacter(IEnumerable<Run> runs) =>
        runs.GroupBy(r => r.Character).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, g.ToList()));

    // ---- winrate ----

    public static Report Winrate(IReadOnlyList<Run> all, Filters f, IGameInfo game)
    {
        var sections = new List<Section>();
        foreach (var coop in new[] { false, true })
        {
            var runs = all.Where(r => r.Coop == coop && MatchesMode(r, f)).ToList();
            var groups = ByCharacter(runs).ToList();
            var rows = groups.Select(g =>
            {
                int w = g.Runs.Count(r => r.Win), l = g.Runs.Count - w;
                return new[] { game.CharacterName(g.Character), w.ToString(), l.ToString(), Pct(w, w + l) };
            }).ToList();
            sections.Add(new Section(
                $"{(coop ? "Coop" : "Solo")} - {Summary(runs)}",
                rows.Count == 0 ? ["(aucune partie)"] : [],
                rows.Count == 0 ? [] : [new Table(null, ["Personnage", "V", "D", "Win%"], rows, groups.Select(g => (ItemRef?)new ItemRef(ItemKind.Character, g.Character)).ToList())]));
        }
        var label = string.Join(", ", new[] { AscensionLabel(f.Ascension), f.GameMode }.Where(p => p != null));
        return new Report($"Victoires - {label}", [], sections);
    }

    // ---- cartes et reliques ----

    private static readonly string[] ChoiceHeaders = ["Parties", "Win%", "Proposé", "Pick%", "Win% pris", "Win% passé"];

    /// <summary>Par élément (carte, relique) : [victoires, parties] en fin de partie, quand proposé, quand pris.</summary>
    private sealed class Choices
    {
        private readonly Dictionary<string, int[]> _final = new(), _offered = new(), _picked = new();

        private static int[] Get(Dictionary<string, int[]> d, string k) =>
            d.TryGetValue(k, out var v) ? v : d[k] = new int[2];

        public void AddRun(Run run, IEnumerable<string> final, IEnumerable<string> offered, IEnumerable<string> picked)
        {
            foreach (var (stats, items) in new[] { (_final, final), (_offered, offered), (_picked, picked) })
                foreach (var item in items.Distinct())
                {
                    var s = Get(stats, item);
                    s[0] += run.Win ? 1 : 0;
                    s[1]++;
                }
        }

        public List<string[]> Rows(int min, SortKey sort)
        {
            var rows = new List<((double, double) Key, string[] Row)>();
            foreach (var item in _final.Keys.Union(_offered.Keys))
            {
                var (fw, fn) = Pair(_final, item);
                var (ow, on) = Pair(_offered, item);
                var (pw, pn) = Pair(_picked, item);
                // Le seuil s'applique à l'échantillon sur lequel porte le tri
                var sample = sort switch { SortKey.Win => fn, SortKey.N => Math.Max(fn, on), _ => on };
                if (sample < min)
                    continue;
                (double, double) key = sort switch
                {
                    SortKey.Win => (fn > 0 ? (double)fw / fn : -1, fn),
                    SortKey.N => (fn, on),
                    _ => (on > 0 ? (double)pn / on : -1, on),
                };
                rows.Add((key, [item, fn.ToString(), Pct(fw, fn), on.ToString(), Pct(pn, on), Pct(pw, pn), Pct(ow - pw, on - pn)]));
            }
            // Tri décroissant, puis par identifiant pour un ordre stable
            return rows.OrderByDescending(r => r.Key.Item1).ThenByDescending(r => r.Key.Item2)
                .ThenBy(r => r.Row[0], StringComparer.Ordinal).Select(r => r.Row).ToList();
        }

        private static (int W, int N) Pair(Dictionary<string, int[]> d, string k) =>
            d.TryGetValue(k, out var v) ? (v[0], v[1]) : (0, 0);
    }

    /// <summary>(clé de tri du groupe, titre du groupe, nom affiché), ou null pour masquer l'élément.</summary>
    private delegate (string Order, string Title, string Name)? Grouping(string item, string character);

    private static Report ByCharacterChoices(
        string title, List<string> notes, IReadOnlyList<Run> all, Filters f, IGameInfo game, string column,
        Func<Run, IEnumerable<(string, bool)>> choices, Func<Run, IEnumerable<string>> final, Grouping group,
        ItemKind? rowKind = null)
    {
        var (runs, label) = Select(all, f, game);
        var sections = new List<Section>();
        string[] headers = [column, .. ChoiceHeaders];
        foreach (var (character, charRuns) in ByCharacter(runs))
        {
            var stats = new Choices();
            foreach (var run in charRuns)
            {
                var c = choices(run).ToList();
                stats.AddRun(run, final(run), c.Select(x => x.Item1), c.Where(x => x.Item2).Select(x => x.Item1));
            }
            var groups = new SortedDictionary<string, (string Title, List<string[]> Rows, List<ItemRef?> Items)>(StringComparer.Ordinal);
            foreach (var row in stats.Rows(f.Min, f.Sort))
            {
                if (group(row[0], character) is not { } g)
                    continue;
                if (!groups.TryGetValue(g.Order, out var entry))
                    groups[g.Order] = entry = (g.Title, [], []);
                entry.Items.Add(rowKind is { } kind ? new ItemRef(kind, row[0]) : null);
                row[0] = g.Name;
                entry.Rows.Add(row);
            }
            sections.Add(new Section(
                $"{game.CharacterName(character)} - {Summary(charRuns)}",
                groups.Count == 0 ? [NoRows] : [],
                groups.Values.Select(g => new Table(g.Title, headers, g.Rows, rowKind == null ? null : g.Items)).ToList(),
                new ItemRef(ItemKind.Character, character)));
        }
        if (sections.Count == 0)
            notes.Add("(aucune partie)");
        return new Report($"{title} - {label}", notes, sections);
    }

    private static readonly Dictionary<string, (string Order, string Title)> PoolGroups = new()
    {
        ["COLORLESS"] = ("2", "Incolores"),
        ["EVENT"] = ("4", "Événements"),
        ["QUEST"] = ("5", "Quêtes"),
        ["TOKEN"] = ("6", "Tokens"),
        ["STATUS"] = ("7", "Statuts"),
        ["CURSE"] = ("8", "Malédictions"),
    };

    public static Report Cards(IReadOnlyList<Run> all, Filters f, IGameInfo game)
    {
        var notes = new List<string>
        {
            "Parties : dans le deck final ; Proposé/Pick% : récompenses de cartes ; passé : proposée mais jamais prise",
        };
        if (!game.HasCardPools)
            notes.Add("(pools de cartes inconnus : les cartes ne sont pas regroupées)");
        Grouping group = (card, character) =>
        {
            if (!game.HasCardPools)
                return ("0", "Cartes", game.CardName(card));
            var pool = game.CardPool(card);
            if (pool == null)
                return null; // carte retirée du jeu
            if (pool == character)
                return ("1", $"Cartes {game.CharacterName(character)}", game.CardName(card));
            if (game.Characters.Contains(pool))
                return ("3", "Autres personnages", $"{game.CardName(card)} ({game.CharacterName(pool)})");
            var (order, title) = PoolGroups.GetValueOrDefault(pool, ("9 " + pool, pool));
            return (order, title, game.CardName(card));
        };
        return ByCharacterChoices("Cartes", notes, all, f, game, "Carte",
            r => r.CardChoices(), r => r.DeckCards, group);
    }

    public static Report Relics(IReadOnlyList<Run> all, Filters f, IGameInfo game)
    {
        var notes = new List<string>
        {
            "Parties : possédée en fin de partie ; Proposé/Pick% : choix des Anciens ; passé : proposée mais jamais prise",
        };
        return ByCharacterChoices("Reliques", notes, all, f, game, "Relique",
            r => r.AncientChoices(), r => r.Relics, (relic, _) => ("0", "Reliques", game.RelicName(relic)), ItemKind.Relic);
    }

    // ---- anciens ----

    private static readonly string[] AncientHeaders = ["Option", "Proposé", "Pick%", "Win% pris", "Win% passé"];

    /// <summary>
    /// Une section par Ancien du jeu, avec toutes les options qu'il peut proposer, même jamais vues.
    /// Les comptes sont faits par partie : une option proposée deux fois dans la même partie compte une fois.
    /// </summary>
    public static Report Ancients(IReadOnlyList<Run> all, Filters f, IGameInfo game)
    {
        var (runs, label) = Select(all, f, game);
        // Par Ancien : parties où il a été rencontré, et [victoires, parties] par option proposée / prise
        var met = new Dictionary<string, List<Run>>();
        var offered = new Dictionary<(string, string), int[]>();
        var picked = new Dictionary<(string, string), int[]>();
        foreach (var run in runs)
        {
            var encounters = run.AncientEncounters().ToList();
            foreach (var ancient in encounters.Select(e => e.Ancient).Distinct())
                (met.TryGetValue(ancient, out var l) ? l : met[ancient] = []).Add(run);
            var choices = encounters.SelectMany(e => e.Choices.Select(c => (e.Ancient, c.Key, c.Picked))).ToList();
            foreach (var (stats, items) in new[]
                     {
                         (offered, choices.Select(c => (c.Ancient, c.Key))),
                         (picked, choices.Where(c => c.Picked).Select(c => (c.Ancient, c.Key))),
                     })
                foreach (var item in items.Distinct())
                {
                    var s = stats.TryGetValue(item, out var v) ? v : stats[item] = new int[2];
                    s[0] += run.Win ? 1 : 0;
                    s[1]++;
                }
        }

        // Anciens triés par acte : ceux d'un seul acte dans l'ordre des actes, puis les Anciens partagés entre
        // plusieurs actes, puis ceux qui n'existent plus mais figurent dans l'historique
        var known = game.Ancients.Select(a => a.Id).ToHashSet();
        var ancients = game.Ancients
            .OrderBy(a => a.Acts.Count == 0 ? 2 : a.Acts.Count > 1 ? 1 : 0)
            .ThenBy(a => a.Acts.Count == 0 ? 0 : a.Acts.Min())
            .Concat(met.Keys.Where(id => !known.Contains(id)).Order(StringComparer.Ordinal)
                .Select(id => new AncientInfo(id, [], [])))
            .ToList();

        var sections = new List<Section>();
        foreach (var ancient in ancients)
        {
            var options = ancient.Options.ToList();
            var keys = options.Select(o => o.Key).ToHashSet();
            options.AddRange(offered.Keys.Where(k => k.Item1 == ancient.Id && !keys.Contains(k.Item2))
                .Select(k => k.Item2).Order(StringComparer.Ordinal)
                .Select(key => new AncientOption(key, game.RelicName(key), key)));

            var rows = new List<((double, double) Key, string[] Row, ItemRef? Item, string? Tooltip)>();
            foreach (var option in options)
            {
                var (ow, on) = offered.TryGetValue((ancient.Id, option.Key), out var o) ? (o[0], o[1]) : (0, 0);
                var (pw, pn) = picked.TryGetValue((ancient.Id, option.Key), out var p) ? (p[0], p[1]) : (0, 0);
                (double, double) key = f.Sort switch
                {
                    SortKey.Win => (pn > 0 ? (double)pw / pn : -1, pn),
                    SortKey.N => (on, pn),
                    _ => (on > 0 ? (double)pn / on : -1, on),
                };
                rows.Add((key,
                    [option.Name, on.ToString(), Pct(pn, on), Pct(pw, pn), Pct(ow - pw, on - pn)],
                    option.RelicId == null ? null : new ItemRef(ItemKind.Relic, option.RelicId),
                    option.Description));
            }
            rows = rows.OrderByDescending(r => r.Key.Item1).ThenByDescending(r => r.Key.Item2)
                .ThenBy(r => r.Row[0], StringComparer.Ordinal).ToList();

            var metRuns = met.GetValueOrDefault(ancient.Id) ?? [];
            sections.Add(new Section(
                $"{game.AncientName(ancient.Id)} - rencontré dans {Summary(metRuns)}",
                rows.Count == 0 ? ["(aucune option connue)"] : [],
                rows.Count == 0 ? [] : [new Table(null, AncientHeaders, rows.Select(r => r.Row).ToList(),
                    rows.Select(r => r.Item).ToList(), rows.Select(r => r.Tooltip).ToList())],
                new ItemRef(ItemKind.Ancient, ancient.Id),
                ancient.ActLabel ?? ActLabel(ancient.Acts)));
        }
        var notes = new List<string>
        {
            "Proposé : parties où l'option a été proposée ; Win% pris / passé : victoires quand elle a été prise / proposée mais pas prise",
        };
        return new Report($"Anciens - {label}", notes, sections);
    }

    /// <summary>[1] -> « Acte 1 », [2, 3] -> « Actes 2 et 3 », [] -> « Autres »</summary>
    public static string ActLabel(IReadOnlyList<int> acts) => acts.Count switch
    {
        0 => "Autres",
        1 => $"Acte {acts[0]}",
        _ => $"Actes {string.Join(", ", acts.Order().SkipLast(1))} et {acts.Max()}",
    };

    // ---- défaites ----

    public static string KillerName(Run run, IGameInfo game) => run.Killer switch
    {
        ("ENCOUNTER", var id) => game.EncounterName(id),
        (_, var id) => game.EventName(id),
        null => "(abandon hors combat)",
    };

    public static Report Deaths(IReadOnlyList<Run> all, Filters f, IGameInfo game)
    {
        var (runs, label) = Select(all, f, game);
        var sections = new List<Section>();
        string[] tail = ["Défaites", "%", "dont abandons"];
        foreach (var (character, charRuns) in ByCharacter(runs))
        {
            var losses = charRuns.Where(r => !r.Win).ToList();
            var tables = new List<Table>();
            foreach (var act in losses.GroupBy(r => r.Act).OrderBy(g => g.Key))
            {
                var actLosses = act.ToList();
                List<string[]> Rows<TKey>(Func<Run, TKey> key, bool byCount) where TKey : notnull
                {
                    var stats = actLosses.GroupBy(key).Select(g => (Key: g.Key, N: g.Count(), Ab: g.Count(r => r.Abandoned)));
                    stats = byCount ? stats.OrderByDescending(s => s.N).ThenBy(s => s.Key.ToString(), StringComparer.Ordinal) : stats.OrderBy(s => s.Key);
                    return stats.Where(s => s.N >= f.Min)
                        .Select(s => new[] { s.Key.ToString()!, s.N.ToString(), Pct(s.N, losses.Count), s.Ab.ToString() }).ToList();
                }
                var abandons = actLosses.Count(r => r.Abandoned);
                tables.Add(new Table(
                    $"Acte {act.Key} - {actLosses.Count} défaites ({Pct(actLosses.Count, losses.Count)}), dont {abandons} abandons",
                    ["Tué par", .. tail], Rows(r => KillerName(r, game), true)));
                tables.Add(new Table(null, ["Étage", .. tail], Rows(r => r.Floor, false)));
            }
            sections.Add(new Section(
                $"{game.CharacterName(character)} - {Summary(charRuns)}",
                [$"{losses.Count} défaites, dont {losses.Count(r => r.Abandoned)} abandons"],
                tables,
                new ItemRef(ItemKind.Character, character)));
        }
        return new Report($"Défaites - {label}", sections.Count == 0 ? ["(aucune partie)"] : [], sections);
    }
}
