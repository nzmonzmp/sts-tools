using System.Text.Json;

namespace StsTools.Analysis;

/// <summary>Une partie terminée, lue depuis un fichier .run de l'historique.</summary>
public sealed class Run
{
    public JsonElement Data { get; }

    /// <summary>Entrée de <c>players</c> correspondant au joueur local.</summary>
    public JsonElement Me { get; }

    public Run(JsonElement data, JsonElement me)
    {
        Data = data;
        Me = me;
    }

    public bool Coop => Data.GetProperty("players").GetArrayLength() > 1;
    public bool Win => Bool(Data, "win");
    public bool Abandoned => Bool(Data, "was_abandoned");
    public int? Ascension => Data.TryGetProperty("ascension", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : null;
    public string GameMode => Str(Data, "game_mode") ?? "standard";
    public string Character => Ids.Short(Str(Me, "character") ?? "?");

    private IEnumerable<JsonElement> Acts => Array(Data, "map_point_history");
    public int Act => Acts.Count();
    public int Floor => Acts.Sum(act => act.GetArrayLength());

    /// <summary>Stats du joueur local pour chaque étage visité.</summary>
    public IEnumerable<JsonElement> PlayerStats() =>
        Acts.SelectMany(act => act.EnumerateArray()).SelectMany(MyStats);

    /// <summary>Stats du joueur local à un étage.</summary>
    private IEnumerable<JsonElement> MyStats(JsonElement point)
    {
        var myId = Me.TryGetProperty("id", out var id) ? id.ToString() : null;
        return Array(point, "player_stats")
            .Where(ps => ps.TryGetProperty("player_id", out var pid) && pid.ToString() == myId);
    }

    /// <summary>Rencontres avec un Ancien : son identifiant, et les options proposées au joueur local.</summary>
    public IEnumerable<(string Ancient, List<(string Key, bool Picked)> Choices)> AncientEncounters()
    {
        foreach (var point in Acts.SelectMany(act => act.EnumerateArray()))
        {
            if (Str(point, "map_point_type") != "ancient")
                continue;
            var room = Array(point, "rooms").Select(r => Str(r, "model_id")).FirstOrDefault(m => m != null);
            if (room == null)
                continue;
            var choices = (from ps in MyStats(point)
                           from choice in Array(ps, "ancient_choice")
                           let key = Str(choice, "TextKey")
                           where key != null
                           select (key, Bool(choice, "was_chosen"))).ToList();
            yield return (Ids.Short(room), choices);
        }
    }

    public IEnumerable<string> DeckCards => Array(Me, "deck").Select(c => Ids.Short(Str(c, "id") ?? "?"));

    /// <summary>Cartes du deck final dont au moins un exemplaire est amélioré.</summary>
    public IEnumerable<string> UpgradedDeckCards => Array(Me, "deck")
        .Where(c => c.TryGetProperty("current_upgrade_level", out var l) && l.ValueKind == JsonValueKind.Number && l.GetInt32() > 0)
        .Select(c => Ids.Short(Str(c, "id") ?? "?"));
    public IEnumerable<string> Relics => Array(Me, "relics").Select(r => Ids.Short(Str(r, "id") ?? "?"));

    /// <summary>Cartes proposées en récompense, et si elles ont été prises.</summary>
    public IEnumerable<(string Card, bool Picked)> CardChoices() =>
        from ps in PlayerStats()
        from choice in Array(ps, "card_choices")
        let card = choice.TryGetProperty("card", out var c) ? Str(c, "id") : null
        where card != null
        select (Ids.Short(card), Bool(choice, "was_picked"));

    /// <summary>Choix proposés par les Anciens (des reliques), et si ils ont été choisis.</summary>
    public IEnumerable<(string Relic, bool Picked)> AncientChoices() =>
        from ps in PlayerStats()
        from choice in Array(ps, "ancient_choice")
        let key = Str(choice, "TextKey")
        where key != null
        select (key, Bool(choice, "was_chosen"));

    /// <summary>(catégorie, identifiant) de ce qui a tué le joueur, ou null pour un abandon hors combat.</summary>
    public (string Kind, string Id)? Killer
    {
        get
        {
            var enc = Str(Data, "killed_by_encounter");
            if (enc != null && enc != "NONE.NONE")
                return ("ENCOUNTER", Ids.Short(enc));
            var ev = Str(Data, "killed_by_event");
            if (ev != null && ev != "NONE.NONE")
                return ("EVENT", Ids.Short(ev));
            return null;
        }
    }

    internal static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;

    internal static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var b) && b.ValueKind == JsonValueKind.True;
}

public static class Ids
{
    /// <summary>CARD.CLAW -> CLAW</summary>
    public static string Short(string id)
    {
        var i = id.IndexOf('.');
        return i < 0 ? id : id[(i + 1)..];
    }
}
