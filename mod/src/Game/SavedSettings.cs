using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace StsTools.Game;

/// <summary>
/// Réglages de l'écran de statistiques (onglet, filtres, colonnes masquées), conservés entre les parties
/// et les redémarrages du jeu dans user://StsTools/settings.json.
/// </summary>
public sealed class SavedSettings
{
    public string Tab { get; set; } = "Winrate";
    public bool Coop { get; set; }

    /// <summary>« all » ou un niveau d'ascension</summary>
    public string Ascension { get; set; } = "10";

    public string? Character { get; set; }
    public string? GameMode { get; set; }
    public int Min { get; set; } = 1;
    public string Sort { get; set; } = "Win";
    public bool ShowAncientRelics { get; set; } = true;

    /// <summary>Colonnes masquées, au format « onglet/colonne » (ex. « Cards/Copies »)</summary>
    public List<string> HiddenColumns { get; set; } = [];

    /// <summary>Onglet affiché dans le panneau de stats en partie (RunPanel)</summary>
    public string? RunPanelTab { get; set; }

    /// <summary>Position du panneau de stats en partie (écart au bord droit, haut) et hauteur de son contenu ; null : par défaut</summary>
    public float? RunPanelRight { get; set; }
    public float? RunPanelTop { get; set; }
    public float? RunPanelHeight { get; set; }

    /// <summary>Panneau de stats en partie replié (seulement son en-tête)</summary>
    public bool RunPanelCollapsed { get; set; }

    /// <summary>Sous-onglet choisi dans chaque onglet du panneau, par titre d'onglet</summary>
    public Dictionary<string, string> RunPanelSubTabs { get; set; } = [];

    /// <summary>
    /// Chercheur de chemins sur la carte (MapPaths) : types de salles à maximiser (Shop, Elite, RestSite, Unknown,
    /// Monster), le plus important d'abord ; null : jamais choisi (par défaut)
    /// </summary>
    public List<string>? MapPathRanked { get; set; }

    /// <summary>Chercheur de chemins : nombre exact voulu par type de salle (Shop, Elite, RestSite, Monster, Unknown) ; absent : n'importe lequel</summary>
    public Dictionary<string, int> MapPathCounts { get; set; } = [];

    /// <summary>
    /// Chercheur de chemins : règle de chaque type du classement (« max », « min », « =3 », « >=3 ») ; null : réglages
    /// d'avant les règles (MapPathCounts)
    /// </summary>
    public Dictionary<string, string>? MapPathRules { get; set; }

    /// <summary>Chercheur de chemins : un seul des chemins à égalité tracé à la fois (par défaut : tous)</summary>
    public bool MapPathShowOne { get; set; }

    /// <summary>Rapports de plantage : « ask » (demander au menu principal), « always » (envoyer sans demander) ou « never »</summary>
    public string CrashReports { get; set; } = "ask";

    /// <summary>Nom donné dans le dernier rapport de bug (Report a bug), proposé la fois suivante</summary>
    public string? BugReportName { get; set; }

    // ---- Options du mod (options du jeu, onglet Mod ; voir ModOptions) ----

    /// <summary>GUI ouvert tout seul au lancement ou à la reprise d'une partie</summary>
    public bool OpenOnRunStart { get; set; }

    /// <summary>Onglets du GUI cachés (titres : « Deck », « Path finder »…)</summary>
    public List<string> HiddenTabs { get; set; } = [];

    /// <summary>Onglet affiché à l'ouverture du GUI ; null : le dernier choisi</summary>
    public string? OpenTab { get; set; }

    /// <summary>Panneau à l'ouverture du GUI : « last » (comme il a été laissé), « expanded » ou « collapsed »</summary>
    public string OpenPanel { get; set; } = "last";

    /// <summary>GUI ouvert : aperçu des cartes jouées et des potions (TargetPreview)</summary>
    public bool CardPreview { get; set; } = true;

    /// <summary>GUI ouvert : têtes de mort (LethalWarning)</summary>
    public bool Skulls { get; set; } = true;

    /// <summary>GUI ouvert : stats sous la description des reliques (RelicHover)</summary>
    public bool RelicStats { get; set; } = true;

    /// <summary>GUI ouvert : aperçu des salles au survol sur la carte (MapRoomPreview)</summary>
    public bool RoomPreview { get; set; } = true;

    /// <summary>Report a bug : case « joindre les logs et la partie » cochée par défaut</summary>
    public bool BugReportAttach { get; set; } = true;

    /// <summary>Touches du mod (noms des touches Godot : « F7 »), réglables dans les options du jeu (Input)</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = [];

    private static string FilePath => ProjectSettings.GlobalizePath("user://StsTools/settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Réglages sauvegardés, ou réglages par défaut si le fichier est absent ou illisible.</summary>
    public static SavedSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(FilePath), Json) ?? new SavedSettings();
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot read settings, using defaults: {e.Message}");
        }
        return new SavedSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot save settings: {e.Message}");
        }
    }
}
