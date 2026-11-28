using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

namespace StsTools.Game;

/// <summary>
/// Chercheur de chemins : sur la carte, GUI ouvert, les meilleurs chemins de la salle actuelle au boss sont tracés. Le
/// joueur classe, dans l'onglet <see cref="PathFinderTab"/> (visible seulement carte ouverte), les types de salles
/// (marchands, élites, feux de camp, « ? », combats) par importance : pour chacun, le plus possible (Max), le moins
/// possible (Min), exactement n ou au moins n salles sur tout l'acte, puis, au même rang, où les placer (tôt, tard,
/// groupées, éloignées) ; les autres (Any) ne départagent pas les chemins.
/// Déplacement libre (Winged Boots, Flight) pris en compte à chaque étape, avec les sauts qui restent. Seulement la carte
/// telle qu'elle est affichée : rien n'est lu de la graine. Ne touche à rien dans la partie, sauf avec les options du
/// mod (décochées par défaut) : en clavier seul, focus sur la prochaine salle du chemin (<see cref="ModOptions.MapPathFocus"/>) ;
/// chemin suivi tout seul, par le vote du joueur comme un clic (<see cref="ModOptions.MapPathAutoTravel"/>).
/// </summary>
public static class MapPaths
{
    private const string LinesName = "StsToolsMapPathLines";

    /// <summary>Au-delà, les chemins à égalité ne sont plus énumérés (affichés comme « N+ »).</summary>
    private const int MaxPaths = 300;

    /// <summary>Types de salles réglables, dans l'ordre d'affichage de la section des filtres.</summary>
    internal static readonly (MapPointType Type, string Name, Color Color)[] Types =
    [
        (MapPointType.Shop, "Shops", StsColors.blue),
        (MapPointType.Elite, "Elites", StsColors.red),
        (MapPointType.RestSite, "Rest", StsColors.orange),
        (MapPointType.Unknown, "?", StsColors.aqua),
        (MapPointType.Monster, "Fights", StsColors.pink),
    ];

    private static readonly MapPointType[] DefaultRanked =
        [MapPointType.Elite, MapPointType.RestSite, MapPointType.Unknown, MapPointType.Monster];

    private static bool _installed;
    private static bool _failed;
    private static bool _mapOpen;

    /// <summary>
    /// Classement d'importance, du plus important au moins important, chaque type avec sa règle (<see cref="_rules"/>) :
    /// appliqués dans l'ordre, chacun ne garde que les chemins qui la respectent le mieux. Les types absents (Any) ne
    /// départagent pas les chemins.
    /// </summary>
    internal static IReadOnlyList<MapPointType> Order { get; private set; } = DefaultRanked;

    private static readonly MapPointType[] CountedTypes = Types.Select(t => t.Type).ToArray();

    internal enum RuleKind { Max, Min, Exact, AtLeast }

    /// <summary>Règle d'un type du classement, comptée sur tout l'acte (salles déjà faites comprises).</summary>
    internal readonly record struct Rule(RuleKind Kind, int N = 0)
    {
        public override string ToString() => Kind switch
        {
            RuleKind.Max => "max",
            RuleKind.Min => "min",
            RuleKind.Exact => $"={N}",
            _ => $">={N}",
        };

        public static Rule? Parse(string text) => text switch
        {
            "max" => new Rule(RuleKind.Max),
            "min" => new Rule(RuleKind.Min),
            _ when text.StartsWith(">=") && int.TryParse(text[2..], out int n) => new Rule(RuleKind.AtLeast, n),
            _ when text.StartsWith('=') && int.TryParse(text[1..], out int n) => new Rule(RuleKind.Exact, n),
            _ => null,
        };
    }

    /// <summary>Règle de chaque type du classement (Max si absente).</summary>
    private static Dictionary<MapPointType, Rule> _rules = [];

    /// <summary>
    /// Où placer les salles d'un type du classement, sur tout l'acte : tôt (étage moyen le plus bas), tard (le plus
    /// haut), groupées (le moins d'étages entre la première et la dernière) ou éloignées (le plus grand écart minimal
    /// entre deux salles qui se suivent). Appliquée juste après la règle du type, au même rang d'importance.
    /// </summary>
    internal enum Placement { None, Early, Late, Grouped, Spread }

    /// <summary>Placement choisi pour chaque type du classement (absent : None).</summary>
    private static Dictionary<MapPointType, Placement> _placements = [];

    /// <summary>Placement de ce type ; None hors du classement.</summary>
    internal static Placement PlacementOf(MapPointType type) =>
        Order.Contains(type) && _placements.TryGetValue(type, out var p) ? p : Placement.None;

    /// <summary>
    /// Par type, nombres proposés (sur l'acte), croissants : pour un type du classement, ceux des chemins qui restent
    /// après les types plus importants ; pour un type Any, ceux des meilleurs chemins.
    /// </summary>
    private static Dictionary<MapPointType, IReadOnlyList<int>> _possible = [];

    /// <summary>Tous les chemins à égalité tracés en même temps (par défaut), plutôt qu'un seul à la fois.</summary>
    internal static bool ShowAll { get; private set; } = true;

    internal static int Index { get; private set; }

    /// <summary>Chemins à égalité après le classement (au plus <see cref="MaxPaths"/>), calculés pour <see cref="_key"/>.</summary>
    internal static IReadOnlyList<List<MapPoint>> Best { get; private set; } = [];

    /// <summary>Plus de chemins à égalité que <see cref="MaxPaths"/> : seuls les premiers sont dans <see cref="Best"/>.</summary>
    internal static bool Truncated { get; private set; }

    /// <summary>Sauts libres pris en compte : null sans déplacement libre, -1 sans limite (Flight).</summary>
    internal static int? FreeMoves { get; private set; }

    /// <summary>Règle de ce type ; null : Any (hors du classement).</summary>
    internal static Rule? RuleOf(MapPointType type) =>
        !Order.Contains(type) ? null : _rules.TryGetValue(type, out var rule) ? rule : new Rule(RuleKind.Max);

    internal static IReadOnlyList<int> Possible(MapPointType type) => _possible.TryGetValue(type, out var list) ? list : [];

    /// <summary>Le nombre choisi n'est respecté par aucun chemin, une fois les types plus importants appliqués.</summary>
    internal static bool IsImpossible(MapPointType type) => RuleOf(type) is { } rule && Possible(type) is var possible && rule.Kind switch
    {
        RuleKind.Exact => !possible.Contains(rule.N),
        RuleKind.AtLeast => possible.Count == 0 || possible.Max() < rule.N,
        _ => false,
    };

    /// <summary>Résultats ou choix changés : l'onglet se redessine.</summary>
    internal static event Action? Changed;

    /// <summary>Carte ouverte ou fermée : l'onglet apparaît ou disparaît.</summary>
    internal static bool MapOpen => _mapOpen;

    private static string? _key, _drawKey;

    /// <summary>Le chercheur tourne à cette image : carte et GUI ouverts, onglet affiché, pas d'erreur.</summary>
    private static bool _running;

    /// <summary>Branché une fois, au début d'une partie : vérifié à chaque image (peu coûteux), recalculé seulement si besoin.</summary>
    public static void Install()
    {
        if (_installed || Engine.GetMainLoop() is not SceneTree tree)
            return;
        _installed = true;
        var saved = SavedSettings.Load();
        if (saved.MapPathRanked != null)
            Order = saved.MapPathRanked
                .Select(n => Enum.TryParse<MapPointType>(n, out var t) ? t : (MapPointType?)null)
                .OfType<MapPointType>().Where(CountedTypes.Contains).Distinct().ToList();
        if (saved.MapPathRules != null)
            _rules = saved.MapPathRules
                .Where(kv => Enum.TryParse<MapPointType>(kv.Key, out var t) && Order.Contains(t) && Rule.Parse(kv.Value) != null)
                .ToDictionary(kv => Enum.Parse<MapPointType>(kv.Key), kv => Rule.Parse(kv.Value)!.Value);
        else
        {
            // Réglages d'avant les règles : nombres exacts ; ceux hors du classement (filtres appliqués avant les
            // Max) passent en tête
            var counts = saved.MapPathCounts
                .Where(kv => Enum.TryParse<MapPointType>(kv.Key, out var t) && CountedTypes.Contains(t))
                .ToDictionary(kv => Enum.Parse<MapPointType>(kv.Key), kv => kv.Value);
            Order = [.. counts.Keys.Where(t => !Order.Contains(t)).OrderBy(t => Array.IndexOf(CountedTypes, t)), .. Order];
            _rules = counts.ToDictionary(kv => kv.Key, kv => new Rule(RuleKind.Exact, kv.Value));
        }
        _placements = (saved.MapPathPlacements ?? [])
            .Where(kv => Enum.TryParse<MapPointType>(kv.Key, out var t) && Order.Contains(t)
                         && Enum.TryParse<Placement>(kv.Value, true, out var p) && p != Placement.None)
            .ToDictionary(kv => Enum.Parse<MapPointType>(kv.Key), kv => Enum.Parse<Placement>(kv.Value, true));
        ShowAll = !saved.MapPathShowOne;
        tree.ProcessFrame += Update;
    }

    private static void Update()
    {
        try
        {
            var screen = NMapScreen.Instance;
            bool mapOpen = screen != null && GodotObject.IsInstanceValid(screen) && screen.IsOpen && screen.IsVisibleInTree()
                           && CardRunStats.LocalPlayer?.RunState is { Map: not NullActMap };
            if (mapOpen != _mapOpen)
            {
                _mapOpen = mapOpen;
                // Carte fermée : la rouvrir pour la regarder (barre du haut) ne fait plus partir
                if (!mapOpen)
                    _proceeded = false;
                RunPanel.TabsChanged();
            }
            // Onglet Path finder caché dans les options du mod : ni recherche ni chemins tracés
            _running = mapOpen && RunPanel.IsOpen && !_failed && !ModOptions.IsTabHidden("Path finder");
            if (!_running)
            {
                Hide();
                return;
            }
            var state = CardRunStats.LocalPlayer!.RunState;
            var lines = EnsureLines(screen!);
            // Caché quand le GUI ou la carte était fermé : de nouveau visible sans rien recalculer
            lines.Visible = Best.Count > 0;
            string searchKey = $"{state.Map.GetHashCode()}|{state.CurrentActIndex}|{state.VisitedMapCoords.Count}|{state.CurrentMapPoint?.coord}|{string.Join(",", Order.Select(t => $"{t}{RuleOf(t)}{PlacementOf(t)}"))}|{FreeMovesNow(state)}";
            if (searchKey != _key)
            {
                _key = searchKey;
                // Chemin choisi (◀ ▶) gardé s'il existe encore ; après un déplacement, sa suite depuis la salle atteinte
                var selected = Best.Count > 0 ? Best[Math.Clamp(Index, 0, Best.Count - 1)] : null;
                Search(state);
                Index = selected == null ? 0 : Math.Max(0, IndexOf(selected, state.CurrentMapPoint));
                _drawKey = null;
            }
            FocusNext(screen!);
            Follow(screen!, state);
            string drawKey = $"{ShowAll}|{Index}";
            if (drawKey == _drawKey)
                return;
            _drawKey = drawKey;
            Draw(screen!, lines);
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            // Une erreur par image remplirait le log : arrêté jusqu'au prochain changement de choix
            Log.Error($"[{ModEntry.Id}] map paths: {e}");
            _failed = true;
            _running = false;
            Hide();
        }
    }

    private static void Hide()
    {
        if (NMapScreen.Instance is { } screen && GodotObject.IsInstanceValid(screen)
            && screen.GetNodeOrNull<Control>("TheMap/Paths")?.GetNodeOrNull<Node2D>(LinesName) is { } lines)
            lines.Visible = false;
    }

    // ---- Focus et déplacement (options du mod) ----

    /// <summary>Position (acte, salles visitées) d'où le chemin a déjà été suivi : un seul vote par salle.</summary>
    private static string? _followKey;

    /// <summary>
    /// Le joueur a appuyé sur Continuer (Proceed), et la carte ouverte par là n'a pas été fermée : seulement alors le
    /// chemin est suivi ; pas quand il ouvre la carte pour la regarder (barre du haut), même si le départ est permis
    /// (chez un marchand, dès l'entrée).
    /// </summary>
    private static bool _proceeded;

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.Proceed))]
    public static class ProceedPatch
    {
        public static void Prefix() => _proceeded = true;
    }

    /// <summary>Chemins et chemin choisi pour lesquels le focus a été placé : une fois par changement.</summary>
    private static string? _focusKey;

    /// <summary>
    /// Prochaine salle du chemin choisi (◀ ▶ ; avec Show all, le premier : les chemins à égalité se valent) ; null sans
    /// chemin.
    /// </summary>
    internal static MapPoint? NextRoom()
    {
        if (!_running || Best.Count == 0)
            return null;
        var selected = Best[Math.Clamp(Index, 0, Best.Count - 1)];
        return selected.Count > 0 ? selected[0] : null;
    }

    /// <summary>Salle du jeu (bouton de la carte) de ce point, si elle est cliquable.</summary>
    private static NMapPoint? EnabledNode(NMapScreen screen, MapPoint point) =>
        screen.GetNodeOrNull<Control>("TheMap/Points")?.GetChildren().OfType<NMapPoint>()
            .FirstOrDefault(n => GodotObject.IsInstanceValid(n) && n.Point.coord == point.coord && n.IsEnabled);

    private static bool FocusWanted => ModOptions.MapPathFocus && ModHotkeys.CurrentMode == InputType.KeyboardOnlyMode;

    /// <summary>
    /// Clavier seul : focus sur la prochaine salle du chemin quand les chemins ou le choix changent, si le focus est sur
    /// la carte (pas dans le GUI ni la légende). Le jeu, quand il remet lui-même le focus, le met aussi là
    /// (<see cref="DefaultFocusPatch"/>).
    /// </summary>
    private static void FocusNext(NMapScreen screen)
    {
        if (!FocusWanted)
            return;
        string key = $"{_key}|{Index}|{ShowAll}";
        if (key == _focusKey || !ActiveScreenContext.Instance.IsCurrent(screen))
            return;
        var owner = screen.GetViewport().GuiGetFocusOwner();
        if (owner != null && owner != screen && owner is not NMapPoint)
            return;
        _focusKey = key;
        if (NextRoom() is { } next && EnabledNode(screen, next) is { } node && owner != node)
            node.GrabFocus();
    }

    /// <summary>
    /// Chemin suivi tout seul, dès que la carte s'ouvre par Continuer (<see cref="_proceeded"/>) : vote
    /// pour la prochaine salle comme un clic, une fois par salle. En solo, le vote part tout de suite en voyage : le
    /// choix est sauté ; à plusieurs, c'est le vote du joueur, aussitôt. Rien si le joueur a déjà voté (à plusieurs),
    /// s'il dessine, ou avant le tutoriel de la carte.
    /// </summary>
    private static void Follow(NMapScreen screen, IRunState state)
    {
        string key = $"{state.CurrentActIndex}|{state.VisitedMapCoords.Count}";
        if (!ModOptions.MapPathAutoTravel || !_proceeded || !screen.IsTravelEnabled || screen.IsTraveling
            || screen.Drawings.GetLocalDrawingMode() != DrawingMode.None || key == _followKey || NextRoom() is not { } next
            || next.coord.row == 0 && !SaveManager.Instance.SeenFtue("map_select_ftue")
            || CardRunStats.LocalPlayer is not { } me || RunManager.Instance.MapSelectionSynchronizer.GetVote(me).HasValue
            || EnabledNode(screen, next) is not { } node)
            return;
        _followKey = key;
        _proceeded = false;
        BugReport.Note($"Path finder: followed the path to {next.coord.col},{next.coord.row} ({next.PointType})");
        screen.OnMapPointSelectedLocally(node);
    }

    /// <summary>Clavier seul, option cochée : le focus par défaut de la carte est la prochaine salle du chemin.</summary>
    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.DefaultFocusedControl), MethodType.Getter)]
    public static class DefaultFocusPatch
    {
        public static void Postfix(NMapScreen __instance, ref Control __result)
        {
            try
            {
                if (FocusWanted && NextRoom() is { } next && EnabledNode(__instance, next) is { } node)
                    __result = node;
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] path finder focus: {e.Message}");
            }
        }
    }

    // ---- Choix du joueur (onglet Path finder) ----

    /// <summary>Monte (-1) ou descend (+1) un type Max dans le classement.</summary>
    internal static void Move(MapPointType type, int delta)
    {
        var order = Order.ToList();
        int from = order.IndexOf(type), to = from + delta;
        if (from < 0 || to < 0 || to >= order.Count)
            return;
        (order[from], order[to]) = (order[to], order[from]);
        Set(() => Order = order);
    }

    internal static bool IsRanked(MapPointType type) => Order.Contains(type);

    /// <summary>Change la règle d'un type : il garde sa place dans le classement, ou y entre en dernier.</summary>
    private static void SetRule(MapPointType type, Rule rule) => Set(() =>
    {
        _rules[type] = rule;
        if (!Order.Contains(type))
            Order = [.. Order, type];
    });

    internal static void SetMax(MapPointType type) => SetRule(type, new Rule(RuleKind.Max));

    internal static void SetMin(MapPointType type) => SetRule(type, new Rule(RuleKind.Min));

    /// <summary>Nombre n : exactement n, ou au moins n si le type était déjà en « au moins ».</summary>
    internal static void SetCount(MapPointType type, int n) =>
        SetRule(type, new Rule(RuleOf(type) is { Kind: RuleKind.AtLeast } ? RuleKind.AtLeast : RuleKind.Exact, n));

    /// <summary>Bascule entre « exactement n » et « au moins n ».</summary>
    internal static void ToggleAtLeast(MapPointType type)
    {
        if (RuleOf(type) is { Kind: RuleKind.Exact or RuleKind.AtLeast } rule)
            SetRule(type, rule with { Kind = rule.Kind == RuleKind.Exact ? RuleKind.AtLeast : RuleKind.Exact });
    }

    /// <summary>Any : le type sort du classement (et perd son placement).</summary>
    internal static void SetAny(MapPointType type) => Set(() =>
    {
        _rules.Remove(type);
        _placements.Remove(type);
        Order = Order.Where(t => t != type).ToList();
    });

    /// <summary>Choisit le placement d'un type du classement ; le choisir de nouveau le retire.</summary>
    internal static void TogglePlacement(MapPointType type, Placement placement)
    {
        if (!Order.Contains(type))
            return;
        Set(() =>
        {
            if (PlacementOf(type) == placement || placement == Placement.None)
                _placements.Remove(type);
            else
                _placements[type] = placement;
        });
    }

    internal static void SetShowAll(bool all) => Set(() => ShowAll = all);

    /// <summary>
    /// Dessine sur la carte, avec l'outil de dessin du jeu (couleur du personnage du joueur), le chemin affiché ou tous
    /// ceux à égalité : il reste une fois le GUI fermé ; à plusieurs, les autres joueurs le voient comme un dessin fait
    /// à la main. Rien d'autre ne change (dessin seulement, effaçable avec les outils du jeu).
    /// </summary>
    internal static void DrawForTeam()
    {
        if (NMapScreen.Instance is not { } screen || !GodotObject.IsInstanceValid(screen) || Best.Count == 0)
            return;
        var drawings = screen.Drawings;
        // Le joueur est en train de dessiner lui-même : on ne coupe pas son trait
        if (drawings.IsLocalDrawing())
            return;
        var pointsNode = screen.GetNode<Control>("TheMap/Points");
        var nodes = pointsNode.GetChildren().OfType<NMapPoint>().Where(GodotObject.IsInstanceValid)
            .GroupBy(n => n.Point.coord).ToDictionary(g => g.Key, g => g.First());
        // Coordonnées des salles (conteneur des salles) vers celles du calque de dessin
        var toDrawings = drawings.GetGlobalTransform().AffineInverse() * pointsNode.GetGlobalTransform();
        var shown = ShowAll ? Best : [Best[Math.Clamp(Index, 0, Best.Count - 1)]];
        int lines = 0;
        foreach (var path in shown)
        {
            var points = new List<Vector2>();
            if (CardRunStats.LocalPlayer?.RunState.CurrentMapPoint is { } current && nodes.TryGetValue(current.coord, out var here))
                points.Add(toDrawings * (here.Position + here.PivotOffset));
            foreach (var point in path)
                if (nodes.TryGetValue(point.coord, out var node))
                    points.Add(toDrawings * (node.Position + node.PivotOffset));
            if (points.Count < 2)
                continue;
            drawings.BeginLineLocal(points[0], DrawingMode.Drawing);
            foreach (var point in points.Skip(1))
                drawings.UpdateCurrentLinePositionLocal(point);
            drawings.StopLineLocal();
            lines++;
        }
        BugReport.Note($"Path finder: drew {lines} path(s) on the map");
    }

    /// <summary>
    /// Place du chemin choisi dans les nouveaux chemins : le même, ou sa suite si l'on vient d'entrer dans l'une de ses
    /// salles ; -1 s'il n'y est plus.
    /// </summary>
    private static int IndexOf(List<MapPoint> selected, MapPoint? current)
    {
        int reached = current == null ? -1 : selected.FindIndex(p => p.coord == current.coord);
        var wanted = selected.Skip(reached + 1).Select(p => p.coord).ToList();
        for (int i = 0; i < Best.Count; i++)
            if (Best[i].Select(p => p.coord).SequenceEqual(wanted))
                return i;
        return -1;
    }

    internal static void Step(int delta)
    {
        if (Best.Count == 0)
            return;
        Index = (Index + delta + Best.Count) % Best.Count;
    }

    private static void Set(Action change)
    {
        change();
        _failed = false;
        var settings = SavedSettings.Load();
        settings.MapPathRanked = Order.Select(t => t.ToString()).ToList();
        settings.MapPathRules = Order.ToDictionary(t => t.ToString(), t => RuleOf(t)!.Value.ToString());
        settings.MapPathPlacements = Order.Where(t => PlacementOf(t) != Placement.None)
            .ToDictionary(t => t.ToString(), t => PlacementOf(t).ToString().ToLowerInvariant());
        settings.MapPathShowOne = !ShowAll;
        settings.Save();
    }

    // ---- Recherche ----

    /// <summary>
    /// Composition d'un chemin : nombre de salles de chaque type, 6 bits par type (types réglables puis coffres) dans un
    /// entier, pour les comparer et les ranger vite.
    /// </summary>
    private static readonly MapPointType[] VectorTypes = [.. CountedTypes, MapPointType.Treasure];

    private const int Bits = 6;

    private static long Unit(MapPointType type) =>
        Array.IndexOf(VectorTypes, type) is >= 0 and var i ? 1L << (Bits * i) : 0;

    private static int Field(long vector, MapPointType type) =>
        Array.IndexOf(VectorTypes, type) is >= 0 and var i ? (int)((vector >> (Bits * i)) & ((1 << Bits) - 1)) : 0;

    /// <summary>Sauts libres qui restent : null sans déplacement libre, -1 sans limite (Flight).</summary>
    internal static int? FreeMovesNow(IRunState state)
    {
        if (!MegaCrit.Sts2.Core.Hooks.Hook.ShouldAllowFreeTravel(state))
            return null;
        var boots = CardRunStats.LocalPlayer?.Relics.OfType<MegaCrit.Sts2.Core.Models.Relics.WingedBoots>()
            .FirstOrDefault(b => !b.IsUsedUp);
        return boots != null ? 3 - boots.TimesUsed : -1;
    }

    private static ActMap _map = NullActMap.Instance;
    private static bool _unlimited;

    /// <summary>
    /// Suite d'un chemin, salle comprise : sa composition (<see cref="Unit"/>), et pour chaque type qui a un placement,
    /// ce qu'il faut pour le juger (<see cref="PosBits"/> bits par type) : somme des étages (Early, Late), premier et
    /// dernier étage (Grouped), premier étage et plus petit écart (Spread). Étages comptés + 1 (0 : aucune salle).
    /// </summary>
    private readonly record struct Sig(long Counts, long Pos);

    private const int PosBits = 12;
    private const int RowBits = 6;
    private const int RowMask = (1 << RowBits) - 1;

    /// <summary>Écart « aucun » (moins de deux salles) : le plus grand possible.</summary>
    private const int NoGap = RowMask;

    /// <summary>Placement de chaque type réglable pendant la recherche (types du classement seulement).</summary>
    private static Placement[] _searchPlacements = new Placement[CountedTypes.Length];

    private static bool _anyPlacement;

    /// <summary>Suite vide : aucune salle, écarts « aucun ».</summary>
    private static long _emptyPos;

    private static int PosField(long pos, int i) => (int)((pos >> (PosBits * i)) & ((1 << PosBits) - 1));

    private static long WithPosField(long pos, int i, int value) =>
        (pos & ~(((1L << PosBits) - 1) << (PosBits * i))) | ((long)value << (PosBits * i));

    /// <summary>La salle <paramref name="point"/> suivie de la suite <paramref name="rest"/>.</summary>
    private static Sig Prepend(Sig rest, MapPoint point)
    {
        long counts = rest.Counts + Unit(point.PointType);
        int i = Array.IndexOf(CountedTypes, point.PointType);
        if (i < 0 || _searchPlacements[i] == Placement.None)
            return new Sig(counts, rest.Pos);
        int row = point.coord.row + 1, field = PosField(rest.Pos, i);
        int first = field & RowMask, second = field >> RowBits;
        field = _searchPlacements[i] switch
        {
            Placement.Early or Placement.Late => field + row,
            // Premier étage ; dernier : celui de la suite, ou cette salle si la suite n'en a pas
            Placement.Grouped => row | ((first == 0 ? row : second) << RowBits),
            // Premier étage ; plus petit écart avec la suite
            _ => row | ((first == 0 ? second : Math.Min(second, first - row)) << RowBits),
        };
        return new Sig(counts, WithPosField(rest.Pos, i, field));
    }

    /// <summary>Suites possibles d'un chemin, salle comprise, par salle et sauts libres restants.</summary>
    private static readonly Dictionary<(MapPoint, int), HashSet<Sig>> _suffix = new();

    /// <summary>
    /// Salles où l'on peut aller ensuite : les salles reliées, et avec un saut libre, toutes celles de la rangée
    /// suivante (comme le jeu, MapTravel).
    /// </summary>
    private static IEnumerable<(MapPoint Point, int Jumps)> Moves(MapPoint point, int jumps)
    {
        foreach (var child in point.Children.OrderBy(c => c.coord.col))
            yield return (child, jumps);
        if (jumps > 0)
            foreach (var other in _map.GetPointsInRow(point.coord.row + 1).Where(p => !point.Children.Contains(p))
                         .OrderBy(p => p.coord.col))
                yield return (other, _unlimited ? jumps : jumps - 1);
    }

    private static HashSet<Sig> Suffix(MapPoint point, int jumps)
    {
        if (_suffix.TryGetValue((point, jumps), out var known))
            return known;
        var set = new HashSet<Sig>();
        bool any = false;
        foreach (var (next, left) in Moves(point, jumps))
        {
            any = true;
            foreach (var rest in Suffix(next, left))
                set.Add(Prepend(rest, point));
        }
        if (!any)
            set.Add(Prepend(new Sig(0, _emptyPos), point));
        _suffix[(point, jumps)] = set;
        return set;
    }

    /// <summary>Chemins (cette salle comprise) dont la suite est exactement <paramref name="need"/>.</summary>
    private static IEnumerable<List<MapPoint>> PathsWith(MapPoint point, int jumps, Sig need)
    {
        long own = Unit(point.PointType);
        if (own != 0 && Field(need.Counts, point.PointType) == 0)
            yield break;
        bool any = false;
        foreach (var (next, left) in Moves(point, jumps))
        {
            any = true;
            var suffix = Suffix(next, left);
            // Sans placement, la suite se déduit ; avec, on cherche celles qui donnent la suite voulue
            var rests = _anyPlacement
                ? suffix.Where(r => Prepend(r, point) == need).ToList()
                : suffix.Contains(new Sig(need.Counts - own, need.Pos)) ? [new Sig(need.Counts - own, need.Pos)] : [];
            foreach (var rest in rests)
                foreach (var tail in PathsWith(next, left, rest))
                    yield return [point, .. tail];
        }
        if (!any && Prepend(new Sig(0, _emptyPos), point) == need)
            yield return [point];
    }

    /// <summary>
    /// Note d'un placement sur tout l'acte (salles faites, de la plus ancienne à la plus récente, puis la suite) :
    /// plus elle est haute, mieux c'est. Early et Late sans aucune salle : la pire note.
    /// </summary>
    private static double Score(Placement placement, List<int> doneRows, int futureCount, int field)
    {
        int first = field & RowMask, second = field >> RowBits;
        switch (placement)
        {
            case Placement.Early or Placement.Late:
                int count = doneRows.Count + futureCount;
                if (count == 0)
                    return double.NegativeInfinity;
                double mean = (doneRows.Sum() + field) / (double)count;
                return placement == Placement.Early ? -mean : mean;
            case Placement.Grouped:
                int? start = doneRows.Count > 0 ? doneRows[0] : first > 0 ? first : null;
                int? end = first > 0 ? second : doneRows.Count > 0 ? doneRows[^1] : null;
                return start is { } a && end is { } b ? -(b - a) : 0;
            default:
                int gap = NoGap;
                for (int k = 1; k < doneRows.Count; k++)
                    gap = Math.Min(gap, doneRows[k] - doneRows[k - 1]);
                if (first > 0)
                {
                    if (doneRows.Count > 0)
                        gap = Math.Min(gap, first - doneRows[^1]);
                    gap = Math.Min(gap, second);
                }
                return gap;
        }
    }

    /// <summary>
    /// Toutes les suites possibles du reste de l'acte (déplacement libre compris), puis le classement appliqué dans
    /// l'ordre sur l'acte entier (règle de chaque type, puis son placement) ; enfin, les chemins qui ont l'une des
    /// suites gardées.
    /// </summary>
    private static void Search(IRunState state)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _map = state.Map;
        _suffix.Clear();
        _searchPlacements = CountedTypes.Select(PlacementOf).ToArray();
        _anyPlacement = _searchPlacements.Any(p => p != Placement.None);
        _emptyPos = 0;
        for (int i = 0; i < CountedTypes.Length; i++)
            if (_searchPlacements[i] == Placement.Spread)
                _emptyPos = WithPosField(_emptyPos, i, NoGap << RowBits);
        FreeMoves = FreeMovesNow(state);
        _unlimited = FreeMoves == -1;
        int jumps = FreeMoves switch { null => 0, -1 => 1, var n => n.Value };
        var current = state.CurrentMapPoint;
        var first = current == null ? [(_map.StartingMapPoint, jumps)] : Moves(current, jumps).ToList();

        // Salles déjà faites dans l'acte, salle actuelle comprise
        var done = state.VisitedMapCoords.Select(c => _map.GetPoint(c)).OfType<MapPoint>().ToList();
        _done = done.GroupBy(p => p.PointType).ToDictionary(g => g.Key, g => g.Count());
        long doneVector = done.Sum(p => Unit(p.PointType));

        var remaining = first.SelectMany(f => Suffix(f.Item1, f.Item2)).Distinct().ToList();

        // Classement appliqué dans l'ordre ; les nombres proposés pour un type sont ceux qui restent avant lui
        _possible = [];
        foreach (var type in Order)
        {
            int Total(Sig sig) => Field(sig.Counts + doneVector, type);
            var values = remaining.Select(Total).Distinct().Order().ToList();
            _possible[type] = values;
            var rule = RuleOf(type)!.Value;
            remaining = rule.Kind switch
            {
                RuleKind.Max when values.Count > 0 => remaining.Where(v => Total(v) == values[^1]).ToList(),
                RuleKind.Min when values.Count > 0 => remaining.Where(v => Total(v) == values[0]).ToList(),
                // Respectés strictement : sans chemin qui a ce nombre, aucun chemin
                RuleKind.Exact => remaining.Where(v => Total(v) == rule.N).ToList(),
                RuleKind.AtLeast => remaining.Where(v => Total(v) >= rule.N).ToList(),
                _ => remaining,
            };
            // Puis son placement, au même rang : les suites les mieux placées
            int i = Array.IndexOf(CountedTypes, type);
            if (_searchPlacements[i] is var placement and not Placement.None && remaining.Count > 0)
            {
                var doneRows = done.Where(p => p.PointType == type).Select(p => p.coord.row + 1).Order().ToList();
                var scores = remaining.ToDictionary(v => v,
                    v => Score(placement, doneRows, Field(v.Counts, type), PosField(v.Pos, i)));
                double best = scores.Values.Max();
                remaining = remaining.Where(v => scores[v] >= best - 1e-9).ToList();
            }
        }
        foreach (var type in CountedTypes.Where(t => !Order.Contains(t)))
            _possible[type] = remaining.Select(v => Field(v.Counts + doneVector, type)).Distinct().Order().ToList();

        var needs = remaining.ToHashSet();
        var bestPaths = new List<List<MapPoint>>();
        Truncated = false;
        foreach (var (point, left) in first)
            foreach (var need in needs.Where(Suffix(point, left).Contains))
                foreach (var path in PathsWith(point, left, need))
                {
                    if (bestPaths.Count >= MaxPaths)
                    {
                        Truncated = true;
                        goto Done;
                    }
                    bestPaths.Add(path);
                }
        Done:
        Best = bestPaths;
        if (watch.ElapsedMilliseconds > 200)
            Log.Info($"[{ModEntry.Id}] path finder: {watch.ElapsedMilliseconds} ms ({_suffix.Values.Sum(v => v.Count)} suffixes)");
    }

    internal static int Count(List<MapPoint> path, MapPointType type) => path.Count(p => p.PointType == type);

    /// <summary>Salles de chaque type déjà faites dans l'acte, salle actuelle comprise.</summary>
    private static Dictionary<MapPointType, int> _done = [];

    /// <summary>Salles de ce type sur l'acte en suivant ce chemin : déjà faites, puis celles du chemin.</summary>
    internal static int ActCount(List<MapPoint> path, MapPointType type) =>
        (_done.TryGetValue(type, out int done) ? done : 0) + Count(path, type);

    // ---- Tracé ----

    private static Node2D EnsureLines(NMapScreen screen)
    {
        var paths = screen.GetNode<Control>("TheMap/Paths");
        if (paths.GetNodeOrNull<Node2D>(LinesName) is { } lines)
            return lines;
        // Dans le conteneur des pointillés du jeu : mêmes coordonnées, défile avec la carte, sous les salles
        // Groupe dessiné d'un bloc puis rendu transparent : des chemins superposés gardent la même opacité
        lines = new CanvasGroup { Name = LinesName, SelfModulate = new Color(1, 1, 1, 0.75f) };
        paths.AddChild(lines);
        _key = null;
        return lines;
    }

    private static void Draw(NMapScreen screen, Node2D lines)
    {
        foreach (var child in lines.GetChildren())
        {
            lines.RemoveChild(child);
            child.QueueFree();
        }
        lines.Visible = Best.Count > 0;
        if (Best.Count == 0)
            return;
        var nodes = screen.GetNode<Control>("TheMap/Points").GetChildren().OfType<NMapPoint>()
            .Where(GodotObject.IsInstanceValid)
            .GroupBy(n => n.Point.coord).ToDictionary(g => g.Key, g => g.First());
        var color = Order.Count > 0 ? Types.First(t => t.Type == Order[0]).Color : StsColors.gold;
        var shown = ShowAll ? Best : [Best[Math.Clamp(Index, 0, Best.Count - 1)]];
        // Sauts libres (Winged Boots, Flight) : vers une salle qui n'est pas reliée à la précédente
        var jumps = new HashSet<(MapCoord, MapCoord)>();
        Vector2 At(MapPoint point) => nodes[point.coord].Position + nodes[point.coord].PivotOffset;
        foreach (var path in shown)
        {
            var points = new List<Vector2>();
            // Depuis la salle actuelle, pour voir d'où part le chemin
            var from = CardRunStats.LocalPlayer?.RunState.CurrentMapPoint;
            if (from != null && nodes.ContainsKey(from.coord))
                points.Add(At(from));
            foreach (var point in path)
            {
                if (!nodes.ContainsKey(point.coord))
                    continue;
                points.Add(At(point));
                if (from != null && !from.Children.Contains(point))
                    jumps.Add((from.coord, point.coord));
                from = point;
            }
            lines.AddChild(new Line2D
            {
                Points = [.. points],
                // Tous à la fois : plus fins ; opaques, la transparence est celle du groupe
                Width = ShowAll ? 8 : 12,
                DefaultColor = color,
                JointMode = Line2D.LineJointMode.Round,
                BeginCapMode = Line2D.LineCapMode.Round,
                EndCapMode = Line2D.LineCapMode.Round,
                Antialiased = true,
            });
        }
        // Par-dessus : un trait doré plus fin sur chaque saut
        foreach (var (a, b) in jumps)
            if (nodes.TryGetValue(a, out var na) && nodes.TryGetValue(b, out var nb))
                lines.AddChild(new Line2D
                {
                    Points = [na.Position + na.PivotOffset, nb.Position + nb.PivotOffset],
                    Width = ShowAll ? 3 : 5,
                    DefaultColor = StsColors.gold,
                    BeginCapMode = Line2D.LineCapMode.Round,
                    EndCapMode = Line2D.LineCapMode.Round,
                    Antialiased = true,
                });
    }
}

/// <summary>
/// Onglet « Path finder » du GUI, seulement quand la carte est ouverte : types de salles à maximiser (Importance,
/// classés), filtres (Any ou nombre exact, seulement les nombres possibles), chemin affiché parmi ceux à égalité ou tous
/// à la fois.
/// </summary>
internal sealed class PathFinderTab : IRunPanelTab
{
    public string Title => "Path finder";

    public bool Available => MapPaths.MapOpen;

    /// <summary>Carte commune à toute l'équipe : pas de rangée des joueurs.</summary>
    public bool PerPlayer => false;

    public void Subscribe(Player player, Action changed) => MapPaths.Changed += changed;

    public void Unsubscribe(Player player, Action changed) => MapPaths.Changed -= changed;

    public void Fill(VBoxContainer content, IReadOnlyList<Player> players, string? subTab)
    {
        // Importance : appliqués dans l'ordre (▲ ▼ pour le changer), chacun le plus possible (Max) ou un nombre exact,
        // puis son placement (Early, Late, Grouped, Spread), sur tout l'acte ; les nombres proposés tiennent compte des
        // types au-dessus
        content.AddChild(RunPanel.Label("Importance (whole act)", 18, StsColors.halfTransparentCream));
        var order = MapPaths.Order;
        if (order.Count == 0)
            content.AddChild(RunPanel.Label("Nothing chosen: pick Max or a number below.", 16, StsColors.halfTransparentCream));
        for (int i = 0; i < order.Count; i++)
        {
            var type = order[i];
            var (_, name, color) = MapPaths.Types.First(t => t.Type == type);
            var row = Row(content);
            row.AddChild(RunPanel.Label($"{i + 1}.", 18, StsColors.halfTransparentCream));
            var label = RunPanel.Label(name, 18, color);
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(label);
            var up = Choice("▲", false, () => MapPaths.Move(type, -1));
            up.Disabled = i == 0;
            row.AddChild(up);
            var down = Choice("▼", false, () => MapPaths.Move(type, 1));
            down.Disabled = i == order.Count - 1;
            row.AddChild(down);
            content.AddChild(Block(color, Labelled("Count", Counts(type)), Labelled("Where", Placements(type))));
        }

        // Types Any, hors du classement : Max ou un nombre les y fait entrer, en dernier
        var others = MapPaths.Types.Where(t => !MapPaths.IsRanked(t.Type)).ToList();
        if (others.Count > 0)
        {
            content.AddChild(RunPanel.Label("Any", 18, StsColors.halfTransparentCream));
            foreach (var (type, name, color) in others)
            {
                // Hors du classement : couleur du type atténuée
                content.AddChild(RunPanel.Label(name, 18, new Color(color, 0.6f)));
                content.AddChild(Block(new Color(color, 0.35f), Labelled("Count", Counts(type))));
            }
        }

        var best = MapPaths.Best;
        content.AddChild(RunPanel.Label("Path", 18, StsColors.halfTransparentCream));
        if (best.Count > 0)
            FreeMovesLine(content);
        if (best.Count == 0)
        {
            content.AddChild(RunPanel.Label("No path with these numbers.", 18, StsColors.red));
            FreeMovesLine(content);
            return;
        }
        if (!MapPaths.ShowAll && best.Count > 1)
        {
            var cycle = Row(content);
            cycle.AddChild(Choice("◀", false, () => MapPaths.Step(-1)));
            var counter = RunPanel.Label($"{MapPaths.Index + 1} / {best.Count}", 18, StsColors.cream);
            counter.HorizontalAlignment = HorizontalAlignment.Center;
            counter.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            cycle.AddChild(counter);
            cycle.AddChild(Choice("▶", false, () => MapPaths.Step(1)));
        }
        // Dessiner le ou les chemins affichés avec l'outil de dessin du jeu : reste sur la carte une fois le GUI fermé ;
        // à plusieurs, les autres joueurs le voient
        if (players.Count > 0)
        {
            bool team = players[0].RunState.Players.Count > 1;
            var draw = Choice(MapPaths.ShowAll && best.Count > 1 ? $"Draw {best.Count} paths" : "Draw path", false,
                MapPaths.DrawForTeam);
            draw.TooltipText = team
                ? "Draw on the map for the other players (erase with the game's map tools)"
                : "Draw on the map with the game's pen: it stays when the GUI is closed (erase with the game's map tools)";
            content.AddChild(draw);
        }

        // Un seul chemin : la case ne servirait à rien
        if (best.Count > 1)
        {
            var all = new CheckBox { Text = $"Show all {best.Count}{(MapPaths.Truncated ? "+" : "")} paths", ButtonPressed = MapPaths.ShowAll };
            KeyboardNav.Focusable(all);
            all.AddThemeFontSizeOverride("font_size", 17);
            all.AddThemeColorOverride("font_color", StsColors.cream);
            all.AddThemeColorOverride("font_hover_color", StsColors.gold);
            all.Toggled += on => MapPaths.SetShowAll(on);
            content.AddChild(all);
        }

        // Composition de l'acte en suivant le chemin affiché (salles déjà faites comprises) ; tous à la fois :
        // fourchette sur les chemins à égalité
        var shown = MapPaths.ShowAll ? best : [best[Math.Clamp(MapPaths.Index, 0, best.Count - 1)]];
        var rows = new[]
        {
            ("Elites", MapPointType.Elite), ("Rest sites", MapPointType.RestSite), ("Fights", MapPointType.Monster),
            ("?", MapPointType.Unknown), ("Shops", MapPointType.Shop), ("Chests", MapPointType.Treasure),
        }.Select(t =>
        {
            int min = shown.Min(p => MapPaths.ActCount(p, t.Item2)), max = shown.Max(p => MapPaths.ActCount(p, t.Item2));
            return new[] { t.Item1, min == max ? min.ToString() : $"{min}-{max}" };
        });
        var nameColors = new Dictionary<string, Color>
        {
            ["Elites"] = ColorOf(MapPointType.Elite), ["Rest sites"] = ColorOf(MapPointType.RestSite),
            ["Fights"] = ColorOf(MapPointType.Monster), ["?"] = ColorOf(MapPointType.Unknown),
            ["Shops"] = ColorOf(MapPointType.Shop), ["Chests"] = StsColors.gold,
        };
        content.AddChild(StatsTable.Create([["", "Act"], .. rows],
            colorFor: name => nameColors.TryGetValue(name, out var c) ? c : null));
    }

    /// <summary>
    /// « Max », « Min », « Any » puis les nombres possibles de ce type de salle, et « ≥ » quand un nombre est choisi
    /// (au moins ce nombre plutôt qu'exactement). Le choix en surbrillance ; un nombre choisi qui n'est plus possible
    /// reste affiché, en rouge.
    /// </summary>
    private static HBoxContainer Counts(MapPointType type)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        var rule = MapPaths.RuleOf(type);
        var kind = rule?.Kind;
        int? number = kind is MapPaths.RuleKind.Exact or MapPaths.RuleKind.AtLeast ? rule!.Value.N : null;
        var accent = ColorOf(type);
        row.AddChild(Choice("Max", kind == MapPaths.RuleKind.Max, () => MapPaths.SetMax(type), accent));
        row.AddChild(Choice("Min", kind == MapPaths.RuleKind.Min, () => MapPaths.SetMin(type), accent));
        // Any : pas la couleur du type (il ne compte pas)
        row.AddChild(Choice("Any", rule == null, () => MapPaths.SetAny(type)));
        var possible = MapPaths.Possible(type);
        bool impossible = MapPaths.IsImpossible(type);
        // Le nombre choisi reste affiché même s'il n'est plus possible (en rouge) : il est toujours appliqué
        foreach (var n in possible.Union(number is { } w ? [w] : []).Order())
        {
            int count = n;
            var button = Choice(count.ToString(), number == count, () => MapPaths.SetCount(type, count), accent);
            if (number == count && impossible)
            {
                button.AddThemeColorOverride("font_color", StsColors.red);
                button.TooltipText = "No path with this number once the types above it are applied";
            }
            row.AddChild(button);
        }
        if (number != null)
        {
            var atLeast = Choice("≥", kind == MapPaths.RuleKind.AtLeast, () => MapPaths.ToggleAtLeast(type), accent);
            atLeast.TooltipText = "At least this number instead of exactly";
            row.AddChild(atLeast);
        }
        return row;
    }

    private static readonly (MapPaths.Placement Placement, string Text, string Tip)[] PlacementChoices =
    [
        (MapPaths.Placement.Early, "Early", "As early in the act as possible (lowest average floor)"),
        (MapPaths.Placement.Late, "Late", "As late in the act as possible (highest average floor)"),
        (MapPaths.Placement.Grouped, "Grouped", "Close together (fewest floors from the first to the last)"),
        (MapPaths.Placement.Spread, "Spread", "Far apart (largest gap between two in a row)"),
    ];

    /// <summary>
    /// Où placer les salles de ce type : Early, Late, Grouped ou Spread, appliqué juste après son nombre (même rang
    /// d'importance) ; le choix en surbrillance, un clic dessus le retire.
    /// </summary>
    private static HBoxContainer Placements(MapPointType type)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        var current = MapPaths.PlacementOf(type);
        foreach (var (placement, text, tip) in PlacementChoices)
        {
            var choice = placement;
            var button = Choice(text, current == choice, () => MapPaths.TogglePlacement(type, choice), ColorOf(type));
            button.TooltipText = current == choice ? tip + " (click again to remove)" : tip;
            row.AddChild(button);
        }
        return row;
    }

    /// <summary>Déplacement libre pris en compte (Winged Boots : sauts restants ; Flight : sans limite).</summary>
    private static void FreeMovesLine(VBoxContainer content)
    {
        if (MapPaths.FreeMoves is { } moves)
            content.AddChild(RunPanel.Label(moves < 0 ? "Free travel: unlimited" : $"Free travel: {moves} move{(moves == 1 ? "" : "s")} left",
                16, StsColors.halfTransparentCream));
    }

    private static HBoxContainer Row(VBoxContainer content)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        content.AddChild(row);
        return row;
    }

    /// <summary>Couleur d'un type de salle (celle du tracé sur la carte).</summary>
    private static Color ColorOf(MapPointType type) =>
        MapPaths.Types.Any(t => t.Type == type) ? MapPaths.Types.First(t => t.Type == type).Color : StsColors.cream;

    /// <summary>Rangées d'un type, avec une barre verticale de sa couleur à gauche.</summary>
    private static Control Block(Color color, params Control[] rows)
    {
        var block = new HBoxContainer();
        block.AddThemeConstantOverride("separation", 10);
        var indent = new MarginContainer();
        indent.AddThemeConstantOverride("margin_left", 10);
        indent.AddChild(new ColorRect { Color = color, CustomMinimumSize = new Vector2(4, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
        block.AddChild(indent);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 4);
        foreach (var row in rows)
            column.AddChild(row);
        block.AddChild(column);
        return block;
    }

    /// <summary>Rangée précédée de son nom atténué (« Count », « Where »), à largeur fixe pour aligner les boutons.</summary>
    private static Control Labelled(string name, Control row)
    {
        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", 6);
        var label = RunPanel.Label(name, 15, StsColors.halfTransparentCream);
        label.CustomMinimumSize = new Vector2(58, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        line.AddChild(label);
        line.AddChild(row);
        return line;
    }

    /// <param name="accent">couleur du choix en surbrillance (celle du type de salle) ; sinon le cyan par défaut</param>
    private static Button Choice(string text, bool selected, Action pressed, Color? accent = null)
    {
        var button = new Button { Text = text };
        KeyboardNav.Focusable(button);
        button.AddThemeFontSizeOverride("font_size", 17);
        var box = new StyleBoxFlat
        {
            BgColor = selected
                ? accent is { } tint ? new Color(0.09f, 0.11f, 0.15f).Lerp(tint, 0.3f) : new Color(0.16f, 0.2f, 0.26f)
                : new Color(0.09f, 0.11f, 0.15f),
            BorderColor = selected ? accent ?? new Color(0.3648f, 0.9104f, 0.96f, 0.75f) : new Color(1, 1, 1, 0.08f),
            BorderWidthBottom = 2, BorderWidthTop = 2, BorderWidthLeft = 2, BorderWidthRight = 2,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginTop = 2, ContentMarginBottom = 2, ContentMarginLeft = 10, ContentMarginRight = 10,
        };
        // Pas « focus » : c'est le cadre de la navigation au clavier (KeyboardNav), dessiné par-dessus
        foreach (var state in new[] { "normal", "hover", "pressed", "disabled" })
            button.AddThemeStyleboxOverride(state, box);
        button.AddThemeColorOverride("font_color", selected ? StsColors.cream : StsColors.halfTransparentCream);
        button.AddThemeColorOverride("font_hover_color", StsColors.gold);
        button.AddThemeColorOverride("font_disabled_color", new Color(1, 1, 1, 0.15f));
        button.Pressed += pressed;
        return button;
    }
}
