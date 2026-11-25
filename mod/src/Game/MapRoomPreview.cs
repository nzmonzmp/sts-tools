using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace StsTools.Game;

/// <summary>
/// Aperçu des salles sur la carte : GUI ouvert, au survol d'une salle pas encore faite, un encadré à côté d'elle dit ce
/// qui peut y arriver, jamais ce qui va vraiment arriver (rien n'est lu de la graine ni des listes déjà tirées par le
/// jeu : ennemis, événements) ; fourchettes quand une valeur dépend du chemin pris d'ici là.
/// <list type="bullet">
/// <item>combats : ennemis possibles (ceux de l'acte, moins ceux que le jeu ne peut plus tirer à cette place d'après
/// les combats déjà faits), or, chance de potion, récompenses de cartes et leurs raretés, reliques ;</item>
/// <item>« ? » : probabilité de chaque issue (événement, combat, marchand, trésor, élite), puis le détail d'un combat
/// et d'un trésor ;</item>
/// <item>trésor : or et relique ; feu de camp : soin ; marchand : ce qu'il peut vendre et ses prix.</item>
/// </list>
/// Lit seulement l'état visible ou déduit de ce qui s'est passé (combats faits, chances courantes de potion, de
/// rareté et des « ? ») et appelle directement les méthodes des reliques qui changent ces règles, sans passer par
/// les hooks du jeu (que le mod compte pour ses stats) : rien ne change dans la partie.
/// </summary>
public static class MapRoomPreview
{
    private static bool _installed;
    private static NMapPoint? _hovered;
    private static PanelContainer? _panel;
    private static GridContainer? _grid;
    private static string? _key;

    /// <summary>Plus de « ? » d'ici là, les chances ne sont plus calculées une à une (bornes larges).</summary>
    private const int MaxUnknownSteps = 6;

    private static readonly AccessTools.FieldRef<UnknownMapPointOdds, Dictionary<RoomType, float>> BaseOdds =
        AccessTools.FieldRefAccess<UnknownMapPointOdds, Dictionary<RoomType, float>>("_baseOdds");

    private static readonly Func<ActModel, int> WeakCount =
        AccessTools.MethodDelegate<Func<ActModel, int>>(AccessTools.PropertyGetter(typeof(ActModel), "NumberOfWeakEncounters"));

    /// <summary>Types tirés par un « ? », dans l'ordre où le jeu les essaie ; Event en dernier (le reste).</summary>
    private static readonly RoomType[] UnknownTypes = [RoomType.Monster, RoomType.Elite, RoomType.Treasure, RoomType.Shop];

    public static void Install()
    {
        if (_installed || Engine.GetMainLoop() is not SceneTree tree)
            return;
        _installed = true;
        tree.ProcessFrame += Update;
    }

    [HarmonyPatch(typeof(NMapPoint), "OnFocus")]
    public static class FocusPatch
    {
        public static void Postfix(NMapPoint __instance) => _hovered = __instance;
    }

    [HarmonyPatch(typeof(NMapPoint), "OnUnfocus")]
    public static class UnfocusPatch
    {
        public static void Postfix(NMapPoint __instance)
        {
            if (_hovered == __instance)
                _hovered = null;
        }
    }

    private static void Update()
    {
        try
        {
            var screen = NMapScreen.Instance;
            var point = _hovered;
            var player = CardRunStats.LocalPlayer;
            if (RunPanel.IsOpen && ModOptions.RoomPreview && screen != null && GodotObject.IsInstanceValid(screen) && screen.IsOpen
                && player?.RunState is RunState { Map: not NullActMap } open)
                WarmUp(open);
            if (point == null || !GodotObject.IsInstanceValid(point) || !point.IsVisibleInTree() || !RunPanel.IsOpen || !ModOptions.RoomPreview
                || screen == null || !GodotObject.IsInstanceValid(screen) || !screen.IsOpen || player?.RunState is not RunState state
                || state.Map is NullActMap || point.Point == null)
            {
                Hide();
                return;
            }
            string key = $"{state.CurrentActIndex}|{point.Point.coord}|{state.VisitedMapCoords.Count}|{state.CurrentMapPoint?.coord}";
            if (key != _key)
            {
                _key = key;
                var rows = Rows(state, player, point.Point);
                EnsurePanel(screen);
                var icons = RowIcons(rows ?? [], player);
                StatsTable.Fill(_grid!, rows ?? [], IconFor, iconForRow: row => StatsTable.LoadIcon(icons.ElementAtOrDefault(row)));
                if (rows is { Count: > 0 })
                    AddMonsterImages(rows);
                _panel!.Visible = rows is { Count: > 0 };
                Resize();
            }
            if (_panel is { Visible: true })
                Place(point);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] map room preview: {e}");
            _hovered = null;
            Hide();
        }
    }

    /// <summary>Ligne de l'encadré → rencontre dont elle porte le nom (remplie par <see cref="Rows"/>).</summary>
    private static readonly Dictionary<int, EncounterModel> _encounterRows = new();

    /// <summary>Acte dont les images des rencontres ont été demandées d'avance.</summary>
    private static int _warmedAct = -1;

    /// <summary>
    /// Petite image fixe des monstres à droite du nom de chaque rencontre (même rendu que l'écran Stats) ; moins haute
    /// quand la liste est longue. Faite une fois par rencontre, elle arrive quelques images plus tard la première fois.
    /// </summary>
    private static void AddMonsterImages(List<string[]> rows)
    {
        if (_grid == null || _encounterRows.Count == 0)
            return;
        int columns = rows.Max(r => r.Length);
        float height = _encounterRows.Count <= 8 ? 40 : 30;
        foreach (var (row, encounter) in _encounterRows)
        {
            int index = row * columns + columns - 1;
            if (index >= _grid.GetChildCount())
                continue;
            var old = _grid.GetChild(index);
            var image = new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
                CustomMinimumSize = new Vector2(0, height),
            };
            _grid.RemoveChild(old);
            old.QueueFree();
            _grid.AddChild(image);
            _grid.MoveChild(image, index);
            void Show(Texture2D texture)
            {
                if (!GodotObject.IsInstanceValid(image))
                    return;
                image.Texture = texture;
                float width = height * texture.GetWidth() / Math.Max(1, texture.GetHeight());
                image.CustomMinimumSize = new Vector2(Math.Min(width, height * 5), height);
                Resize();
            }
            if (EncounterVisuals.Thumbnail(encounter, Show) is { } ready)
                Show(ready);
        }
    }

    /// <summary>Images des rencontres de l'acte demandées dès l'ouverture de la carte (GUI ouvert), une à la fois.</summary>
    private static void WarmUp(RunState state)
    {
        if (_warmedAct == state.CurrentActIndex)
            return;
        _warmedAct = state.CurrentActIndex;
        var act = state.Act;
        var rooms = act.GetRoomsDebug();
        foreach (var encounter in act.AllWeakEncounters.Concat(act.AllRegularEncounters).Concat(act.AllEliteEncounters)
                     .Concat(new[] { rooms.Boss, rooms.SecondBoss }.OfType<EncounterModel>()))
            EncounterVisuals.Thumbnail(encounter);
    }

    private static void Resize()
    {
        if (_panel == null || !GodotObject.IsInstanceValid(_panel))
            return;
        _panel.ResetSize();
        _panel.Size = _panel.GetCombinedMinimumSize();
        if (_panel.Visible && _hovered != null && GodotObject.IsInstanceValid(_hovered))
            Place(_hovered);
    }

    private static void Hide()
    {
        _key = null;
        if (_panel != null && GodotObject.IsInstanceValid(_panel))
            _panel.Visible = false;
    }

    private static void EnsurePanel(NMapScreen screen)
    {
        if (_panel != null && GodotObject.IsInstanceValid(_panel) && _panel.GetParent() == screen)
            return;
        _panel = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore, TopLevel = true, ZIndex = 100, Visible = false,
        };
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.05f, 0.08f, 0.95f),
            BorderColor = new Color(StsColors.gold, 0.5f),
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 8, ContentMarginBottom = 8,
        });
        _grid = StatsTable.Create([]);
        _panel.AddChild(_grid);
        screen.AddChild(_panel);
    }

    /// <summary>
    /// Hors de la carte, pour ne cacher aucune salle (les autres choix possibles) : dans la marge à gauche ou à droite
    /// des salles, de préférence celle où il ne couvre pas le GUI ; si aucune marge n'est assez large, contre le bord
    /// gauche de l'écran. Centré sur la salle en hauteur, dans l'écran.
    /// </summary>
    private static void Place(NMapPoint point)
    {
        // Demi-largeur visible d'une icône de salle (le rectangle du nœud est plus large que l'icône)
        const float gap = 16, margin = 8, icon = 36;
        var rect = point.GetGlobalRect();
        var size = _panel!.Size;
        var screen = _panel.GetViewportRect().Size;
        float left = rect.GetCenter().X - icon, right = rect.GetCenter().X + icon;
        foreach (var other in point.GetParent().GetChildren().OfType<NMapPoint>())
            if (other.IsVisibleInTree())
            {
                float center = other.GetGlobalRect().GetCenter().X;
                left = Math.Min(left, center - icon);
                right = Math.Max(right, center + icon);
            }
        float y = Math.Clamp(rect.GetCenter().Y - size.Y / 2, 0, Math.Max(0, screen.Y - size.Y));
        var gui = RunPanel.OpenPanel is { } panel ? panel.GetGlobalRect() : (Rect2?)null;
        var slots = new[] { left - gap - size.X, right + gap }
            .Where(x => x >= margin && x + size.X <= screen.X - margin).ToList();
        float chosen = slots.FirstOrDefault(x => gui is not { } g || !g.Intersects(new Rect2(x, y, size)), slots.FirstOrDefault(margin));
        _panel.GlobalPosition = new Vector2(chosen, y);
    }

    private static Texture2D? IconFor(string name) => StatsTable.LoadIcon(IconPath(name.Trim()));

    private const string MapIcons = "res://images/packed/common_ui/map/icons/";

    /// <summary>Icône d'une ligne ou d'un titre de groupe d'après son nom, ou null.</summary>
    private static string? IconPath(string name)
    {
        // Titres de catégorie du marchand : « If shop: cards ×5 » → « Cards »
        if (name.StartsWith("If shop: "))
            name = char.ToUpperInvariant(name["If shop: ".Length]) + name["If shop: ".Length..][1..];
        if (name.LastIndexOf(" ×", StringComparison.Ordinal) is var times and > 0)
            name = name[..times];
        if (name.StartsWith("Possible enemies") || name.StartsWith("If fight") || name == "Enemy" || name == "Fight")
            return MapIcons + "map_monster.png";
        return name switch
        {
            "Gold" => "res://images/packed/sprite_fonts/gold_icon.png",
            "Potion" or "Potions" => "res://images/packed/sprite_fonts/potion_icon.png",
            "Card rewards" or "Cards" or "Colorless cards" => "res://images/packed/sprite_fonts/card_icon.png",
            "Heal" => "res://images/packed/common_ui/top_bar/top_bar_heart.png",
            "Relics" => "res://images/ui/reward_screen/reward_icon_shared_relic.png",
            "Card removal" or "Services" => "res://images/ui/reward_screen/reward_icon_card_removal.png",
            "On sale" => "res://images/rooms/merchant_room/shop_sales_tag.png",
            "Shop" or "Shop relic" or "Offers" or "If shop" => MapIcons + "map_shop.png",
            "Elite" or "If elite" => MapIcons + "map_elite.png",
            "Treasure" or "If treasure" => MapIcons + "map_chest.png",
            "Boss" => MapIcons + "map_chest_boss.png",
            "Event" or "Unknown" or "Possible rooms" => MapIcons + "map_unknown.png",
            "Rest site" => MapIcons + "map_rest.png",
            _ => null,
        };
    }

    /// <summary>
    /// Icône de chaque ligne : la sienne (nom connu, relique possédée), sinon celle de la ligne dont elle dépend (ligne
    /// non décalée au-dessus pour une ligne décalée, sinon titre du groupe ; ennemis d'une élite : icône d'élite).
    /// </summary>
    private static List<string?> RowIcons(List<string[]> rows, Player player)
    {
        var relics = new Dictionary<string, string>();
        foreach (var relic in player.Relics)
            relics.TryAdd(relic.Title.GetFormattedText(), relic.PackedIconPath);
        var icons = new List<string?>();
        string? group = rows.Count > 0 ? IconPath(rows[0][0]) : null, parent = group;
        for (int i = 0; i < rows.Count; i++)
        {
            string name = rows[i][0], trimmed = name.Trim();
            string? own = relics.GetValueOrDefault(trimmed) ?? IconPath(trimmed);
            if (i > 0 && rows[i].Length == 1)
            {
                group = parent = own ?? group;
                if (trimmed.StartsWith("Possible enemies") && rows[0][0] == "Elite")
                    group = parent = IconPath("Elite");
                icons.Add(own == null ? null : group);
                continue;
            }
            bool indented = name.StartsWith("  ");
            string? icon = own ?? (indented ? parent : group);
            if (!indented)
                parent = icon;
            icons.Add(icon);
        }
        return icons;
    }

    // ---- Contenu ----

    /// <summary>Lignes de l'encadré (en-tête, puis groupes) ; null : rien à dire sur cette salle.</summary>
    internal static List<string[]>? Rows(RunState state, Player player, MapPoint point)
    {
        // Salles déjà faites (le jeu a sa propre infobulle) et salle actuelle
        if (state.VisitedMapCoords.Contains(point.coord) || state.CurrentMapPoint?.coord == point.coord)
            return null;
        _encounterRows.Clear();
        var before = Between(state, point);
        var rows = new List<string[]>();
        switch (point.PointType)
        {
            case MapPointType.Monster:
                rows.Add(["Fight", Floor(state, point)]);
                Combat(rows, state, player, RoomType.Monster, before, point);
                break;
            case MapPointType.Elite:
                rows.Add(["Elite", Floor(state, point)]);
                Combat(rows, state, player, RoomType.Elite, before, point);
                break;
            case MapPointType.Boss:
                rows.Add(["Boss", Floor(state, point)]);
                Combat(rows, state, player, RoomType.Boss, before, point);
                break;
            case MapPointType.Unknown:
                rows.Add(["Unknown", Floor(state, point)]);
                Unknown(rows, state, player, before, point);
                break;
            case MapPointType.Treasure:
                rows.Add(["Treasure", Floor(state, point)]);
                Treasure(rows, state, player);
                break;
            case MapPointType.RestSite:
                rows.Add(["Rest site", Floor(state, point)]);
                Rest(rows, state, player);
                break;
            case MapPointType.Shop:
                rows.Add(["Shop", Floor(state, point)]);
                Shop(rows, state, player, before);
                break;
            default:
                return null;
        }
        // Soins des reliques en entrant (Pantograph avant un boss…), après l'en-tête
        var heals = EntryHeals(state, player, point.PointType);
        rows.InsertRange(1, heals);
        // Les lignes des rencontres descendent d'autant
        if (heals.Count > 0)
            foreach (var (row, encounter) in _encounterRows.OrderByDescending(kv => kv.Key).ToList())
            {
                _encounterRows.Remove(row);
                _encounterRows[row + heals.Count] = encounter;
            }
        // Trois colonnes (nom, chance, prix du marchand) : les autres valeurs vont dans la dernière, alignées à droite
        if (rows.Any(r => r.Length == 3))
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Length == 2)
                    rows[i] = [rows[i][0], "", rows[i][1]];
        return rows;
    }

#if TESTBRIDGE
    /// <summary>Pour le pont de test : salles comptées entre ici et cette salle, sauts libres.</summary>
    internal static string Debug(RunState state, MapPoint point) => $"{Between(state, point)} jumps={MapPaths.FreeMovesNow(state)}";
#endif

    private static string Floor(RunState state, MapPoint point)
    {
        int earlier = state.MapPointHistory.Take(state.CurrentActIndex).Sum(a => a.Count);
        return $"Floor {earlier + point.coord.row + 1}";
    }

    /// <summary>
    /// Salles entre la salle actuelle (exclue) et la salle visée (exclue) : le moins et le plus de combats, d'élites et
    /// de « ? » sur les chemins possibles, déplacement libre compris (Winged Boots : sauts restants ; Flight : sans
    /// limite) ; un marchand juste avant (sûr ou possible) interdit le marchand à un « ? ».
    /// </summary>
    /// <param name="MinCombats">le moins de combats sûrs (combats et élites) sur un même chemin</param>
    /// <param name="MaxCombats">le plus de combats possibles (un « ? » peut en être un) sur un même chemin</param>
    /// <param name="MaxShops">le plus de marchands possibles (un « ? » peut en être un) sur un même chemin</param>
    private sealed record Before(int MinFights, int MaxFights, int MinElites, int MaxElites, int MinUnknowns, int MaxUnknowns,
        int MinCombats, int MaxCombats, bool AfterShopOnly, bool AfterShopMaybe, int MaxShops);

    private readonly record struct Span(int MinF, int MaxF, int MinE, int MaxE, int MinU, int MaxU, int MinC, int MaxC, int MaxS);

    /// <summary>
    /// Salles où l'on peut aller ensuite, avec les sauts libres qui restent (-1 : sans limite) : les salles reliées et,
    /// avec un saut, toutes celles de la rangée suivante (comme <see cref="MapPaths"/>).
    /// </summary>
    private static IEnumerable<(MapPoint Point, int Jumps)> Moves(ActMap map, MapPoint point, int jumps)
    {
        foreach (var child in point.Children)
            yield return (child, jumps);
        if (jumps != 0)
            foreach (var other in map.GetPointsInRow(point.coord.row + 1).Where(p => !point.Children.Contains(p)))
                yield return (other, jumps < 0 ? -1 : jumps - 1);
    }

    private static Before Between(RunState state, MapPoint target)
    {
        var map = state.Map;
        var start = state.CurrentMapPoint ?? map.StartingMapPoint;
        int startJumps = MapPaths.FreeMovesNow(state) ?? 0;
        var memo = new Dictionary<(MapCoord, int), Span?>();
        Span? From(MapPoint p, int jumps)
        {
            if (p.coord == target.coord)
                return new Span(0, 0, 0, 0, 0, 0, 0, 0, 0);
            if (p.coord.row >= target.coord.row && target.PointType != MapPointType.Boss)
                return null;
            if (memo.TryGetValue((p.coord, jumps), out var known))
                return known;
            Span? best = null;
            foreach (var (next, left) in Moves(map, p, jumps))
            {
                if (From(next, left) is not { } s)
                    continue;
                // La salle de départ du calcul n'est pas comptée, les suivantes oui (sauf la cible)
                if (next.coord != target.coord)
                {
                    int f = next.PointType == MapPointType.Monster ? 1 : 0;
                    int e = next.PointType == MapPointType.Elite ? 1 : 0;
                    int u = next.PointType == MapPointType.Unknown ? 1 : 0;
                    int shop = next.PointType == MapPointType.Shop ? 1 : 0;
                    s = new Span(s.MinF + f, s.MaxF + f, s.MinE + e, s.MaxE + e, s.MinU + u, s.MaxU + u,
                        s.MinC + f + e, s.MaxC + f + e + u, s.MaxS + shop + u);
                }
                best = best is not { } b ? s : new Span(Math.Min(b.MinF, s.MinF), Math.Max(b.MaxF, s.MaxF),
                    Math.Min(b.MinE, s.MinE), Math.Max(b.MaxE, s.MaxE), Math.Min(b.MinU, s.MinU), Math.Max(b.MaxU, s.MaxU),
                    Math.Min(b.MinC, s.MinC), Math.Max(b.MaxC, s.MaxC), Math.Max(b.MaxS, s.MaxS));
            }
            memo[(p.coord, jumps)] = best;
            return best;
        }
        // Salles d'où l'on peut entrer dans la cible : celle d'avant compte pour le marchand d'un « ? »
        var seen = new HashSet<(MapCoord, int)> { (start.coord, startJumps) };
        var queue = new Queue<(MapPoint Point, int Jumps)>([(start, startJumps)]);
        var previous = new List<MapPoint>();
        while (queue.Count > 0)
        {
            var (p, jumps) = queue.Dequeue();
            if (p.coord.row >= target.coord.row && target.PointType != MapPointType.Boss)
                continue;
            foreach (var (next, left) in Moves(map, p, jumps))
            {
                if (next.coord == target.coord)
                {
                    if (p != start)
                        previous.Add(p);
                    continue;
                }
                if (seen.Add((next.coord, left)))
                    queue.Enqueue((next, left));
            }
        }
        bool fromStart = Moves(map, start, startJumps).Any(m => m.Point.coord == target.coord);
        bool currentShop = fromStart && state.CurrentMapPointHistoryEntry?.HasRoomOfType(RoomType.Shop) == true;
        var all = previous.Select(p => p.PointType).ToList();
        bool shopOnly = !fromStart ? all.Count > 0 && all.All(t => t == MapPointType.Shop) : currentShop && all.Count == 0;
        bool shopMaybe = shopOnly || currentShop || all.Any(t => t is MapPointType.Shop or MapPointType.Unknown);
        if (From(start, startJumps) is { } span)
            return new Before(span.MinF, span.MaxF, span.MinE, span.MaxE, span.MinU, span.MaxU, span.MinC, span.MaxC,
                shopOnly, shopMaybe, span.MaxS);
        // Hors d'atteinte (salle d'une autre branche) : bornes larges
        int rows = Math.Max(0, target.coord.row - (state.CurrentMapPoint?.coord.row ?? 0) - 1);
        return new Before(0, rows, 0, rows, 0, rows, 0, rows, shopOnly, true, rows);
    }

    // ---- Combats ----

    private static void Combat(List<string[]> rows, RunState state, Player player, RoomType type, Before before, MapPoint point)
    {
        var candidates = Encounters(state, type, before, point);
        Enemies(rows, state, candidates, type == RoomType.Boss ? "Enemy" : "Possible enemies");
        Rewards(rows, state, player, type, before, candidates);
    }

    /// <summary>
    /// Noms des rencontres possibles ; s'il y en a des deux sortes, les faciles (premiers combats de l'acte) et les
    /// difficiles à part.
    /// </summary>
    private static void Enemies(List<string[]> rows, RunState state, List<EncounterModel> candidates, string title)
    {
        if (candidates.Count == 0)
            return;
        var weak = candidates.Where(e => e.RoomType == RoomType.Monster && e.IsWeak).ToList();
        var groups = weak.Count > 0 && weak.Count < candidates.Count
            ? new[] { ($"{title} (easy)", weak), ($"{title} (hard)", candidates.Except(weak).ToList()) }
            : [(title, candidates)];
        foreach (var (name, list) in groups)
        {
            rows.Add([name]);
            foreach (var same in list.GroupBy(Title).OrderBy(g => g.Key))
            {
                rows.Add([same.Key, ""]);
                _encounterRows[rows.Count - 1] = same.First();
            }
        }
    }

    private static string Title(EncounterModel e)
    {
        try
        {
            return e.Title.GetFormattedText();
        }
        catch (Exception)
        {
            return e.Id.Entry;
        }
    }

    /// <summary>
    /// Rencontres possibles à cette salle. Le jeu tire en début d'acte une liste de rencontres dans un sac (chaque
    /// rencontre une fois avant de remettre tout le sac, pas deux fois de suite la même famille) ; le mod ne lit pas
    /// cette liste, seulement les combats déjà faits : à la place n de la liste, une rencontre déjà tirée depuis le
    /// dernier remplissage du sac ne peut pas revenir, ni la famille du combat juste avant.
    /// </summary>
    private static List<EncounterModel> Encounters(RunState state, RoomType type, Before before, MapPoint point)
    {
        var act = state.Act;
        if (type == RoomType.Boss)
        {
            var rooms = act.GetRoomsDebug();
            var boss = state.Map.SecondBossMapPoint?.coord == point.coord ? rooms.SecondBoss : rooms.Boss;
            return boss == null ? [] : [boss];
        }
        var done = (state.MapPointHistory.ElementAtOrDefault(state.CurrentActIndex) ?? [])
            .SelectMany(entry => entry.Rooms).Where(r => r.RoomType == type && r.ModelId != null).Select(r => r.ModelId!).ToList();
        var result = new HashSet<EncounterModel>();
        if (type == RoomType.Elite)
        {
            var pool = act.AllEliteEncounters.ToList();
            var drawn = done.Select(id => pool.FirstOrDefault(e => e.Id == id)).OfType<EncounterModel>().ToList();
            int unknownElites = state.Odds.UnknownMapPoint.EliteOdds > 0 ? before.MaxUnknowns : 0;
            for (int i = drawn.Count + before.MinElites; i <= drawn.Count + before.MaxElites + unknownElites; i++)
                result.UnionWith(Candidates(pool, drawn, i, 0));
            return result.ToList();
        }
        var weak = act.AllWeakEncounters.ToList();
        var regular = act.AllRegularEncounters.ToList();
        int weakCount = state.GameMode == GameMode.Blitz ? 1 : WeakCount(act);
        var all = weak.Concat(regular).ToList();
        var fought = done.Select(id => all.FirstOrDefault(e => e.Id == id)).OfType<EncounterModel>().ToList();
        for (int i = fought.Count + before.MinFights; i <= fought.Count + before.MaxFights + before.MaxUnknowns; i++)
        {
            if (i < weakCount)
                result.UnionWith(Candidates(weak, fought, i, 0));
            else
                result.UnionWith(Candidates(regular, fought, i, weakCount));
        }
        return result.ToList();
    }

    /// <summary>Rencontres possibles à la place <paramref name="index"/> de la liste, segment commençant à <paramref name="offset"/>.</summary>
    private static IEnumerable<EncounterModel> Candidates(List<EncounterModel> pool, List<EncounterModel> drawn, int index, int offset)
    {
        if (pool.Count == 0)
            return [];
        int j = index - offset;
        int cycleStart = offset + j / pool.Count * pool.Count;
        var left = pool.Where(e => !drawn.Skip(cycleStart).Take(Math.Max(0, Math.Min(index, drawn.Count) - cycleStart)).Contains(e)).ToList();
        // Le combat juste avant est connu : ni lui ni sa famille, sauf s'il ne reste rien d'autre
        if (index - 1 < drawn.Count && index - 1 >= 0)
        {
            var previous = drawn[index - 1];
            var other = left.Where(e => e != previous && !e.SharesTagsWith(previous)).ToList();
            if (other.Count > 0)
                left = other;
        }
        return left;
    }

    private static IEnumerable<AbstractModel> Listeners(RunState state) => state.IterateHookListeners(null);

    private static void Rewards(List<string[]> rows, RunState state, Player player, RoomType type, Before before,
        List<EncounterModel> candidates)
    {
        // Pas de récompenses après le dernier boss
        if (type == RoomType.Boss && state.CurrentActIndex >= state.Acts.Count - 1)
            return;
        rows.Add(["Rewards"]);
        int minGold = candidates.Count > 0 ? candidates.Min(e => e.MinGoldReward) : DefaultGold(type).Min;
        int maxGold = candidates.Count > 0 ? candidates.Max(e => e.MaxGoldReward) : DefaultGold(type).Max;
        rows.Add(["Gold", Range(Gold(state, player, minGold), Gold(state, player, maxGold))]);
        if (player.Relics.OfType<AmethystAubergine>().FirstOrDefault() is { } aubergine)
            rows.Add(["Gold", $"+{Gold(state, player, aubergine.DynamicVars.Gold.IntValue)} ({aubergine.Title.GetFormattedText()})"]);
        rows.Add(["Potion", PotionChance(state, player, type, before)]);

        bool vintage = type == RoomType.Monster && state.Modifiers.OfType<Vintage>().Any();
        int cardRewards = 1 + (type == RoomType.Monster && player.Relics.OfType<PrayerWheel>().Any() ? 1 : 0);
        int relics = type == RoomType.Elite ? 1 + player.Relics.OfType<BlackStar>().Count() : 0;
        if (type == RoomType.Boss && state.CurrentActIndex == 0 && player.Relics.OfType<LavaRock>().FirstOrDefault() is { HasTriggered: false } lava)
            relics += lava.DynamicVars["Relics"].IntValue;
        if (vintage)
        {
            relics += cardRewards;
            cardRewards = 0;
        }
        // Wongo's Mystery Ticket : ses reliques à la fin du 5e combat (combats d'ici là, puis celui-ci)
        int extraRelicsMin = 0, extraRelicsMax = 0;
        if (player.Relics.OfType<WongosMysteryTicket>().FirstOrDefault(t => !t.GaveRelic) is { } ticket)
        {
            int count = ticket.DynamicVars.Repeat.IntValue;
            if (ticket.CombatsFinished + before.MinCombats + 1 >= 5)
                extraRelicsMin = count;
            if (ticket.CombatsFinished + before.MaxCombats + 1 >= 5)
                extraRelicsMax = count;
        }
        if (cardRewards > 0)
        {
            rows.Add(["Card rewards", (cardRewards == 1 ? "3 cards" : $"{cardRewards} × 3 cards") + LastingCandyText(player, before)]);
            CardRarities(rows, state, player, type, before);
            CardModifiers(rows, state, player, before);
        }
        if (type == RoomType.Elite && player.Relics.OfType<WhiteStar>().Any())
            rows.Add(["Card rewards", "+3 rare cards"]);
        if (relics + extraRelicsMax > 0)
            RelicRarities(rows, Range(relics + extraRelicsMin, relics + extraRelicsMax));
    }

    /// <summary>
    /// Lasting Candy : une carte Pouvoir en plus dans la récompense de cartes d'un combat sur deux (compteur de la
    /// relique, plus un par combat d'ici là).
    /// </summary>
    private static string LastingCandyText(Player player, Before before)
    {
        if (player.Relics.OfType<LastingCandy>().FirstOrDefault() is not { } candy)
            return "";
        var triggers = CombatCounts(before).Select(c => candy.CombatRewardsSeen + c)
            .Select(seen => seen > 0 && seen % 2 == 1).Distinct().ToList();
        return triggers.Count > 1 ? " (+1 Power: maybe)" : triggers[0] ? " + 1 Power" : "";
    }

    /// <summary>
    /// Cartes proposées améliorées ou enchantées : chance du jeu à partir de l'acte 2 (cartes non rares), et reliques
    /// qui changent les cartes proposées.
    /// </summary>
    private static void CardModifiers(List<string[]> rows, RunState state, Player player, Before before)
    {
        decimal scaling = AscensionHelper.GetValueIfAscension(AscensionLevel.Scarcity, 0.125m, 0.25m);
        decimal upgrade = Math.Min(1, state.CurrentActIndex * scaling);
        if (upgrade > 0)
            rows.Add(["  Upgraded (not rare)", $"{upgrade * 100:0}%"]);
        string Name(RelicModel relic) => "  " + relic.Title.GetFormattedText();
        foreach (var relic in player.Relics)
        {
            string? effect = relic switch
            {
                MoltenEgg => "Attacks upgraded",
                ToxicEgg => "Skills upgraded",
                FrozenEgg => "Powers upgraded",
                LavaLamp => "Upgraded if no damage taken",
                SilverCrucible crucible => CrucibleText(crucible, before),
                Glitter => "Glam",
                SilkenTress { IsUsedUp: false } => "Glam",
                FresnelLens => "Nimble",
                WingCharm => "Swift on one card",
                _ => null,
            };
            if (effect != null)
                rows.Add([Name(relic), effect]);
        }
    }

    /// <summary>Silver Crucible : les cartes de ses premières récompenses sont améliorées.</summary>
    private static string? CrucibleText(SilverCrucible crucible, Before before)
    {
        int left = crucible.DynamicVars.Cards.IntValue - crucible.TimesUsed;
        if (left <= before.MinCombats)
            return null;
        return left > before.MaxCombats ? "Upgraded" : "Upgraded: maybe";
    }

    private static (int Min, int Max) DefaultGold(RoomType type)
    {
        var (min, max) = type switch
        {
            RoomType.Monster => (10m, 20m),
            RoomType.Elite => (35m, 45m),
            _ => (100m, 100m),
        };
        if (AscensionHelper.HasAscension(AscensionLevel.Poverty))
            (min, max) = (min * 0.75m, max * 0.75m);
        return ((int)min, (int)max);
    }

    /// <summary>Or vraiment gagné : modifié par les reliques (Bowler Hat, Ectoplasm…), comme le jeu.</summary>
    private static int Gold(RunState state, Player player, int amount)
    {
        decimal value = amount;
        foreach (var model in Listeners(state))
            value = model.ModifyGoldGained(player, value);
        return (int)value;
    }

    /// <summary>Au-delà, les combats d'ici là ne changent plus les chances affichées que de très peu.</summary>
    private const int MaxCombatSteps = 20;

    /// <summary>Nombres de combats possibles d'ici là (un « ? » compté comme un combat possible).</summary>
    private static IEnumerable<int> CombatCounts(Before before) =>
        Enumerable.Range(Math.Min(before.MinCombats, MaxCombatSteps),
            Math.Min(before.MaxCombats, MaxCombatSteps) - Math.Min(before.MinCombats, MaxCombatSteps) + 1);

    /// <summary>
    /// Chance de potion dans cette salle : la chance courante du joueur (elle baisse de 10 % après une potion, monte de
    /// 10 % sinon), plus la moitié du bonus d'élite. Les combats d'ici là la font varier : chance moyenne sur tous leurs
    /// résultats possibles, pour chaque nombre de combats d'ici là (fourchette).
    /// </summary>
    private static string PotionChance(RunState state, Player player, RoomType type, Before before)
    {
        if (Listeners(state).Any(m => m.ShouldForcePotionReward(player, type)))
            return "100%";
        float bonus = type == RoomType.Elite ? PotionRewardOdds.eliteBonus * 0.5f : 0f;
        var values = new Dictionary<int, double> { [0] = 1 };
        float current = player.PlayerOdds.PotionReward.CurrentValue;
        // Valeur courante + 0,1 × k : seul k change
        double Chance(int k, float extra) => Math.Clamp(current + 0.1 * k + extra, 0, 1);
        var chances = new List<double>();
        var counts = CombatCounts(before).ToHashSet();
        for (int step = 0; step <= counts.Max(); step++)
        {
            if (counts.Contains(step))
                chances.Add(values.Sum(kv => kv.Value * Chance(kv.Key, bonus)));
            var next = new Dictionary<int, double>();
            foreach (var (k, p) in values)
            {
                double potion = Chance(k, 0);
                next[k - 1] = next.GetValueOrDefault(k - 1) + p * potion;
                next[k + 1] = next.GetValueOrDefault(k + 1) + p * (1 - potion);
            }
            values = next;
        }
        return Percent((float)chances.Min(), (float)chances.Max());
    }

    /// <summary>
    /// Rareté des cartes proposées : chances de base de la salle, décalées vers les rares par la chance courante du
    /// joueur (elle monte à chaque carte non rare, revient au plus bas après une rare). Moyenne sur les 3 cartes et
    /// sur tous les tirages possibles des combats d'ici là (3 cartes par combat), pour chaque nombre de combats
    /// (fourchette).
    /// </summary>
    private static void CardRarities(List<string[]> rows, RunState state, Player player, RoomType type, Before before)
    {
        if (type == RoomType.Boss || type == RoomType.Elite && state.Modifiers.OfType<BigGameHunter>().Any())
        {
            rows.Add(["  Rare", "100%"]);
            return;
        }
        var odds = player.PlayerOdds.CardRarity;
        float growth = odds.RarityGrowth;
        float rareBase = type == RoomType.Elite ? CardRarityOdds.EliteRareOdds : CardRarityOdds.RegularRareOdds;
        float uncommonBase = type == RoomType.Elite ? CardRarityOdds.eliteUncommonOdds : CardRarityOdds.regularUncommonOdds;
        double Rare(float baseRare, float offset) => Math.Clamp(baseRare + offset, 0, 1);
        Dictionary<float, double> Card(Dictionary<float, double> offsets, float baseRare) => NextCard(offsets, baseRare, growth);
        var offsets = new Dictionary<float, double> { [MathF.Round(odds.CurrentValue, 4)] = 1 };
        var counts = CombatCounts(before).ToHashSet();
        var results = new List<(double Rare, double Uncommon)>();
        for (int step = 0; step <= counts.Max(); step++)
        {
            if (counts.Contains(step))
            {
                double rare = 0, uncommon = 0;
                var during = offsets;
                for (int card = 0; card < 3; card++)
                {
                    foreach (var (o, p) in during)
                    {
                        double r = Rare(rareBase, o);
                        rare += p * r / 3;
                        uncommon += p * (Math.Clamp(uncommonBase + rareBase + o, 0, 1) - r) / 3;
                    }
                    during = Card(during, rareBase);
                }
                results.Add((rare, uncommon));
            }
            for (int card = 0; card < 3; card++)
                offsets = Card(offsets, CardRarityOdds.RegularRareOdds);
        }
        string Of(Func<(double Rare, double Uncommon), double> f) => Percent((float)results.Min(f), (float)results.Max(f));
        rows.Add(["  Rare", Of(r => r.Rare)]);
        rows.Add(["  Uncommon", Of(r => r.Uncommon)]);
        rows.Add(["  Common", Of(r => 1 - r.Rare - r.Uncommon)]);
    }

    /// <summary>Décalage de rareté après une carte proposée : au plus bas après une rare, sinon un peu plus haut.</summary>
    private static Dictionary<float, double> NextCard(Dictionary<float, double> offsets, float baseRare, float growth)
    {
        var next = new Dictionary<float, double>();
        foreach (var (o, p) in offsets)
        {
            double rare = Math.Clamp(baseRare + o, 0, 1);
            float up = MathF.Round(Math.Min(o + growth, 0.4f), 4);
            next[-0.05f] = next.GetValueOrDefault(-0.05f) + p * rare;
            next[up] = next.GetValueOrDefault(up) + p * (1 - rare);
        }
        return next;
    }

    /// <summary>Rareté d'une relique de récompense ou de coffre : 50 % commune, 33 % peu commune, 17 % rare.</summary>
    private static void RelicRarities(List<string[]> rows, string count)
    {
        rows.Add(["Relics", count]);
        rows.Add(["  Common", "50%"]);
        rows.Add(["  Uncommon", "33%"]);
        rows.Add(["  Rare", "17%"]);
    }

    // ---- « ? » ----

    private static void Unknown(List<string[]> rows, RunState state, Player player, Before before, MapPoint point)
    {
        var chances = UnknownChances(state, before, point);
        rows.Add(["Possible rooms"]);
        foreach (var (type, low, high) in chances.Where(c => c.High > 0).OrderByDescending(c => c.High))
            rows.Add([RoomName(type), Percent(low, high)]);
        if (chances.Any(c => c.Type == RoomType.Monster && c.High > 0))
        {
            var candidates = Encounters(state, RoomType.Monster, before, point);
            Enemies(rows, state, candidates, "If fight");
            var sub = new List<string[]>();
            Rewards(sub, state, player, RoomType.Monster, before, candidates);
            sub[0] = ["Fight rewards"];
            rows.AddRange(sub);
        }
        if (chances.Any(c => c.Type == RoomType.Elite && c.High > 0))
            rows.Add(["If elite", "elite fight, relic"]);
        if (chances.Any(c => c.Type == RoomType.Treasure && c.High > 0))
        {
            rows.Add(["If treasure"]);
            Treasure(rows, state, player);
        }
        if (chances.Any(c => c.Type == RoomType.Shop && c.High > 0))
        {
            Shop(rows, state, player, before, "If shop: ");
        }
    }

    private static string RoomName(RoomType type) => type switch
    {
        RoomType.Monster => "Fight",
        RoomType.Elite => "Elite",
        RoomType.Treasure => "Treasure",
        RoomType.Shop => "Shop",
        RoomType.Event => "Event",
        _ => type.ToString(),
    };

    /// <summary>
    /// Chances de chaque issue d'un « ? » : celles du jeu à cet instant (chaque type non tiré gagne sa chance de base,
    /// le type tiré revient à sa base), avancées pour chaque nombre possible de « ? » d'ici là ; plus petite et plus
    /// grande chance sur ces nombres.
    /// </summary>
    private static List<(RoomType Type, float Low, float High)> UnknownChances(RunState state, Before before, MapPoint point)
    {
        var odds = state.Odds.UnknownMapPoint;
        var current = new[] { odds.MonsterOdds, odds.EliteOdds, odds.TreasureOdds, odds.ShopOdds };
        var baseOdds = BaseOdds(odds);
        var bases = UnknownTypes.Select(t => baseOdds.TryGetValue(t, out var v) ? v : 0f).ToArray();
        // Types possibles : le marchand est exclu si la salle d'avant est un marchand ou si tous les enfants le sont
        IReadOnlySet<RoomType> Allowed(bool noShop, bool lastStep)
        {
            var set = new HashSet<RoomType>(UnknownTypes.Append(RoomType.Event));
            if (noShop || lastStep && point.Children.Count > 0 && point.Children.All(c => c.PointType == MapPointType.Shop))
                set.Remove(RoomType.Shop);
            IReadOnlySet<RoomType> result = set;
            foreach (var model in Listeners(state))
                result = model.ModifyUnknownMapPointRoomTypes(result);
            return result;
        }
        float Increase(RoomType type, float value)
        {
            foreach (var model in Listeners(state))
                value = model.ModifyOddsIncreaseForUnrolledRoomType(type, value);
            return value;
        }
        bool firstRun = state.UnlockState.NumberOfRuns == 0;
        int visitedUnknown = state.MapPointHistory.SelectMany(l => l).Count(p => p.MapPointType == MapPointType.Unknown);

        // Chances d'un tirage depuis un état des chances
        Dictionary<RoomType, float> Roll(float[] s, IReadOnlySet<RoomType> allowed, int step)
        {
            var result = new Dictionary<RoomType, float>();
            if (firstRun && visitedUnknown + step <= 2)
            {
                result[visitedUnknown + step < 2 ? RoomType.Event : RoomType.Monster] = 1;
                return result;
            }
            var fallback = allowed.Contains(RoomType.Event) ? RoomType.Event : allowed.Order().First();
            float cumulative = 0;
            for (int i = 0; i < UnknownTypes.Length; i++)
            {
                if (!allowed.Contains(UnknownTypes[i]) || s[i] < 0)
                    continue;
                float before = Math.Min(cumulative, 1);
                cumulative += s[i];
                result[UnknownTypes[i]] = result.GetValueOrDefault(UnknownTypes[i]) + Math.Min(cumulative, 1) - before;
            }
            result[fallback] = result.GetValueOrDefault(fallback) + 1 - Math.Min(cumulative, 1);
            return result;
        }
        float[] After(float[] s, RoomType rolled, IReadOnlySet<RoomType> allowed)
        {
            var next = (float[])s.Clone();
            for (int i = 0; i < UnknownTypes.Length; i++)
            {
                if (UnknownTypes[i] == rolled)
                    next[i] = bases[i];
                else if (allowed.Contains(UnknownTypes[i]))
                    next[i] += Increase(UnknownTypes[i], bases[i]);
            }
            return next;
        }

        var low = new Dictionary<RoomType, float>();
        var high = new Dictionary<RoomType, float>();
        var states = new List<(float[] S, float P)> { (current, 1) };
        int maxSteps = Math.Min(before.MaxUnknowns, MaxUnknownSteps);
        var middle = Allowed(false, false);
        Dictionary<RoomType, float> Total(IReadOnlySet<RoomType> allowed, int step)
        {
            var total = new Dictionary<RoomType, float>();
            foreach (var (s, p) in states)
                foreach (var (type, chance) in Roll(s, allowed, step))
                    total[type] = total.GetValueOrDefault(type) + p * chance;
            return total;
        }
        for (int step = 0; step <= maxSteps; step++)
        {
            if (step >= before.MinUnknowns)
            {
                // Marchand juste avant (ou peut-être, si c'est un « ? ») : le marchand est exclu, autre cas possible
                var cases = new List<Dictionary<RoomType, float>> { Total(Allowed(before.AfterShopOnly, true), step) };
                if (before.AfterShopMaybe && !before.AfterShopOnly)
                    cases.Add(Total(Allowed(true, true), step));
                foreach (var c in cases)
                    foreach (var type in UnknownTypes.Append(RoomType.Event))
                    {
                        float v = c.GetValueOrDefault(type);
                        low[type] = low.TryGetValue(type, out var l) ? Math.Min(l, v) : v;
                        high[type] = high.TryGetValue(type, out var h) ? Math.Max(h, v) : v;
                    }
            }
            if (step == maxSteps)
                break;
            // Un « ? » de plus d'ici là : chaque issue possible fait avancer les chances
            var next = new Dictionary<string, (float[] S, float P)>();
            foreach (var (s, p) in states)
                foreach (var (type, chance) in Roll(s, middle, step))
                {
                    if (chance <= 0)
                        continue;
                    var n = firstRun && visitedUnknown + step <= 2 ? s : After(s, type, middle);
                    string key = string.Join(",", n.Select(v => v.ToString("0.0000")));
                    next[key] = next.TryGetValue(key, out var e) ? (n, e.P + p * chance) : (n, p * chance);
                }
            states = next.Values.ToList();
        }
        // Plus de « ? » d'ici là que le calcul n'en suit : bornes larges
        if (before.MaxUnknowns > MaxUnknownSteps)
            foreach (var type in UnknownTypes)
            {
                low[type] = 0;
                high[type] = Math.Max(high.GetValueOrDefault(type), 1);
            }
        return UnknownTypes.Append(RoomType.Event)
            .Select(t => (t, low.GetValueOrDefault(t), high.GetValueOrDefault(t))).ToList();
    }

    // ---- Autres salles ----

    /// <summary>Coffre : 42 à 52 pièces d'or (moins en Poverty, aucune en Blitz), modifiées comme tout or gagné, et une
    /// relique (deux en Blitz).</summary>
    private static void Treasure(List<string[]> rows, RunState state, Player player)
    {
        if (Listeners(state).Any(m => !m.ShouldGenerateTreasure(player)))
        {
            rows.Add(["Nothing", ""]);
            return;
        }
        if (state.GameMode != GameMode.Blitz)
        {
            decimal min = 42, max = 52;
            if (AscensionHelper.HasAscension(AscensionLevel.Poverty))
                (min, max) = (min * 0.75m, max * 0.75m);
            rows.Add(["Gold", Range(Gold(state, player, (int)min), Gold(state, player, (int)max))]);
        }
        // En Blitz, deux tours de choix : deux reliques par joueur (en coop aussi depuis la v0.114)
        RelicRarities(rows, state.GameMode == GameMode.Blitz ? "2" : "1");
    }

    // ---- Marchand ----

    /// <summary>Cartes de personnage du marchand, une par type, dans l'ordre du jeu.</summary>
    private static readonly CardType[] ShopCardTypes = [CardType.Attack, CardType.Attack, CardType.Skill, CardType.Skill, CardType.Power];

    /// <summary>
    /// Marchand : ce qu'il peut vendre et à quels prix, jamais ce qu'il vendra (tiré en entrant, avec l'aléa des
    /// boutiques, que le mod ne lit pas). 5 cartes du personnage (2 attaques, 2 compétences, 1 pouvoir ; une à moitié
    /// prix), 2 incolores (peu commune, rare), 2 reliques au hasard et 1 de boutique, 3 potions, retrait de carte. Prix
    /// de base à ±5 % (reliques ±15 %), puis réductions des reliques (Membership Card, The Courier). Rareté des cartes :
    /// chances du marchand décalées par la chance courante du joueur (comme une récompense, sans la changer), qui bouge
    /// avec les combats d'ici là (fourchette) ; un type sans carte de la rareté tirée prend la suivante, comme le jeu.
    /// En Blitz, marchand à choix (6 offres, 3 choix).
    /// </summary>
    /// <param name="prefix">devant le titre de chaque catégorie (« ? » : « If shop: »)</param>
    private static void Shop(List<string[]> rows, RunState state, Player player, Before before, string prefix = "")
    {
        if (state.GameMode == GameMode.Blitz)
        {
            if (prefix != "")
                rows.Add(["If shop"]);
            rows.Add(["Offers", "6, pick 3"]);
            return;
        }
        string Title(string name) => prefix == "" ? name : prefix + name.ToLowerInvariant();
        // Entrée témoin pour les méthodes de prix des reliques (elles ne regardent que le joueur)
        var probe = new MerchantCardRemovalEntry(player);
        int Price(int cost)
        {
            decimal value = cost;
            foreach (var model in Listeners(state))
                value = model.ModifyMerchantPrice(player, probe, value);
            return (int)value;
        }
        string Cost(int min, int max) => $"{Range(Price(min), Price(max))} gold";
        string CardCost(int cost, bool colorless = false)
        {
            if (colorless)
                cost = Mathf.RoundToInt(cost * 1.15f);
            return Cost(Mathf.RoundToInt(cost * 0.95f), Mathf.RoundToInt(cost * 1.05f));
        }

        // Cartes du personnage : nom, chance, prix (trois colonnes alignées)
        var chances = ShopCardRarities(state, player, before);
        string Of(Func<(double Common, double Uncommon, double Rare), double> f) =>
            Percent((float)chances.Min(f), (float)chances.Max(f));
        rows.Add([Title("Cards") + " ×5"]);
        rows.Add(["Common", Of(c => c.Common), CardCost(50)]);
        rows.Add(["Uncommon", Of(c => c.Uncommon), CardCost(75)]);
        rows.Add(["Rare", Of(c => c.Rare), CardCost(150)]);
        rows.Add(["On sale", "1 card", "half price"]);
        foreach (var relic in player.Relics)
        {
            string? effect = relic switch
            {
                MoltenEgg => "Attacks upgraded",
                ToxicEgg => "Skills upgraded",
                FrozenEgg => "Powers upgraded",
                FresnelLens => "Nimble",
                _ => null,
            };
            if (effect != null)
                rows.Add([relic.Title.GetFormattedText(), "", effect]);
        }

        rows.Add([Title("Colorless cards") + " ×2"]);
        rows.Add(["Uncommon", "1", CardCost(75, true)]);
        rows.Add(["Rare", "1", CardCost(150, true)]);

        // Reliques : 2 de rareté tirée, 1 de boutique
        string RelicCost(int cost) => Cost((int)Math.Round(cost * 0.85f), (int)Math.Round(cost * 1.15f));
        rows.Add([Title("Relics") + " ×3"]);
        rows.Add(["Common", "50%", RelicCost(175)]);
        rows.Add(["Uncommon", "33%", RelicCost(225)]);
        rows.Add(["Rare", "17%", RelicCost(275)]);
        rows.Add(["Shop relic", "1", RelicCost(200)]);

        // Potions
        string PotionCost(int cost) => Cost((int)Mathf.Round(cost * 0.95f), (int)Mathf.Round(cost * 1.05f));
        rows.Add([Title("Potions") + " ×3"]);
        rows.Add(["Common", "65%", PotionCost(50)]);
        rows.Add(["Uncommon", "25%", PotionCost(75)]);
        rows.Add(["Rare", "10%", PotionCost(100)]);

        // Retrait : plus cher à chaque retrait fait chez un marchand (ceux d'ici là compris) ; reliques du marchand
        rows.Add([Title("Services")]);
        if (Listeners(state).Any(m => !m.ShouldAllowMerchantCardRemoval(player)))
            rows.Add(["Card removal", "", "Not available"]);
        else
        {
            int baseCost = AscensionHelper.GetValueIfAscension(AscensionLevel.Inflation, 100, 75);
            int increase = MerchantCardRemovalEntry.PriceIncrease;
            int used = player.ExtraFields.CardShopRemovalsUsed;
            rows.Add(["Card removal", "", Cost(baseCost + increase * used, baseCost + increase * (used + before.MaxShops))]);
        }
        foreach (var relic in player.Relics)
        {
            string? effect = relic switch
            {
                MembershipCard m => $"−{100 - m.DynamicVars["Discount"].IntValue}% prices",
                TheCourier c => $"−{c.DynamicVars["Discount"].IntValue}% prices, restocks",
                _ => null,
            };
            if (effect != null)
                rows.Add([relic.Title.GetFormattedText(), "", effect]);
        }
    }

    /// <summary>
    /// Chances de rareté d'une carte du personnage chez le marchand, en moyenne sur ses 5 cartes, pour chaque nombre de
    /// combats d'ici là (3 cartes proposées par combat font bouger la chance courante du joueur).
    /// </summary>
    private static List<(double Common, double Uncommon, double Rare)> ShopCardRarities(RunState state, Player player, Before before)
    {
        var pool = player.Character.CardPool.GetUnlockedCards(player.UnlockState, ((IRunState)state).CardMultiplayerConstraint);
        foreach (var model in Listeners(state))
            pool = model.ModifyMerchantCardPool(player, pool);
        var cards = pool.Where(c => c.Rarity != CardRarity.Basic && c.IsAllowedInRun(state)).ToList();
        // Rareté vraiment donnée à un type : la suivante (avec retour aux communes) si le type n'en a aucune
        CardRarity Allowed(CardRarity rarity, CardType type)
        {
            var first = rarity;
            while (!cards.Any(c => c.Rarity == rarity && c.Type == type))
            {
                rarity = rarity.GetNextHighestRarityWithWrapping();
                if (rarity == first || rarity == CardRarity.None)
                    return first;
            }
            return rarity;
        }
        var odds = player.PlayerOdds.CardRarity;
        (double, double, double) Chances(Dictionary<float, double> offsets)
        {
            var total = new Dictionary<CardRarity, double>();
            foreach (var (o, p) in offsets)
            {
                double rare = Math.Clamp(CardRarityOdds.ShopRareOdds + o, 0, 1);
                double uncommon = Math.Clamp(CardRarityOdds.shopUncommonOdds + CardRarityOdds.ShopRareOdds + o, 0, 1) - rare;
                foreach (var type in ShopCardTypes)
                    foreach (var (rarity, chance) in new[] { (CardRarity.Rare, rare), (CardRarity.Uncommon, uncommon),
                                 (CardRarity.Common, 1 - rare - uncommon) })
                    {
                        var given = Allowed(rarity, type);
                        total[given] = total.GetValueOrDefault(given) + p * chance / ShopCardTypes.Length;
                    }
            }
            return (total.GetValueOrDefault(CardRarity.Common), total.GetValueOrDefault(CardRarity.Uncommon),
                total.GetValueOrDefault(CardRarity.Rare));
        }
        var current = new Dictionary<float, double> { [MathF.Round(odds.CurrentValue, 4)] = 1 };
        var counts = CombatCounts(before).ToHashSet();
        var results = new List<(double, double, double)>();
        for (int step = 0; step <= counts.Max(); step++)
        {
            if (counts.Contains(step))
                results.Add(Chances(current));
            for (int card = 0; card < 3; card++)
                current = NextCard(current, CardRarityOdds.RegularRareOdds, odds.RarityGrowth);
        }
        return results;
    }

    /// <summary>Feu de camp : soin de 30 % des PV max, modifié par les reliques comme le jeu.</summary>
    private static void Rest(List<string[]> rows, RunState state, Player player)
    {
        var creature = player.Creature;
        decimal heal = creature.MaxHp * 0.3m;
        foreach (var model in Listeners(state))
            heal = model.ModifyRestSiteHealAmount(creature, heal);
        rows.Add(["Heal", $"+{(int)heal} HP (rest)"]);
    }

    /// <summary>
    /// Soins des reliques à l'entrée de ce type de salle, une ligne par relique, comme le jeu : Pantograph et Bath
    /// Robes (pas encore utilisé) au début d'un boss, Eternal Feather au feu de camp (deck actuel), Planisphere dans un
    /// « ? », Meal Ticket chez le marchand (salle marchand seulement : un « ? » ne l'est que peut-être).
    /// </summary>
    private static List<string[]> EntryHeals(RunState state, Player player, MapPointType type)
    {
        var rows = new List<string[]>();
        if (player.Creature.IsDead)
            return rows;
        foreach (var relic in player.Relics)
        {
            string? heal = (relic, type) switch
            {
                (Pantograph p, MapPointType.Boss) => $"+{p.DynamicVars.Heal.IntValue} HP",
                (BathRobes { WasUsed: false }, MapPointType.Boss) => "Full HP",
                (EternalFeather f, MapPointType.RestSite) =>
                    $"+{f.DynamicVars.Heal.IntValue * (player.Deck.Cards.Count / f.DynamicVars.Cards.IntValue)} HP",
                (Planisphere p, MapPointType.Unknown) => $"+{p.DynamicVars.Heal.IntValue} HP",
                (MealTicket m, MapPointType.Shop) => $"+{m.DynamicVars.Heal.IntValue} HP",
                _ => null,
            };
            if (heal != null)
                rows.Add(["Heal", $"{heal} ({relic.Title.GetFormattedText()})"]);
        }
        return rows;
    }

    // ---- Format ----

    private static string Range(int min, int max) => min == max ? $"{min}" : $"{min}–{max}";

    private static string Percent(float low, float high)
    {
        string P(float v) => $"{Math.Round(v * 100):0}%";
        return P(low) == P(high) ? P(low) : $"{P(low)}–{P(high)}";
    }
}
