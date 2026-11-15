using Godot;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using StsTools.Analysis;

namespace StsTools.Game;

/// <summary>
/// Écran plein écran avec les quatre analyses. Construit uniquement avec des nœuds Godot standards
/// (pas de scène ni de script Godot propre au mod) : l'état est gardé dans cet objet C#.
/// </summary>
public sealed class StatsScreen
{
    private enum Tab { Winrate, Cards, Relics, Ancients, Deaths }

    private static readonly string[] TabNames = ["Victoires", "Cartes", "Reliques", "Anciens", "Défaites"];
    private static readonly (string Label, SortKey Key)[] Sorts = [("Win%", SortKey.Win), ("Parties", SortKey.N), ("Pick%", SortKey.Pick)];
    private const int DefaultAscension = 10;

    private static StatsScreen? _current;

    private readonly CanvasLayer _layer;
    private readonly Control _root;
    private readonly VBoxContainer _content;
    private readonly ScrollContainer _scroll;
    private readonly Label _status;
    private readonly PanelContainer _hover;
    private readonly Label _hoverText;
    private readonly OptionButton _mode = new(), _ascension = new(), _character = new(), _gameMode = new(), _sort = new();
    private readonly SpinBox _min = new() { MinValue = 1, MaxValue = 999, Value = 1 };
    private readonly Dictionary<Control, Tab[]> _filterTabs = new();
    private Tab _tab = Tab.Winrate;

    private GameInfo? _game;
    private List<Run> _runs = [];
    private List<string> _characters = [], _gameModes = [];
    private List<int> _ascensions = [];
    private string? _loadError;

    public static void Open(Node parent)
    {
        if (_current != null && GodotObject.IsInstanceValid(_current._layer))
            return;
        try
        {
            _current = new StatsScreen(parent, FindGameFont(parent));
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] impossible d'ouvrir l'écran de statistiques : {e}");
        }
    }

    /// <summary>Police des boutons du menu, pour que l'écran ressemble au reste du jeu.</summary>
    private static Font? FindGameFont(Node parent)
    {
        try
        {
            return parent.GetNode("MainMenuTextButtons/CompendiumButton").GetChild<Label>(0).GetThemeFont("font");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private StatsScreen(Node parent, Font? font)
    {
        var theme = new Theme { DefaultFontSize = 22 };
        if (font != null)
            theme.DefaultFont = font;

        _layer = new CanvasLayer { Layer = 100 };
        _root = new Control { Theme = theme, MouseFilter = Control.MouseFilterEnum.Stop, FocusMode = Control.FocusModeEnum.All };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.GuiInput += OnInput;
        _layer.AddChild(_root);

        var bg = new Panel();
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        bg.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.04f, 0.05f, 0.08f, 0.96f) });
        _root.AddChild(bg);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride("margin_" + side, 48);
        _root.AddChild(margin);

        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 16);
        margin.AddChild(page);

        // En-tête : titre, onglets, boutons
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        page.AddChild(header);
        header.AddChild(MakeLabel("STS Tools", 34, StsColors.gold));
        header.AddChild(new Control { CustomMinimumSize = new Vector2(32, 0) });
        var group = new ButtonGroup();
        for (int i = 0; i < TabNames.Length; i++)
        {
            var tab = (Tab)i;
            var b = new Button { Text = TabNames[i], ToggleMode = true, ButtonGroup = group, ButtonPressed = tab == _tab };
            b.Pressed += () => { _tab = tab; Refresh(); };
            header.AddChild(b);
        }
        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var reload = new Button { Text = "Recharger" };
        reload.Pressed += () => { Load(); Refresh(); };
        header.AddChild(reload);
        var close = new Button { Text = "Fermer" };
        close.Pressed += Close;
        header.AddChild(close);

        // Filtres
        var filters = new HBoxContainer();
        filters.AddThemeConstantOverride("separation", 8);
        page.AddChild(filters);
        AddFilter(filters, "Parties", _mode, [Tab.Cards, Tab.Relics, Tab.Ancients, Tab.Deaths]);
        AddFilter(filters, "Ascension", _ascension, [Tab.Winrate, Tab.Cards, Tab.Relics, Tab.Ancients, Tab.Deaths]);
        AddFilter(filters, "Personnage", _character, [Tab.Cards, Tab.Relics, Tab.Ancients, Tab.Deaths]);
        AddFilter(filters, "Mode", _gameMode, [Tab.Winrate, Tab.Cards, Tab.Relics, Tab.Ancients, Tab.Deaths]);
        AddFilter(filters, "Min. parties", _min, [Tab.Cards, Tab.Relics, Tab.Deaths]);
        AddFilter(filters, "Tri", _sort, [Tab.Cards, Tab.Relics, Tab.Ancients]);
        _character.AddThemeConstantOverride("icon_max_width", 28);
        _character.GetPopup().AddThemeConstantOverride("icon_max_width", 28);
        _mode.AddItem("Solo");
        _mode.AddItem("Coop");
        foreach (var (label, _) in Sorts)
            _sort.AddItem(label);
        foreach (var option in new[] { _mode, _ascension, _character, _sort })
            option.ItemSelected += _ => Refresh();
        _gameMode.ItemSelected += _ => { FillAscensions(); Refresh(); };
        _min.ValueChanged += _ => Refresh();

        _status = MakeLabel("", 18, StsColors.cream with { A = 0.6f });
        page.AddChild(_status);

        _scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        page.AddChild(_scroll);
        _content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _content.AddThemeConstantOverride("separation", 6);
        _scroll.AddChild(_content);

        _hover = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, TopLevel = true };
        _hover.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.13f, 0.98f),
            BorderColor = StsColors.gold,
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 10, ContentMarginBottom = 10,
        });
        _hoverText = MakeLabel("", 20, StsColors.cream);
        _hoverText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _hoverText.CustomMinimumSize = new Vector2(440, 0);
        _hoverText.MouseFilter = Control.MouseFilterEnum.Ignore;
        _hover.AddChild(_hoverText);
        _root.AddChild(_hover);

        parent.AddChild(_layer);
        Load();
        Refresh();
        _root.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void AddFilter(HBoxContainer box, string label, Control control, Tab[] tabs)
    {
        var item = new HBoxContainer();
        item.AddThemeConstantOverride("separation", 6);
        item.AddChild(MakeLabel(label, 18, StsColors.cream));
        item.AddChild(control);
        item.AddChild(new Control { CustomMinimumSize = new Vector2(16, 0) });
        box.AddChild(item);
        _filterTabs[item] = tabs;
    }

    private void OnInput(InputEvent ev)
    {
        if (ev.IsActionPressed("ui_cancel"))
        {
            _root.AcceptEvent();
            Close();
        }
    }

    private void Close()
    {
        _current = null;
        _layer.QueueFree();
    }

    /// <summary>Relit l'historique et remplit les listes de filtres avec les valeurs présentes.</summary>
    private void Load()
    {
        try
        {
            _game ??= new GameInfo();
            var (dirs, playerId) = GameInfo.HistorySource();
            var errors = new List<string>();
            _runs = RunLoader.Load(dirs, playerId, errors);
            foreach (var e in errors)
                Log.Warn($"[{ModEntry.Id}] fichier ignoré : {e}");
            _loadError = null;
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] lecture de l'historique impossible : {e}");
            _runs = [];
            _loadError = $"Lecture de l'historique impossible : {e.Message}";
        }

        var selectedChar = _character.Selected > 0 ? _characters[_character.Selected - 1] : null;
        _characters = _runs.Select(r => r.Character).Distinct().Order(StringComparer.Ordinal).ToList();
        Fill(_character, ["Tous", .. _characters.Select(c => _game?.CharacterName(c) ?? c)],
            selectedChar == null ? 0 : _characters.IndexOf(selectedChar) + 1,
            [null, .. _characters.Select(c => _game?.CharacterIcon(c))]);

        var selectedMode = _gameMode.Selected > 0 ? _gameModes[_gameMode.Selected - 1] : null;
        _gameModes = _runs.Select(r => r.GameMode).Distinct().Order(StringComparer.Ordinal).ToList();
        Fill(_gameMode, ["Tous", .. _gameModes.Select(m => char.ToUpperInvariant(m[0]) + m[1..])],
            selectedMode == null ? 0 : _gameModes.IndexOf(selectedMode) + 1);

        FillAscensions();
    }

    /// <summary>
    /// Ascensions proposées : jusqu'à A10, ou A5 en mode blitz. Garde la sélection en cours, ramenée au maximum
    /// du mode si besoin ; au premier remplissage, A10 (ou A5 en blitz).
    /// </summary>
    private void FillAscensions()
    {
        var max = SelectedGameMode() == "blitz" ? AscensionManager.maxAscensionAllowedBlitz : AscensionManager.maxAscensionAllowed;
        var selected = _ascension.ItemCount == 0 ? DefaultAscension : SelectedAscension(_ascension.Selected);
        _ascensions = Enumerable.Range(0, max + 1).ToList();
        Fill(_ascension, ["Toutes", .. _ascensions.Select(a => $"A{a}")],
            selected is { } a ? Math.Min(a, max) + 1 : 0);
    }

    private string? SelectedGameMode() => _gameMode.Selected > 0 ? _gameModes[_gameMode.Selected - 1] : null;

    private int? SelectedAscension(int index) => index > 0 && index <= _ascensions.Count ? _ascensions[index - 1] : null;

    private static void Fill(OptionButton option, IEnumerable<string> items, int selected, IReadOnlyList<Texture2D?>? icons = null)
    {
        option.Clear();
        foreach (var (item, i) in items.Select((x, i) => (x, i)))
        {
            if (icons?.ElementAtOrDefault(i) is { } icon)
                option.AddIconItem(icon, item);
            else
                option.AddItem(item);
        }
        option.Select(Math.Max(selected, 0));
    }

    private Filters CurrentFilters() => new()
    {
        Coop = _mode.Selected == 1,
        Ascension = SelectedAscension(_ascension.Selected),
        Character = _character.Selected > 0 ? _characters[_character.Selected - 1] : null,
        GameMode = SelectedGameMode(),
        Min = (int)_min.Value,
        Sort = Sorts[Math.Max(_sort.Selected, 0)].Key,
    };

    private void Refresh()
    {
        HideHover();
        foreach (var (item, tabs) in _filterTabs)
            item.Visible = tabs.Contains(_tab);
        foreach (var child in _content.GetChildren())
        {
            _content.RemoveChild(child);
            child.QueueFree();
        }
        _scroll.ScrollVertical = 0;
        _status.Text = $"{_runs.Count} parties dans l'historique";

        if (_loadError != null || _game == null)
        {
            _content.AddChild(MakeLabel(_loadError ?? "Données du jeu indisponibles", 22, StsColors.red));
            return;
        }
        try
        {
            var f = CurrentFilters();
            var report = _tab switch
            {
                Tab.Winrate => Stats.Winrate(_runs, f, _game),
                Tab.Cards => Stats.Cards(_runs, f, _game),
                Tab.Relics => Stats.Relics(_runs, f, _game),
                Tab.Ancients => Stats.Ancients(_runs, f, _game),
                _ => Stats.Deaths(_runs, f, _game),
            };
            Render(report);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] erreur pendant l'analyse : {e}");
            _content.AddChild(MakeLabel($"Erreur pendant l'analyse : {e.Message}", 22, StsColors.red));
        }
    }

    private void Render(Report report)
    {
        _content.AddChild(MakeLabel(report.Title, 28, StsColors.gold));
        foreach (var note in report.Notes)
            _content.AddChild(MakeLabel(note, 18, StsColors.cream with { A = 0.6f }));
        string? group = null;
        foreach (var section in report.Sections)
        {
            if (section.Group != null && section.Group != group)
            {
                group = section.Group;
                _content.AddChild(new Control { CustomMinimumSize = new Vector2(0, 24) });
                _content.AddChild(GroupBanner(group));
            }
            _content.AddChild(new Control { CustomMinimumSize = new Vector2(0, 18) });
            var title = MakeLabel(section.Title, 26, StsColors.gold);
            _content.AddChild(section.Item == null ? title : WithIcon(section.Item, 44, title));
            foreach (var line in section.Lines)
                _content.AddChild(MakeLabel(line, 20, StsColors.cream));
            foreach (var table in section.Tables)
                RenderTable(table);
        }
    }

    private void RenderTable(Table table)
    {
        if (table.Title != null)
        {
            _content.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
            _content.AddChild(MakeLabel(table.Title, 21, StsColors.blue));
        }
        if (table.Rows.Count == 0)
        {
            _content.AddChild(MakeLabel(Stats.NoRows, 18, StsColors.cream with { A = 0.6f }));
            return;
        }
        var grid = new GridContainer { Columns = table.Headers.Length };
        grid.AddThemeConstantOverride("h_separation", 28);
        grid.AddThemeConstantOverride("v_separation", 2);
        foreach (var (h, i) in table.Headers.Select((h, i) => (h, i)))
            grid.AddChild(Cell(h, i, StsColors.gold));
        foreach (var (row, r) in table.Rows.Select((row, r) => (row, r)))
            foreach (var (cell, i) in row.Select((c, i) => (c, i)))
            {
                var label = Cell(cell, i, StsColors.cream);
                var item = i == 0 ? table.RowItems?.ElementAtOrDefault(r) : null;
                var control = item == null ? label : WithIcon(item, 32, label);
                if (i == 0 && table.RowTooltips?.ElementAtOrDefault(r) is { } tooltip)
                {
                    // Pass : le survol est détecté, et la molette fait toujours défiler la page.
                    // Les bulles d'aide Godot (TooltipText) ne s'affichent pas dans le jeu : panneau maison.
                    control.MouseFilter = Control.MouseFilterEnum.Pass;
                    control.MouseDefaultCursorShape = Control.CursorShape.Help;
                    control.MouseEntered += () => ShowHover(tooltip);
                    control.MouseExited += HideHover;
                }
                grid.AddChild(control);
            }
        _content.AddChild(grid);
    }

    /// <summary>
    /// Image de l'élément devant le texte : portrait du personnage (la tête affichée en haut à gauche en partie)
    /// ou icône de la relique. Pour un personnage, son nom prend sa couleur ; le reste du texte garde la sienne.
    /// </summary>
    private Control WithIcon(ItemRef item, int size, Label label)
    {
        if (_game == null)
            return label;
        var box = new HBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        var icon = item.Kind switch
        {
            ItemKind.Character => _game.CharacterIcon(item.Id),
            ItemKind.Ancient => _game.AncientIcon(item.Id),
            _ => _game.RelicIcon(item.Id),
        };
        if (icon != null)
            box.AddChild(new TextureRect
            {
                Texture = icon,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(size, size),
            });

        var labels = new List<Label> { label };
        var name = item.Kind == ItemKind.Character ? _game.CharacterName(item.Id) : "";
        if (item.Kind == ItemKind.Character && _game.CharacterColor(item.Id) is { } color && label.Text.StartsWith(name))
        {
            var rest = label.Text[name.Length..];
            if (rest.Length > 0)
            {
                // Le nom dans un label à part, collé au reste du texte
                var nameLabel = (Label)label.Duplicate();
                nameLabel.Text = name;
                nameLabel.CustomMinimumSize = Vector2.Zero;
                label.Text = rest;
                labels.Insert(0, nameLabel);
            }
            labels[0].AddThemeColorOverride("font_color", color);
        }
        var text = new HBoxContainer();
        text.AddThemeConstantOverride("separation", 0);
        foreach (var l in labels)
        {
            l.VerticalAlignment = VerticalAlignment.Center;
            l.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            text.AddChild(l);
        }
        box.AddChild(text);
        return box;
    }

    /// <summary>Affiche le panneau de description près de la souris, sans sortir de l'écran.</summary>
    private void ShowHover(string text)
    {
        _hoverText.Text = text;
        _hover.ResetSize();
        var size = _hover.GetCombinedMinimumSize();
        var screen = _root.GetViewportRect().Size;
        var pos = _root.GetGlobalMousePosition() + new Vector2(24, 24);
        if (pos.X + size.X > screen.X)
            pos.X = Math.Max(0, pos.X - size.X - 48);
        if (pos.Y + size.Y > screen.Y)
            pos.Y = Math.Max(0, pos.Y - size.Y - 48);
        _hover.GlobalPosition = pos;
        _hover.Visible = true;
    }

    private void HideHover() => _hover.Visible = false;

    /// <summary>Bandeau pleine largeur annonçant un groupe de sections (l'acte des Anciens).</summary>
    private static Control GroupBanner(string text)
    {
        var banner = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        banner.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = StsColors.gold with { A = 0.16f },
            BorderColor = StsColors.gold,
            BorderWidthLeft = 6,
            BorderWidthBottom = 2,
            ContentMarginLeft = 18,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
        });
        banner.AddChild(MakeLabel(text.ToUpperInvariant(), 30, StsColors.gold));
        return banner;
    }

    private static Label Cell(string text, int column, Color color)
    {
        var label = MakeLabel(text, 20, color);
        label.HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        label.CustomMinimumSize = new Vector2(column == 0 ? 320 : 90, 0);
        return label;
    }

    private static Label MakeLabel(string text, int size, Color color)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }
}
