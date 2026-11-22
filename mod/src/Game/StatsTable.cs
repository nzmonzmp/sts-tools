using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace StsTools.Game;

/// <summary>
/// Tableau de stats du panneau Run stats (survol d'une carte, onglet Combat) : première ligne en-têtes, première
/// colonne noms des stats précédés de leur icône s'il y en a une, valeurs alignées à droite. Une ligne d'un seul
/// élément est un titre de groupe.
/// </summary>
public static class StatsTable
{
    private const int FontSize = 18;
    private const int IconSize = 22;

    /// <param name="iconFor">icône de chaque ligne d'après son nom ; par défaut, celles des stats de cartes</param>
    /// <param name="colorFor">couleur du nom d'une ligne, ou null pour la couleur atténuée par défaut</param>
    /// <param name="highlight">ligne survolée mise en évidence (tableaux du GUI ; pas ceux des bulles de survol)</param>
    public static GridContainer Create(IReadOnlyList<string[]> rows, Func<string, Texture2D?>? iconFor = null,
        Func<string, Color?>? colorFor = null, bool highlight = false)
    {
        var grid = new GridContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 4);
        Fill(grid, rows, iconFor, colorFor);
        // Ni l'en-tête ni les titres de groupe (une seule case remplie)
        if (highlight)
            RowHighlight.Attach(grid, row => row > 0 && rows.ElementAtOrDefault(row) is { Length: > 1 });
        return grid;
    }

    /// <summary>Remplace le contenu de <paramref name="grid"/> par <paramref name="rows"/>.</summary>
    public static void Fill(GridContainer grid, IReadOnlyList<string[]> rows, Func<string, Texture2D?>? iconFor = null,
        Func<string, Color?>? colorFor = null)
    {
        iconFor ??= CardRunStats.IconFor;
        foreach (var child in grid.GetChildren())
        {
            grid.RemoveChild(child);
            child.QueueFree();
        }
        if (rows.Count == 0)
            return;
        int columns = rows.Max(r => r.Length);
        grid.Columns = columns;
        for (int r = 0; r < rows.Count; r++)
        {
            // Ligne d'un seul élément : titre de groupe, en doré, avec un peu d'espace au-dessus
            if (r > 0 && rows[r].Length == 1)
            {
                var title = Label(rows[r][0], StsColors.gold);
                title.CustomMinimumSize = new Vector2(0, FontSize + 14);
                title.VerticalAlignment = VerticalAlignment.Bottom;
                grid.AddChild(title);
                for (int c = 1; c < columns; c++)
                    grid.AddChild(new Control { MouseFilter = Control.MouseFilterEnum.Ignore });
                continue;
            }
            for (int c = 0; c < columns; c++)
            {
                var text = c < rows[r].Length ? rows[r][c] : "";
                // En-têtes et noms des stats atténués, valeurs en clair
                var color = r == 0 ? StsColors.halfTransparentCream
                    : c == 0 ? colorFor?.Invoke(text) ?? StsColors.halfTransparentCream
                    : StsColors.cream;
                var label = Label(text, color);
                label.HorizontalAlignment = c == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                grid.AddChild(c == 0 && r > 0 ? WithIcon(iconFor(text), label) : label);
            }
        }
    }

    /// <summary>
    /// Nom de stat précédé de son icône ; une case vide de même taille si la stat n'en a pas, pour garder les noms
    /// alignés.
    /// </summary>
    private static Control WithIcon(Texture2D? icon, Label label)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 6);
        row.AddChild(new TextureRect
        {
            // Une image libérée entre-temps ferait échouer tout le tableau : case vide à la place
            Texture = icon != null && GodotObject.IsInstanceValid(icon) ? icon : null,
            CustomMinimumSize = new Vector2(IconSize, IconSize),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });
        row.AddChild(label);
        return row;
    }

    private static readonly Dictionary<string, Texture2D?> Loaded = new();

    /// <summary>Image du jeu, ou null si le chemin n'existe pas ; gardée en mémoire après le premier chargement.</summary>
    public static Texture2D? LoadIcon(string? path)
    {
        if (path == null)
            return null;
        // Image libérée par le jeu (il vide son cache de temps en temps) : rechargée
        if (Loaded.TryGetValue(path, out var cached) && (cached == null || GodotObject.IsInstanceValid(cached)))
            return cached;
        Texture2D? texture = null;
        try
        {
            if (ResourceLoader.Exists(path))
                texture = ResourceLoader.Load<Texture2D>(path);
        }
        catch (Exception)
        {
            // Image absente ou d'un autre type : pas d'icône
        }
        return Loaded[path] = texture;
    }

    private static Label Label(string text, Color color)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", FontSize);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }
}

/// <summary>
/// Ligne d'un tableau (<see cref="GridContainer"/>) sous la souris mise en évidence par un fond clair, pour suivre une
/// ligne d'un bout à l'autre des colonnes. Dessiné par le tableau, derrière ses cases.
/// </summary>
public static class RowHighlight
{
    private static readonly Color Fill = new(1f, 1f, 1f, 0.08f);

    /// <param name="highlightable">ligne (à partir de 0) qui peut être mise en évidence ; toutes par défaut</param>
    public static void Attach(GridContainer grid, Func<int, bool>? highlightable = null)
    {
        // Pass : le tableau est « survolé » sous ses cases qui ignorent la souris ; la molette défile toujours
        if (grid.MouseFilter == Control.MouseFilterEnum.Ignore)
            grid.MouseFilter = Control.MouseFilterEnum.Pass;
        (float Top, float Bottom)? hovered = null;
        void Update()
        {
            if (!GodotObject.IsInstanceValid(grid))
                return;
            var row = RowAt(grid, highlightable);
            if (row != hovered)
            {
                hovered = row;
                grid.QueueRedraw();
            }
        }
        grid.Draw += () =>
        {
            if (hovered is { } r)
                grid.DrawRect(new Rect2(0, r.Top, grid.Size.X, r.Bottom - r.Top), Fill);
        };
        Action tick = Update;
        grid.TreeEntered += () => grid.GetTree().ProcessFrame += tick;
        grid.TreeExiting += () => grid.GetTree().ProcessFrame -= tick;
        if (grid.IsInsideTree())
            grid.GetTree().ProcessFrame += tick;
    }

    /// <summary>Haut et bas de la ligne sous la souris (espacement compris), ou null.</summary>
    private static (float Top, float Bottom)? RowAt(GridContainer grid, Func<int, bool>? highlightable)
    {
        if (!grid.IsVisibleInTree() || grid.Columns <= 0)
            return null;
        // Souris sur le tableau ou l'une de ses cases, pas sur ce qui le recouvre (bulle, menu, bord de défilement)
        if (grid.GetViewport()?.GuiGetHoveredControl() is not { } over || over != grid && !grid.IsAncestorOf(over))
            return null;
        var mouse = grid.GetLocalMousePosition();
        if (mouse.X < 0 || mouse.X > grid.Size.X)
            return null;
        float gap = grid.GetThemeConstant("v_separation") / 2f;
        var cells = grid.GetChildren().OfType<Control>().Where(c => c.Visible).ToList();
        for (int i = 0, row = 0; i < cells.Count; i += grid.Columns, row++)
        {
            var line = cells.Skip(i).Take(grid.Columns).ToList();
            float top = line.Min(c => c.Position.Y) - gap, bottom = line.Max(c => c.Position.Y + c.Size.Y) + gap;
            if (mouse.Y >= top && mouse.Y < bottom)
                return highlightable == null || highlightable(row) ? (top, bottom) : null;
        }
        return null;
    }
}
