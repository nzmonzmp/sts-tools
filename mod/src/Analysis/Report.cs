namespace StsTools.Analysis;

/// <summary>Résultat d'une analyse, indépendant de l'affichage (UI du jeu ou console).</summary>
public sealed record Report(string Title, List<string> Notes, List<Section> Sections);

public sealed record Section(string Title, List<string> Lines, List<Table> Tables);

/// <summary>Tableau : la première colonne est alignée à gauche, les autres à droite.</summary>
public sealed record Table(string? Title, string[] Headers, List<string[]> Rows);

public enum SortKey { Win, N, Pick }

public sealed record Filters
{
    public bool Coop { get; init; }

    /// <summary>null = toutes les ascensions</summary>
    public int? Ascension { get; init; } = 10;

    /// <summary>identifiant du personnage (IRONCLAD...), null = tous</summary>
    public string? Character { get; init; }

    /// <summary>standard, blitz..., null = tous les modes</summary>
    public string? GameMode { get; init; }

    /// <summary>nombre minimum de parties pour afficher une ligne</summary>
    public int Min { get; init; } = 1;

    public SortKey Sort { get; init; } = SortKey.Win;
}

/// <summary>Informations venant du jeu : noms affichés et pools de cartes.</summary>
public interface IGameInfo
{
    string CharacterName(string id);
    string CardName(string id);
    string RelicName(string id);
    string EncounterName(string id);
    string EventName(string id);

    /// <summary>Faux si les pools de cartes sont inconnus : les cartes ne sont alors pas regroupées.</summary>
    bool HasCardPools { get; }

    /// <summary>
    /// Pool de la carte : identifiant de personnage (IRONCLAD...) ou COLORLESS, EVENT, QUEST, TOKEN, STATUS, CURSE...
    /// null si la carte n'existe plus dans le jeu.
    /// </summary>
    string? CardPool(string id);

    /// <summary>Identifiants des personnages jouables.</summary>
    IReadOnlySet<string> Characters { get; }
}

/// <summary>Sans le jeu : identifiants bruts.</summary>
public class RawGameInfo : IGameInfo
{
    private readonly IReadOnlyDictionary<string, string>? _pools;

    public RawGameInfo(IReadOnlyDictionary<string, string>? pools = null) => _pools = pools;

    public virtual string CharacterName(string id) => id;
    public virtual string CardName(string id) => id;
    public virtual string RelicName(string id) => id;
    public virtual string EncounterName(string id) => id;
    public virtual string EventName(string id) => "EVENT " + id;
    public bool HasCardPools => _pools is { Count: > 0 };
    public string? CardPool(string id) => _pools?.GetValueOrDefault(id);

    public IReadOnlySet<string> Characters { get; } =
        new HashSet<string> { "DEFECT", "IRONCLAD", "NECROBINDER", "REGENT", "SILENT" };
}
