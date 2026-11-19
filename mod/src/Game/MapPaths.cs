using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace StsTools.Game;

/// <summary>
/// Chercheur de chemins : sur la carte, GUI ouvert, le meilleur chemin de la salle actuelle au boss est tracé. Le
/// joueur choisit, dans l'onglet <see cref="PathFinderTab"/> (visible seulement carte ouverte), pour chaque type de salle
/// (marchands, élites, feux de camp, « ? », combats) : le plus possible (Max, classés entre eux par importance),
/// n'importe combien (Any) ou un nombre exact (seulement les nombres possibles) ; les chemins à égalité se parcourent un
/// par un ou s'affichent tous. Seulement la carte telle qu'elle est affichée : rien
/// n'est lu de la graine. Ne touche à rien dans la partie (aucun vote, aucun déplacement).
/// </summary>
public static class MapPaths
{
    private const string LinesName = "StsToolsMapPathLines";

    /// <summary>Au-delà, on arrête d'énumérer (une carte normale en a quelques centaines ou milliers).</summary>
    private const int MaxPaths = 200_000;

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
    /// Classement d'importance, du plus important au moins important : chaque type y est « Max » (le plus de salles)
    /// ou un nombre exact (<see cref="_wanted"/>). Appliqués dans l'ordre : on garde les chemins qui ont le plus de
    /// salles du premier type (ou exactement son nombre), puis parmi eux ceux du deuxième, etc. Les types absents
    /// (Any) sont rangés dans les filtres et ne départagent pas les chemins.
    /// </summary>
    internal static IReadOnlyList<MapPointType> Order { get; private set; } = DefaultRanked;

    private static readonly MapPointType[] CountedTypes = Types.Select(t => t.Type).ToArray();

    /// <summary>
    /// Nombre exact voulu pour un type du classement ; absent : Max. Compté sur tout l'acte (salles déjà faites
    /// comprises), pas seulement sur le chemin restant.
    /// </summary>
    private static Dictionary<MapPointType, int> _wanted = [];

    /// <summary>
    /// Par type, nombres proposés (sur l'acte), croissants : pour un type du classement, ceux des chemins qui restent
    /// après les types plus importants ; pour un type Any, ceux des meilleurs chemins.
    /// </summary>
    private static Dictionary<MapPointType, IReadOnlyList<int>> _possible = [];

    /// <summary>Tous les chemins à égalité tracés en même temps (par défaut), plutôt qu'un seul à la fois.</summary>
    internal static bool ShowAll { get; private set; } = true;

    internal static int Index { get; private set; }

    /// <summary>Chemins classés à égalité (le meilleur score), calculés pour <see cref="_key"/>.</summary>
    internal static IReadOnlyList<List<MapPoint>> Best { get; private set; } = [];

    /// <summary>Nombre exact choisi pour ce type ; le chemin doit le respecter, même s'il n'est plus possible.</summary>
    internal static int? Wanted(MapPointType type) => _wanted.TryGetValue(type, out var n) ? n : null;

    internal static IReadOnlyList<int> Possible(MapPointType type) => _possible.TryGetValue(type, out var list) ? list : [];

    /// <summary>Résultats ou choix changés : l'onglet se redessine.</summary>
    internal static event Action? Changed;

    /// <summary>Carte ouverte ou fermée : l'onglet apparaît ou disparaît.</summary>
    internal static bool MapOpen => _mapOpen;

    private static string? _key, _drawKey;

    /// <summary>Branché une fois, au début d'une partie : vérifié à chaque image (peu coûteux), recalculé seulement si besoin.</summary>
    public static void Install()
    {
        if (_installed || Engine.GetMainLoop() is not SceneTree tree)
            return;
        _installed = true;
        var saved = SavedSettings.Load();
        _wanted = saved.MapPathCounts
            .Where(kv => Enum.TryParse<MapPointType>(kv.Key, out var t) && CountedTypes.Contains(t))
            .ToDictionary(kv => Enum.Parse<MapPointType>(kv.Key), kv => kv.Value);
        // Classement sauvegardé (jamais sauvegardé : par défaut). Réglages d'avant le classement des nombres : un
        // nombre hors du classement était un filtre appliqué avant les Max, il passe en tête
        if (saved.MapPathRanked != null)
            Order = saved.MapPathRanked
                .Select(n => Enum.TryParse<MapPointType>(n, out var t) ? t : (MapPointType?)null)
                .OfType<MapPointType>().Where(CountedTypes.Contains).Distinct().ToList();
        Order = [.. _wanted.Keys.Where(t => !Order.Contains(t)).OrderBy(t => Array.IndexOf(CountedTypes, t)), .. Order];
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
                RunPanel.TabsChanged();
            }
            if (!mapOpen || !RunPanel.IsOpen || _failed)
            {
                Hide();
                return;
            }
            var state = CardRunStats.LocalPlayer!.RunState;
            var lines = EnsureLines(screen!);
            // Caché quand le GUI ou la carte était fermé : de nouveau visible sans rien recalculer
            lines.Visible = Best.Count > 0;
            string searchKey = $"{state.Map.GetHashCode()}|{state.CurrentActIndex}|{state.VisitedMapCoords.Count}|{state.CurrentMapPoint?.coord}|{string.Join(",", Order)}|{string.Join(",", _wanted.OrderBy(kv => kv.Key))}";
            if (searchKey != _key)
            {
                _key = searchKey;
                Index = 0;
                Search(state);
                _drawKey = null;
            }
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
            Hide();
        }
    }

    private static void Hide()
    {
        if (NMapScreen.Instance is { } screen && GodotObject.IsInstanceValid(screen)
            && screen.GetNodeOrNull<Control>("TheMap/Paths")?.GetNodeOrNull<Node2D>(LinesName) is { } lines)
            lines.Visible = false;
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

    internal static bool IsMax(MapPointType type) => Order.Contains(type) && !_wanted.ContainsKey(type);

    internal static bool IsRanked(MapPointType type) => Order.Contains(type);

    /// <summary>
    /// Nombre exact de salles d'un type sur l'acte : le type garde sa place dans le classement, ou y entre en dernier.
    /// null (Any) : il sort du classement.
    /// </summary>
    internal static void SetCount(MapPointType type, int? count) => Set(() =>
    {
        if (count is { } n)
        {
            _wanted[type] = n;
            if (!Order.Contains(type))
                Order = [.. Order, type];
        }
        else
        {
            _wanted.Remove(type);
            Order = Order.Where(t => t != type).ToList();
        }
    });

    /// <summary>Le plus possible de ce type : il garde sa place dans le classement, ou y entre en dernier.</summary>
    internal static void SetMax(MapPointType type) => Set(() =>
    {
        _wanted.Remove(type);
        if (!Order.Contains(type))
            Order = [.. Order, type];
    });

    internal static void SetShowAll(bool all) => Set(() => ShowAll = all);

    /// <summary>
    /// Multijoueur : dessine sur la carte, avec l'outil de dessin du jeu (couleur du personnage du joueur), le chemin
    /// affiché ou tous ceux à égalité ; les autres joueurs le voient comme un dessin fait à la main. Rien d'autre ne
    /// change (dessin seulement, effaçable avec les outils du jeu).
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
        BugReport.Note($"Path finder: drew {lines} path(s) on the map for the team");
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
        settings.MapPathCounts = _wanted.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
        settings.MapPathShowOne = !ShowAll;
        settings.Save();
    }

    // ---- Recherche ----

    /// <summary>
    /// Tous les chemins de la salle suivante jusqu'au boss, puis le classement appliqué dans l'ordre : pour chaque type,
    /// le plus de salles (Max) ou exactement le nombre choisi, compté sur tout l'acte.
    /// </summary>
    private static void Search(IRunState state)
    {
        var map = state.Map;
        var current = state.CurrentMapPoint;
        // Premier pas : les salles où l'on peut aller maintenant (déplacement libre compris), sinon le départ
        IEnumerable<MapPoint> first = current == null
            ? [map.StartingMapPoint]
            : MapTravel.GetTravelablePointsFrom(state, current);

        var all = new List<List<MapPoint>>();
        var stack = new List<MapPoint>();
        void Walk(MapPoint point)
        {
            if (all.Count >= MaxPaths)
                return;
            stack.Add(point);
            if (point.Children.Count == 0)
                all.Add([.. stack]);
            else
                foreach (var child in point.Children.OrderBy(c => c.coord.col))
                    Walk(child);
            stack.RemoveAt(stack.Count - 1);
        }
        foreach (var point in first.OrderBy(p => p.coord.col))
            Walk(point);
        if (all.Count >= MaxPaths)
            Log.Warn($"[{ModEntry.Id}] map paths: more than {MaxPaths} paths, search cut short");

        // Nombres sur tout l'acte : salles déjà faites (salle actuelle comprise) plus celles du chemin
        var done = state.VisitedMapCoords.Select(c => map.GetPoint(c)).OfType<MapPoint>().ToList();
        _done = done.GroupBy(p => p.PointType).ToDictionary(g => g.Key, g => g.Count());
        var counts = all.Select(p => CountedTypes.ToDictionary(t => t, t => ActCount(p, t))).ToList();
        var indices = Enumerable.Range(0, all.Count).ToList();

        // Classement appliqué dans l'ordre ; les nombres proposés pour un type sont ceux des chemins qui restent
        // avant lui (types plus importants appliqués)
        _possible = [];
        var remaining = indices;
        foreach (var type in Order)
        {
            _possible[type] = remaining.Select(i => counts[i][type]).Distinct().Order().ToList();
            remaining = _wanted.TryGetValue(type, out int n)
                // Respecté strictement : sans chemin qui a ce nombre, aucun chemin
                ? remaining.Where(i => counts[i][type] == n).ToList()
                : Top(remaining, [type], counts);
        }
        // Types Any : nombres des meilleurs chemins
        foreach (var type in CountedTypes.Where(t => !Order.Contains(t)))
            _possible[type] = remaining.Select(i => counts[i][type]).Distinct().Order().ToList();
        Best = remaining.Select(i => all[i]).ToList();
    }

    /// <summary>
    /// Chemins à égalité en tête : le plus de salles du premier type, puis du deuxième parmi ceux-là, etc. ; sans type
    /// à maximiser, tous.
    /// </summary>
    private static List<int> Top(List<int> paths, IReadOnlyList<MapPointType> order,
        List<Dictionary<MapPointType, int>> counts)
    {
        foreach (var type in order)
        {
            if (paths.Count == 0)
                break;
            int best = paths.Max(i => counts[i][type]);
            paths = paths.Where(i => counts[i][type] == best).ToList();
        }
        return paths;
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
        foreach (var path in shown)
        {
            var points = new List<Vector2>();
            // Depuis la salle actuelle, pour voir d'où part le chemin
            if (CardRunStats.LocalPlayer?.RunState.CurrentMapPoint is { } current && nodes.TryGetValue(current.coord, out var here))
                points.Add(here.Position + here.PivotOffset);
            foreach (var point in path)
                if (nodes.TryGetValue(point.coord, out var node))
                    points.Add(node.Position + node.PivotOffset);
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

    public void Subscribe(Player player, Action changed) => MapPaths.Changed += changed;

    public void Unsubscribe(Player player, Action changed) => MapPaths.Changed -= changed;

    public void Fill(VBoxContainer content, IReadOnlyList<Player> players, string? subTab)
    {
        // Importance : appliqués dans l'ordre (▲ ▼ pour le changer), chacun le plus possible (Max) ou un nombre exact,
        // sur tout l'acte ; les nombres proposés tiennent compte des types au-dessus
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
            var label = RunPanel.Label(name, 18, i == 0 ? color : StsColors.cream);
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(label);
            var up = Choice("▲", false, () => MapPaths.Move(type, -1));
            up.Disabled = i == 0;
            row.AddChild(up);
            var down = Choice("▼", false, () => MapPaths.Move(type, 1));
            down.Disabled = i == order.Count - 1;
            row.AddChild(down);
            content.AddChild(Indented(Counts(type)));
        }

        // Types Any, hors du classement : Max ou un nombre les y fait entrer, en dernier
        var others = MapPaths.Types.Where(t => !MapPaths.IsRanked(t.Type)).ToList();
        if (others.Count > 0)
        {
            content.AddChild(RunPanel.Label("Any", 18, StsColors.halfTransparentCream));
            foreach (var (type, name, _) in others)
            {
                content.AddChild(RunPanel.Label(name, 18, StsColors.cream));
                content.AddChild(Indented(Counts(type)));
            }
        }

        var best = MapPaths.Best;
        content.AddChild(RunPanel.Label("Path", 18, StsColors.halfTransparentCream));
        if (best.Count == 0)
        {
            content.AddChild(RunPanel.Label("No path with these numbers.", 18, StsColors.red));
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
        // Multijoueur : dessiner le ou les chemins affichés pour les autres joueurs (outil de dessin du jeu)
        if (players.Count > 0 && players[0].RunState.Players.Count > 1)
        {
            var draw = Choice(MapPaths.ShowAll && best.Count > 1 ? $"Draw {best.Count} paths" : "Draw path", false,
                MapPaths.DrawForTeam);
            draw.TooltipText = "Draw on the map for the other players (erase with the game's map tools)";
            content.AddChild(draw);
        }

        // Un seul chemin : la case ne servirait à rien
        if (best.Count > 1)
        {
            var all = new CheckBox { Text = $"Show all {best.Count} paths", ButtonPressed = MapPaths.ShowAll };
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
        content.AddChild(StatsTable.Create([["", "Act"], .. rows]));
    }

    /// <summary>
    /// « Max », « Any » puis les nombres possibles de ce type de salle, le choix en surbrillance (un nombre choisi qui
    /// n'est plus possible reste affiché, en rouge).
    /// </summary>
    private static HBoxContainer Counts(MapPointType type)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        bool max = MapPaths.IsMax(type);
        var wanted = max ? null : MapPaths.Wanted(type);
        row.AddChild(Choice("Max", max, () => MapPaths.SetMax(type)));
        row.AddChild(Choice("Any", !max && wanted == null, () => MapPaths.SetCount(type, null)));
        var possible = MapPaths.Possible(type);
        // Le nombre choisi reste affiché même s'il n'est plus possible (en rouge) : il est toujours appliqué
        foreach (var n in possible.Union(wanted is { } w ? [w] : []).Order())
        {
            int count = n;
            var button = Choice(count.ToString(), wanted == count, () => MapPaths.SetCount(type, count));
            if (!possible.Contains(count))
            {
                button.AddThemeColorOverride("font_color", StsColors.red);
                button.TooltipText = "No path with this number once the types above it are applied";
            }
            row.AddChild(button);
        }
        return row;
    }

    /// <summary>Décalé sous le nom du type de salle.</summary>
    private static MarginContainer Indented(Control control)
    {
        var indent = new MarginContainer();
        indent.AddThemeConstantOverride("margin_left", 24);
        indent.AddChild(control);
        return indent;
    }

    private static HBoxContainer Row(VBoxContainer content)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        content.AddChild(row);
        return row;
    }

    private static Button Choice(string text, bool selected, Action pressed)
    {
        var button = new Button { Text = text };
        KeyboardNav.Focusable(button);
        button.AddThemeFontSizeOverride("font_size", 17);
        var box = new StyleBoxFlat
        {
            BgColor = selected ? new Color(0.16f, 0.2f, 0.26f) : new Color(0.09f, 0.11f, 0.15f),
            BorderColor = selected ? new Color(0.3648f, 0.9104f, 0.96f, 0.75f) : new Color(1, 1, 1, 0.08f),
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
