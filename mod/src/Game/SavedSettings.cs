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

    /// <summary>Rapports de plantage : « ask » (demander au menu principal), « always » (envoyer sans demander) ou « never »</summary>
    public string CrashReports { get; set; } = "ask";

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
