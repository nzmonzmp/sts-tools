namespace StsTools.Analysis;

/// <summary>Résultat d'une analyse, indépendant de l'affichage (UI du jeu ou console).</summary>
public sealed record Report(string Title, List<string> Notes, List<Section> Sections);

/// <param name="Item">élément du jeu dont parle la section (pour afficher son image), ou null</param>
/// <param name="Group">groupe de la section (par exemple l'acte d'un Ancien) : un bandeau est affiché à chaque changement</param>
public sealed record Section(string Title, List<string> Lines, List<Table> Tables, ItemRef? Item = null, string? Group = null);

/// <summary>Tableau : la première colonne est alignée à gauche, les autres à droite.</summary>
/// <param name="RowItems">élément du jeu nommé dans la première colonne de chaque ligne (pour afficher son image)</param>
/// <param name="RowTooltips">texte affiché au survol de la première colonne de chaque ligne</param>
public sealed record Table(string? Title, string[] Headers, List<string[]> Rows, List<ItemRef?>? RowItems = null,
    List<string?>? RowTooltips = null);

public enum ItemKind { Character, Relic, Ancient, Card }

/// <summary>Référence à un élément du jeu (personnage, relique) par son identifiant.</summary>
public sealed record ItemRef(ItemKind Kind, string Id);

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

    /// <summary>afficher les reliques d'Anciens dans l'onglet Reliques</summary>
    public bool ShowAncientRelics { get; init; } = true;
}

/// <summary>Informations venant du jeu : noms affichés et pools de cartes.</summary>
public interface IGameInfo
{
    string CharacterName(string id);
    string CardName(string id);
    string RelicName(string id);

    /// <summary>Effet de la relique en texte brut, ou null si inconnu.</summary>
    string? RelicDescription(string id);

    /// <summary>Vrai pour une relique proposée par les Anciens (rareté Ancient).</summary>
    bool IsAncientRelic(string id);
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

    /// <summary>Anciens du jeu, dans l'ordre des actes, avec toutes les options qu'ils peuvent proposer.</summary>
    IReadOnlyList<AncientInfo> Ancients { get; }

    string AncientName(string id);
}

/// <param name="Key">clé de l'option dans les fichiers .run (TextKey)</param>
/// <param name="RelicId">relique obtenue avec cette option, pour son icône</param>
/// <param name="Description">effet de l'option, en texte brut</param>
public sealed record AncientOption(string Key, string Name, string? RelicId, string? Description = null);

/// <param name="Acts">numéros des actes où l'Ancien peut apparaître (1 = premier acte)</param>
/// <param name="ActLabel">libellé de ces actes, par exemple avec leur nom ; null pour un libellé par défaut</param>
public sealed record AncientInfo(string Id, IReadOnlyList<AncientOption> Options, IReadOnlyList<int> Acts, string? ActLabel = null);

/// <summary>Sans le jeu : identifiants bruts.</summary>
public class RawGameInfo : IGameInfo
{
    private readonly IReadOnlyDictionary<string, string>? _pools;

    public RawGameInfo(IReadOnlyDictionary<string, string>? pools = null) => _pools = pools;

    public virtual string CharacterName(string id) => id;
    public virtual string CardName(string id) => id;
    public virtual string RelicName(string id) => id;
    public virtual string? RelicDescription(string id) => null;
    public virtual bool IsAncientRelic(string id) => false;
    public virtual string EncounterName(string id) => id;
    public virtual string EventName(string id) => "EVENT " + id;
    public bool HasCardPools => _pools is { Count: > 0 };
    public string? CardPool(string id) => _pools?.GetValueOrDefault(id);

    public IReadOnlySet<string> Characters { get; } =
        new HashSet<string> { "DEFECT", "IRONCLAD", "NECROBINDER", "REGENT", "SILENT" };

    public IReadOnlyList<AncientInfo> Ancients { get; } = [];
    public virtual string AncientName(string id) => id;
}
