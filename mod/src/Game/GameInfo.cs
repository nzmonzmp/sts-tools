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

    public IReadOnlySet<string> Characters { get; }

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
