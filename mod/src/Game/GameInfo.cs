using Godot;
using MegaCrit.Sts2.Core.Random;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Relics;
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

    /// <summary>Icône déjà chargée, sauf si le jeu l'a libérée entre-temps (il vide son cache de temps en temps).</summary>
    private bool TryIcon(string key, out Texture2D? icon) =>
        _icons.TryGetValue(key, out icon) && (icon == null || GodotObject.IsInstanceValid(icon));

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
                : ancientActs.Count > 1 ? $"{Stats.ActLabel(ancientActs)} · Shared Ancient" : null;

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
                Log.Warn($"[{ModEntry.Id}] cannot read options of {ancient.Id}: {e.Message}");
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
            m.Groups[1].Value.Contains("energy", StringComparison.OrdinalIgnoreCase) ? "energy"
            : m.Groups[1].Value.Contains("star", StringComparison.OrdinalIgnoreCase) ? "star"
            : "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[/?[a-zA-Z_]+[^\]]*\]", "");
        return System.Text.RegularExpressions.Regex.Replace(s, @"[ \t]+", " ").Trim();
    }

    public string AncientName(string id) => Name<AncientEventModel>("EVENT", id, m => m.Title.GetFormattedText());

    /// <summary>Icône de l'Ancien, celle de l'historique des parties ; null si introuvable.</summary>
    public Texture2D? AncientIcon(string id)
    {
        if (TryIcon("ANCIENT." + id, out var icon))
            return icon;
        try
        {
            icon = ModelDb.GetByIdOrNull<AncientEventModel>(new ModelId("EVENT", id))?.RunHistoryIcon;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] icon not found for {id}: {e.Message}");
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
                Log.Warn($"[{ModEntry.Id}] unknown pool for {card.Id}: {e.Message}");
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

    private readonly Dictionary<string, string?> _relicDescriptions = new();

    /// <summary>Effet de la relique, le texte affiché au survol en partie, en texte brut.</summary>
    public string? RelicDescription(string id)
    {
        if (_relicDescriptions.TryGetValue(id, out var text))
            return text;
        var relic = SafeModel<RelicModel>("RELIC", id);
        return _relicDescriptions[id] = relic == null ? null : SafeText(() => PlainText(relic.DynamicDescription.GetFormattedText()));
    }

    public bool IsAncientRelic(string id) => SafeModel<RelicModel>("RELIC", id)?.Rarity == RelicRarity.Ancient;

    private static T? SafeModel<T>(string category, string id) where T : AbstractModel
    {
        try
        {
            return ModelDb.GetByIdOrNull<T>(new ModelId(category, id));
        }
        catch (Exception)
        {
            return null;
        }
    }
    public string EncounterName(string id) => Name<EncounterModel>("ENCOUNTER", id, m => m.Title.GetFormattedText());
    public string EventName(string id) => "Event: " + Name<EventModel>("EVENT", id, m => m.Title.GetFormattedText());

    /// <summary>Tête du personnage, telle qu'affichée en haut à gauche pendant une partie ; null si introuvable.</summary>
    public Texture2D? CharacterIcon(string id)
    {
        if (TryIcon(id, out var icon))
            return icon;
        try
        {
            icon = ModelDb.GetByIdOrNull<CharacterModel>(new ModelId("CHARACTER", id))?.IconTexture;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] icon not found for {id}: {e.Message}");
            icon = null;
        }
        return _icons[id] = icon;
    }

    /// <summary>Icône de la relique ; null si introuvable (par exemple un choix d'Ancien qui n'est pas une relique).</summary>
    public Texture2D? RelicIcon(string id)
    {
        if (TryIcon("RELIC." + id, out var icon))
            return icon;
        try
        {
            var relic = ModelDb.GetByIdOrNull<RelicModel>(new ModelId("RELIC", id));
            icon = relic != null && ResourceLoader.Exists(relic.PackedIconPath) ? relic.Icon : null;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] icon not found for {id}: {e.Message}");
            icon = null;
        }
        return _icons["RELIC." + id] = icon;
    }

    /// <summary>Couleur du nom du personnage, celle de l'écran Statistiques du jeu ; null si introuvable.</summary>
    /// <summary>
    /// Icône d'une rencontre, celle de l'écran Historique du jeu : la tête du boss pour un boss,
    /// l'icône générique d'élite ou de monstre sinon.
    /// </summary>
    public Texture2D? EncounterIcon(string id)
    {
        if (TryIcon("ENCOUNTER." + id, out var icon))
            return icon;
        var encounter = SafeModel<EncounterModel>("ENCOUNTER", id);
        string? path = null;
        try
        {
            path = encounter?.RoomType switch
            {
                RoomType.Boss => ImageHelper.GetRoomIconPath(MapPointType.Boss, RoomType.Boss, encounter.Id),
                RoomType.Elite => ImageHelper.GetRoomIconPath(MapPointType.Elite, RoomType.Elite, null),
                RoomType.Monster => ImageHelper.GetRoomIconPath(MapPointType.Monster, RoomType.Monster, null),
                _ => null,
            };
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] icon not found for {id}: {e.Message}");
        }
        return _icons["ENCOUNTER." + id] = LoadTexture(path);
    }

    /// <summary>Icône générique d'événement de l'écran Historique.</summary>
    public Texture2D? EventIcon() =>
        TryIcon("EVENT", out var icon) ? icon
            : _icons["EVENT"] = LoadTexture(ImageHelper.GetRoomIconPath(MapPointType.Unknown, RoomType.Event, null));

    private static Texture2D? LoadTexture(string? path)
    {
        try
        {
            return path != null && ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Composition d'une rencontre comme en combat : une copie de la rencontre (pour sa scène d'emplacements,
    /// si elle en a une) et ses monstres avec leur emplacement. GenerateMonsters est protégée et tire ses
    /// variantes au hasard : on lui donne un générateur aléatoire et on l'appelle par réflexion.
    /// En cas d'échec, tous les monstres possibles, sans emplacement. null si la rencontre n'existe plus.
    /// </summary>
    public (EncounterModel Encounter, List<(MonsterModel Monster, string? Slot)> Monsters)? EncounterPreview(string id) =>
        SafeModel<EncounterModel>("ENCOUNTER", id) is { } canonical ? EncounterVisuals.Generate(canonical) : null;

    /// <summary>Couleur d'une carte de personnage : celle du nom du personnage ; null pour les autres pools.</summary>
    public Color? CardColor(string id) =>
        CardPool(id) is { } pool && Characters.Contains(pool) ? CharacterColor(pool) : null;

    /// <summary>Modèle de la carte (pour l'afficher), ou null si elle n'existe plus.</summary>
    public CardModel? Card(string id) => SafeModel<CardModel>("CARD", id);

    /// <summary>Vrai pour une carte rare (cadre doré dans le jeu).</summary>
    public bool IsRareCard(string id) => SafeModel<CardModel>("CARD", id)?.Rarity == CardRarity.Rare;

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
#if TESTBRIDGE
    /// <summary>Dossier de parties de test, lu en plus de l'historique du jeu par la build de test seulement.</summary>
    public const string TestHistoryPath = "user://StsTools/test_history";
#endif

    public static (List<string> Dirs, ulong LocalPlayerId) HistorySource()
    {
        var profile = SaveManager.Instance.CurrentProfileId;
        var account = UserDataPathProvider.GetAccountScopedBasePath(null);
        var dirs = new[] { false, true }
            .Select(modded => ProjectSettings.GlobalizePath(account.PathJoin(RunHistorySaveManager.GetHistoryPath(profile, modded))))
            .ToList();
#if TESTBRIDGE
        // Parties de test propres au mod (jamais lues par le jeu) : supprimer ce dossier pour les retirer
        dirs.Add(ProjectSettings.GlobalizePath(TestHistoryPath));
#endif
        return (dirs, PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform));
    }
}
