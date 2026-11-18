using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.FoolArrows;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.ValueProps;

namespace StsTools.Game;

/// <summary>
/// Joue une carte « à blanc » pour en prévoir l'effet (<see cref="TargetPreview"/>), avec le code du jeu lui-même :
/// celui de la carte, de son enchantement, et des reliques, pouvoirs et cartes qui réagissent (avant et après la
/// carte jouée, attaque). Rien de la partie ne change :
/// <list type="bullet">
/// <item>chaque écouteur du jeu est remplacé par une copie (<see cref="AbstractModel.MutableClone"/>) : ses compteurs
/// (Shuriken, Rainbow Ring…) avancent sur la copie ;</item>
/// <item>chaque commande du jeu (dégâts, Block, pouvoirs, énergie, pioche…) est interceptée : notée, pas exécutée.
/// Dégâts, Block et pouvoirs sont calculés par les calculs du jeu (Force, Vulnerable, Cruelty, Miniature Cannon…) ;
/// les pouvoirs donnés et le Block, les PV perdus restent dans l'état simulé et comptent pour la suite ;</item>
/// <item>les générateurs aléatoires sont remis dans leur état d'avant ; une carte créée n'est pas inscrite au
/// combat.</item>
/// </list>
/// Le marqueur de simulation est un <see cref="AsyncLocal{T}"/> : une suite asynchrone qui reprendrait plus tard
/// reste simulée. Une empreinte de l'état (PV, Block, pouvoirs, énergie, mains) avant et après le vérifie.
/// </summary>
public sealed class PlaySimulation
{
    private static AsyncLocal<PlaySimulation?> CurrentSim => SimulationFlag.Current;

    /// <summary>Dernière étape de l'aperçu en cours (rapport de gel, <see cref="BugReport"/>) ; null : aucun aperçu.</summary>
    public static volatile string? DebugState;

    /// <summary>Étape de l'aperçu en cours : trace pas à pas (<see cref="BugReport.PreviewStep"/>) et dernière étape connue.</summary>
    private static void Trace(string what)
    {
        DebugState = what;
        BugReport.PreviewStep(what);
    }

    private static bool _testModeBefore;

    /// <summary>
    /// Début de simulation. Le mode test du jeu est activé le temps du calcul : le jeu y saute lui-même son affichage
    /// (nœuds des créatures, effets visuels…), que le code simulé toucherait sinon pour de vrai (Reattach rendait un
    /// segment de Decimillipede impossible à viser et effaçait son intention).
    /// </summary>
    private static void Enter(PlaySimulation sim)
    {
        _testModeBefore = MegaCrit.Sts2.Core.TestSupport.TestMode.IsOn;
        MegaCrit.Sts2.Core.TestSupport.TestMode.IsOn = true;
        CurrentSim.Value = sim;
    }

    private static void Exit()
    {
        CurrentSim.Value = null;
        MegaCrit.Sts2.Core.TestSupport.TestMode.IsOn = _testModeBefore;
    }

    /// <summary>Une simulation est en cours : les stats ne comptent rien (<see cref="CardRunStats"/>).</summary>
    public static bool Active => SimulationFlag.Current.Value != null;

    // ---- Résultat ----

    /// <summary>Noms des caractéristiques notées (en plus des noms des pouvoirs).</summary>
    public static class Stat
    {
        public const string Damage = "Damage";
        public const string Hits = "Hits";
        public const string ThroughBlock = "Through Block";
        public const string HpLost = "HP lost";
        public const string Lethal = "Lethal";

        /// <summary>Mort après avoir agi (Doom à la fin du tour ennemi) : n'empêche pas son attaque.</summary>
        public const string LethalLate = "Dies after acting";
        public const string Stunned = "Stunned";
        public const string Block = "Block";
        public const string Healed = "Healed";
        public const string Energy = "Energy";
        public const string Stars = "Stars";
        public const string Draw = "Draw";
        public const string Created = "Cards created";
        public const string Avoided = "Incoming damage avoided";
        public const string Prevented = "Damage prevented";
        public const string Channel = "Channel ";

        /// <summary>Devant le nom d'un pouvoir changé en fin de tour à cause de la carte.</summary>
        public const string EndOfTurn = "End of turn: ";
        public const string ArrowsShot = "Arrows shot";
        public const string ArrowsRetrieved = "Arrows retrieved";
        public const string ArrowSlots = "Arrow slots";
        public const string Upgraded = "Cards upgraded";
        public const string Transformed = "Cards transformed";

        /// <summary>Suivi du nom d'une pile (« Cards to Hand »).</summary>
        public const string MovedTo = "Cards to ";
    }

    /// <summary>
    /// Effet de la carte : pour chaque créature touchée, ses caractéristiques (dans l'ordre où elles arrivent) ;
    /// pour un joueur, aussi l'énergie, la pioche… (sur sa créature). Moyennes si l'effet dépend du hasard.
    /// </summary>
    public sealed class Outcome
    {
        public readonly Dictionary<Creature, Dictionary<string, double>> Creatures = new();

        /// <summary>Icônes des pouvoirs notés (nom → chemin).</summary>
        public readonly Dictionary<string, string> PowerIcons = new();

        /// <summary>Nombre de simulations moyennées (plus d'une : l'effet dépend du hasard).</summary>
        public int Runs = 1;

        /// <summary>Une partie du code n'a pas pu être simulée : l'aperçu peut être incomplet.</summary>
        public bool Incomplete;

        public Dictionary<string, double> Of(Creature creature) =>
            Creatures.TryGetValue(creature, out var stats) ? stats : Creatures[creature] = new Dictionary<string, double>();

        public void Add(Creature creature, string stat, double value)
        {
            var stats = Of(creature);
            stats[stat] = stats.GetValueOrDefault(stat) + value;
        }

        /// <summary>Moyenne de plusieurs simulations (une absence compte pour zéro ; Lethal devient une probabilité).</summary>
        /// <summary>Plus grande valeur quand elle dépend d'un choix du joueur (la valeur notée est la plus petite).</summary>
        public readonly Dictionary<Creature, Dictionary<string, double>> Max = new();

        /// <summary>Premier choix libre en main rencontré par la simulation (candidats, nombre à choisir).</summary>
        internal (int Index, List<CardModel> Cards, int Min, int Max)? OpenChoice;

        /// <summary>
        /// Effet selon chaque choix possible : ce qui est pareil dans tous les cas est certain ; le reste va de la
        /// plus petite à la plus grande valeur (une absence compte pour zéro).
        /// </summary>
        public static Outcome Range(IReadOnlyList<Outcome> options)
        {
            var range = new Outcome
            {
                Runs = options.Max(o => o.Runs),
                Incomplete = options.Any(o => o.Incomplete),
            };
            foreach (var creature in options.SelectMany(o => o.Creatures.Keys).Distinct().ToList())
                foreach (var stat in options.SelectMany(o => o.Creatures.GetValueOrDefault(creature)?.Keys ?? Enumerable.Empty<string>()).Distinct().ToList())
                {
                    var values = options.Select(o => o.Creatures.GetValueOrDefault(creature)?.GetValueOrDefault(stat) ?? 0).ToList();
                    range.Of(creature)[stat] = values.Min();
                    if (values.Max() - values.Min() >= 0.05)
                    {
                        if (!range.Max.TryGetValue(creature, out var max))
                            range.Max[creature] = max = new Dictionary<string, double>();
                        max[stat] = values.Max();
                    }
                }
            foreach (var option in options)
                foreach (var (name, path) in option.PowerIcons)
                    range.PowerIcons[name] = path;
            return range;
        }

        /// <summary>
        /// Effet qui dépend du hasard : plus petite et plus grande valeur obtenues (la valeur notée est l'espérance).
        /// </summary>
        public readonly Dictionary<Creature, Dictionary<string, (double Low, double High)>> Spread = new();

        public static Outcome Average(IReadOnlyList<Outcome> runs)
        {
            var average = new Outcome { Runs = runs.Count, Incomplete = runs.Any(r => r.Incomplete) };
            foreach (var creature in runs.SelectMany(r => r.Creatures.Keys).Distinct().ToList())
                foreach (var stat in runs.SelectMany(r => r.Creatures.GetValueOrDefault(creature)?.Keys ?? Enumerable.Empty<string>()).Distinct().ToList())
                {
                    var values = runs.Select(r => r.Creatures.GetValueOrDefault(creature)?.GetValueOrDefault(stat) ?? 0).ToList();
                    if (values.Max() - values.Min() < 0.05)
                        continue;
                    if (!average.Spread.TryGetValue(creature, out var spread))
                        average.Spread[creature] = spread = new Dictionary<string, (double, double)>();
                    spread[stat] = (values.Min(), values.Max());
                }
            foreach (var run in runs)
            {
                foreach (var (creature, stats) in run.Creatures)
                    foreach (var (stat, value) in stats)
                        average.Add(creature, stat, value / runs.Count);
                foreach (var (name, path) in run.PowerIcons)
                    average.PowerIcons[name] = path;
            }
            return average;
        }
    }

    // ---- État simulé ----

    private readonly ICombatState _combat;
    private readonly Creature? _aimed;
    private Outcome _outcome = new();

    /// <summary>Écouteur réel → sa copie ; les copies (et pouvoirs ajoutés) eux-mêmes.</summary>
    private readonly Dictionary<AbstractModel, AbstractModel> _shadows = new(ReferenceEqualityComparer.Instance);

    private readonly HashSet<AbstractModel> _clones = new(ReferenceEqualityComparer.Instance);

    /// <summary>Pouvoirs donnés pendant la simulation (absents de la partie).</summary>
    private readonly List<PowerModel> _added = [];

    /// <summary>Pouvoirs retirés (réels ou copies).</summary>
    private readonly HashSet<AbstractModel> _removed = new(ReferenceEqualityComparer.Instance);

    /// <summary>PV et Block simulés de chaque créature touchée.</summary>
    private readonly Dictionary<Creature, int[]> _bodies = new();

    /// <summary>
    /// Pendant un événement (carte jouée, attaque…), les écouteurs qu'on ne sait pas copier sont écartés (leur
    /// réaction changerait la vraie partie), sauf les monstres, dont l'état est remis après. Pour les calculs purs,
    /// tous comptent.
    /// </summary>
    private bool _inEvent;

    private PlaySimulation(ICombatState combat, Creature? aimed)
    {
        _combat = combat;
        _aimed = aimed;
    }

    /// <summary>Simulations pour la moyenne d'un effet qui dépend du hasard.</summary>
    private const int RandomRuns = 12;

    /// <summary>
    /// Effet de <paramref name="card"/> (en main) jouée sur <paramref name="target"/> (null : carte sans cible) ;
    /// null si impossible. Si le hasard intervient (cibles au hasard, effets aléatoires), moyenne de plusieurs
    /// simulations, chacune avec d'autres tirages.
    /// </summary>
    /// <param name="selection">
    /// cartes choisies par le joueur dans la sélection en cours (écran de défausse, d'épuisement…) pendant que la
    /// carte se joue : la simulation reprend la carte à ce choix (ce qui le précède est déjà fait dans la partie)
    /// </param>
    /// <param name="selectionIndex">rang de ce choix parmi les choix en main de la carte</param>
    public static Outcome? Run(CardModel card, Creature? target, IReadOnlyList<CardModel>? selection = null, int selectionIndex = 0)
    {
        if (!EnsurePatched())
            return null;
        if (Active || card.CombatState is not { } combat || card.Owner is not { } player)
            return null;
        var choice = selection == null ? null : new Choice(selection, selectionIndex, Resume: true);
        var act = new Act(card.Id.Entry, sim => sim.Play(card, player, target), card);
        return Traced(act, target, () =>
        {
            var rngs = RngState.Capture(combat);
            var baseline = BaselineOf(combat, player.Creature, rngs);
            return WithChoices(act, player, target, choice, combat, rngs, baseline);
        });
    }

    /// <summary>Effet de <paramref name="potion"/> utilisée en combat sur <paramref name="target"/> ; null si impossible.</summary>
    public static Outcome? RunPotion(PotionModel potion, Creature? target)
    {
        if (!EnsurePatched())
            return null;
        if (Active || potion.Owner is not { } player || player.Creature.CombatState is not { } combat)
            return null;
        // Sans cible, une potion qui peut viser son lanceur le vise (comme le jeu)
        if (target == null && potion.IsValidTarget(player.Creature))
            target = player.Creature;
        var act = new Act(potion.Id.Entry, sim => sim.UsePotion(potion, target), null);
        return Traced(act, target, () =>
        {
            var rngs = RngState.Capture(combat);
            var baseline = BaselineOf(combat, player.Creature, rngs);
            return WithChoices(act, player, target, null, combat, rngs, baseline);
        });
    }

    /// <summary>Aperçu suivi par le rapport de bug : trace pas à pas, durée, étapes, résultat partiel.</summary>
    private static Outcome Traced(Act act, Creature? target, Func<Outcome> run)
    {
        BugReport.BeginPreview($"{act.Name} on {target?.Name ?? "-"}");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var outcome = run();
        BugReport.EndPreview($"preview {act.Name} on {target?.Name ?? "-"}: {watch.ElapsedMilliseconds} ms"
            + (outcome.Incomplete ? ", partial" : "") + (outcome.Runs > 1 ? $", {outcome.Runs} draws" : ""));
        DebugState = null;
        return outcome;
    }

    /// <summary>
    /// Choix libre en main (Burning Pact, potion de défausse…) : chaque choix possible ; le certain d'un côté, le
    /// reste en fourchette.
    /// </summary>
    private static Outcome WithChoices(Act act, Player player, Creature? target, Choice? choice, ICombatState combat,
        RngState rngs, Baseline baseline)
    {
        var outcome = Simulate(act, player, target, choice, combat, rngs, baseline);
        if (choice == null && outcome.OpenChoice is { } open && Options(open.Cards, open.Min, open.Max) is { Count: > 1 } options)
            outcome = Outcome.Range(options.Select(o =>
                Simulate(act, player, target, new Choice(o, open.Index, Resume: false), combat, rngs, baseline)).ToList());
        return outcome;
    }

    private static readonly MethodInfo OnUseMethod = AccessTools.Method(typeof(PotionModel), "OnUse");

    /// <summary>Comme le jeu : la potion quitte son emplacement, réactions avant, son effet, réactions après.</summary>
    private void UsePotion(PotionModel potion, Creature? target)
    {
        var shadow = (PotionModel)ShadowOf(potion);
        _removed.Add(potion);
        _removed.Add(shadow);
        var context = new BlockingPlayerChoiceContext();
        Event(() => Hook.BeforePotionUsed(_combat.RunState, _combat, shadow, target));
        Wait("potion", (Task)OnUseMethod.Invoke(shadow, [context, target])!);
        Event(() => Hook.AfterPotionUsed(_combat.RunState, _combat, shadow, target));
    }

    /// <summary>Choix possibles : chaque groupe du nombre minimum de cartes (au moins une), dix au plus.</summary>
    private static List<List<CardModel>> Options(List<CardModel> cards, int min, int max)
    {
        int size = Math.Clamp(min, 1, Math.Max(1, max));
        var options = new List<List<CardModel>>();
        void Pick(int from, List<CardModel> picked)
        {
            if (options.Count >= 10)
                return;
            if (picked.Count == size)
            {
                options.Add(picked.ToList());
                return;
            }
            for (int i = from; i < cards.Count; i++)
            {
                picked.Add(cards[i]);
                Pick(i + 1, picked);
                picked.RemoveAt(picked.Count - 1);
            }
        }
        Pick(0, []);
        return options;
    }

    /// <summary>Une simulation, ou la moyenne de plusieurs si le hasard intervient.</summary>
    private static Outcome Simulate(Act act, Player player, Creature? target, Choice? choice, ICombatState combat,
        RngState rngs, Baseline baseline)
    {
        var first = RunOnce(act, player, target, choice, combat, rngs, 0, baseline, out bool random);
        if (!random)
            return first;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var runs = new List<Outcome> { first };
        for (int i = 1; i < RandomRuns; i++)
            runs.Add(RunOnce(act, player, target, choice, combat, rngs, i, baseline, out _));
        // Moyenne coûteuse : signalée si elle ralentit le jeu
        if (watch.ElapsedMilliseconds > 100 && Reported.Add("slow:" + act.Name))
            Log.Info($"[{ModEntry.Id}] preview of {act.Name}: {RandomRuns} draws in {watch.ElapsedMilliseconds} ms");
        var average = Outcome.Average(runs);
        average.OpenChoice = first.OpenChoice;
        return average;
    }

    /// <summary>Ce qui attend le joueur sans jouer la carte : dégâts qu'il prendra, attaques de chaque ennemi.</summary>
    /// <param name="Allies">dégâts que chaque allié du joueur (multijoueur) prendra d'ici l'attaque des ennemis</param>
    private sealed record Baseline(int Taken, Dictionary<Creature, int> Incoming, Outcome End, bool Dies = false,
        AbstractModel? ExtraTurn = null, Dictionary<Creature, int>? Allies = null, IReadOnlyList<Creature>? DeadEnemies = null, IReadOnlyList<Creature>? LateDeadEnemies = null);

    /// <summary>Ce qui accorde un tour supplémentaire au joueur en fin de tour (Pael's Eye…), dans la simulation.</summary>
    private AbstractModel? _extraTurn;

    /// <summary>Choix du joueur en cours : cartes choisies, rang du choix.</summary>
    /// <summary>Ce qui est simulé : une carte jouée ou une potion utilisée.</summary>
    private sealed record Act(string Name, Action<PlaySimulation> Run, CardModel? Card);

    /// <param name="Resume">la carte se joue déjà : ce qui précède le choix est déjà fait (rien n'est noté)</param>
    private sealed record Choice(IReadOnlyList<CardModel> Cards, int Index, bool Resume);

    /// <summary>
    /// Si le joueur termine son tour maintenant : PV qu'il aura perdus quand les ennemis auront attaqué (fin de tour,
    /// puis attaques moins son Block), et ses PV actuels ; null si impossible.
    /// </summary>
    /// <returns>Forced : le joueur meurt déjà pendant la fin de tour ou le début du tour ennemi (Sandpit…), mort
    /// que rien n'a empêchée</returns>
    /// <returns>ExtraTurn : ce qui accorde un tour supplémentaire en terminant le tour maintenant (Pael's Eye…)</returns>
    /// <returns>DeadEnemies : ennemis qui meurent d'ici leur attaque sans que le joueur fasse rien (poison, Doom…)</returns>
    public static (int Taken, int Hp, bool Forced, AbstractModel? ExtraTurn, IReadOnlyList<Creature> DeadEnemies,
        IReadOnlyList<Creature> LateDeadEnemies)? PredictTurnEnd(Player player)
    {
        if (!EnsurePatched() || Active || player.Creature.CombatState is not { } combat)
            return null;
        var rngs = RngState.Capture(combat);
        var baseline = BaselineOf(combat, player.Creature, rngs);
        return (baseline.Taken, player.Creature.CurrentHp, baseline.Dies, baseline.ExtraTurn, baseline.DeadEnemies ?? [],
            baseline.LateDeadEnemies ?? []);
    }

    private static Baseline BaselineOf(ICombatState combat, Creature me, RngState rngs)
    {
        // PV réels avant la simulation (pendant, la créature porte les PV simulés)
        int hp = me.CurrentHp;
        var sim = new PlaySimulation(combat, null);
        Enter(sim);
        try
        {
            var (taken, incoming, end, allies) = sim.UntilEnemyAttacks(me);
            bool dies = sim._dead.Contains(me);
            return new Baseline(dies ? Math.Max(taken, hp) : taken, incoming, end, dies, sim._extraTurn, allies,
                sim._dead.Where(c => c.IsEnemy && !sim._lateDead.Contains(c)).ToList(), sim._lateDead.Where(c => c.IsEnemy).ToList());
        }
        catch (Exception e)
        {
            sim.Fail("turn end", e);
            var incoming = IncomingDamage(combat);
            return new Baseline(Math.Max(0, incoming.Values.Sum() - me.Block), incoming, new Outcome());
        }
        finally
        {
            sim.RestoreBodies();
            Exit();
            rngs.Restore();
        }
    }

    private static readonly Func<AbstractModel, PlayerChoiceContext, CombatSide, IEnumerable<Creature>, Task>[] BeforeTurnEndSteps =
    [
        (m, c, s, p) => m.BeforeSideTurnEndVeryEarly(c, s, p),
        (m, c, s, p) => m.BeforeSideTurnEndEarly(c, s, p),
        (m, c, s, p) => m.BeforeSideTurnEnd(c, s, p),
    ];

    private static readonly Func<AbstractModel, PlayerChoiceContext, CombatSide, IEnumerable<Creature>, Task>[] AfterTurnEndSteps =
    [
        (m, c, s, p) => m.AfterSideTurnEnd(c, s, p),
        (m, c, s, p) => m.AfterSideTurnEndLate(c, s, p),
    ];

    /// <summary>Chaque écouteur (copies) réagit, dans l'ordre du jeu.</summary>
    private void EachListener(Func<AbstractModel, Task> call) =>
        Event(async () =>
        {
            foreach (var model in _combat.IterateHookListeners().ToList())
            {
                Trace($"listener {model.GetType().Name}");
                await call(model);
            }
        });

    /// <summary>
    /// De maintenant jusqu'à l'attaque des ennemis, comme le jeu (sur les copies, sans le reste du jeu) : fin du
    /// tour des joueurs (Plating, Metallicize, orbes, cartes en main comme Burn, Ethereal…), puis début du tour des
    /// ennemis (Poison qui peut les tuer…). Renvoie les dégâts que le joueur aura pris d'ici là plus ceux de leurs
    /// attaques qui passeront son Block, et l'attaque annoncée de chaque ennemi à ce moment. Ce qui se passe pendant
    /// n'est pas affiché : ce n'est pas l'effet de la carte.
    /// </summary>
    /// <param name="played">carte jouée : plus en main à la fin du tour</param>
    /// <returns>Allies : la même chose pour chaque allié du joueur (multijoueur), les attaques calculées contre lui</returns>
    private (int Taken, Dictionary<Creature, int> Incoming, Outcome End, Dictionary<Creature, int> Allies) UntilEnemyAttacks(
        Creature me, CardModel? played = null)
    {
        var kept = _outcome;
        var end = _outcome = new Outcome();
        int hpBefore = Body(me)[0];
        var allies = _combat.Players.Select(p => p.Creature).Where(c => c != me && !IsDead(c)).ToList();
        var alliesBefore = allies.ToDictionary(c => c, c => Body(c)[0]);
        // PV perdus par chaque allié depuis le début (fin de tour…), plus les attaques qui passeront son Block
        Dictionary<Creature, int> AlliesTaken(bool attacked) => allies.ToDictionary(c => c, c =>
            Math.Max(0, alliesBefore[c] - Body(c)[0])
            + (attacked && !IsDead(c) ? Math.Max(0, IncomingAgainst(c) - Body(c)[1]) : 0));
        try
        {
            var context = new BlockingPlayerChoiceContext();
            var players = _combat.Players.Select(p => p.Creature).ToList();
            // Fin de la phase où l'on joue (Howl from Beyond épuisé se joue…)
            foreach (var player in _combat.Players.Where(p => !IsDead(p.Creature)))
                EachListener(m => m.AfterAutoPostPlayPhaseEntered(context, player));
            foreach (var step in BeforeTurnEndSteps)
                EachListener(m => step(m, context, CombatSide.Player, players));
            foreach (var player in _combat.Players)
            {
                if (player.PlayerCombatState is not { } state || IsDead(player.Creature))
                    continue;
                foreach (var orb in state.OrbQueue.Orbs.ToList())
                    Event(() => ((OrbModel)ShadowOf(orb)).BeforeTurnEndOrbTrigger(context));
                foreach (var card in state.Hand.Cards.Where(c => c.HasTurnEndInHandEffect && c != played && c.Pile?.Type == PileType.Hand).ToList())
                    Event(() => ((CardModel)ShadowOf(card)).OnTurnEndInHandWrapper(context));
            }
            foreach (var step in AfterTurnEndSteps)
                EachListener(m => step(m, context, CombatSide.Player, players));
            // Tour supplémentaire (Pael's Eye sans carte jouée ce tour…) : les ennemis ne jouent pas avant le
            // prochain tour du joueur, ni leur début de tour ni leurs attaques
            if (me.Player is { } extraTurnPlayer && Hook.ShouldTakeExtraTurn(_combat, extraTurnPlayer, out var extraTurnSource))
            {
                _extraTurn = extraTurnSource is { } copy && _realOf.TryGetValue(copy, out var real) ? real : extraTurnSource;
                return (Math.Max(0, hpBefore - Body(me)[0]), _combat.Enemies.ToDictionary(e => e, _ => 0), end, AlliesTaken(false));
            }
            var enemies = _combat.Enemies.Where(e => !IsDead(e)).ToList();
            EachListener(m => m.BeforeSideTurnStart(context, CombatSide.Enemy, enemies, _combat));
            EachListener(m => m.AfterSideTurnStart(CombatSide.Enemy, enemies, _combat));
            EachListener(m => m.AfterSideTurnStartLate(CombatSide.Enemy, enemies, _combat));
        }
        finally
        {
            kept.Incomplete |= _outcome.Incomplete;
            _outcome = kept;
        }
        var incoming = IncomingDamage(_combat, this);
        var alliesTaken = AlliesTaken(true);
        int lost = Math.Max(0, hpBefore - Body(me)[0]);
        int block = IsDead(me) ? 0 : Body(me)[1];
        // Fin du tour ennemi, après leurs attaques (Doom les tue alors) : ces morts n'empêchent pas leur attaque
        var kept2 = _outcome;
        _outcome = end;
        _late = true;
        try
        {
            var context = new BlockingPlayerChoiceContext();
            var enemies = _combat.Enemies.Where(e => !IsDead(e)).ToList();
            foreach (var step in BeforeTurnEndSteps)
                EachListener(m => step(m, context, CombatSide.Enemy, enemies));
        }
        catch (Exception e)
        {
            Fail("enemy turn end", e);
        }
        finally
        {
            _late = false;
            kept.Incomplete |= _outcome.Incomplete;
            _outcome = kept2;
        }
        return (lost + Math.Max(0, incoming.Values.Sum() - block), incoming, end, alliesTaken);
    }

    private static Outcome RunOnce(Act act, Player player, Creature? target, Choice? choice, ICombatState combat,
        RngState rngs, int draw, Baseline baseline, out bool random)
    {
        var sim = new PlaySimulation(combat, target) { _choice = choice, _muted = choice is { Resume: true }, _name = act.Name };
        Trace($"run {draw}{(choice != null ? $" choice {string.Join("+", choice.Cards.Select(c => c.Id.Entry))}" : "")}");
        var fingerprint = Fingerprint(combat);
        if (draw > 0)
            rngs.Perturb(draw);
        Enter(sim);
        try
        {
            act.Run(sim);
            // Jusqu'à l'attaque des ennemis : dégâts que le joueur prendra (fin de tour comprise : Burn, Plating…),
            // et attaque de chaque ennemi, sans et avec la carte (ennemi tué, étourdi, affaibli, empoisonné…)
            var me = player.Creature;
            var (taken, incoming, end, allies) = sim.UntilEnemyAttacks(me, act.Card);
            // Ce que la carte change d'ici l'attaque des ennemis (Howl from Beyond épuisé qui se joue, Block du
            // Plating donné…) : écart avec la même fin de tour sans la carte
            sim.MergeDifference(end, baseline.End);
            foreach (var (enemy, after) in incoming)
                if (baseline.Incoming.GetValueOrDefault(enemy) - after is var avoided and not 0)
                    sim._outcome.Add(enemy, Stat.Avoided, avoided);
            if (baseline.Taken - taken is var prevented and not 0)
                sim._outcome.Add(me, Stat.Prevented, prevented);
            // Multijoueur : dégâts évités (ou ajoutés) à chaque allié, encadré à côté de lui (ennemi tué, Weak,
            // Block donné…)
            foreach (var (ally, after) in allies)
                if (baseline.Allies?.TryGetValue(ally, out var before) == true && before - after is var saved and not 0)
                    sim._outcome.Add(ally, Stat.Prevented, saved);
        }
        catch (Exception e)
        {
            sim.Fail("play", e);
        }
        finally
        {
            sim.RestoreBodies();
            Exit();
            random = rngs.Changed();
            rngs.Restore();
        }
        var state = Fingerprint(combat);
        if (!state.SequenceEqual(fingerprint))
            Log.Warn($"[{ModEntry.Id}] the preview of {act.Name} changed the game state: "
                + string.Join(", ", fingerprint.Except(state).Take(6)) + " -> " + string.Join(", ", state.Except(fingerprint).Take(6)));
        return sim._outcome;
    }

    /// <param name="auto">jouée par un effet (Sly défaussée, Howl from Beyond…) : sans payer son coût</param>
    private void Play(CardModel card, Player player, Creature? target, bool auto = false)
    {
        Trace($"play {card.Id.Entry}{(auto ? " (auto)" : "")} on {target?.Name ?? "-"}");
        card = RealOf(card);
        var shadowCard = (CardModel)ShadowOf(card);
        int cost = card.EnergyCost.CostsX ? card.Owner.PlayerCombatState?.Energy ?? 0 : card.EnergyCost.GetAmountToSpend();
        int energy = auto ? 0 : cost;
        int stars = auto ? 0 : Math.Max(0, card.GetStarCostWithModifiers());
        if (shadowCard.EnergyCost.CostsX)
            shadowCard.EnergyCost.CapturedXValue = cost;
        var context = new BlockingPlayerChoiceContext();
        MoveTo(card, PileType.Play);
        if (!auto)
            Event(() => Hook.AfterEnergySpent(_combat, shadowCard, energy));

        int playCount = PlayCount(shadowCard, target);
        var resultPile = ResultPile(shadowCard);
        for (int i = 0; i < playCount; i++)
        {
            if (IsDead(player.Creature))
                break;
            var play = new CardPlay
            {
                Card = shadowCard,
                Player = player,
                Target = target,
                ResultPile = resultPile,
                Resources = new ResourceInfo { EnergySpent = energy, EnergyValue = cost, StarsSpent = stars, StarValue = stars },
                IsAutoPlay = auto,
                PlayIndex = i,
                PlayCount = playCount,
            };
            var history = CombatManager.Instance.History;
            // Reprise après un choix : le début de la carte est déjà dans l'historique réel
            if (!_muted)
                AddHistory(new CardPlayStartedEntry(play, _combat.RoundNumber, _combat.CurrentSide, history, _combat.Players));
            Event(() => Hook.BeforeCardPlayed(_combat, play));
            Wait("card", OnPlay(shadowCard, context, play));
            if (shadowCard.Enchantment is { } enchantment)
                Wait("enchantment", enchantment.OnPlay(context, play));
            if (shadowCard.Affliction is { } affliction)
                Wait("affliction", affliction.OnPlay(context, target));
            AddHistory(new CardPlayFinishedEntry(play, _combat.RoundNumber, _combat.CurrentSide, history, _combat.Players));
            Event(() => Hook.AfterCardPlayed(_combat, context, play));
        }
        MoveTo(card, resultPile);
    }

    /// <summary>Créature hors du combat (invoquée pendant la simulation).</summary>
    internal static Creature Detached(MonsterModel monster, CombatSide side, string? slot)
    {
        if (!monster.IsMutable)
            monster = monster.ToMutable();
        return new Creature(monster, side, slot);
    }

    /// <summary>Créature créée pendant la simulation (familier, invocation) : pas inscrite au combat.</summary>
    [HarmonyPatch(typeof(CombatState), nameof(CombatState.CreateCreature))]
    public static class CreateCreaturePatch
    {
        public static bool Prefix(MonsterModel monster, CombatSide side, string? slot, ref Creature __result)
        {
            if (!Active)
                return true;
            __result = Detached(monster, side, slot);
            return false;
        }
    }

    /// <summary>
    /// Enchantement (commande qui ne renvoie pas de tâche, donc pas interceptée) : pendant la simulation, sur la
    /// copie de la carte, jamais sur la vraie.
    /// </summary>
    [HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Enchant), [typeof(EnchantmentModel), typeof(CardModel), typeof(decimal)])]
    public static class EnchantPatch
    {
        public static void Prefix(ref CardModel card)
        {
            if (CurrentSim.Value is { _finished: false } sim)
                card = (CardModel)sim.ShadowOf(card);
        }
    }

    // ---- Flèches (The Fool) ----

    /// <summary>Flèches disponibles en plus (récupérées) ou en moins (tirées) pendant la simulation, par joueur.</summary>
    private readonly Dictionary<Player, int> _arrows = new();

    private int AvailableArrows(Player player) =>
        Math.Max(0, (player.PlayerCombatState?.AvailableArrowCount ?? 0) + _arrows.GetValueOrDefault(player));

    /// <summary>Impaled sur la créature dans l'état simulé (copies des réels, pouvoirs donnés, sans les retirés).</summary>
    private List<ImpaledPower> ImpaledOn(Creature target, Creature? applier) =>
        target.Powers.OfType<ImpaledPower>().Where(p => !_removed.Contains(p)).Select(p => (ImpaledPower)ShadowOf(p))
            .Concat(_added.OfType<ImpaledPower>().Where(p => p.Owner == target))
            .Where(p => !_removed.Contains(p) && (applier == null || p.Applier == applier))
            .Distinct().ToList();

    /// <summary>Comme le jeu : une flèche disponible part (réactions au tir), sinon rien.</summary>
    private ArrowSlot? TakeArrow(Player player, CardModel? source)
    {
        var context = new BlockingPlayerChoiceContext();
        if (AvailableArrows(player) <= 0)
        {
            Event(() => Hook.AfterArrowShot(_combat, player, null, context, source));
            return null;
        }
        _arrows[player] = _arrows.GetValueOrDefault(player) - 1;
        _outcome.Add(player.Creature, Stat.ArrowsShot, 1);
        // Flèche à part : les réactions peuvent la modifier, les vraies ne bougent pas
        var slot = new ArrowSlot(player) { Status = ArrowStatus.Away };
        Event(() => Hook.AfterArrowShot(_combat, player, slot, context, source));
        return slot;
    }

    private bool Shoot(Creature target, Player player, CardModel? card, AttackCommand? attack, Func<Task>? action)
    {
        if (IsDead(target) || TakeArrow(player, card) is not { } slot)
            return false;
        Impale(target, slot, action, attack, card);
        return true;
    }

    /// <summary>
    /// Comme le jeu : l'attaque de la flèche, puis Impaled sur la cible (une flèche de plus si elle l'est déjà),
    /// réactions ; flèche détruite, ou retirée si la cible est morte.
    /// </summary>
    private void Impale(Creature target, ArrowSlot slot, Func<Task>? action, AttackCommand? attack, CardModel? card)
    {
        if (IsDead(target))
            return;
        var context = new BlockingPlayerChoiceContext();
        if (attack != null)
            Wait("arrow attack", attack.Execute(context));
        else if (action != null)
            Wait("arrow action", action());
        var archer = slot.owner.Creature;
        var impaled = ImpaledOn(target, archer).FirstOrDefault();
        if (impaled == null)
        {
            Apply(ModelDb.Power<ImpaledPower>().ToMutable(), target, 1, archer, card);
            impaled = ImpaledOn(target, archer).FirstOrDefault();
        }
        if (impaled == null)
            return;
        impaled.AddArrow(slot);
        Event(() => Hook.AfterArrowImpaled(_combat, impaled, slot, context, card));
        if (slot.DestroyAfterImpaled)
        {
            impaled.RemoveFirstArrow();
            if (impaled.Arrows.Count == 0)
                Remove(impaled);
        }
        else if (IsDead(target))
            Dislodge(impaled, card);
    }

    /// <summary>Comme le jeu : la première flèche quitte Impaled et revient à son tireur (sauf détruite), réactions.</summary>
    private void Dislodge(ImpaledPower power, CardModel? source)
    {
        var impaled = (ImpaledPower)ShadowOf(power);
        if (impaled.Arrows.Count == 0)
            return;
        // Copie : une vraie flèche (déjà plantée avant l'aperçu) ne doit pas changer
        var arrow = (ArrowSlot)Copy.Object(impaled.RemoveFirstArrow())!;
        Copy.ClearEvents(arrow);
        if (arrow.DestroyAfterImpaled)
            return;
        _arrows[arrow.owner] = _arrows.GetValueOrDefault(arrow.owner) + 1;
        _outcome.Add(arrow.owner.Creature, Stat.ArrowsRetrieved, 1);
        Event(() => Hook.AfterArrowDislodged(_combat, impaled, arrow, new BlockingPlayerChoiceContext(), source));
    }

    /// <summary>Tests du jeu sur les flèches : dans l'état simulé pendant la simulation.</summary>
    [HarmonyPatch(typeof(ArrowCmd), nameof(ArrowCmd.CreatureIsImpaledByPlayersArrow))]
    public static class ImpaledByPlayerPatch
    {
        public static void Postfix(Creature creature, Player player, ref bool __result)
        {
            if (CurrentSim.Value is { _finished: false } sim)
                __result = sim.ImpaledOn(creature, player.Creature).Count > 0;
        }
    }

    [HarmonyPatch(typeof(ArrowCmd), nameof(ArrowCmd.CreatureIsImpaledByArrow))]
    public static class ImpaledPatch
    {
        public static void Postfix(Creature creature, ref bool __result)
        {
            if (CurrentSim.Value is { _finished: false } sim)
                __result = sim.ImpaledOn(creature, null).Count > 0;
        }
    }

    [HarmonyPatch(typeof(ArrowCmd), nameof(ArrowCmd.PlayerHasAnyArrows))]
    public static class HasArrowsPatch
    {
        public static void Postfix(Player player, ref bool __result)
        {
            if (CurrentSim.Value is { _finished: false } sim)
                __result = sim.AvailableArrows(player) > 0;
        }
    }

    [HarmonyPatch(typeof(ArrowCmd), nameof(ArrowCmd.GetAvailableArrowCount))]
    public static class ArrowCountPatch
    {
        public static void Postfix(Player player, ref int __result)
        {
            if (CurrentSim.Value is { _finished: false } sim)
                __result = sim.AvailableArrows(player);
        }
    }

    // ---- Historique du combat : les cartes simulées comptent (mélodies Wah, Cho, Ki…) ----

    private static readonly FieldInfo HistoryEntries = AccessTools.Field(typeof(CombatHistory), "_entries");

    /// <summary>Entrées ajoutées à l'historique réel pendant la simulation, retirées à la fin.</summary>
    private readonly List<CombatHistoryEntry> _history = [];

    /// <summary>
    /// Le jeu compte les cartes jouées ce tour dans son historique (première Wah du tour : Lost Lute, Motif, Song
    /// Path…) : la carte simulée y est ajoutée directement (sans ses événements), et retirée à la fin.
    /// </summary>
    private void AddHistory(CombatHistoryEntry entry)
    {
        if (HistoryEntries.GetValue(CombatManager.Instance.History) is List<CombatHistoryEntry> entries)
        {
            entries.Add(entry);
            _history.Add(entry);
        }
    }

    private void RemoveHistory()
    {
        if (_history.Count > 0 && HistoryEntries.GetValue(CombatManager.Instance.History) is List<CombatHistoryEntry> entries)
            foreach (var entry in _history)
                entries.Remove(entry);
        _history.Clear();
    }

    /// <summary>
    /// Notification « l'état du combat a changé » pour l'interface : pas pendant la simulation (rien n'a vraiment
    /// changé). En mode test (activé pendant la simulation), le jeu la refuse même par une exception, ce qui faisait
    /// échouer tous les aperçus.
    /// </summary>
    [HarmonyPatch(typeof(CombatStateTracker), "NotifyCombatStateChanged")]
    public static class CombatStateChangedPatch
    {
        public static bool Prefix() => !Active;
    }

    /// <summary>Bulle de dialogue : pas pendant la simulation.</summary>
    [HarmonyPatch(typeof(TalkCmd), nameof(TalkCmd.Play))]
    public static class TalkPatch
    {
        public static bool Prefix() => !Active;
    }

    /// <summary>
    /// Effets visuels créés directement par le code des cartes, pouvoirs et reliques (sans commande, donc sans le mode
    /// test : <c>NPowerUpVfx.CreateNormal</c> d'Inflame) : pas ajoutés à la scène pendant la simulation, et libérés.
    /// Sinon l'aperçu les affichait pour de vrai (carré blanc derrière le personnage en tenant Inflame).
    /// </summary>
    [HarmonyPatch]
    public static class AddNodePatch
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
        [
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Helpers.GodotTreeExtensions), nameof(MegaCrit.Sts2.Core.Helpers.GodotTreeExtensions.AddChildSafely)),
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Helpers.GodotTreeExtensions), nameof(MegaCrit.Sts2.Core.Helpers.GodotTreeExtensions.AddSiblingSafely)),
        ];

        public static bool Prefix(Godot.Node? child)
        {
            if (!Active)
                return true;
            if (child != null && Godot.GodotObject.IsInstanceValid(child) && !child.IsInsideTree())
                child.QueueFree();
            return false;
        }
    }

    /// <summary>Musique changée par un effet (Infested : deuxième phase de l'élite) : pas pendant la simulation.</summary>
    [HarmonyPatch]
    public static class MusicPatch
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
            typeof(MegaCrit.Sts2.Core.Nodes.Audio.NRunMusicController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.ReturnType == typeof(void) && !m.Name.StartsWith('_') && !m.IsSpecialName);

        public static bool Prefix() => !Active;
    }

    // ---- Piles simulées ----

    /// <summary>Pile où la simulation a mis une carte (jouée, défaussée, épuisée…), par carte réelle.</summary>
    private readonly Dictionary<CardModel, PileType> _piles = new(ReferenceEqualityComparer.Instance);

    /// <summary>Copie → carte réelle.</summary>
    private readonly Dictionary<AbstractModel, AbstractModel> _realOf = new(ReferenceEqualityComparer.Instance);

    private CardModel RealOf(CardModel card) => _realOf.TryGetValue(card, out var real) ? (CardModel)real : card;

    private void MoveTo(CardModel card, PileType pile) => _piles[RealOf(card)] = pile;

    /// <summary>
    /// Pile d'une carte pendant la simulation : celle où la simulation l'a mise, sinon celle de la carte réelle
    /// (une copie n'est dans aucune pile du jeu : sans ça, une carte copiée n'aurait pas de combat).
    /// </summary>
    private CardPile? PileOf(CardModel card, CardPile? actual)
    {
        var real = RealOf(card);
        if (_piles.TryGetValue(real, out var type))
            return real.Owner is { } owner ? CardPile.Get(type, owner) : actual;
        return ReferenceEquals(real, card) ? actual : real.Pile;
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.Pile), MethodType.Getter)]
    public static class PilePatch
    {
        public static void Postfix(CardModel __instance, ref CardPile? __result)
        {
            if (CurrentSim.Value is { } sim)
                __result = sim.PileOf(__instance, __result);
        }
    }

    // ---- Choix, défausse, épuisement, cartes jouées par un effet ----

    /// <summary>Choix du joueur en cours (aperçu pendant un écran de sélection), sinon null.</summary>
    private Choice? _choice;

    /// <summary>Avant le choix en cours : déjà fait dans la partie, rien n'est noté.</summary>
    private bool _muted;

    private int _handSelections;

    /// <summary>
    /// Choix en main : celui en cours de l'écran de sélection ; sinon imposé par le jeu (pas plus de cartes que le
    /// minimum à choisir : Survivor avec une seule autre carte en main) ; sinon inconnu, rien.
    /// </summary>
    private List<CardModel> SelectFromHand(Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter)
    {
        int index = _handSelections++;
        // Des copies, jamais les vraies cartes : le code qui suit les modifie souvent (Touch of Insanity les rend
        // gratuites…)
        if (_choice != null && index == _choice.Index)
        {
            _muted = false;
            return _choice.Cards.Select(Copied).ToList();
        }
        if (_muted || player.PlayerCombatState is not { } state)
            return [];
        var candidates = state.Hand.Cards.Where(c => c.Pile?.Type == PileType.Hand && (filter?.Invoke(c) ?? true)).ToList();
        var forced = Forced(candidates, prefs);
        // Choix libre : noté (le premier) pour simuler ensuite chaque choix possible
        if (forced.Count == 0 && candidates.Count > 0 && _outcome.OpenChoice == null && _choice == null)
            _outcome.OpenChoice = (index, candidates, prefs.MinSelect, prefs.MaxSelect);
        return forced.Select(Copied).ToList();
    }

    private CardModel Copied(CardModel card) => (CardModel)ShadowOf(RealOf(card));

    private static List<CardModel> Forced(List<CardModel> cards, CardSelectorPrefs prefs) =>
        cards.Count > 0 && !prefs.RequireManualConfirmation && cards.Count <= prefs.MinSelect ? cards : [];

    private void Exhaust(CardModel card, bool causedByEthereal)
    {
        card = RealOf(card);
        if (card.Pile?.Type == PileType.Exhaust)
            return;
        MoveTo(card, PileType.Exhaust);
        var shadow = (CardModel)ShadowOf(card);
        Event(() => Hook.AfterCardExhausted(_combat, new BlockingPlayerChoiceContext(), shadow, causedByEthereal));
    }

    /// <summary>Comme le jeu : défausse (réactions), pioche, puis les cartes Sly défaussées de la main se jouent.</summary>
    private void Discard(IEnumerable<CardModel> cards, int draw)
    {
        var context = new BlockingPlayerChoiceContext();
        var discarded = cards.Select(RealOf).Distinct().ToList();
        var sly = discarded.Where(c => c.Pile?.Type == PileType.Hand && c.IsSlyThisTurn).ToList();
        foreach (var card in discarded)
        {
            MoveTo(card, PileType.Discard);
            var shadow = (CardModel)ShadowOf(card);
            Event(() => Hook.AfterCardDiscarded(_combat, context, shadow));
        }
        if (draw > 0 && discarded.FirstOrDefault()?.Owner is { } owner)
            _outcome.Add(owner.Creature, Stat.Draw, draw);
        foreach (var card in sly)
            AutoPlay(card, null);
    }

    private int _autoPlays;

    /// <summary>Carte jouée par un effet (Sly, Howl from Beyond…), sur la cible visée ou le premier ennemi vivant.</summary>
    private void AutoPlay(CardModel card, Creature? target)
    {
        card = RealOf(card);
        if (_autoPlays > 5 || card.Owner is not { } owner || card.Keywords.Contains(CardKeyword.Unplayable))
            return;
        // Sans cible, comme le jeu : tirée au hasard (générateur du jeu, remis en place après ; changé d'une
        // simulation à l'autre pour la fourchette et l'espérance) parmi les créatures encore vivantes
        var rng = owner.RunState.Rng.CombatTargets;
        if (target == null && card.TargetType == TargetType.AnyEnemy)
            target = rng.NextItem(_combat.Enemies.Where(e => !IsDead(e) && e.IsHittable).ToList());
        else if (target == null && card.TargetType == TargetType.AnyAlly)
            target = rng.NextItem(_combat.Allies.Where(c => c.IsPlayer && c != owner.Creature && !IsDead(c)).ToList());
        if (target == null && card.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly)
            return;
        _autoPlays++;
        try
        {
            var shadow = (CardModel)ShadowOf(card);
            Event(() => Hook.BeforeCardAutoPlayed(_combat, shadow, target, AutoPlayType.Default));
            Play(card, owner, target, auto: true);
        }
        finally
        {
            _autoPlays--;
        }
    }

    /// <summary>
    /// Ce qui compte de la fin de tour dans l'effet de la carte : dégâts, morts, étourdis, Block, soins (Howl from
    /// Beyond épuisé, Plating donné…). Pas les pouvoirs : en fin de tour, ce sont surtout des restitutions (Tender
    /// rend la Force et la Dextérité perdues ce tour), qui annuleraient l'effet de la carte pendant le tour.
    /// </summary>
    private static readonly HashSet<string> TurnEndStats =
        [Stat.Damage, Stat.Hits, Stat.ThroughBlock, Stat.Lethal, Stat.LethalLate, Stat.Stunned, Stat.HpLost, Stat.Block, Stat.Healed];

    /// <summary>Ajoute l'écart entre deux fins de tour simulées (avec et sans la carte).</summary>
    private void MergeDifference(Outcome with, Outcome without)
    {
        foreach (var creature in with.Creatures.Keys.Union(without.Creatures.Keys).ToList())
        {
            var a = with.Creatures.GetValueOrDefault(creature);
            var b = without.Creatures.GetValueOrDefault(creature);
            foreach (var stat in (a?.Keys ?? Enumerable.Empty<string>()).Union(b?.Keys ?? Enumerable.Empty<string>()))
            {
                double delta = (a?.GetValueOrDefault(stat) ?? 0) - (b?.GetValueOrDefault(stat) ?? 0);
                if (delta == 0)
                    continue;
                // Pouvoirs changés en fin de tour (Tender rend la Force…) : à part, pour ne pas annuler l'effet de
                // la carte pendant le tour
                if (!TurnEndStats.Contains(stat))
                {
                    _outcome.Add(creature, Stat.EndOfTurn + stat, delta);
                    continue;
                }
                _outcome.Add(creature, stat, delta);
                // Mort, étourdi : oui ou non
                if (stat is Stat.Lethal or Stat.LethalLate or Stat.Stunned)
                    _outcome.Of(creature)[stat] = Math.Clamp(_outcome.Of(creature)[stat], 0, 1);
            }
        }
        foreach (var (name, path) in with.PowerIcons)
            _outcome.PowerIcons[name] = path;
    }

    private static readonly MethodInfo OnPlayMethod = AccessTools.Method(typeof(CardModel), "OnPlay");
    private static readonly MethodInfo PlayCountMethod = AccessTools.Method(typeof(CardModel), "GeneratePlayCount");

    /// <summary>
    /// Nombre de fois où la carte se joue, calculé par le jeu : rejeux de son enchantement (Glam), puis effets qui
    /// le modifient (Echo Form, Ki Verse…), et leurs réactions (sur les copies).
    /// </summary>
    private int PlayCount(CardModel shadowCard, Creature? target)
    {
        bool outer = _inEvent;
        _inEvent = true;
        try
        {
            if (PlayCountMethod.Invoke(shadowCard, [_combat, target]) is Task<int> { IsCompletedSuccessfully: true } task)
                return task.Result;
        }
        catch (Exception e)
        {
            Fail("play count", e);
        }
        finally
        {
            _inEvent = outer;
        }
        return Hook.ModifyCardPlayCount(_combat, shadowCard, 1, target, out _);
    }
    private static readonly MethodInfo ResultLocationMethod = AccessTools.Method(typeof(CardModel), "GetResultLocationForCardPlay");

    private static Task OnPlay(CardModel card, PlayerChoiceContext context, CardPlay play) =>
        (Task)OnPlayMethod.Invoke(card, [context, play])!;

    private static PileType ResultPile(CardModel card)
    {
        try
        {
            return ((CardLocation)ResultLocationMethod.Invoke(card, [])!).pileType;
        }
        catch (Exception)
        {
            return PileType.Discard;
        }
    }

    /// <summary>Lance un événement du jeu (ses écouteurs réagissent : copies seulement).</summary>
    private void Event(Func<Task> start, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        Trace($"event (PlaySimulation.cs line {line}) depth {_eventDepth}");
        // Réactions en chaîne trop profondes (boucle d'effets) : arrêtées avant de faire déborder la pile, ce qui
        // fermerait le jeu sans message
        if (_finished || _aborted)
            return;
        _steps++;
        if (_eventDepth >= MaxEventDepth)
        {
            Fail("event", new InvalidOperationException("chain of effects too deep"));
            return;
        }
        bool outer = _inEvent;
        _inEvent = true;
        _eventDepth++;
        try
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            SyncBodies();
            Wait("event", start());
        }
        catch (Exception e)
        {
            Fail("event", e);
        }
        finally
        {
            _eventDepth--;
            _inEvent = outer;
        }
    }

    private const int MaxEventDepth = 24;

    private int _eventDepth;

    /// <summary>
    /// Toutes les commandes étant interceptées, le code simulé se termine sans attendre. Sinon (attente d'autre
    /// chose), sa suite tournera plus tard, toujours simulée (AsyncLocal) : l'aperçu est seulement incomplet.
    /// </summary>
    private void Wait(string what, Task task)
    {
        if (!task.IsCompleted)
        {
            _outcome.Incomplete = true;
            // Sa suite reprendra plus tard : sans effet (simulation terminée, voir _finished), mais à connaître
            if (Reported.Add("unfinished:" + what + ":" + _name))
                Log.Info($"[{ModEntry.Id}] preview of {_name}: step {what} did not finish at once");
        }
        else if (task.Exception is { } e)
            Fail(what, e.InnerException ?? e);
    }

    private static readonly HashSet<string> Reported = [];

    private void Fail(string what, Exception e)
    {
        _outcome.Incomplete = true;
        // Une fois par problème : l'aperçu est recalculé souvent
        if (Reported.Add($"{what}:{e.GetType().Name}:{e.Message}"))
            Log.Info($"[{ModEntry.Id}] card preview ({what}) incomplete: {e.GetType().Name}: {e.Message}");
    }

    // ---- Écouteurs : copies ----

    private static bool Copyable(AbstractModel model) =>
        model is PowerModel or RelicModel or CardModel or EnchantmentModel or OrbModel or PotionModel or AfflictionModel;

    /// <summary>Écouteurs que voit le jeu pendant la simulation : copies, sans les retirés, plus les pouvoirs donnés.</summary>
    private IEnumerable<AbstractModel> Substitute(IEnumerable<AbstractModel> listeners, bool combat)
    {
        foreach (var model in listeners)
        {
            if (_removed.Contains(model))
                continue;
            if (_clones.Contains(model))
            {
                yield return model;
                continue;
            }
            // Pas de copie possible : dans les calculs, toujours ; dans les événements, seulement les monstres (leurs
            // champs et leur machine à états des coups sont sauvegardés et remis, l'affichage est coupé : la Queen
            // passe en coup enragé à la mort de son Amalgam…), pas les modificateurs de la partie
            if (!Copyable(model))
            {
                if (!_inEvent || model is MonsterModel)
                    yield return model;
                continue;
            }
            var shadow = ShadowOf(model);
            if (!_removed.Contains(shadow))
                yield return shadow;
        }
        if (combat)
            foreach (var power in _added.ToList())
                if (!_removed.Contains(power))
                    yield return power;
    }

    private static readonly FieldInfo PowerOwner = AccessTools.Field(typeof(PowerModel), "_owner");
    private static readonly FieldInfo PowerAmount = AccessTools.Field(typeof(PowerModel), "_amount");
    private static readonly FieldInfo PowerData = AccessTools.Field(typeof(PowerModel), "_internalData");

    /// <summary>Copie simulée d'un écouteur réel (créée à la première rencontre), ou l'objet lui-même s'il est simulé.</summary>
    private AbstractModel ShadowOf(AbstractModel model)
    {
        if (_clones.Contains(model))
            return model;
        if (_shadows.TryGetValue(model, out var shadow))
            return shadow;
        // Copie en cours de ce même écouteur (la copie relance un calcul du jeu qui parcourt les écouteurs) :
        // l'original, plutôt qu'une récursion sans fin
        if (!_copying.Add(model))
            return model;
        Trace($"copy {model.GetType().Name}");
        try
        {
            shadow = model.MutableClone();
        }
        finally
        {
            _copying.Remove(model);
        }
        if (model is PowerModel)
        {
            // La copie perd son porteur et ses données internes (compteurs) : on les lui rend
            PowerOwner.SetValue(shadow, PowerOwner.GetValue(model));
            PowerData.SetValue(shadow, Copy.Object(PowerData.GetValue(model)));
        }
        Copy.Collections(shadow);
        Copy.ClearEvents(shadow);
        _shadows[model] = shadow;
        _clones.Add(shadow);
        _realOf[shadow] = model;
        if (model is CardModel card && shadow is CardModel cardShadow)
        {
            // La copie d'une carte a sa propre copie d'enchantement et d'affliction
            if (card.Enchantment != null && cardShadow.Enchantment != null)
                Register(card.Enchantment, cardShadow.Enchantment);
            if (card.Affliction != null && cardShadow.Affliction != null)
                Register(card.Affliction, cardShadow.Affliction);
        }
        return shadow;
    }

    private readonly HashSet<AbstractModel> _copying = new(ReferenceEqualityComparer.Instance);

    private void Register(AbstractModel real, AbstractModel shadow)
    {
        _shadows[real] = shadow;
        _clones.Add(shadow);
    }

    [HarmonyPatch(typeof(CombatState), nameof(CombatState.IterateHookListeners))]
    public static class CombatListenersPatch
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(ref IEnumerable<AbstractModel> __result)
        {
            if (CurrentSim.Value is { } sim)
                __result = sim.Substitute(__result, true);
        }
    }

    [HarmonyPatch(typeof(RunState), nameof(RunState.IterateHookListeners))]
    public static class RunListenersPatch
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(ref IEnumerable<AbstractModel> __result)
        {
            if (CurrentSim.Value is { } sim)
                __result = sim.Substitute(__result, false);
        }
    }

    private static readonly FieldInfo CardOwner = AccessTools.Field(typeof(CardModel), "_owner");

    /// <summary>
    /// Carte créée ou ajoutée pendant la simulation : elle a son propriétaire, mais n'est inscrite ni au combat ni à
    /// la partie (Swipe du Thieving Hopper rendait la carte volée à chaque aperçu d'un coup fatal).
    /// </summary>
    [HarmonyPatch]
    public static class AddCardPatch
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
        [
            AccessTools.Method(typeof(CombatState), nameof(CombatState.AddCard), [typeof(CardModel), typeof(Player)]),
            AccessTools.Method(typeof(RunState), nameof(RunState.AddCard), [typeof(CardModel), typeof(Player)]),
        ];

        public static bool Prefix(CardModel card, Player owner)
        {
            if (!Active)
                return true;
            CardOwner.SetValue(card, owner);
            return false;
        }
    }

    /// <summary>Le même, pour les ajouts directs et les retraits (pas pendant la simulation).</summary>
    [HarmonyPatch]
    public static class CardRegistryPatch
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
        [
            AccessTools.Method(typeof(CombatState), "AddCard", [typeof(CardModel)]),
            AccessTools.Method(typeof(RunState), "AddCard", [typeof(CardModel)]),
            AccessTools.Method(typeof(CombatState), nameof(CombatState.RemoveCard)),
            AccessTools.Method(typeof(RunState), nameof(RunState.RemoveCard)),
        ];

        public static bool Prefix() => !Active;
    }

    /// <summary>Récompense de fin de combat ajoutée par un effet (carte volée rendue…) : pas pendant la simulation.</summary>
    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Rooms.CombatRoom), nameof(MegaCrit.Sts2.Core.Rooms.CombatRoom.AddExtraReward))]
    public static class ExtraRewardPatch
    {
        public static bool Prefix() => !Active;
    }

    // ---- PV et Block simulés ----

    private int[] Body(Creature creature)
    {
        if (_bodies.TryGetValue(creature, out var body))
            return body;
        _realBodies[creature] = (creature.CurrentHp, creature.Block);
        return _bodies[creature] = [creature.CurrentHp, creature.Block];
    }

    /// <summary>PV et Block réels des créatures touchées, remis en place à la fin de la simulation.</summary>
    private readonly Dictionary<Creature, (int Hp, int Block)> _realBodies = new();

    private static readonly FieldInfo HpField = AccessTools.Field(typeof(Creature), "_currentHp");
    private static readonly FieldInfo BlockField = AccessTools.Field(typeof(Creature), "_block");

    /// <summary>
    /// Le code du jeu lit les PV et le Block des créatures (Plow du Ceremonial Beast : étourdi sous un seuil de PV ;
    /// créature morte…) : pendant la simulation, ce sont les valeurs simulées, écrites directement (sans prévenir
    /// l'interface), et remises à la fin (<see cref="RestoreBodies"/>).
    /// </summary>
    private void SyncBodies()
    {
        if (_finished)
            return;
        foreach (var (creature, body) in _bodies)
        {
            HpField.SetValue(creature, Math.Max(0, body[0]));
            BlockField.SetValue(creature, Math.Max(0, body[1]));
        }
    }

    /// <summary>
    /// Simulation terminée : une suite asynchrone qui reprendrait plus tard (toujours simulée, AsyncLocal) n'a plus
    /// aucun effet, ni sur l'aperçu ni sur la partie (sinon elle réécrirait des PV simulés dans les vraies créatures).
    /// </summary>
    private bool _finished;

    /// <summary>
    /// Étapes simulées (commandes, événements) : au-delà, le code du jeu tourne en rond (boucle qui attend un
    /// changement que la simulation ne fait pas : flèche jamais retirée…). La simulation s'arrête alors en faisant
    /// échouer la commande suivante, ce qui sort de la boucle ; sinon le jeu restait figé.
    /// </summary>
    private int _steps;

    private const int MaxSteps = 3000;

    private bool _aborted;

    private sealed class AbortedException() : Exception("too many steps: the game code is looping");

    private void Step()
    {
        if (++_steps <= MaxSteps && !_aborted)
            return;
        if (!_aborted)
        {
            _aborted = true;
            Fail("loop", new AbortedException());
        }
        throw new AbortedException();
    }

    /// <summary>Nom de l'action simulée, pour le journal.</summary>
    private string _name = "";

    private void RestoreBodies()
    {
        _finished = true;
        RemoveHistory();
        foreach (var (creature, (hp, block)) in _realBodies)
        {
            HpField.SetValue(creature, hp);
            BlockField.SetValue(creature, block);
        }
    }

    private bool IsDead(Creature creature) => Body(creature)[0] <= 0;

    // ---- Commandes interceptées ----

    /// <summary>Commande du jeu appelée pendant la simulation : notée ; renvoie ce que la commande aurait renvoyé.</summary>
    private object? Intercept(MethodBase method, object?[] args)
    {
        if (_finished)
            return Defaults.For(((MethodInfo)method).ReturnType);
        Trace($"command {method.DeclaringType?.Name}.{method.Name} (step {_steps})");
        Step();
        try
        {
            // Pile presque pleine (effets en chaîne) : arrêt propre plutôt que la fermeture du jeu
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            var handled = Handle(method, args);
            SyncBodies();
            if (handled is { } result)
                return result;
        }
        catch (Exception e)
        {
            Fail($"{method.DeclaringType?.Name}.{method.Name}", e);
        }
        return Defaults.For(((MethodInfo)method).ReturnType);
    }

    private object? Handle(MethodBase method, object?[] args)
    {
        var parameters = method.GetParameters();
        T? Arg<T>(string name)
        {
            for (int i = 0; i < parameters.Length; i++)
                if (parameters[i].Name == name && i < args.Length && args[i] is T value)
                    return value;
            return default;
        }
        decimal Amount(string name = "amount") => Arg<decimal>(name);

        var key = $"{method.DeclaringType?.Name}.{method.Name}";
        // Avant le choix en cours : déjà fait dans la partie
        if (_muted && key is not ("CardSelectCmd.FromHand" or "CardSelectCmd.FromHandForDiscard"))
            return null;
        switch (key)
        {
            case "CreatureCmd.Add" when Arg<MonsterModel>("monster") is { } monster:
                // Créature invoquée (Wrigglers d'Infested…) : jamais ajoutée au combat, mais pas nulle (le code du
                // jeu s'en sert aussitôt ; un objet nul ferme le jeu)
                return Task.FromResult(Detached(monster, Arg<CombatSide>("side"), Arg<string>("slotName")));
            case "OstyCmd.Summon" when Arg<Player>("summoner") is { } summoner:
                decimal summoned = Hook.ModifySummonAmount(_combat, summoner, Amount(), Arg<AbstractModel>("source"));
                return Task.FromResult(new MegaCrit.Sts2.Core.Entities.Creatures.SummonResult(summoner.Osty ?? summoner.Creature, summoned));
            case "CardSelectCmd.FromHand":
            case "CardSelectCmd.FromHandForDiscard":
                IEnumerable<CardModel> chosen = SelectFromHand(Arg<Player>("player")!, Arg<CardSelectorPrefs>("prefs"),
                    Arg<Func<CardModel, bool>>("filter"));
                return Task.FromResult(chosen);
            case "CardSelectCmd.FromCombatPile":
                var pileCards = (Arg<CardPile>("pile")?.Cards ?? []).Where(c => Arg<Func<CardModel, bool>>("filter")?.Invoke(c) ?? true).ToList();
                IEnumerable<CardModel> fromPile = Forced(pileCards, Arg<CardSelectorPrefs>("prefs")).Select(Copied).ToList();
                return Task.FromResult(fromPile);
            case "CardSelectCmd.FromSimpleGrid":
                IEnumerable<CardModel> fromGrid = Forced(Arg<IReadOnlyList<CardModel>>("cardsIn")?.ToList() ?? [], Arg<CardSelectorPrefs>("prefs"))
                    .Select(Copied).ToList();
                return Task.FromResult(fromGrid);
            case "CardCmd.Exhaust":
                if (Arg<CardModel>("card") is { } exhausted)
                    Exhaust(exhausted, Arg<bool>("causedByEthereal"));
                return null;
            case "CardCmd.Discard":
            case "CardCmd.DiscardAndDraw":
                var toDiscard = Arg<IEnumerable<CardModel>>("cards") ?? Arg<IEnumerable<CardModel>>("cardsToDiscard")
                    ?? (Arg<CardModel>("card") is { } single ? [single] : []);
                Discard(toDiscard, Arg<int>("cardsToDraw"));
                return null;
            case "CardCmd.AutoPlay":
                if (Arg<CardModel>("card") is { } autoPlayed)
                    AutoPlay(autoPlayed, Arg<Creature>("target"));
                return null;
            case "CreatureCmd.Damage":
                var targets = Arg<IEnumerable<DamageTargetData>>("targetData")?.Select(d => d.target).ToList() ?? [];
                IEnumerable<DamageResult> results = Damage(targets, Amount(), Arg<ValueProp>("props"), Arg<Creature>("dealer"),
                    Arg<CardModel>("cardSource"), Arg<CardPlay>("cardPlay"));
                return Task.FromResult(results);
            case "CreatureCmd.GainBlock":
                return Task.FromResult(GainBlock(Arg<Creature>("creature")!, Amount(), Arg<ValueProp>("props"), Arg<CardPlay>("cardPlay")));
            case "CreatureCmd.LoseBlock":
                var loser = Body(Arg<Creature>("target")!);
                loser[1] = Math.Max(0, loser[1] - (int)Amount());
                return null;
            case "CreatureCmd.Heal":
                Heal(Arg<Creature>("creature")!, Amount());
                return null;
            case "CreatureCmd.Kill":
                var victims = Arg<IReadOnlyCollection<Creature>>("creatures")?.ToList() ?? [];
                if (Arg<Creature>("creature") is { } victim)
                    victims.Add(victim);
                foreach (var c in victims.Where(c => !IsDead(c)))
                    Die(c, Arg<CardModel>("cardSource"), Arg<bool>("force"));
                return null;
            case "CreatureCmd.Stun":
                if (Arg<Creature>("creature") is { } stunned && !IsDead(stunned))
                {
                    _stunned.Add(stunned);
                    _outcome.Of(stunned)[Stat.Stunned] = 1;
                }
                return null;
            case "PowerCmd.Apply":
                Apply(Arg<PowerModel>("power")!, Arg<Creature>("target")!, Amount(), Arg<Creature>("applier"), Arg<CardModel>("cardSource"));
                return null;
            case "PowerCmd.ModifyAmount":
                return Task.FromResult(ModifyAmount(Arg<PowerModel>("power")!, Amount("offset"), Arg<Creature>("applier"), Arg<CardModel>("cardSource")));
            case "CardPileCmd.Add":
                var moved = Arg<IEnumerable<CardModel>>("cards") ?? (Arg<CardModel>("card") is { } one ? [one] : []);
                var destination = Arg<CardPile>("newPile")?.Type ?? Arg<PileType>("newPileType");
                foreach (var card in moved.Select(RealOf).ToList())
                {
                    if (card.Pile?.Type == destination || card.Owner is not { } mover)
                        continue;
                    MoveTo(card, destination);
                    _outcome.Add(mover.Creature, Stat.MovedTo + destination, 1);
                }
                return null;
            case "CardCmd.Upgrade":
                var upgraded = Arg<IEnumerable<CardModel>>("cards") ?? (Arg<CardModel>("card") is { } toUpgrade ? [toUpgrade] : []);
                foreach (var card in upgraded.Where(c => c.IsUpgradable))
                    if (card.Owner is { } upgrader)
                        _outcome.Add(upgrader.Creature, Stat.Upgraded, 1);
                return null;
            case "CardCmd.Transform":
                var transformations = Arg<IEnumerable<CardTransformation>>("transformations")?.ToList();
                var transformedOwner = (Arg<CardModel>("original") ?? (transformations is { Count: > 0 } ? transformations[0].Original : null))?.Owner;
                int transformed = transformations?.Count ?? 1;
                if (transformedOwner != null)
                    _outcome.Add(transformedOwner.Creature, Stat.Transformed, transformed);
                return null;
            case "ArrowCmd.ShootArrowAtTarget" when Arg<Creature>("target") is { } shotTarget && Arg<Player>("player") is { } shooter:
                var shot = Arg<AttackCommand>("attackCommand");
                return Task.FromResult(Shoot(shotTarget, shooter, Arg<CardModel>("card") ?? shot?.CardPlay?.Card, shot,
                    Arg<Func<Task>>("actionToPerform")));
            case "ArrowCmd.TryShootArrow" when Arg<Player>("player") is { } archer:
                return Task.FromResult(TakeArrow(archer, Arg<CardModel>("source")));
            case "ArrowCmd.ImpaleTarget" when Arg<Creature>("target") is { } impaleTarget && Arg<ArrowSlot>("arrowSlot") is { } slot:
                Impale(impaleTarget, slot, Arg<Func<Task>>("actionToPerform"), null, Arg<CardModel>("card"));
                return null;
            case "ArrowCmd.DislodgeArrow":
                if (Arg<ImpaledPower>("impaledArrow") is { } impaledArrow)
                    Dislodge(impaledArrow, Arg<CardModel>("source"));
                else if (Arg<Creature>("target") is { } dislodgeTarget && Arg<Player>("player") is { } owner
                    && ImpaledOn(dislodgeTarget, owner.Creature).FirstOrDefault() is { } impaled)
                {
                    Dislodge(impaled, Arg<CardModel>("source"));
                    if (impaled.Arrows.Count == 0)
                        Remove(impaled);
                }
                return null;
            case "ArrowCmd.GainArrowSlot" when Arg<Player>("player") is { } gainer:
                _arrows[gainer] = _arrows.GetValueOrDefault(gainer) + 1;
                _outcome.Add(gainer.Creature, Stat.ArrowSlots, 1);
                return null;
            case "PowerCmd.Decrement" when Arg<PowerModel>("power") is { } decremented:
                ModifyAmount(decremented, -1, null, null);
                return null;
            case "PowerCmd.TickDownDuration" when Arg<PowerModel>("power") is { } ticked:
                var tickedShadow = (PowerModel)ShadowOf(ticked);
                if (tickedShadow.SkipNextDurationTick)
                    tickedShadow.SkipNextDurationTick = false;
                else
                    ModifyAmount(ticked, -1, null, null);
                return null;
            case "PowerCmd.Remove":
                if (Arg<PowerModel>("power") is { } removed)
                    Remove(removed);
                return null;
            case "PlayerCmd.GainEnergy":
                _outcome.Add(Arg<Player>("player")!.Creature, Stat.Energy, (int)Hook.ModifyEnergyGain(_combat, Arg<Player>("player")!, Amount(), out _));
                return null;
            case "PlayerCmd.GainStars":
                _outcome.Add(Arg<Player>("player")!.Creature, Stat.Stars, (int)Amount());
                return null;
            case "CardPileCmd.Draw":
                _outcome.Add(Arg<Player>("player")!.Creature, Stat.Draw, parameters.Any(p => p.Name == "count") ? (int)Amount("count") : 1);
                return null;
            case "CardPileCmd.DrawWithoutBlockingOnOtherPlayers":
                _outcome.Add(Arg<Player>("player")!.Creature, Stat.Draw, (int)Amount("count"));
                return null;
            case "CardPileCmd.AddGeneratedCardToCombat":
                if (Arg<Player>("creator") is { } creator)
                    _outcome.Add(creator.Creature, Stat.Created, 1);
                return null;
            case "CardPileCmd.AddGeneratedCardsToCombat":
                if (Arg<Player>("creator") is { } creators)
                    _outcome.Add(creators.Creature, Stat.Created, Arg<IEnumerable<CardModel>>("cards")?.Count() ?? 0);
                return null;
            case "OrbCmd.Channel":
                if (Arg<OrbModel>("orb") is { } orb && Arg<Player>("player") is { } channeler)
                    _outcome.Add(channeler.Creature, Stat.Channel + orb.Title.GetFormattedText(), 1);
                return null;
        }
        return null;
    }

    /// <summary>Riposte en cours (Thorns qui touche un attaquant à épines…) : limitée pour ne pas boucler.</summary>
    private int _damageDepth;

    /// <summary>
    /// Coup(s) : calcul du jeu (dégâts modifiés, Block, PV perdus), sur les PV et le Block simulés, et les réactions
    /// des créatures touchées et de l'attaquant, comme le jeu (Thorns, Curl Up, Envenom…). Côté joueur, des PV
    /// perdus ; côté monstres, des dégâts, la part qui passe le Block et le nombre de coups.
    /// </summary>
    private List<DamageResult> Damage(IReadOnlyList<Creature> targets, decimal amount, ValueProp props, Creature? dealer,
        CardModel? cardSource, CardPlay? cardPlay)
    {
        var results = new List<DamageResult>();
        if (dealer != null && IsDead(dealer) || _damageDepth > 4)
            return results;
        _damageDepth++;
        try
        {
            DamageEach(targets, amount, props, dealer, cardSource, cardPlay, results);
        }
        finally
        {
            _damageDepth--;
        }
        return results;
    }

    private void DamageEach(IReadOnlyList<Creature> targets, decimal amount, ValueProp props, Creature? dealer,
        CardModel? cardSource, CardPlay? cardPlay, List<DamageResult> results)
    {
        var context = new BlockingPlayerChoiceContext();
        var killedNow = new List<Creature>();
        var runState = _combat.RunState;
        foreach (var target in targets)
        {
            if (IsDead(target))
                continue;
            decimal modified = Hook.ModifyDamage(runState, _combat, target, dealer, amount, props, cardSource, cardPlay,
                ModifyDamageHookType.All, CardPreviewMode.None, out _);
            Event(() => Hook.BeforeDamageReceived(context, runState, _combat, target, modified, props, dealer, cardSource));
            if (dealer != null && IsDead(dealer))
                break;
            // Le Block d'un familier est celui de son maître
            var blocker = Body(target.PetOwner?.Creature ?? target);
            decimal blocked = props.HasFlag(ValueProp.Unblockable) ? 0 : Math.Min(blocker[1], modified);
            blocker[1] -= (int)blocked;
            decimal rest = Math.Max(modified - blocked, 0);
            decimal unblocked = Hook.ModifyHpLost(runState, _combat, target, rest, props, dealer, cardSource, HpLossHookPhase.BeforeOsty, out _);
            var receiver = Hook.ModifyUnblockedDamageTarget(_combat, target, unblocked, props, dealer);
            unblocked = Hook.ModifyHpLost(runState, _combat, receiver, unblocked, props, dealer, cardSource, HpLossHookPhase.AfterOsty, out _);
            var body = Body(receiver);
            int lost = (int)Math.Clamp(unblocked, 0, 999999999);
            bool killed = body[0] > 0 && lost >= body[0];
            int hpLost = Math.Min(lost, body[0]);
            body[0] -= hpLost;

            if (target.Side == CombatSide.Player)
                _outcome.Add(receiver, Stat.HpLost, hpLost);
            else
            {
                _outcome.Add(target, Stat.Damage, (int)modified);
                _outcome.Add(target, Stat.Hits, 1);
                _outcome.Add(target, Stat.ThroughBlock, lost);
            }
            if (killed)
                killedNow.Add(receiver);
            var result = new DamageResult(receiver, props)
            {
                UnblockedDamage = hpLost,
                OverkillDamage = lost - hpLost,
                WasTargetKilled = killed,
                BlockedDamage = (int)blocked,
                IntendedHpLoss = (int)rest,
                OriginalTarget = target,
                WasBlockBroken = blocked > 0 && blocker[1] <= 0,
                WasFullyBlocked = !props.HasFlag(ValueProp.Unblockable) && blocked > 0 && lost == 0,
            };
            results.Add(result);

            // Réactions au coup, comme le jeu après les dégâts
            if (result.WasBlockBroken)
                Event(() => Hook.AfterBlockBroken(_combat, context, receiver, dealer));
            if (hpLost > 0)
                Event(() => Hook.AfterCurrentHpChanged(runState, _combat, receiver, -hpLost));
            Event(() => Hook.AfterDamageGiven(context, _combat, dealer, result, props, receiver, cardSource));
            if (!killed)
                Event(() => Hook.AfterDamageReceived(context, runState, _combat, receiver, result, props, dealer, cardSource));
        }
        // Comme le jeu : les morts après tous les coups
        foreach (var dead in killedNow)
            Die(dead, cardSource);
    }

    /// <summary>Fin du tour ennemi simulée (après leurs attaques) : les morts y sont « après avoir agi ».</summary>
    private bool _late;

    private readonly HashSet<Creature> _lateDead = new();

    /// <summary>Créatures mortes, étourdies dans la simulation.</summary>
    private readonly HashSet<Creature> _dead = new(), _stunned = new();

    /// <summary>
    /// Mort, comme le jeu : avant la mort, mort évitée (Lizard Tail…) ou pas, puis réactions à la mort (Ravenous des
    /// Corpse Slugs : les autres sont étourdis et gagnent de la Force…).
    /// </summary>
    /// <param name="force">mort imposée (Sandpit) : rien ne l'empêche</param>
    private void Die(Creature creature, CardModel? cardSource, bool force = false)
    {
        Trace($"death of {creature.Name}");
        if (!_dead.Add(creature))
            return;
        var runState = _combat.RunState;
        Body(creature)[0] = 0;
        Event(() => Hook.BeforeDeath(runState, _combat, creature));
        if (!force && !Hook.ShouldDie(runState, _combat, creature, out _))
        {
            Body(creature)[0] = 1;
            _dead.Remove(creature);
            return;
        }
        _outcome.Of(creature)[_late ? Stat.LethalLate : Stat.Lethal] = 1;
        if (_late)
            _lateDead.Add(creature);
        Event(() => Hook.AfterDeath(runState, _combat, creature, cardSource, false, 0f));
    }

    private decimal GainBlock(Creature creature, decimal amount, ValueProp props, CardPlay? cardPlay)
    {
        if (IsDead(creature))
            return 0;
        decimal block = Math.Max(0, Hook.ModifyBlock(_combat, creature, amount, props, cardPlay?.Card, cardPlay, out _));
        Body(creature)[1] += (int)block;
        _outcome.Add(creature, Stat.Block, (int)block);
        return block;
    }

    private void Heal(Creature creature, decimal amount)
    {
        if (IsDead(creature))
            return;
        var body = Body(creature);
        int healed = Math.Clamp((int)amount, 0, Math.Max(0, creature.MaxHp - body[0]));
        body[0] += healed;
        _outcome.Add(creature, Stat.Healed, healed);
    }

    // ---- Pouvoirs ----

    /// <summary>Pouvoir déjà sur la créature avec lequel le nouveau s'empile (donné pendant la simulation, ou réel).</summary>
    private PowerModel? Existing(PowerModel power, Creature target, Creature? applier)
    {
        var added = _added.FirstOrDefault(p => !_removed.Contains(p) && p.Owner == target && p.Id == power.Id
            && (power.InstanceType == PowerInstanceType.None
                || power.InstanceType == PowerInstanceType.InstancedPerApplier && p.Applier == applier));
        if (added != null)
            return added;
        var real = PowerCmd.FindExistingInstanceForStacking(power, target, applier);
        return real != null && !_removed.Contains(real) ? real : null;
    }

    private decimal ModifiedAmount(PowerModel power, Creature target, decimal amount, Creature? applier, CardModel? cardSource)
    {
        if (applier != null && _combat.ContainsCreature(applier))
            amount = Hook.ModifyPowerAmountGiven(_combat, power, applier, amount, target, cardSource, out _);
        return Hook.ModifyPowerAmountReceived(_combat, power, target, amount, applier, out _);
    }

    private void Apply(PowerModel power, Creature target, decimal amount, Creature? applier, CardModel? cardSource)
    {
        if (amount == 0 || !target.CanReceivePowers || IsDead(target))
            return;
        if (Existing(power, target, applier) is { } existing)
        {
            ModifyAmount(existing, amount, applier, cardSource);
            return;
        }
        if (!power.IsMutable)
            power = power.ToMutable();
        power.Applier = applier;
        var context = new BlockingPlayerChoiceContext();
        var applied = power;
        // Comme le jeu : réactions avant, quantité modifiée, le pouvoir s'applique, réactions après (Vicious fait
        // piocher quand on applique du Vulnerable…)
        Event(() => Hook.BeforePowerAmountChanged(_combat, applied, amount, target, applier, cardSource));
        decimal modified = ModifiedAmount(power, target, amount, applier, cardSource);
        if (_combat.Players.Count > 1 && (target.IsPrimaryEnemy || target.IsSecondaryEnemy) && power.ShouldScaleInMultiplayer)
            modified = power.GetScaledAmountForMultiplayer(_combat, applier, modified, target, cardSource);
        Event(() => applied.BeforeApplied(target, modified, applier, cardSource));
        if ((int)modified == 0)
            return;
        // Le pouvoir neuf n'entre pas dans la partie : il est seulement vu des calculs simulés
        PowerOwner.SetValue(power, target);
        PowerAmount.SetValue(power, (int)modified);
        _added.Add(power);
        _clones.Add(power);
        Note(target, power, (int)modified);
        Event(() => applied.AfterApplied(applier, cardSource));
        Event(() => Hook.AfterPowerAmountChanged(_combat, context, applied, modified, applier, cardSource));
    }

    private int ModifyAmount(PowerModel power, decimal offset, Creature? applier, CardModel? cardSource)
    {
        var shadow = (PowerModel)ShadowOf(power);
        var owner = shadow.Owner;
        Event(() => Hook.BeforePowerAmountChanged(_combat, shadow, offset, owner, applier, cardSource));
        decimal modified = ModifiedAmount(shadow, owner, offset, applier, cardSource);
        int amount = shadow.Amount + (int)modified;
        PowerAmount.SetValue(shadow, amount);
        Note(owner, shadow, (int)modified);
        if ((int)modified != 0)
            Event(() => Hook.AfterPowerAmountChanged(_combat, new BlockingPlayerChoiceContext(), shadow, modified, applier, cardSource));
        // Comme le jeu : plus de charge, le pouvoir est retiré (et réagit à son retrait)
        if (shadow.ShouldRemoveDueToAmount())
            Remove(shadow, noted: true);
        return amount;
    }

    /// <summary>
    /// Comme le jeu : le pouvoir est retiré, puis réagit à son retrait (Sandpit de The Insatiable tue le joueur
    /// quand il n'a plus de charge).
    /// </summary>
    /// <param name="noted">sa quantité retirée est déjà notée (retrait parce qu'il n'a plus de charge)</param>
    private void Remove(PowerModel power, bool noted = false)
    {
        var shadow = (PowerModel)ShadowOf(power);
        if (_removed.Contains(shadow))
            return;
        if (!noted)
            Note(shadow.Owner, shadow, -shadow.Amount);
        _removed.Add(shadow);
        var owner = shadow.Owner;
        Event(() => shadow.AfterRemoved(owner));
    }

    private void Note(Creature creature, PowerModel power, int delta)
    {
        if (delta == 0)
            return;
        var title = power.Title.GetFormattedText();
        _outcome.Add(creature, title, delta);
        _outcome.PowerIcons[title] = power.IconPath;
    }

    // ---- Attaques ----

    private static readonly FieldInfo HitCount = AccessTools.Field(typeof(AttackCommand), "_hitCount");
    private static readonly FieldInfo DamagePerHit = AccessTools.Field(typeof(AttackCommand), "_damagePerHit");
    private static readonly FieldInfo CalculatedDamage = AccessTools.Field(typeof(AttackCommand), "_calculatedDamageVar");
    private static readonly FieldInfo BeforeDamage = AccessTools.Field(typeof(AttackCommand), "_beforeDamage");
    private static readonly MethodInfo PossibleTargets = AccessTools.Method(typeof(AttackCommand), "GetPossibleTargets");

    /// <summary>Comme <see cref="AttackCommand.Execute"/> : nombre de coups modifié, cibles vivantes, chaque coup.</summary>
    private async Task<AttackCommand> Attack(AttackCommand attack, PlayerChoiceContext? context)
    {
        Trace($"attack by {attack.Attacker?.Name} from {attack.ModelSource?.GetType().Name}");
        var attacker = attack.Attacker;
        if (attacker == null || IsDead(attacker))
            return attack;
        context ??= new BlockingPlayerChoiceContext();
        bool outer = _inEvent;
        _inEvent = true;
        await Hook.BeforeAttack(_combat, attack);
        _inEvent = outer;
        decimal count = Hook.ModifyAttackHitCount(_combat, attack, (int)HitCount.GetValue(attack)!);
        var calculated = CalculatedDamage.GetValue(attack) as CalculatedDamageVar;
        for (int i = 0; i < count; i++)
        {
            if (IsDead(attacker))
                break;
            var valid = ((IReadOnlyList<Creature>)PossibleTargets.Invoke(attack, [])!).Where(c => !IsDead(c)).ToList();
            if (valid.Count == 0)
                break;
            Creature? single = valid.Count == 1 ? valid[0] : null;
            if (attack.IsRandomlyTargeted && valid.Count > 1)
            {
                // Au hasard, avec le générateur du jeu (comme lui) : il est remis en place après, et changé d'une
                // simulation à l'autre pour la moyenne
                var rng = (attacker.Player ?? attacker.PetOwner)?.RunState.Rng.CombatTargets;
                single = rng?.NextItem(valid) ?? (_aimed != null && valid.Contains(_aimed) ? _aimed : valid[0]);
            }
            if (BeforeDamage.GetValue(attack) is Func<Task> before)
                await before();
            decimal amount = calculated != null ? calculated.Calculate(single) : (decimal)DamagePerHit.GetValue(attack)!;
            attack.AddResultsInternal(Damage(single != null ? [single] : valid, amount, attack.DamageProps, attacker,
                attack.ModelSource as CardModel, attack.CardPlay));
        }
        _inEvent = true;
        await Hook.AfterAttack(_combat, context, attack);
        _inEvent = outer;
        return attack;
    }

    [HarmonyPatch(typeof(AttackCommand), nameof(AttackCommand.Execute))]
    public static class AttackPatch
    {
        public static bool Prefix(AttackCommand __instance, PlayerChoiceContext? choiceContext, ref Task<AttackCommand> __result)
        {
            if (CurrentSim.Value is not { } sim)
                return true;
            if (!sim._finished && !sim._muted)
                sim.Step();
            __result = sim._muted || sim._finished ? Task.FromResult(__instance) : sim.Attack(__instance, choiceContext);
            return false;
        }
    }

    // ---- Dégâts annoncés par les ennemis ----

    /// <summary>Attaques annoncées par chaque ennemi vivant (dans l'état simulé si <paramref name="sim"/>).</summary>
    private static Dictionary<Creature, int> IncomingDamage(ICombatState combat, PlaySimulation? sim = null)
    {
        var incoming = new Dictionary<Creature, int>();
        try
        {
            foreach (var enemy in combat.Enemies)
            {
                if (enemy.IsDead || enemy.Monster?.NextMove is not { } move)
                    continue;
                int total = 0;
                // Mort ou étourdi (Corpse Slugs) : n'attaquera pas
                if (sim == null || !sim.IsDead(enemy) && !sim._stunned.Contains(enemy))
                    foreach (var intent in move.Intents)
                        if (intent is AttackIntent attack && attack.DamageCalc != null)
                            total += attack.GetTotalDamage(combat.Allies, enemy);
                incoming[enemy] = total;
            }
        }
        catch (Exception e)
        {
            sim?.Fail("intents", e);
        }
        return incoming;
    }

    /// <summary>
    /// Attaques annoncées par les ennemis vivants contre <paramref name="target"/> (un allié du joueur local : le jeu
    /// ne calcule les intentions que contre le joueur local), comme le jeu les calcule : chaque coup modifié par les
    /// pouvoirs (Vulnerable de la cible, Weak de l'ennemi…), fois le nombre de coups. Les monstres frappent tous les
    /// joueurs à chaque attaque.
    /// </summary>
    private int IncomingAgainst(Creature target)
    {
        int total = 0;
        try
        {
            if ((target.Player ?? target.PetOwner)?.RunState is not { } runState)
                return 0;
            foreach (var enemy in _combat.Enemies)
            {
                if (enemy.IsDead || IsDead(enemy) || _stunned.Contains(enemy) || enemy.Monster?.NextMove is not { } move)
                    continue;
                foreach (var intent in move.Intents)
                    if (intent is AttackIntent { DamageCalc: { } calc } attack)
                    {
                        decimal single = Hook.ModifyDamage(runState, _combat, target, enemy, calc(), ValueProp.Move, null, null,
                            ModifyDamageHookType.All, CardPreviewMode.None, out _);
                        total += Math.Max(0, (int)single) * Math.Max(1, attack.Repeats);
                    }
            }
        }
        catch (Exception e)
        {
            Fail("ally intents", e);
        }
        return total;
    }

    // ---- Interception de toutes les commandes ----

    /// <summary>
    /// Toutes les commandes publiques du jeu (classes statiques de MegaCrit.Sts2.Core.Commands) qui renvoient une
    /// tâche ou rien, sauf les surcharges qui ne font que relayer vers une autre (CreatureCmd.Damage, GainBlock avec
    /// une variable) : on intercepte celle qui fait le travail, avec tous ses paramètres. Les méthodes génériques
    /// (PowerCmd.Apply&lt;T&gt;…) ne sont pas patchables : elles tournent et appellent une commande interceptée.
    /// </summary>
    private static bool? _patched;

    /// <summary>
    /// Pose les patchs des commandes au premier aperçu, en combat : les poser au démarrage, pendant que le jeu
    /// charge sur d'autres fils, le bloquait parfois avant le menu principal.
    /// </summary>
    private static bool EnsurePatched()
    {
        if (_patched is { } done)
            return done;
        try
        {
            PatchCommands(ModEntry.Harmony!);
            _patched = true;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] card preview disabled: {e.Message}");
            _patched = false;
        }
        return _patched.Value;
    }

    private static void PatchCommands(Harmony harmony)
    {
        int count = 0;
        foreach (var type in typeof(CreatureCmd).Assembly.GetTypes())
        {
            if (type.Namespace != typeof(CreatureCmd).Namespace || !type.IsAbstract || !type.IsSealed)
                continue;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.IsGenericMethodDefinition || method.IsSpecialName || Relays(method))
                    continue;
                var prefix = PrefixFor(method.ReturnType);
                if (prefix == null)
                    continue;
                try
                {
                    harmony.Patch(method, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
                    count++;
                }
                catch (Exception e)
                {
                    Log.Warn($"[{ModEntry.Id}] cannot intercept {type.Name}.{method.Name}: {e.Message}");
                }
            }
        }
        Log.Info($"[{ModEntry.Id}] card preview: {count} game commands intercepted");
    }

    private static bool Relays(MethodInfo method)
    {
        var types = method.GetParameters().Select(p => p.ParameterType).ToList();
        return method.DeclaringType == typeof(CreatureCmd) && method.Name switch
        {
            nameof(CreatureCmd.Damage) => !types.Contains(typeof(IEnumerable<DamageTargetData>)),
            nameof(CreatureCmd.GainBlock) => !types.Contains(typeof(decimal)),
            _ => false,
        };
    }

    private static MethodInfo? PrefixFor(Type returnType)
    {
        if (returnType == typeof(void))
            return AccessTools.Method(typeof(VoidPrefix), nameof(VoidPrefix.Prefix));
        if (returnType == typeof(Task))
            return AccessTools.Method(typeof(TaskPrefix), nameof(TaskPrefix.Prefix));
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            return AccessTools.Method(typeof(TaskPrefix<>).MakeGenericType(returnType.GetGenericArguments()[0]), "Prefix");
        return null;
    }

    public static class VoidPrefix
    {
        public static bool Prefix(MethodBase __originalMethod, object?[] __args)
        {
            if (CurrentSim.Value is not { } sim)
                return true;
            sim.Intercept(__originalMethod, __args);
            return false;
        }
    }

    public static class TaskPrefix
    {
        public static bool Prefix(MethodBase __originalMethod, object?[] __args, ref Task __result)
        {
            if (CurrentSim.Value is not { } sim)
                return true;
            __result = sim.Intercept(__originalMethod, __args) as Task ?? Task.CompletedTask;
            return false;
        }
    }

    public static class TaskPrefix<T>
    {
        public static bool Prefix(MethodBase __originalMethod, object?[] __args, ref Task<T> __result)
        {
            if (CurrentSim.Value is not { } sim)
                return true;
            __result = sim.Intercept(__originalMethod, __args) as Task<T> ?? Task.FromResult((T)Defaults.Value(typeof(T))!);
            return false;
        }
    }

    // ---- Empreinte de l'état (vérification) ----

    /// <summary>État observé (une entrée par élément), pour vérifier qu'un aperçu ne l'a pas changé et dire quoi.</summary>
    private static List<string> Fingerprint(ICombatState combat)
    {
        try
        {
            var parts = new List<string>();
            foreach (var creature in combat.Creatures)
            {
                parts.Add($"{creature.Name} hp {creature.CurrentHp}/{creature.MaxHp} block {creature.Block} powers {creature.Powers.Count}");
                parts.AddRange(creature.Powers.Select(p => $"{creature.Name} {p.Id.Entry}={p.Amount}"));
            }
            foreach (var player in combat.Players)
                if (player.PlayerCombatState is { } state)
                {
                    parts.Add($"energy {state.Energy} stars {state.Stars} gold {player.Gold}");
                    parts.AddRange(state.AllPiles.Select(p => $"{p.Type} {p.Cards.Count} cards"));
                    parts.AddRange(state.Hand.Cards.Select((c, i) =>
                        $"hand {i} {c.Id.Entry}{(c.IsUpgraded ? "+" : "")} cost {c.EnergyCost.GetWithModifiers(CostModifiers.All)}{(c.Enchantment != null ? " " + c.Enchantment.Id.Entry : "")}"));
                }
            return parts;
        }
        catch (Exception)
        {
            return [];
        }
    }
}

/// <summary>
/// Marqueur de simulation, seul dans sa classe : les patchs des commandes le lisent sans initialiser le moteur.
/// </summary>
internal static class SimulationFlag
{
    public static readonly AsyncLocal<PlaySimulation?> Current = new();
}

/// <summary>Valeurs renvoyées par une commande interceptée : tâche terminée avec une valeur vide.</summary>
internal static class Defaults
{
    private static readonly Dictionary<Type, object?> Results = new();
    private static readonly MethodInfo FromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!;

    /// <summary>Résultat d'une commande de ce type : rien, tâche terminée, ou tâche terminée avec une valeur vide.</summary>
    public static object? For(Type returnType)
    {
        if (returnType == typeof(void))
            return null;
        if (returnType == typeof(Task))
            return Task.CompletedTask;
        if (Results.TryGetValue(returnType, out var cached))
            return cached;
        var valueType = returnType.GetGenericArguments()[0];
        var value = Value(valueType);
        var task = FromResult.MakeGenericMethod(valueType).Invoke(null, [value]);
        // Un objet renvoyé pourrait être modifié par l'appelant : seulement les valeurs immuables sont gardées
        if (value == null || value is Array { Length: 0 } || valueType.IsValueType)
            Results[returnType] = task;
        return task;
    }

    /// <summary>Valeur vide : liste vide pour une collection, défaut sinon.</summary>
    public static object? Value(Type type)
    {
        if (type.IsArray)
            return Array.CreateInstance(type.GetElementType()!, 0);
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();
            if (args.Length == 1 && (definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>)
                    || definition == typeof(IReadOnlyCollection<>) || definition == typeof(IList<>) || definition == typeof(ICollection<>)))
                return Array.CreateInstance(args[0], 0);
            if (definition == typeof(List<>))
                return Activator.CreateInstance(type);
        }
        if (type.IsValueType)
            return Activator.CreateInstance(type);
        // Objet vide plutôt que nul quand c'est possible : le code du jeu s'en sert souvent sans vérifier, et un
        // objet nul ferme le jeu
        if (!type.IsAbstract && !type.IsInterface && type.GetConstructor(Type.EmptyTypes) != null && !typeof(Godot.GodotObject).IsAssignableFrom(type))
            try
            {
                return Activator.CreateInstance(type);
            }
            catch (Exception)
            {
                // Pas constructible : nul
            }
        return null;
    }
}

/// <summary>Copies pour la simulation : les collections d'un objet copié ne doivent pas rester partagées.</summary>
internal static class Copy
{
    private static readonly MethodInfo MemberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly Dictionary<Type, FieldInfo[]> CollectionFields = new();

    /// <summary>Copie d'un objet de données (compteurs d'un pouvoir), ses collections comprises.</summary>
    public static object? Object(object? value)
    {
        if (value == null || value.GetType().IsValueType || value is string)
            return value;
        var copy = MemberwiseCloneMethod.Invoke(value, null)!;
        Collections(copy);
        return copy;
    }

    /// <summary>Remplace chaque liste, ensemble ou dictionnaire de <paramref name="target"/> par une copie.</summary>
    public static void Collections(object target)
    {
        foreach (var field in FieldsOf(target.GetType()))
            if (field.GetValue(target) is { } value)
                try
                {
                    field.SetValue(target, Activator.CreateInstance(value.GetType(), value));
                }
                catch (Exception)
                {
                    // Pas de constructeur de copie : laissée telle quelle
                }
    }

    private static readonly Dictionary<Type, FieldInfo[]> EventFields = new();

    /// <summary>
    /// Détache la copie des abonnés de l'original (interface du jeu) : sans ça, une relique copiée qui s'allume
    /// allumerait la vraie.
    /// </summary>
    public static void ClearEvents(object target)
    {
        var type = target.GetType();
        if (!EventFields.TryGetValue(type, out var fields))
        {
            var list = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                foreach (var ev in t.GetEvents(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (t.GetField(ev.Name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } field
                        && typeof(Delegate).IsAssignableFrom(field.FieldType))
                        list.Add(field);
            EventFields[type] = fields = list.ToArray();
        }
        foreach (var field in fields)
            field.SetValue(target, null);
    }

    private static FieldInfo[] FieldsOf(Type type)
    {
        if (CollectionFields.TryGetValue(type, out var cached))
            return cached;
        var fields = new List<FieldInfo>();
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() is var d
                    && (d == typeof(List<>) || d == typeof(HashSet<>) || d == typeof(Dictionary<,>)))
                    fields.Add(field);
        return CollectionFields[type] = fields.ToArray();
    }
}

/// <summary>
/// État des générateurs aléatoires de la partie (partie, joueurs, monstres), remis tel quel après la simulation :
/// un effet au hasard simulé ne doit pas changer les tirages suivants. Un générateur créé pendant la simulation est
/// oublié (il sera recréé neuf).
/// </summary>
internal sealed class RngState
{
    private static readonly FieldInfo? RunRngs = AccessTools.Field(typeof(RunRngSet), "_rngs");
    private static readonly FieldInfo? PlayerRngs = AccessTools.Field(typeof(PlayerRngSet), "_rngs");
    private static readonly FieldInfo? MonsterRng = AccessTools.Field(typeof(MonsterModel), "_rng");
    private static readonly FieldInfo CardEnergy = AccessTools.Field(typeof(CardModel), "_energyCost");

    private readonly List<(Rng Rng, SerializableRng State)> _saved = [];
    private readonly List<(System.Collections.IDictionary Rngs, HashSet<object> Keys)> _sets = [];
    private readonly List<(MonsterModel Monster, Rng? Rng)> _monsters = [];

    /// <summary>
    /// Monstres et leur machine à états des coups : un pouvoir copié peut les modifier directement (Ravenous :
    /// IsRavenous des Corpse Slugs ; Reattach : coup suivant du segment de Decimillipede) ; remis tels quels.
    /// </summary>
    private readonly List<ObjectState> _objects = [];

    public static RngState Capture(ICombatState combat)
    {
        var state = new RngState();
        try
        {
            state.AddSet(RunRngs?.GetValue(combat.RunState.Rng));
            foreach (var player in combat.Players)
                state.AddSet(PlayerRngs?.GetValue(player.PlayerRng));
            // Cartes du combat : le code simulé modifie parfois directement les vraies (coût, mots-clés, valeurs)
            foreach (var player in combat.Players)
                if (player.PlayerCombatState is { } piles)
                    foreach (var card in piles.AllPiles.SelectMany(p => p.Cards))
                    {
                        state._objects.Add(new ObjectState(card, typeof(AbstractModel)));
                        if (CardEnergy.GetValue(card) is { } cost)
                            state._objects.Add(new ObjectState(cost, typeof(object)));
                        foreach (var variable in card.DynamicVars.Values)
                            state._objects.Add(new ObjectState(variable, typeof(object)));
                    }
            foreach (var creature in combat.Creatures)
                if (creature.Monster is { IsMutable: true } monster && MonsterRng != null)
                {
                    var rng = MonsterRng.GetValue(monster) as Rng;
                    state._monsters.Add((monster, rng));
                    state._objects.Add(new ObjectState(monster, typeof(AbstractModel)));
                    if (monster.MoveStateMachine is { } moves)
                        state._objects.Add(new ObjectState(moves, typeof(object)));
                    if (rng != null)
                        state._saved.Add((rng, rng.ToSerializable()));
                }
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot save the random generators: {e.Message}");
        }
        return state;
    }

    private void AddSet(object? value)
    {
        if (value is not System.Collections.IDictionary rngs)
            return;
        var keys = new HashSet<object>();
        foreach (System.Collections.DictionaryEntry entry in rngs)
        {
            keys.Add(entry.Key);
            if (entry.Value is Rng rng)
                _saved.Add((rng, rng.ToSerializable()));
        }
        _sets.Add((rngs, keys));
    }

    /// <summary>Un tirage a eu lieu (générateur avancé ou créé) depuis la sauvegarde.</summary>
    public bool Changed()
    {
        try
        {
            return _saved.Any(r => r.Rng.ToSerializable() != r.State)
                || _sets.Any(set => set.Rngs.Count != set.Keys.Count)
                || _monsters.Any(m => !ReferenceEquals(MonsterRng!.GetValue(m.Monster), m.Rng));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Autres tirages pour la simulation n° <paramref name="draw"/> : état des générateurs brouillé.</summary>
    public void Perturb(int draw)
    {
        ulong mix = 0x9E3779B97F4A7C15UL * (ulong)draw;
        foreach (var (rng, saved) in _saved)
            rng.LoadFromSerializable(saved with
            {
                state0 = saved.state0 ^ mix,
                state1 = saved.state1 ^ (mix << 13 | mix >> 51),
                state2 = saved.state2 ^ (mix << 29 | mix >> 35),
                state3 = saved.state3 ^ (mix << 41 | mix >> 23) ^ 1,
            });
    }

    public void Restore()
    {
        try
        {
            foreach (var (rng, saved) in _saved)
                if (rng.ToSerializable() != saved)
                    rng.LoadFromSerializable(saved);
            foreach (var (rngs, keys) in _sets)
                foreach (var key in rngs.Keys.Cast<object>().Where(k => !keys.Contains(k)).ToList())
                    rngs.Remove(key);
            foreach (var saved in _objects)
                saved.Restore();
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot restore the random generators: {e.Message}");
        }
    }
}

/// <summary>
/// Champs d'un objet du jeu (et contenu de ses listes), sauvegardés puis remis tels quels : ce que le code simulé
/// modifie directement, hors commandes, sur un objet qu'on ne peut pas copier.
/// </summary>
internal sealed class ObjectState
{
    private static readonly Dictionary<(Type, Type), FieldInfo[]> Fields = new();

    private readonly object _target;
    private readonly FieldInfo[] _fields;
    private readonly object?[] _values;
    private readonly object?[]?[] _lists;

    /// <param name="stopAt">classe de base dont les champs ne sont pas pris</param>
    public ObjectState(object target, Type stopAt)
    {
        _target = target;
        var type = target.GetType();
        if (!Fields.TryGetValue((type, stopAt), out var fields))
        {
            var list = new List<FieldInfo>();
            for (var t = type; t != null && t != stopAt && t != typeof(object); t = t.BaseType)
                list.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(f => !typeof(Delegate).IsAssignableFrom(f.FieldType)));
            Fields[(type, stopAt)] = fields = list.ToArray();
        }
        _fields = fields;
        _values = fields.Select(f => f.GetValue(target)).ToArray();
        _lists = _values.Select(v => v is System.Collections.IList || v != null && IsSet(v)
            ? ((System.Collections.IEnumerable)v!).Cast<object?>().ToArray() : null).ToArray();
    }

    private static bool IsSet(object value) =>
        value.GetType() is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(HashSet<>);

    public void Restore()
    {
        for (int i = 0; i < _fields.Length; i++)
        {
            if (!Equals(_fields[i].GetValue(_target), _values[i]))
                _fields[i].SetValue(_target, _values[i]);
            // Même ensemble, contenu changé (mot-clé ajouté à une carte…)
            if (_values[i] is { } set && IsSet(set) && _lists[i] is { } members)
            {
                var current = ((System.Collections.IEnumerable)set).Cast<object?>().ToList();
                if (current.Count != members.Length || current.Except(members).Any())
                {
                    var type = set.GetType();
                    type.GetMethod("Clear")!.Invoke(set, null);
                    var add = type.GetMethod("Add")!;
                    foreach (var member in members)
                        add.Invoke(set, [member]);
                }
            }
            // Même liste, contenu changé (coup ajouté à l'historique des coups…)
            if (_values[i] is System.Collections.IList { IsReadOnly: false, IsFixedSize: false } list && _lists[i] is { } items
                && (list.Count != items.Length || !list.Cast<object?>().SequenceEqual(items)))
            {
                list.Clear();
                foreach (var item in items)
                    list.Add(item);
            }
        }
    }
}
