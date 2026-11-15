using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using StsTools.Analysis;

namespace StsTools.Game;

/// <summary>Noms traduits et pools de cartes, lus dans la base de modèles du jeu.</summary>
public sealed class GameInfo : IGameInfo
{
    private readonly Dictionary<string, string> _pools = new();
    private readonly Dictionary<(string, string), string> _names = new();
    private readonly Dictionary<string, Texture2D?> _icons = new();

    public IReadOnlySet<string> Characters { get; }

    private List<AncientInfo>? _ancients;

    public IReadOnlyList<AncientInfo> Ancients => _ancients ??= LoadAncients();

    /// <summary>
    /// Options possibles de chaque Ancien. La clé est calculée comme le jeu le fait pour l'historique
    /// (AncientChoiceHistoryEntry.TextKey) : avant-dernière partie de la clé de traduction du titre.
    /// </summary>
    private static List<AncientInfo> LoadAncients()
    {
        // Actes où chaque Ancien peut apparaître. Les Anciens partagés (Darv) sont répartis au hasard
        // dans les actes après le premier (RunManager.GenerateRooms).
        var acts = ModelDb.Acts.Where(a => a.Index >= 0).ToList();
        var actCount = acts.Max(a => a.Index) + 1;
        var shared = ModelDb.AllSharedAncients.ToHashSet();
        string ActNames(int index) => string.Join(" / ", acts.Where(a => a.Index == index)
            .Select(a => SafeText(() => a.Title.GetFormattedText()) ?? a.Id.Entry));

        var ancients = new List<AncientInfo>();
        foreach (var ancient in ModelDb.AllAncients)
        {
            List<int> ancientActs = shared.Contains(ancient)
                ? Enumerable.Range(2, actCount - 1).ToList()
                : acts.Where(a => a.AllAncients.Contains(ancient)).Select(a => a.Index + 1).Distinct().Order().ToList();
            var actLabel = ancientActs.Count == 1
                ? $"{Stats.ActLabel(ancientActs)} · {ActNames(ancientActs[0] - 1)}"
                : ancientActs.Count > 1 ? $"{Stats.ActLabel(ancientActs)} · Ancien partagé" : null;

            var options = new List<AncientOption>();
            try
            {
                foreach (var option in ancient.AllPossibleOptions)
                {
                    var parts = option.Title.LocEntryKey.Split('.');
                    if (parts.Length < 2 || options.Any(o => o.Key == parts[^2]))
                        continue;
                    var name = option.Title.GetFormattedText();
                    options.Add(new AncientOption(parts[^2], string.IsNullOrWhiteSpace(name) ? parts[^2] : name,
                        option.Relic?.CanonicalInstance?.Id.Entry,
                        SafeText(() => PlainText(option.Description.GetFormattedText()))));
                }
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] options de {ancient.Id} illisibles : {e.Message}");
            }
            ancients.Add(new AncientInfo(ancient.Id.Entry, options, ancientActs, actLabel));
        }
        return ancients;
    }

    private static string? SafeText(Func<string> text)
    {
        try
        {
            var s = text();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Texte de jeu (BBCode : [gold]...[/gold], [img]...[/img]) en texte brut pour une bulle d'aide.
    /// Les icônes d'énergie et d'étoile deviennent des mots.
    /// </summary>
    private static string PlainText(string bbcode)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(bbcode, @"\[img[^\]]*\](.*?)\[/img\]", m =>
            m.Groups[1].Value.Contains("energy", StringComparison.OrdinalIgnoreCase) ? "énergie"
            : m.Groups[1].Value.Contains("star", StringComparison.OrdinalIgnoreCase) ? "étoile"
            : "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[/?[a-zA-Z_]+[^\]]*\]", "");
        return System.Text.RegularExpressions.Regex.Replace(s, @"[ \t]+", " ").Trim();
    }

    public string AncientName(string id) => Name<AncientEventModel>("EVENT", id, m => m.Title.GetFormattedText());

    /// <summary>Icône de l'Ancien, celle de l'historique des parties ; null si introuvable.</summary>
    public Texture2D? AncientIcon(string id)
    {
        if (_icons.TryGetValue("ANCIENT." + id, out var icon))
            return icon;
        try
        {
            icon = ModelDb.GetByIdOrNull<AncientEventModel>(new ModelId("EVENT", id))?.RunHistoryIcon;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] icône introuvable pour {id}: {e.Message}");
            icon = null;
        }
        return _icons["ANCIENT." + id] = icon;
    }

    public GameInfo()
    {
        var characters = new HashSet<string>();
        var poolOwners = new Dictionary<CardPoolModel, string>();
        foreach (var c in ModelDb.AllCharacters)
        {
            characters.Add(c.Id.Entry);
            poolOwners[c.CardPool] = c.Id.Entry;
        }
        Characters = characters;
        foreach (var card in ModelDb.AllCards)
        {
            try
            {
                var pool = card.Pool;
                _pools[card.Id.Entry] = poolOwners.TryGetValue(pool, out var owner) ? owner : PoolName(pool.Id.Entry);
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] pool inconnu pour {card.Id}: {e.Message}");
            }
        }
    }

    /// <summary>COLORLESS_CARD_POOL -> COLORLESS</summary>
    private static string PoolName(string entry) =>
        entry.EndsWith("_CARD_POOL") ? entry[..^"_CARD_POOL".Length] : entry;

    public bool HasCardPools => _pools.Count > 0;
    public string? CardPool(string id) => _pools.GetValueOrDefault(id);

    public string CharacterName(string id) => Name<CharacterModel>("CHARACTER", id, m => m.Title.GetFormattedText());
    public string CardName(string id) => Name<CardModel>("CARD", id, m => m.TitleLocString.GetFormattedText());
    public string RelicName(string id) => Name<RelicModel>("RELIC", id, m => m.Title.GetFormattedText());
    public string EncounterName(string id) => Name<EncounterModel>("ENCOUNTER", id, m => m.Title.GetFormattedText());
    public string EventName(string id) => "Événement : " + Name<EventModel>("EVENT", id, m => m.Title.GetFormattedText());

    /// <summary>Tête du personnage, telle qu'affichée en haut à gauche pendant une partie ; null si introuvable.</summary>
    public Texture2D? CharacterIcon(string id)
    {
        if (_icons.TryGetValue(id, out var icon))
            return icon;
        try
        {
            icon = ModelDb.GetByIdOrNull<CharacterModel>(new ModelId("CHARACTER", id))?.IconTexture;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] icône introuvable pour {id}: {e.Message}");
            icon = null;
        }
        return _icons[id] = icon;
    }

    /// <summary>Icône de la relique ; null si introuvable (par exemple un choix d'Ancien qui n'est pas une relique).</summary>
    public Texture2D? RelicIcon(string id)
    {
        if (_icons.TryGetValue("RELIC." + id, out var icon))
            return icon;
        try
        {
            var relic = ModelDb.GetByIdOrNull<RelicModel>(new ModelId("RELIC", id));
            icon = relic != null && ResourceLoader.Exists(relic.PackedIconPath) ? relic.Icon : null;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] icône introuvable pour {id}: {e.Message}");
            icon = null;
        }
        return _icons["RELIC." + id] = icon;
    }

    /// <summary>Couleur du nom du personnage, celle de l'écran Statistiques du jeu ; null si introuvable.</summary>
    public Color? CharacterColor(string id)
    {
        try
        {
            return ModelDb.GetByIdOrNull<CharacterModel>(new ModelId("CHARACTER", id))?.NameColor;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Nom traduit du modèle, ou son identifiant s'il n'existe plus ou n'a pas de traduction.</summary>
    private string Name<T>(string category, string id, Func<T, string> title) where T : AbstractModel
    {
        if (_names.TryGetValue((category, id), out var name))
            return name;
        try
        {
            var model = ModelDb.GetByIdOrNull<T>(new ModelId(category, id));
            name = model == null ? id : title(model);
        }
        catch (Exception)
        {
            name = id;
        }
        if (string.IsNullOrWhiteSpace(name))
            name = id;
        return _names[(category, id)] = name;
    }

    /// <summary>
    /// Dossiers d'historique du profil courant. Le jeu moddé écrit ses parties dans un dossier séparé :
    /// on lit les deux pour avoir tout l'historique.
    /// </summary>
    public static (List<string> Dirs, ulong LocalPlayerId) HistorySource()
    {
        var profile = SaveManager.Instance.CurrentProfileId;
        var account = UserDataPathProvider.GetAccountScopedBasePath(null);
        var dirs = new[] { false, true }
            .Select(modded => ProjectSettings.GlobalizePath(account.PathJoin(RunHistorySaveManager.GetHistoryPath(profile, modded))))
            .ToList();
        return (dirs, PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform));
    }
}
