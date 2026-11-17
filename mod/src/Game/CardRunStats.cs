using System.Runtime.CompilerServices;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.ValueProps;
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
public sealed partial class CardRunStats
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
    public const string BonusDamage = "Bonus damage";
    public const string Prevented = "Damage prevented";
    public const string BonusBlock = "Bonus block";
    public const string Healed = "HP healed";
    public const string EnergyGained = "Energy gained";
    public const string Gold = "Gold gained";
    // Hors combat surtout : ce qu'une relique fait quand on la prend (et les cartes qui font la même chose)
    public const string MaxHpGained = "Max HP gained";
    public const string MaxHpLost = "Max HP lost";
    public const string DeckAdded = "Cards added to deck";
    public const string DeckRemoved = "Cards removed from deck";
    public const string Upgraded = "Cards upgraded";
    public const string UpgradedInCombat = "Cards upgraded in combat";
    public const string EnergySaved = "Energy saved";
    public const string StarsSaved = "Stars saved";
    public const string Stunned = "Enemies stunned";
    public const string OrbsEvoked = "Orbs evoked";
    public const string OrbSlots = "Orb slots";
    public const string Forged = "Forged";
    public const string BlockRetained = "Block retained";
    public const string DeathsPrevented = "Deaths prevented";
    public const string CardsRetained = "Cards retained";
    public const string ExtraTurns = "Extra turns";
    public const string XBonus = "X bonus";
    public const string ExtraHits = "Extra hits";
    public const string OrbTriggers = "Extra orb triggers";
    public const string DebuffsPrevented = "Debuffs prevented";
    public const string GoldSaved = "Gold saved";
    public const string CardChoices = "Card choices added";
    public const string RewardsAdded = "Rewards added";
    public const string PotionRewards = "Potion rewards forced";
    public const string RestOptions = "Rest site options added";
    public const string TreasuresSkipped = "Treasures skipped";
    public const string ItemsRestocked = "Items restocked";
    public const string EnemyHpRemoved = "Enemy HP removed";
    public const string SafeUnknownRooms = "Unknown rooms without combat";
    public const string FreeTravels = "Free travels";
    public const string OfferedUpgraded = "Offered cards upgraded";
    public const string OfferedEnchanted = "Offered cards enchanted";
    public const string OrbBonus = "Orb bonus";
    public const string GoldenPathRooms = "Golden path rooms";
    public const string Transformed = "Cards transformed";
    public const string Enchanted = "Cards enchanted";
    public const string Potions = "Potions obtained";
    public const string PotionSlots = "Potion slots";
    // Seulement dans l'onglet Combat : ce que le joueur a reçu, quelle que soit la source
    public const string DamageTaken = "Damage taken";
    public const string DamageBlocked = "Damage blocked";
    public const string OstyTanked = "Tanked by Osty";
    public const string Summoned = "Summoned";

    /// <summary>
    /// Stats hors combat (deck, récompenses, boutique, carte du jeu) : leurs moyennes par combat ou par tour, et leur
    /// part du combat en cours, n'ont pas de sens.
    /// </summary>
    public static readonly HashSet<string> OutOfCombat =
    [
        MaxHpGained, MaxHpLost, DeckAdded, DeckRemoved, Upgraded, Transformed, Enchanted, Potions, PotionSlots,
        GoldSaved, CardChoices, RewardsAdded, PotionRewards, RestOptions, TreasuresSkipped, ItemsRestocked,
        SafeUnknownRooms, FreeTravels, OfferedUpgraded, OfferedEnchanted, GoldenPathRooms,
    ];

    /// <summary>Ordre d'affichage ; les pouvoirs (« Vulnerable applied », « Strength gained »…) viennent après.</summary>
    public static readonly string[] Order =
    [
        Played, AutoPlayed, CardsPlayed, Damage, BonusDamage, Kills, DamageTaken, DamageBlocked, OstyTanked, Block, BonusBlock, Prevented,
        Healed, SelfDamage, Energy,
        EnergyGained, StarsSpent, StarsGained, Gold, Drawn, CardsDrawn,
        CardsDiscarded, CardsExhausted, CardsCreated, Exhausted, Orbs, Summoned,
        MaxHpGained, MaxHpLost, DeckAdded, DeckRemoved, Upgraded, UpgradedInCombat, Transformed, Enchanted, Potions,
        PotionSlots, EnergySaved, StarsSaved, Stunned, OrbsEvoked, OrbSlots, Forged, BlockRetained, DeathsPrevented,
        CardsRetained, ExtraTurns, XBonus, ExtraHits, OrbTriggers, DebuffsPrevented, GoldSaved, CardChoices, RewardsAdded,
        PotionRewards, RestOptions, TreasuresSkipped, ItemsRestocked, EnemyHpRemoved, SafeUnknownRooms, FreeTravels,
        OfferedUpgraded, OfferedEnchanted, OrbBonus, GoldenPathRooms,
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
        [BonusDamage] = "res://images/atlases/power_atlas.sprites/strength_power.tres",
        [Prevented] = "res://images/packed/intents/intent_defend.png",
        [BonusBlock] = "res://images/ui/combat/block.png",
        [Healed] = "res://images/packed/intents/intent_heal.png",
        [EnergyGained] = "res://images/packed/sprite_fonts/colorless_energy_icon.png",
        [Gold] = "res://images/packed/sprite_fonts/gold_icon.png",
        [DamageTaken] = "res://images/packed/common_ui/top_bar/top_bar_heart.png",
        [DamageBlocked] = "res://images/ui/combat/block.png",
        [OstyTanked] = "res://images/atlases/relic_atlas.sprites/ostys_devotion.tres",
        [Summoned] = "res://images/packed/intents/intent_summon.png",
        [MaxHpGained] = "res://images/packed/common_ui/top_bar/top_bar_heart.png",
        [MaxHpLost] = "res://images/packed/common_ui/top_bar/top_bar_heart.png",
        [DeckAdded] = "res://images/packed/sprite_fonts/card_icon.png",
        [DeckRemoved] = "res://images/packed/sprite_fonts/card_icon.png",
        [Upgraded] = "res://images/packed/blitz/small_tokens/entry_card_upgrade.png",
        [UpgradedInCombat] = "res://images/packed/blitz/small_tokens/entry_card_upgrade.png",
        [EnergySaved] = "res://images/packed/sprite_fonts/colorless_energy_icon.png",
        [StarsSaved] = "res://images/packed/sprite_fonts/star_icon.png",
        [Stunned] = "res://images/packed/intents/intent_stun.png",
        [OrbsEvoked] = "res://images/orbs/lightning_orb.png",
        [OrbSlots] = "res://images/orbs/empty_orb.png",
        [BlockRetained] = "res://images/ui/combat/block.png",
        [DeathsPrevented] = "res://images/packed/common_ui/top_bar/top_bar_heart.png",
        [CardsRetained] = "res://images/packed/sprite_fonts/card_icon.png",
        [ExtraTurns] = "res://images/packed/sprite_fonts/colorless_energy_icon.png",
        [XBonus] = "res://images/packed/sprite_fonts/colorless_energy_icon.png",
        [ExtraHits] = "res://images/packed/intents/attack/intent_attack_1.png",
        [OrbTriggers] = "res://images/orbs/lightning_orb.png",
        [DebuffsPrevented] = "res://images/packed/intents/intent_defend.png",
        [GoldSaved] = "res://images/packed/sprite_fonts/gold_icon.png",
        [CardChoices] = "res://images/packed/sprite_fonts/card_icon.png",
        [RewardsAdded] = "res://images/packed/sprite_fonts/card_icon.png",
        [PotionRewards] = "res://images/packed/sprite_fonts/potion_icon.png",
        [RestOptions] = "res://images/packed/common_ui/top_bar/top_bar_heart.png",
        [TreasuresSkipped] = "res://images/packed/sprite_fonts/gold_icon.png",
        [ItemsRestocked] = "res://images/packed/sprite_fonts/gold_icon.png",
        [EnemyHpRemoved] = "res://images/packed/intents/attack/intent_attack_1.png",
        [SafeUnknownRooms] = "res://images/packed/intents/intent_defend.png",
        [OfferedUpgraded] = "res://images/packed/blitz/small_tokens/entry_card_upgrade.png",
        [OfferedEnchanted] = "res://images/packed/blitz/small_tokens/entry_card_enchant.png",
        [OrbBonus] = "res://images/orbs/lightning_orb.png",
        [GoldenPathRooms] = "res://images/packed/sprite_fonts/gold_icon.png",
        [FreeTravels] = "res://images/packed/sprite_fonts/card_icon.png",
        [Forged] = "res://images/packed/card_portraits/token/sovereign_blade.png",
        [Transformed] = "res://images/packed/blitz/small_tokens/entry_card_transform.png",
        [Enchanted] = "res://images/packed/blitz/small_tokens/entry_card_enchant.png",
        [Potions] = "res://images/packed/sprite_fonts/potion_icon.png",
        [PotionSlots] = "res://images/packed/sprite_fonts/potion_icon.png",
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

    /// <summary>Tours joués depuis que la relique est là (seulement pour les reliques)</summary>
    public int Turns;

    /// <summary>Valeurs au début du combat en cours, pour en déduire ce combat ; null avant le premier combat (reliques)</summary>
    public Dictionary<string, decimal>? CombatStart { get; private set; }

    /// <summary>Ce qui a été fait depuis le début du combat en cours (ou du dernier), ou null s'il n'y en a pas eu.</summary>
    public CardRunStats? SinceCombatStart()
    {
        if (CombatStart == null)
            return null;
        var since = new CardRunStats();
        foreach (var (name, value) in Values)
            if (value - CombatStart.GetValueOrDefault(name) is var delta and not 0)
                since.Values[name] = delta;
        return since;
    }

    /// <summary>Une stat a changé, pour n'importe qui (carte, relique) : pour rafraîchir ce qui est affiché.</summary>
    public static event Action? AnyChanged;

    public Dictionary<string, decimal> Values { get; } = new();

    public decimal this[string name] => Values.GetValueOrDefault(name);

    /// <summary>Joueur à qui appartient la carte ; null pour une somme (ligne de cartes, combat, tour)</summary>
    private Player? _owner;

    private void Add(string name, decimal amount)
    {
        if (amount == 0)
            return;
        Values[name] = Values.GetValueOrDefault(name) + amount;
        AnyChanged?.Invoke();
        // Une carte du joueur local : compte aussi pour le combat et le tour en cours (onglet Combat). Sauf les
        // soins : les onglets Combat et Game comptent tous les PV rendus au joueur, quelle qu'en soit la source
        if (_owner != null && _owner == _player && name != Healed)
            AddToCombat(name, amount);
    }

    /// <summary>Compte pour le joueur local : combat et tour en cours (onglet Combat), partie entière (onglet Game).</summary>
    private static void AddToCombat(string name, decimal amount)
    {
        if (amount == 0)
            return;
        RollTurn();
        _combat.Values[name] = _combat[name] + amount;
        _turnStats.Values[name] = _turnStats[name] + amount;
        _run.Values[name] = _run[name] + amount;
        Changed?.Invoke();
    }

    // ---- Partie entière (onglet Game), sauvegardée avec la partie ----

    private static CardRunStats _run = new();
    private static int _runCombats, _runTurns;

    /// <summary>Stats du joueur local sur toute la partie.</summary>
    public static CardRunStats RunTotals => _run;

    /// <summary>Combats commencés dans la partie.</summary>
    public static int RunCombats => _runCombats;

    /// <summary>Tours joués dans la partie, tous combats confondus, le tour en cours compris.</summary>
    public static int RunTurns
    {
        get
        {
            RollTurn();
            return _runTurns;
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

    /// <summary>
    /// Les intentions des ennemis ont été mises à jour (nouveau tour, pouvoir appliqué…) : l'onglet Combat se
    /// redessine, pour les dégâts annoncés.
    /// </summary>
    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Combat.NCreature), nameof(MegaCrit.Sts2.Core.Nodes.Combat.NCreature.UpdateIntent))]
    public static class IntentPatch
    {
        public static void Postfix() => Changed?.Invoke();
    }

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
            _runTurns++;
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

    /// <summary>
    /// Stats par relique possédée : ce que ses effets ont fait (dégâts, Block, pouvoirs donnés et leurs effets…).
    /// Elles ne comptent pas dans les onglets Combat et Game, réservés aux cartes. Sauvegardées comme celles des cartes.
    /// </summary>
    private static readonly ConditionalWeakTable<RelicModel, CardRunStats> RelicStats = new();

    public static CardRunStats? Of(RelicModel relic) => RelicStats.TryGetValue(relic, out var stats) ? stats : null;

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

    /// <summary>Joueur local de la partie en cours.</summary>
    public static Player? LocalPlayer => _player;
    private static string? _runKey;

    private sealed record SavedCard(int Index, string Id, string Title, int Combats, Dictionary<string, decimal>? Values,
        // Format de la première version, relu pour ne pas perdre une partie en cours
        int Played = 0, int Damage = 0, int Block = 0, int Drawn = 0);
    private sealed record SavedRelic(int Index, string Id, int Combats, Dictionary<string, decimal> Values, int Turns = 0);
    private sealed record SavedRun(string Key, List<SavedCard> Cards, Dictionary<string, string>? Icons = null,
        Dictionary<string, decimal>? RunValues = null, int RunCombats = 0, int RunTurns = 0, List<SavedRelic>? Relics = null);

    /// <summary>
    /// Début ou reprise d'une partie : retient le joueur local et, si le fichier vient de cette même partie,
    /// remet les stats sauvegardées sur les cartes du deck.
    /// </summary>
    public static void Track(IRunState runState, Player player)
    {
        _player = player;
        _runKey = RunKey(runState);
        // Nouvelle partie par défaut ; les totaux sont remis plus bas si le fichier vient de cette partie
        _run = new CardRunStats();
        _runCombats = _runTurns = 0;
        _combat = new CardRunStats();
        _turnStats = new CardRunStats();
        _turn = _combatTurns = 0;
        try
        {
            if (!File.Exists(FilePath))
                return;
            var saved = JsonSerializer.Deserialize<SavedRun>(File.ReadAllText(FilePath), Json);
            if (saved == null || saved.Key != _runKey)
                return;
            foreach (var (name, path) in saved.Icons ?? [])
                PowerIcons[name] = path;
            foreach (var (name, value) in saved.RunValues ?? [])
                _run.Values[name] = value;
            (_runCombats, _runTurns) = (saved.RunCombats, saved.RunTurns);
            Restore(PileType.Deck.GetPile(player).Cards, saved.Cards);
            RestoreRelics(player.Relics, saved.Relics ?? []);
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

    /// <summary>Même principe que pour les cartes : la relique à la même place, sinon la même relique pas encore servie.</summary>
    private static void RestoreRelics(IReadOnlyList<RelicModel> relics, List<SavedRelic> saved)
    {
        var used = new HashSet<RelicModel>();
        var pending = new List<SavedRelic>();
        foreach (var r in saved)
        {
            if (r.Index < relics.Count && relics[r.Index].Id.ToString() == r.Id && used.Add(relics[r.Index]))
                ApplyRelic(relics[r.Index], r);
            else
                pending.Add(r);
        }
        foreach (var r in pending)
            if (relics.FirstOrDefault(relic => !used.Contains(relic) && relic.Id.ToString() == r.Id) is { } relic)
            {
                used.Add(relic);
                ApplyRelic(relic, r);
            }
    }

    private static void ApplyRelic(RelicModel relic, SavedRelic saved)
    {
        var s = RelicStats.GetOrCreateValue(relic);
        s.Combats = saved.Combats;
        s.Turns = saved.Turns;
        s.Values.Clear();
        foreach (var (name, value) in saved.Values)
            s.Values[name] = value;
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
            var relics = new List<SavedRelic>();
            for (int i = 0; i < _player.Relics.Count; i++)
                if (Of(_player.Relics[i]) is { } s)
                    relics.Add(new SavedRelic(i, _player.Relics[i].Id.ToString(), s.Combats, new(s.Values), s.Turns));
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(
                new SavedRun(_runKey, cards, new(PowerIcons), new(_run.Values), _runCombats, _runTurns, relics), Json));
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

    /// <summary>Début d'un combat : un combat de plus pour chaque carte du deck et chaque relique de chaque joueur.</summary>
    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
    public static class CombatStartPatch
    {
        public static void Postfix(CombatState state)
        {
            try
            {
                _frames.Clear();
                _pendingHits.Clear();
                _pendingEnergy = _pendingStars = 0;
                _combat = new CardRunStats();
                _turnStats = new CardRunStats();
                _turn = _combatTurns = 0;
                _runCombats++;
                Changed?.Invoke();
                foreach (var player in state.Players)
                    foreach (var card in PileType.Deck.GetPile(player).Cards)
                        Stats.GetOrCreateValue(card).Combats++;
                foreach (var player in state.Players)
                    foreach (var relic in player.Relics)
                    {
                        var stats = RelicStats.GetOrCreateValue(relic);
                        stats.Combats++;
                        stats.CombatStart = new(stats.Values);
                    }
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
    /// pendant ce temps (pioche, pouvoirs, dégâts sans carte source…) est attribué à la carte ou à la relique du
    /// dernier élément, ou à personne si c'est un effet ennemi.
    /// </summary>
    internal sealed class Frame
    {
        /// <summary>Carte en train d'être jouée, ou null pour un écouteur</summary>
        public CardModel? PlayedCard;
        /// <summary>Écouteur dont le jeu exécute l'effet, ou null pour une carte jouée</summary>
        public AbstractModel? Listener;
    }

    private static readonly List<Frame> _frames = [];

    /// <summary>
    /// Pouvoir → cartes qui l'ont donné, avec la quantité donnée par chacune (Poison posé par deux cartes) : ses
    /// effets des tours suivants sont partagés entre elles au prorata.
    /// </summary>
    private static readonly ConditionalWeakTable<PowerModel, Dictionary<CardRunStats, decimal>> PowerSources = new();

    /// <summary>
    /// Cartes à qui revient un effet, avec leur part (somme 1) : une seule carte, ou les cartes qui ont donné le
    /// pouvoir qui agit.
    /// </summary>
    private sealed class Credit(IReadOnlyList<(CardRunStats Stats, decimal Share)> shares)
    {
        public IReadOnlyList<(CardRunStats Stats, decimal Share)> Shares { get; } = shares;

        public static Credit One(CardRunStats stats) => new([(stats, 1m)]);

        public void Add(string name, decimal amount)
        {
            foreach (var (stats, share) in Shares)
                stats.Add(name, amount * share);
        }
    }

    private static Credit? CreditFor(CardModel? card) => For(card) is { } stats ? Credit.One(stats) : null;

    /// <summary>Énergie et étoiles dépensées juste avant le début du jeu d'une carte : c'est son coût.</summary>
    private static decimal _pendingEnergy, _pendingStars;

    /// <summary>Carte à qui revient ce qui se passe maintenant ; calculée seulement quand un événement arrive.</summary>
    private static Credit? Current => _frames.Count == 0 ? null
        : _frames[^1].PlayedCard is { } played ? CreditFor(played)
        : _frames[^1].Listener is { } listener ? CreditOf(listener)
        : null;

    /// <summary>
    /// Cartes du deck ou reliques à qui reviennent les effets d'un écouteur du jeu (pouvoir donné par des cartes ou
    /// des reliques, carte, relique).
    /// </summary>
    private static Credit? CreditOf(AbstractModel model) => model switch
    {
        PowerModel power => PowerSources.TryGetValue(power, out var sources) && sources.Count > 0
            ? new Credit(sources.Select(s => (s.Key, s.Value / sources.Values.Sum())).ToList())
            : null,
        CardModel card => CreditFor(card),
        RelicModel relic => Credit.One(RelicStats.GetOrCreateValue(relic)),
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
                if (start.CardPlay.IsFirstInSeries && CreditFor(start.CardPlay.Card) is { } payer)
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
                if (finish.CardPlay.IsFirstInSeries && CreditFor(finish.CardPlay.Card) is { } played)
                {
                    played.Add(Played, 1);
                    // Parmi ses jeux, ceux qui ne venaient pas du joueur
                    if (finish.CardPlay.IsAutoPlay)
                        played.Add(AutoPlayed, 1);
                }
                return;

            case DamageReceivedEntry damage:
                // Dégâts reçus par le joueur local, de toute source (ennemis, Poison, ses propres cartes) :
                // PV perdus et dégâts absorbés par son Block
                if (damage.Receiver.Player is { } hurt && hurt == _player)
                {
                    AddToCombat(DamageTaken, damage.Result.UnblockedDamage);
                    AddToCombat(DamageBlocked, damage.Result.BlockedDamage);
                }
                // Dégâts encaissés par le familier du joueur (Osty, Necrobinder) à sa place
                else if (damage.Receiver.PetOwner is { } owner && owner == _player)
                    AddToCombat(OstyTanked, damage.Result.TotalDamage);
                // La carte source, sinon ce qui agit (Poison donné par une carte, carte qui en joue une autre…)
                RecordModifiers(damage);
                var source = CreditFor(damage.CardSource) ?? Current;
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
                (CreditFor(block.CardPlay?.Card) ?? Current)?.Add(Block, block.Amount);
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
                // Les effets futurs de ce pouvoir reviendront aux cartes qui l'ont donné, au prorata
                var sources = PowerSources.GetOrCreateValue(power.Power);
                foreach (var (stats, share) in current.Shares)
                    sources[stats] = sources.GetValueOrDefault(stats) + power.Amount * share;
                return;
        }
    }

    // ---- Dégâts bonus et dégâts évités (Force, Vulnerable, Weak…) ----

    /// <summary>Paramètres du dernier calcul de dégâts du jeu, et ceux des coups réels en attente, par cible.</summary>
    private sealed record DamageCalc(IRunState RunState, ICombatState? CombatState, Creature? Target, Creature? Dealer,
        decimal Damage, ValueProp Props, CardModel? CardSource, CardPlay? CardPlay, ModifyDamageHookType HookType);

    private static DamageCalc? _lastCalc;
    private static readonly Dictionary<Creature, DamageCalc> _pendingHits = new();

    /// <summary>
    /// Le jeu calcule les dégâts d'un coup (et aussi ceux affichés sur les cartes et les intentions) : on retient
    /// seulement les paramètres, le calcul détaillé ne sera refait que pour un vrai coup.
    /// </summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
    public static class ModifyDamagePatch
    {
        public static void Postfix(IRunState runState, ICombatState? combatState, Creature? target, Creature? dealer,
            decimal damage, ValueProp props, CardModel? cardSource, CardPlay? cardPlay, ModifyDamageHookType modifyDamageHookType,
            CardPreviewMode previewMode)
        {
            if (previewMode == CardPreviewMode.None)
                _lastCalc = new DamageCalc(runState, combatState, target, dealer, damage, props, cardSource, cardPlay, modifyDamageHookType);
        }
    }

    /// <summary>Appelé seulement pour un vrai coup, juste après son calcul : le dernier calcul est celui-là.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterModifyingDamageAmount))]
    public static class RealHitPatch
    {
        public static void Prefix()
        {
            if (_lastCalc is { Target: { } target } calc)
                _pendingHits[target] = calc;
            _lastCalc = null;
        }
    }

    /// <summary>
    /// Coup reçu : refait le calcul de ses dégâts écouteur par écouteur, comme le jeu, pour savoir ce que chacun a
    /// ajouté ou retiré. Ce qu'un pouvoir donné par une carte a ajouté aux coups de son camp compte comme dégâts
    /// bonus pour la carte (Force, Vulnerable) ; ce qu'il a retiré aux coups ennemis, comme dégâts évités (Weak).
    /// </summary>
    private static void RecordModifiers(DamageReceivedEntry damage)
    {
        var target = damage.Result.OriginalTarget ?? damage.Receiver;
        if (!_pendingHits.Remove(target, out var calc))
            return;
        var changes = new List<(AbstractModel Model, decimal Delta)>();
        decimal amount = calc.Damage;
        if (calc.HookType.HasFlag(ModifyDamageHookType.Additive))
            foreach (var model in calc.RunState.IterateHookListeners(calc.CombatState))
            {
                decimal delta = model.ModifyDamageAdditive(calc.Target, amount, calc.Props, calc.Dealer, calc.CardSource, calc.CardPlay);
                if (delta != 0)
                    changes.Add((model, delta));
                amount += delta;
            }
        if (calc.HookType.HasFlag(ModifyDamageHookType.Multiplicative))
        {
            foreach (var model in calc.RunState.IterateHookListeners(calc.CombatState))
            {
                decimal factor = model.ModifyDamageMultiplicative(calc.Target, amount, calc.Props, calc.Dealer, calc.CardSource, calc.CardPlay);
                if (factor != 1)
                    changes.Add((model, amount * (factor - 1)));
                amount *= factor;
            }
            foreach (var model in calc.RunState.IterateHookListeners(calc.CombatState))
            {
                decimal factor = model.ModifyDamageMultiplicativeLate(calc.Target, amount, calc.Props, calc.Dealer, calc.CardSource, calc.CardPlay);
                if (factor != 1)
                    changes.Add((model, amount * (factor - 1)));
                amount *= factor;
            }
        }
        bool enemyHit = calc.Dealer is { IsEnemy: true };
        // Un coup qui a fait moins que prévu (cible morte avant la fin) : les bonus sont réduits d'autant
        decimal dealt = damage.Result.TotalDamage;
        decimal ratio = amount > 0 ? Math.Min(1, dealt / amount) : 0;
        foreach (var (model, delta) in changes)
        {
            if (CreditOf(model) is not { } credit)
                continue;
            if (delta > 0 && !enemyHit && damage.Receiver.IsEnemy)
                credit.Add(BonusDamage, Math.Round(delta * ratio, 2));
            else if (delta < 0 && enemyHit && !damage.Receiver.IsEnemy)
                credit.Add(Prevented, -delta);
        }
    }

    // ---- Ce que l'historique de combat ne note pas : soins, énergie gagnée, or ----

    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Heal))]
    public static class HealPatch
    {
        public static void Prefix(Creature creature, decimal amount)
        {
            if (creature is not { IsEnemy: false })
                return;
            decimal healed = Math.Max(0, Math.Min(amount, creature.MaxHp - creature.CurrentHp));
            Current?.Add(Healed, healed);
            // PV rendus au joueur local, de toute source (cartes, reliques, potions, feu de camp…) : onglets Combat
            // (pendant un combat) et Game
            if (healed > 0 && creature.Player is { } player && player == _player)
            {
                if (CombatManager.Instance.IsInProgress)
                {
                    if (creature.CombatState?.IsLiveCombat() ?? true)
                        AddToCombat(Healed, healed);
                }
                else
                {
                    _run.Values[Healed] = _run[Healed] + healed;
                    Changed?.Invoke();
                }
            }
        }
    }

    [HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.GainEnergy))]
    public static class EnergyPatch
    {
        public static void Prefix(decimal amount)
        {
            if (amount > 0)
                Current?.Add(EnergyGained, amount);
        }
    }

    [HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.GainGold))]
    public static class GoldPatch
    {
        public static void Prefix(decimal amount)
        {
            if (amount > 0)
                Current?.Add(Gold, amount);
        }
    }

    /// <summary>La carte elle-même, en train d'être jouée (elle part à la défausse ou est épuisée après son jeu).</summary>
    private static bool IsPlaying(CardModel card) => _frames.Any(f => f.PlayedCard == card);
}
