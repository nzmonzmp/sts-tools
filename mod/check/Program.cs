// Usage : dotnet run -- <winrate|cards|relics|deaths> <dossier steam/<id>> [--coop] [-a N|all] [-c perso] [-m mode] [-n N] [-s win|n|pick]
using System.Text.Json;
using StsTools.Analysis;

var cmd = args[0];
var steamDir = args[1];
var f = new Filters();
for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--coop": f = f with { Coop = true }; break;
        case "-a": f = f with { Ascension = args[++i] == "all" ? null : int.Parse(args[i]) }; break;
        case "-c": f = f with { Character = args[++i].ToUpperInvariant() }; break;
        case "-m": f = f with { GameMode = args[++i] }; break;
        case "-n": f = f with { Min = int.Parse(args[++i]) }; break;
        case "-s": f = f with { Sort = Enum.Parse<SortKey>(args[++i], true) }; break;
    }
}

var steamId = ulong.Parse(Path.GetFileName(Path.TrimEndingDirectorySeparator(steamDir)));
var errors = new List<string>();
var runs = RunLoader.Load(
    [Path.Combine(steamDir, "profile1/saves/history"), Path.Combine(steamDir, "modded/profile1/saves/history")],
    steamId, errors);
errors.ForEach(e => Console.Error.WriteLine(e));

var poolsJson = Path.Combine(AppContext.BaseDirectory, "../../../../../src/sts_tools/card_pools.json");
var pools = File.Exists(poolsJson) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(poolsJson)) : null;
var game = new RawGameInfo(pools);

var report = cmd switch
{
    "winrate" => Stats.Winrate(runs, f, game),
    "cards" => Stats.Cards(runs, f, game),
    "relics" => Stats.Relics(runs, f, game),
    "ancients" => Stats.Ancients(runs, f, game),
    "deaths" => Stats.Deaths(runs, f, game),
    _ => throw new ArgumentException(cmd),
};

Console.WriteLine($"== {report.Title} ==");
report.Notes.ForEach(Console.WriteLine);
string? group = null;
foreach (var s in report.Sections)
{
    if (s.Group != null && s.Group != group) Console.WriteLine($"\n######## {group = s.Group} ########");
    Console.WriteLine($"\n-- {s.Title} --");
    s.Lines.ForEach(Console.WriteLine);
    foreach (var t in s.Tables)
    {
        if (t.Title != null) Console.WriteLine($"\n[{t.Title}]");
        if (t.Subtitle != null) Console.WriteLine($"[{t.Subtitle}]");
        if (t.Rows.Count == 0) { Console.WriteLine(Stats.NoRows); continue; }
        var widths = t.Headers.Select((h, i) => t.Rows.Select(r => r[i].Length).Append(h.Length).Max()).ToArray();
        string Fmt(string[] r) => string.Join("  ", r.Select((x, i) => i == 0 ? x.PadRight(widths[i]) : x.PadLeft(widths[i])));
        Console.WriteLine(Fmt(t.Headers));
        t.Rows.ForEach(r => Console.WriteLine(Fmt(r)));
    }
}
