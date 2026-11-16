using System.Runtime.CompilerServices;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace StsTools.Game;

/// <summary>
/// Stats d'une carte du deck pendant la partie en cours (panneau Run stats, sans lien avec l'écran Stats) :
/// des valeurs nommées, pour pouvoir suivre tout ce que le jeu note, quel que soit le personnage.
/// </summary>
public sealed class CardRunStats
{
    // Noms des stats, aussi utilisés comme libellés et comme clés dans la sauvegarde
    public const string Played = "Played";
    public const string AutoPlayed = "Auto-played";
    public const string CardsPlayed = "Cards played";
    public const string Damage = "Damage dealt";
    public const string Kills = "Enemies killed";
    public const string Block = "Block gained";
    public const string SelfDamage = "HP lost";
    public const string Drawn = "Times drawn";
    public const string CardsDrawn = "Cards drawn";
    public const string CardsDiscarded = "Cards discarded";
    public const string CardsExhausted = "Cards exhausted";
    public const string CardsCreated = "Cards created";
    public const string Exhausted = "Times exhausted";
    public const string Energy = "Energy spent";
    public const string StarsSpent = "Stars spent";
    public const string StarsGained = "Stars gained";
    public const string Orbs = "Orbs channeled";
    public const string Summoned = "Summoned";

    /// <summary>Ordre d'affichage ; les pouvoirs (« Vulnerable applied », « Strength gained »…) viennent après.</summary>
    public static readonly string[] Order =
    [
        Played, AutoPlayed, CardsPlayed, Damage, Kills, Block, SelfDamage, Energy, StarsSpent, StarsGained, Drawn, CardsDrawn,
        CardsDiscarded, CardsExhausted, CardsCreated, Exhausted, Orbs, Summoned,
    ];

    /// <summary>Icônes des stats fixes, prises dans les images du jeu</summary>
    private static readonly Dictionary<string, string> Icons = new()
    {
        [Played] = "res://images/packed/sprite_fonts/card_icon.png",
        [AutoPlayed] = "res://images/packed/sprite_fonts/card_icon.png",
        [CardsPlayed] = "res://images/packed/sprite_fonts/card_icon.png",
        [Damage] = "res://images/packed/intents/attack/intent_attack_1.png",
        [Kills] = "res://images/packed/intents/intent_death_blow.png",
        [Block] = "res://images/ui/combat/block.png",
        [SelfDamage] = "res://images/packed/common_ui/top_bar/top_bar_heart.png",
        [Energy] = "res://images/packed/sprite_fonts/colorless_energy_icon.png",
        [StarsSpent] = "res://images/packed/sprite_fonts/star_icon.png",
        [StarsGained] = "res://images/packed/sprite_fonts/star_icon.png",
        [Drawn] = "res://images/packed/combat_ui/draw_pile.png",
        [CardsDrawn] = "res://images/packed/combat_ui/draw_pile.png",
        [CardsDiscarded] = "res://images/packed/combat_ui/discard_pile.png",
        [CardsExhausted] = "res://images/packed/combat_ui/exhaust_pile.png",
        [CardsCreated] = "res://images/packed/sprite_fonts/card_icon.png",
        [Exhausted] = "res://images/packed/combat_ui/exhaust_pile.png",
        [Orbs] = "res://images/orbs/empty_orb.png",
        [Summoned] = "res://images/packed/intents/intent_summon.png",
    };

    /// <summary>Icône de chaque pouvoir suivi (« Vulnerable applied » → icône de Vulnerable), sauvegardée avec la partie</summary>
    private static readonly Dictionary<string, string> PowerIcons = new();

    private static readonly Dictionary<string, Texture2D?> LoadedIcons = new();

    /// <summary>Icône d'une stat, ou null si elle n'en a pas ou si l'image n'existe pas dans le jeu.</summary>
    public static Texture2D? IconFor(string name)
    {
        if (LoadedIcons.TryGetValue(name, out var cached))
            return cached;
        Texture2D? texture = null;
        try
        {
            if ((Icons.GetValueOrDefault(name) ?? PowerIcons.GetValueOrDefault(name)) is { } path && ResourceLoader.Exists(path))
                texture = ResourceLoader.Load<Texture2D>(path);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot load icon for {name}: {e.Message}");
        }
        // Pas de mise en cache d'un pouvoir dont l'icône n'est pas encore connue : elle peut arriver plus tard
        if (texture != null || Icons.ContainsKey(name))
            LoadedIcons[name] = texture;
        return texture;
    }

    /// <summary>Combats commencés depuis que la carte est dans le deck</summary>
    public int Combats;

    public Dictionary<string, decimal> Values { get; } = new();

    public decimal this[string name] => Values.GetValueOrDefault(name);

    /// <summary>Joueur à qui appartient la carte ; null pour une somme (ligne de cartes, combat, tour)</summary>
    private Player? _owner;

    private void Add(string name, decimal amount)
    {
        if (amount == 0)
            return;
        Values[name] = Values.GetValueOrDefault(name) + amount;
        // Une carte du joueur local : compte aussi pour le combat et le tour en cours (onglet Combat)
        if (_owner != null && _owner == _player)
        {
            RollTurn();
            _combat.Values[name] = _combat[name] + amount;
            _turnStats.Values[name] = _turnStats[name] + amount;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Stats non nulles, dans l'ordre d'affichage : l'ordre fixe, puis les debuffs infligés aux ennemis
    /// (« … applied »), puis les buffs gagnés, chacun par nom.
    /// </summary>
    public IEnumerable<string> DisplayNames()
    {
        var powers = Values.Keys.Except(Order)
            .OrderBy(name => name.EndsWith(" applied") ? 0 : 1)
            .ThenBy(name => name);
        return Order.Concat(powers).Where(name => this[name] != 0);
    }

    // ---- Combat en cours (onglet Combat) : toutes les cartes du joueur local ----

    /// <summary>Une stat du joueur local a changé pendant le combat.</summary>
    public static event Action? Changed;

    private static CardRunStats _combat = new(), _turnStats = new();
    private static int _turn, _combatTurns;

    private static int CurrentTurn => _player?.PlayerCombatState?.TurnNumber ?? 0;

    /// <summary>Nouveau tour : on repart de zéro pour « ce tour ».</summary>
    private static void RollTurn()
    {
        int turn = CurrentTurn;
        if (turn != 0 && turn != _turn)
        {
            _turn = turn;
            _turnStats = new CardRunStats();
        }
        _combatTurns = Math.Max(_combatTurns, turn);
    }

    /// <summary>Stats du combat en cours (ou du dernier combat, une fois fini).</summary>
    public static CardRunStats CombatTotals => _combat;

    /// <summary>Stats du tour en cours ; vides si rien ne s'est encore passé pendant ce tour.</summary>
    public static CardRunStats TurnTotals
    {
        get
        {
            RollTurn();
            return _turnStats;
        }
    }

    /// <summary>Tours joués dans le combat en cours (ou le dernier), le tour en cours compris.</summary>
    public static int CombatTurns
    {
        get
        {
            RollTurn();
            return _combatTurns;
        }
    }

    /// <summary>
    /// Stats par carte du deck. Clé : l'objet carte du deck lui-même ; les cartes de combat y renvoient par
    /// <see cref="CardModel.DeckVersion"/>. Table faible : les stats disparaissent avec les cartes, donc avec la partie.
    /// Sauvegardées en même temps que la partie (<see cref="SaveRunPatch"/>) et restaurées quand on la reprend.
    /// </summary>
    private static readonly ConditionalWeakTable<CardModel, CardRunStats> Stats = new();

    public static CardRunStats? Of(CardModel deckCard) => Stats.TryGetValue(deckCard, out var stats) ? stats : null;

    /// <summary>Somme des stats de plusieurs copies (une ligne « 4× Strike »).</summary>
    public static CardRunStats Sum(IEnumerable<CardModel> deckCards)
    {
        var sum = new CardRunStats();
        foreach (var card in deckCards)
        {
            if (Of(card) is not { } s)
                continue;
            // Les copies sont dans le deck en même temps : les combats ne s'additionnent pas
            sum.Combats = Math.Max(sum.Combats, s.Combats);
            foreach (var (name, value) in s.Values)
                sum.Add(name, value);
        }
        return sum;
    }

    // ---- Sauvegarde : un fichier pour la partie en cours, écrit à chaque sauvegarde du jeu ----

    private static string FilePath => ProjectSettings.GlobalizePath("user://StsTools/run_card_stats.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Joueur local et identifiant de la partie en cours, pour savoir quoi sauvegarder.</summary>
    private static Player? _player;
    private static string? _runKey;

    private sealed record SavedCard(int Index, string Id, string Title, int Combats, Dictionary<string, decimal>? Values,
        // Format de la première version, relu pour ne pas perdre une partie en cours
        int Played = 0, int Damage = 0, int Block = 0, int Drawn = 0);
    private sealed record SavedRun(string Key, List<SavedCard> Cards, Dictionary<string, string>? Icons = null);

    /// <summary>
    /// Début ou reprise d'une partie : retient le joueur local et, si le fichier vient de cette même partie,
    /// remet les stats sauvegardées sur les cartes du deck.
    /// </summary>
    public static void Track(IRunState runState, Player player)
    {
        _player = player;
        _runKey = RunKey(runState);
        try
        {
            if (!File.Exists(FilePath))
                return;
            var saved = JsonSerializer.Deserialize<SavedRun>(File.ReadAllText(FilePath), Json);
            if (saved == null || saved.Key != _runKey)
                return;
            foreach (var (name, path) in saved.Icons ?? [])
                PowerIcons[name] = path;
            Restore(PileType.Deck.GetPile(player).Cards, saved.Cards);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot restore card stats: {e.Message}");
        }
    }

    /// <summary>
    /// Graine et heure de début de la partie : différentes d'une partie à l'autre, identiques quand on la reprend.
    /// </summary>
    private static string RunKey(IRunState runState)
    {
        long start = 0;
        try
        {
            start = (long)(AccessTools.Field(typeof(RunManager), "_startTime")?.GetValue(RunManager.Instance) ?? 0L);
        }
        catch (Exception)
        {
            // Champ renommé dans une mise à jour du jeu : la graine seule suffit presque toujours
        }
        return $"{runState.Rng.StringSeed}:{start}";
    }

    /// <summary>
    /// Remet chaque stat sur la carte à la même place dans le deck, si c'est bien la même carte ; sinon (deck
    /// réordonné), sur une carte identique pas encore servie.
    /// </summary>
    private static void Restore(IReadOnlyList<CardModel> deck, List<SavedCard> cards)
    {
        var used = new HashSet<CardModel>();
        var pending = new List<SavedCard>();
        foreach (var c in cards)
        {
            if (c.Index < deck.Count && Same(deck[c.Index], c) && used.Add(deck[c.Index]))
                Apply(deck[c.Index], c);
            else
                pending.Add(c);
        }
        foreach (var c in pending)
            if (deck.FirstOrDefault(card => !used.Contains(card) && Same(card, c)) is { } card)
            {
                used.Add(card);
                Apply(card, c);
            }
    }

    private static bool Same(CardModel card, SavedCard saved) => card.Id.ToString() == saved.Id && card.Title == saved.Title;

    private static void Apply(CardModel card, SavedCard saved)
    {
        var s = Stats.GetOrCreateValue(card);
        s.Combats = saved.Combats;
        s.Values.Clear();
        if (saved.Values != null)
            foreach (var (name, value) in saved.Values)
                s.Values[name] = value;
        else
        {
            s.Add(Played, saved.Played);
            s.Add(Damage, saved.Damage);
            s.Add(Block, saved.Block);
            s.Add(Drawn, saved.Drawn);
        }
    }

    private static void Save()
    {
        if (_player == null || _runKey == null)
            return;
        try
        {
            var deck = PileType.Deck.GetPile(_player).Cards;
            var cards = new List<SavedCard>();
            for (int i = 0; i < deck.Count; i++)
                if (Of(deck[i]) is { } s)
                    cards.Add(new SavedCard(i, deck[i].Id.ToString(), deck[i].Title, s.Combats, new(s.Values)));
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new SavedRun(_runKey, cards, new(PowerIcons)), Json));
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot save card stats: {e.Message}");
        }
    }

    /// <summary>
    /// Le jeu sauvegarde la partie : on sauvegarde les stats au même moment. En reprenant la partie, les stats
    /// correspondent donc au point de sauvegarde du jeu (un combat recommencé repart des stats d'avant ce combat).
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRun))]
    public static class SaveRunPatch
    {
        public static void Prefix() => Save();
    }

    /// <summary>Stats de la carte du deck dont <paramref name="card"/> est la version de combat.</summary>
    private static CardRunStats? For(CardModel? card)
    {
        if (card?.DeckVersion is not { } deckCard)
            return null;
        var stats = Stats.GetOrCreateValue(deckCard);
        stats._owner ??= deckCard.Owner;
        return stats;
    }

    /// <summary>Début d'un combat : un combat de plus pour chaque carte du deck de chaque joueur.</summary>
    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
    public static class CombatStartPatch
    {
        public static void Postfix(CombatState state)
        {
            try
            {
                _frames.Clear();
                _pendingEnergy = _pendingStars = 0;
                _combat = new CardRunStats();
                _turnStats = new CardRunStats();
                _turn = _combatTurns = 0;
                Changed?.Invoke();
                foreach (var player in state.Players)
                    foreach (var card in PileType.Deck.GetPile(player).Cards)
                        Stats.GetOrCreateValue(card).Combats++;
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] cannot count combat for card stats: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Ce qui agit en ce moment, le plus récent en dernier : une carte en train d'être jouée, ou un pouvoir, une
    /// relique, une carte… dont le jeu exécute un effet (début de tour, après une carte jouée…). Ce qui se passe
    /// pendant ce temps (pioche, pouvoirs, dégâts sans carte source…) est attribué à la carte du dernier élément,
    /// ou à personne si c'est une relique ou un effet ennemi.
    /// </summary>
    private sealed class Frame
    {
        /// <summary>Carte en train d'être jouée, ou null pour un écouteur</summary>
        public CardModel? PlayedCard;
        /// <summary>Écouteur dont le jeu exécute l'effet, ou null pour une carte jouée</summary>
        public AbstractModel? Listener;
    }

    private static readonly List<Frame> _frames = [];

    /// <summary>
    /// Pouvoir → carte qui l'a donné (Demon Form, Poison…) : ses effets des tours suivants comptent pour la carte.
    /// Un pouvoir déjà présent qu'une autre carte renforce passe à cette dernière.
    /// </summary>
    private static readonly ConditionalWeakTable<PowerModel, CardRunStats> PowerSources = new();

    /// <summary>Énergie et étoiles dépensées juste avant le début du jeu d'une carte : c'est son coût.</summary>
    private static decimal _pendingEnergy, _pendingStars;

    /// <summary>Carte à qui revient ce qui se passe maintenant ; calculée seulement quand un événement arrive.</summary>
    private static CardRunStats? Current => _frames.Count == 0 ? null
        : _frames[^1].PlayedCard is { } played ? For(played)
        : _frames[^1].Listener is { } listener ? StatsOf(listener)
        : null;

    /// <summary>Carte du deck à qui reviennent les effets d'un écouteur du jeu (pouvoir donné par une carte, carte).</summary>
    private static CardRunStats? StatsOf(AbstractModel model) => model switch
    {
        PowerModel power => PowerSources.TryGetValue(power, out var stats) ? stats : null,
        CardModel card => For(card),
        _ => null,
    };

    /// <summary>
    /// Le jeu exécute un effet en parcourant ses écouteurs un par un et en attendant que chacun ait fini avant de
    /// passer au suivant. On enveloppe ce parcours : chaque écouteur est « ce qui agit » jusqu'au suivant.
    /// Ces parcours sont très fréquents (textes des cartes à chaque image) : un seul objet par parcours, et rien
    /// d'autre tant qu'aucun événement n'arrive.
    /// </summary>
    private static IEnumerable<AbstractModel> Track(IEnumerable<AbstractModel> listeners)
    {
        var frame = new Frame();
        _frames.Add(frame);
        try
        {
            foreach (var model in listeners)
            {
                frame.Listener = model;
                yield return model;
            }
        }
        finally
        {
            _frames.Remove(frame);
        }
    }

    [HarmonyPatch(typeof(CombatState), nameof(CombatState.IterateHookListeners))]
    public static class CombatListenersPatch
    {
        public static void Postfix(ref IEnumerable<AbstractModel> __result) => __result = Track(__result);
    }

    [HarmonyPatch(typeof(RunState), nameof(RunState.IterateHookListeners))]
    public static class RunListenersPatch
    {
        public static void Postfix(ref IEnumerable<AbstractModel> __result) => __result = Track(__result);
    }

    /// <summary>
    /// Chaque événement que le jeu note dans l'historique du combat (seulement en combat réel, pas pendant les
    /// simulations de l'IA), attribué à la carte qui en est la source ou, à défaut, à la carte en train d'être jouée.
    /// </summary>
    [HarmonyPatch(typeof(CombatHistory), "Add")]
    public static class HistoryPatch
    {
        public static void Postfix(ICombatState combatState, CombatHistoryEntry entry)
        {
            try
            {
                if (combatState.IsLiveCombat())
                    Record(entry);
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] cannot record card stats: {e.Message}");
            }
        }
    }

    private static void Record(CombatHistoryEntry entry)
    {
        // Le coût est payé juste avant le début du jeu : tout autre événement entre les deux annule l'attente
        if (entry is not (EnergySpentEntry or StarsModifiedEntry { Amount: < 0 } or CardPlayStartedEntry))
            _pendingEnergy = _pendingStars = 0;

        switch (entry)
        {
            case EnergySpentEntry energy when _frames.Count == 0:
                _pendingEnergy += energy.Amount;
                return;
            case StarsModifiedEntry { Amount: < 0 } stars when _frames.Count == 0:
                _pendingStars -= stars.Amount;
                return;

            case CardPlayStartedEntry start:
                // Carte jouée indirectement (Hellraiser, Havoc…) : la carte ou le pouvoir qui agit la fait jouer
                if (start.CardPlay is { IsAutoPlay: true, IsFirstInSeries: true })
                    Current?.Add(CardsPlayed, 1);
                _frames.Add(new Frame { PlayedCard = start.CardPlay.Card });
                // Une carte rejouée (double jeu) n'a pas de coût de plus
                if (start.CardPlay.IsFirstInSeries && For(start.CardPlay.Card) is { } payer)
                {
                    payer.Add(Energy, _pendingEnergy);
                    payer.Add(StarsSpent, _pendingStars);
                }
                _pendingEnergy = _pendingStars = 0;
                return;
            case CardPlayFinishedEntry finish:
                int index = _frames.FindLastIndex(f => f.PlayedCard == finish.CardPlay.Card);
                if (index >= 0)
                    _frames.RemoveAt(index);
                // Une carte rejouée ne compte qu'une fois
                if (finish.CardPlay.IsFirstInSeries && For(finish.CardPlay.Card) is { } played)
                {
                    played.Add(Played, 1);
                    // Parmi ses jeux, ceux qui ne venaient pas du joueur
                    if (finish.CardPlay.IsAutoPlay)
                        played.Add(AutoPlayed, 1);
                }
                return;

            case DamageReceivedEntry damage:
                // La carte source, sinon ce qui agit (Poison donné par une carte, carte qui en joue une autre…)
                var source = For(damage.CardSource) ?? Current;
                if (source == null)
                    return;
                if (damage.Receiver.IsEnemy)
                {
                    source.Add(Damage, damage.Result.TotalDamage);
                    if (damage.Result.WasTargetKilled)
                        source.Add(Kills, 1);
                }
                else if (damage.Dealer is not { IsEnemy: true })
                    // PV perdus par son propre camp à cause de la carte : cartes qui coûtent des PV, ou pouvoir
                    // donné par la carte (Inferno au début de chaque tour). Jamais les coups des ennemis
                    source.Add(SelfDamage, damage.Result.UnblockedDamage);
                return;
            case BlockGainedEntry block:
                (For(block.CardPlay?.Card) ?? Current)?.Add(Block, block.Amount);
                return;

            case CardDrawnEntry drawn:
                For(drawn.Card)?.Add(Drawn, 1);
                Current?.Add(CardsDrawn, 1);
                return;
            case CardDiscardedEntry discarded when !IsPlaying(discarded.Card):
                Current?.Add(CardsDiscarded, 1);
                return;
            case CardExhaustedEntry exhausted:
                For(exhausted.Card)?.Add(Exhausted, 1);
                if (!IsPlaying(exhausted.Card))
                    Current?.Add(CardsExhausted, 1);
                return;
            case CardGeneratedEntry:
                Current?.Add(CardsCreated, 1);
                return;

            case StarsModifiedEntry { Amount: > 0 } stars:
                Current?.Add(StarsGained, stars.Amount);
                return;
            case OrbChanneledEntry:
                Current?.Add(Orbs, 1);
                return;
            case SummonedEntry summoned:
                Current?.Add(Summoned, summoned.Amount);
                return;
            // Pas les pouvoirs qu'un ennemi se donne lui-même en réaction (Force quand on le frappe…).
            // Seulement les hausses : les baisses (durée d'un debuff qui diminue à chaque tour, Force temporaire
            // perdue en fin de tour) viennent du pouvoir lui-même et annuleraient ce que la carte a infligé
            case PowerReceivedEntry { Amount: > 0 } power when Current is { } current && power.Applier is not { IsEnemy: true }:
                // « Vulnerable applied » sur les ennemis (debuffs infligés), « Strength gained » sur son propre camp
                var title = power.Power.Title.GetFormattedText();
                var name = power.Power.Owner.IsEnemy ? $"{title} applied" : $"{title} gained";
                PowerIcons[name] = power.Power.IconPath;
                current.Add(name, power.Amount);
                // Les effets futurs de ce pouvoir reviendront à la carte
                PowerSources.AddOrUpdate(power.Power, current);
                return;
        }
    }

    /// <summary>La carte elle-même, en train d'être jouée (elle part à la défausse ou est épuisée après son jeu).</summary>
    private static bool IsPlaying(CardModel card) => _frames.Any(f => f.PlayedCard == card);
}
