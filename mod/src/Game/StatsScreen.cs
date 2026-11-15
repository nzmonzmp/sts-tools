using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Cards;
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

    private static readonly string[] TabNames = ["Wins", "Cards", "Relics", "Ancients", "Losses"];
    private static readonly (string Label, SortKey Key)[] Sorts = [("Win%", SortKey.Win), ("Runs", SortKey.N), ("Pick%", SortKey.Pick)];
    private const int DefaultAscension = 10;

    private static StatsScreen? _current;

    private readonly CanvasLayer _layer;
    private readonly Control _root;
    private readonly VBoxContainer _content;
    private readonly ScrollContainer _scroll;
    private readonly Label _status;
    private readonly PanelContainer _hover;
    /// <summary>Carte affichée au survol de son nom, créée à la première utilisation.</summary>
    private readonly Control _cardHover;
    private NCard? _cardNode;
    private const float CardHoverScale = 0.8f;
    private readonly Label _hoverText;
    private readonly OptionButton _mode = new(), _ascension = new(), _character = new(), _gameMode = new(), _sort = new();
    private readonly CheckBox _ancientRelics = new() { Text = "Ancient relics", ButtonPressed = true };
    private readonly SpinBox _min = new() { MinValue = 1, MaxValue = 999, Value = 1 };
    private readonly Dictionary<Control, Tab[]> _filterTabs = new();
    private Tab _tab = Tab.Winrate;

    /// <summary>Colonnes masquées, par onglet et nom de colonne (communes à tous les tableaux de l'onglet).</summary>
    private readonly HashSet<(Tab, string)> _hiddenColumns = new();

    private GameInfo? _game;
    private List<Run> _runs = [];
    private List<string> _characters = [], _gameModes = [];
    private List<int> _ascensions = [];
    private string? _loadError;

    /// <summary>Réglages conservés entre les parties et les redémarrages du jeu.</summary>
    private readonly SavedSettings _saved = SavedSettings.Load();

    /// <param name="parent">nœud qui accueille l'écran (menu principal ou menu pause) ; l'écran disparaît avec lui</param>
    /// <param name="font">police du jeu à utiliser ; par défaut, celle des boutons du menu principal</param>
    public static void Open(Node parent, Font? font = null)
    {
        if (_current != null && GodotObject.IsInstanceValid(_current._layer))
            return;
        try
        {
            _current = new StatsScreen(parent, font ?? FindGameFont(parent));
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] cannot open the stats screen: {e}");
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
        // Réglages sauvegardés : onglet et colonnes masquées tout de suite, filtres une fois les listes remplies
        if (Enum.TryParse<Tab>(_saved.Tab, out var savedTab))
            _tab = savedTab;
        foreach (var column in _saved.HiddenColumns)
            if (column.Split('/', 2) is [var t, var h] && Enum.TryParse<Tab>(t, out var tab))
                _hiddenColumns.Add((tab, h));

        var theme = new Theme { DefaultFontSize = 22 };
        if (font != null)
            theme.DefaultFont = font;

        // Always : l'écran reste utilisable même si le jeu met l'arbre en pause (menu pause)
        _layer = new CanvasLayer { Layer = 100, ProcessMode = Node.ProcessModeEnum.Always };
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
        var reload = new Button { Text = "Reload" };
        reload.Pressed += () => { Load(); Refresh(); };
        header.AddChild(reload);
        var close = new Button { Text = "Close" };
        close.Pressed += Close;
        header.AddChild(close);

        // Filtres
        var filters = new HBoxContainer();
        filters.AddThemeConstantOverride("separation", 8);
        page.AddChild(filters);
        AddFilter(filters, "Party", _mode, [Tab.Cards, Tab.Relics, Tab.Ancients, Tab.Deaths]);
        AddFilter(filters, "Ascension", _ascension, [Tab.Winrate, Tab.Cards, Tab.Relics, Tab.Ancients, Tab.Deaths]);
        AddFilter(filters, "Character", _character, [Tab.Cards, Tab.Relics, Tab.Ancients, Tab.Deaths]);
        AddFilter(filters, "Mode", _gameMode, [Tab.Winrate, Tab.Cards, Tab.Relics, Tab.Ancients, Tab.Deaths]);
        AddFilter(filters, "Min. runs", _min, [Tab.Cards, Tab.Relics, Tab.Deaths]);
        AddFilter(filters, "Sort", _sort, [Tab.Cards, Tab.Relics, Tab.Ancients]);
        AddFilter(filters, null, _ancientRelics, [Tab.Relics]);
        _character.AddThemeConstantOverride("icon_max_width", 28);
        _character.GetPopup().AddThemeConstantOverride("icon_max_width", 28);
        _mode.AddItem("Solo");
        _mode.AddItem("Coop");
        foreach (var (label, _) in Sorts)
            _sort.AddItem(label);
        // Avant de brancher les signaux, pour ne pas déclencher de rafraîchissement
        _mode.Select(_saved.Coop ? 1 : 0);
        _sort.Select(Math.Max(0, Array.FindIndex(Sorts, s => s.Key.ToString() == _saved.Sort)));
        _min.Value = Math.Max(1, _saved.Min);
        _ancientRelics.ButtonPressed = _saved.ShowAncientRelics;
        _ancientRelics.AddThemeColorOverride("font_color", StsColors.cream);
        _ancientRelics.AddThemeFontSizeOverride("font_size", 22);
        StyleCheckBox(_ancientRelics);
        _ancientRelics.Toggled += _ => Refresh();
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

        _cardHover = new Control
        {
            Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, TopLevel = true,
            Size = NCard.defaultSize * CardHoverScale,
        };
        _root.AddChild(_cardHover);

        parent.AddChild(_layer);
        // Loupe centrée sur le pointeur tant que l'écran est ouvert ; curseur du jeu rétabli à la fermeture
        SetInspectCursorHotSpot(InspectLensCenter);
        _layer.TreeExiting += () => SetInspectCursorHotSpot(GameInspectHotSpot);
        Load();
        Refresh();
        _root.CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <param name="label">libellé devant le contrôle, ou null (case à cocher, qui a son propre texte)</param>
    private void AddFilter(HBoxContainer box, string? label, Control control, Tab[] tabs)
    {
        var item = new HBoxContainer();
        item.AddThemeConstantOverride("separation", 6);
        if (label != null)
            item.AddChild(MakeLabel(label, 18, StsColors.cream));
        item.AddChild(control);
        item.AddChild(new Control { CustomMinimumSize = new Vector2(16, 0) });
        box.AddChild(item);
        _filterTabs[item] = tabs;
    }

    private const string InspectCursorPath = "res://images/packed/common_ui/cursor_inspect.png";

    /// <summary>Point actif du curseur loupe dans le jeu (NCursorManager._inspectHotSpot) : bord de l'anneau.</summary>
    private static readonly Vector2 GameInspectHotSpot = new(12, 12);

    /// <summary>Centre du verre dans l'image 64 × 64 du curseur loupe (anneau doré de x = 5 à 40, y = 6 à 41).</summary>
    private static readonly Vector2 InspectLensCenter = new(22, 23);

    /// <summary>Réapplique l'image du curseur loupe du jeu avec un autre point actif.</summary>
    private static void SetInspectCursorHotSpot(Vector2 hotSpot)
    {
        try
        {
            if (ResourceLoader.Load<Image>(InspectCursorPath) is { } image)
                Input.SetCustomMouseCursor(image, Input.CursorShape.Help, hotSpot);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot set the inspect cursor: {e.Message}");
        }
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
                Log.Warn($"[{ModEntry.Id}] skipped file: {e}");
            _loadError = null;
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] cannot read run history: {e}");
            _runs = [];
            _loadError = $"Cannot read run history: {e.Message}";
        }

        // Au premier remplissage, valeur sauvegardée ; ensuite, la sélection en cours
        var selectedChar = _character.ItemCount == 0 ? _saved.Character
            : _character.Selected > 0 ? _characters[_character.Selected - 1] : null;
        _characters = _runs.Select(r => r.Character).Distinct().Order(StringComparer.Ordinal).ToList();
        Fill(_character, ["All", .. _characters.Select(c => _game?.CharacterName(c) ?? c)],
            selectedChar == null ? 0 : _characters.IndexOf(selectedChar) + 1,
            [null, .. _characters.Select(c => _game?.CharacterIcon(c))]);

        var selectedMode = _gameMode.ItemCount == 0 ? _saved.GameMode : SelectedGameMode();
        _gameModes = _runs.Select(r => r.GameMode).Distinct().Order(StringComparer.Ordinal).ToList();
        Fill(_gameMode, ["All", .. _gameModes.Select(m => char.ToUpperInvariant(m[0]) + m[1..])],
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
        var selected = _ascension.ItemCount == 0
            ? (_saved.Ascension == "all" ? null : int.TryParse(_saved.Ascension, out var savedAsc) ? savedAsc : DefaultAscension)
            : SelectedAscension(_ascension.Selected);
        _ascensions = Enumerable.Range(0, max + 1).ToList();
        Fill(_ascension, ["All", .. _ascensions.Select(a => $"A{a}")],
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

    /// <summary>Enregistre l'état de l'écran ; appelé à chaque changement (tout changement rafraîchit l'écran).</summary>
    private void SaveSettings()
    {
        var f = CurrentFilters();
        _saved.Tab = _tab.ToString();
        _saved.Coop = f.Coop;
        _saved.Ascension = f.Ascension?.ToString() ?? "all";
        _saved.Character = f.Character;
        _saved.GameMode = f.GameMode;
        _saved.Min = f.Min;
        _saved.Sort = f.Sort.ToString();
        _saved.ShowAncientRelics = f.ShowAncientRelics;
        _saved.HiddenColumns = _hiddenColumns.Select(c => $"{c.Item1}/{c.Item2}").Order(StringComparer.Ordinal).ToList();
        _saved.Save();
    }

    private Filters CurrentFilters() => new()
    {
        Coop = _mode.Selected == 1,
        Ascension = SelectedAscension(_ascension.Selected),
        Character = _character.Selected > 0 ? _characters[_character.Selected - 1] : null,
        GameMode = SelectedGameMode(),
        Min = (int)_min.Value,
        Sort = Sorts[Math.Max(_sort.Selected, 0)].Key,
        ShowAncientRelics = _ancientRelics.ButtonPressed,
    };

    /// <param name="keepScroll">garder la position de défilement (quand on masque ou réaffiche une colonne)</param>
    private void Refresh(bool keepScroll = false)
    {
        HideHover();
        SaveSettings();
        var scroll = _scroll.ScrollVertical;
        if (keepScroll)
            // Après la mise en page des nouveaux tableaux, à la frame suivante
            _scroll.GetTree().CreateTimer(0).Timeout += () => _scroll.ScrollVertical = scroll;
        foreach (var (item, tabs) in _filterTabs)
            item.Visible = tabs.Contains(_tab);
        foreach (var child in _content.GetChildren())
        {
            _content.RemoveChild(child);
            child.QueueFree();
        }
        _scroll.ScrollVertical = 0;
        _status.Text = $"{_runs.Count} runs in history";

        if (_loadError != null || _game == null)
        {
            _content.AddChild(MakeLabel(_loadError ?? "Game data unavailable", 22, StsColors.red));
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
            Log.Error($"[{ModEntry.Id}] analysis failed: {e}");
            _content.AddChild(MakeLabel($"Analysis failed: {e.Message}", 22, StsColors.red));
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
        // Ordre d'affichage : la colonne des noms, les colonnes affichées, puis les colonnes masquées à droite
        var hidden = table.Headers.Select(h => _hiddenColumns.Contains((_tab, h))).ToArray();
        hidden[0] = false;
        var order = Enumerable.Range(0, table.Headers.Length).OrderBy(i => i == 0 ? 0 : hidden[i] ? 2 : 1).ToList();

        var grid = new GridContainer { Columns = table.Headers.Length };
        grid.AddThemeConstantOverride("h_separation", 28);
        grid.AddThemeConstantOverride("v_separation", 2);
        // Ligne de cases à cocher, sans texte, centrées au-dessus de chaque colonne (sauf celle des noms)
        foreach (var i in order)
        {
            if (i == 0)
            {
                grid.AddChild(new Control());
                continue;
            }
            var header = table.Headers[i];
            var box = new CheckBox
            {
                ButtonPressed = !hidden[i],
                FocusMode = Control.FocusModeEnum.None,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            };
            StyleCheckBox(box);
            box.Toggled += shown =>
            {
                if (shown)
                    _hiddenColumns.Remove((_tab, header));
                else
                    _hiddenColumns.Add((_tab, header));
                // Différé : la case est détruite par la reconstruction de l'écran
                Callable.From(() => Refresh(keepScroll: true)).CallDeferred();
            };
            grid.AddChild(box);
        }
        foreach (var i in order)
            grid.AddChild(Cell(table.Headers[i], i, hidden[i] ? StsColors.gold with { A = 0.35f } : StsColors.gold));
        foreach (var (row, r) in table.Rows.Select((row, r) => (row, r)))
            foreach (var i in order)
            {
                if (hidden[i])
                {
                    // Colonne masquée : cellule vide, qui garde la hauteur de la ligne
                    grid.AddChild(Cell("", i, StsColors.cream));
                    continue;
                }
                var label = Cell(row[i], i, StsColors.cream);
                var item = i == 0 ? table.RowItems?.ElementAtOrDefault(r) : null;
                var control = item == null ? label : WithIcon(item, 32, label);
                if (item is { Kind: ItemKind.Card } card && _game?.Card(card.Id) is { } cardModel)
                {
                    control.MouseFilter = Control.MouseFilterEnum.Pass;
                    control.MouseDefaultCursorShape = Control.CursorShape.Help;
                    control.MouseEntered += () => ShowCard(cardModel);
                    control.MouseExited += HideHover;
                    control.GuiInput += ev =>
                    {
                        if (ev is InputEventMouseMotion && _cardHover.Visible)
                            PlaceCard();
                    };
                }
                if (i == 0 && table.RowTooltips?.ElementAtOrDefault(r) is { } tooltip)
                {
                    // Pass : le survol est détecté, et la molette fait toujours défiler la page.
                    // Les bulles d'aide Godot (TooltipText) ne s'affichent pas dans le jeu : panneau maison.
                    control.MouseFilter = Control.MouseFilterEnum.Pass;
                    control.MouseDefaultCursorShape = Control.CursorShape.Help;
                    control.MouseEntered += () => ShowHover(tooltip);
                    control.MouseExited += HideHover;
                    // Le panneau suit la souris tant qu'elle reste sur le nom
                    control.GuiInput += ev =>
                    {
                        if (ev is InputEventMouseMotion && _hover.Visible)
                            PlaceHover();
                    };
                }
                grid.AddChild(control);
            }
        // Marge à gauche : le contour des cartes rares déborde du texte et serait coupé par la zone de défilement
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddChild(grid);
        _content.AddChild(margin);
    }

    /// <summary>
    /// Image de l'élément devant le texte : portrait du personnage (la tête affichée en haut à gauche en partie)
    /// ou icône de la relique. Pour un personnage, son nom prend sa couleur ; le reste du texte garde la sienne.
    /// </summary>
    private Control WithIcon(ItemRef item, int size, Label label)
    {
        if (_game == null)
            return label;
        if (item.Kind == ItemKind.Card)
        {
            // Pas d'image : nom dans la couleur du personnage, contour doré pour les cartes rares
            if (_game.CardColor(item.Id) is { } cardColor)
                label.AddThemeColorOverride("font_color", cardColor);
            if (_game.IsRareCard(item.Id))
            {
                // Deux bords : un liseré noir d'environ 5 px (le contour), puis un anneau doré d'environ 2,5 px.
                // Largeur visible du doré = (shadow_outline_size - outline_size) / 2.
                // Un Label n'a qu'un contour : l'anneau doré est l'ombre, sans décalage, avec son propre contour,
                // dessinée derrière le contour noir.
                label.AddThemeColorOverride("font_outline_color", Colors.Black);
                label.AddThemeConstantOverride("outline_size", 10);
                label.AddThemeColorOverride("font_shadow_color", StsColors.gold);
                label.AddThemeConstantOverride("shadow_outline_size", 15);
                label.AddThemeConstantOverride("shadow_offset_x", 0);
                label.AddThemeConstantOverride("shadow_offset_y", 0);
            }
            return label;
        }
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

    private const int CheckBoxSize = 26;
    private static Texture2D? _uncheckedIcon, _checkedIcon;

    /// <summary>
    /// Icônes dessinées par le mod : contour blanc, intérieur doré si cochée. L'icône « décochée » du thème
    /// du jeu est vide : une case sans texte était alors invisible et sans surface cliquable.
    /// </summary>
    private static void StyleCheckBox(CheckBox box)
    {
        _uncheckedIcon ??= CheckBoxIcon(false);
        _checkedIcon ??= CheckBoxIcon(true);
        foreach (var name in new[] { "unchecked", "unchecked_disabled" })
            box.AddThemeIconOverride(name, _uncheckedIcon);
        foreach (var name in new[] { "checked", "checked_disabled" })
            box.AddThemeIconOverride(name, _checkedIcon);
        box.CustomMinimumSize = new Vector2(CheckBoxSize, CheckBoxSize);
    }

    private static Texture2D CheckBoxIcon(bool isChecked)
    {
        const int border = 2, inset = 6;
        var image = Image.CreateEmpty(CheckBoxSize, CheckBoxSize, false, Image.Format.Rgba8);
        image.Fill(new Color(0, 0, 0, 0.35f));
        for (int x = 0; x < CheckBoxSize; x++)
            for (int y = 0; y < CheckBoxSize; y++)
            {
                var edge = Math.Min(Math.Min(x, y), Math.Min(CheckBoxSize - 1 - x, CheckBoxSize - 1 - y));
                if (edge < border)
                    image.SetPixel(x, y, Colors.White);
                else if (isChecked && edge >= inset)
                    image.SetPixel(x, y, StsColors.gold);
            }
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>Affiche le panneau de description près de la souris, sans sortir de l'écran.</summary>
    private void ShowHover(string text)
    {
        _hoverText.Text = text;
        _hover.ResetSize();
        PlaceHover();
        _hover.Visible = true;
    }

    /// <summary>
    /// Place le panneau au-dessus de la souris, centré sur elle. Il reste dans l'écran ; s'il n'y a pas
    /// la place au-dessus, il passe sous la souris.
    /// </summary>
    private void PlaceHover() => PlaceAboveMouse(_hover, _hover.GetCombinedMinimumSize());

    private void PlaceAboveMouse(Control panel, Vector2 size)
    {
        var screen = _root.GetViewportRect().Size;
        var mouse = _root.GetGlobalMousePosition();
        var x = Math.Clamp(mouse.X - size.X / 2, 0, Math.Max(0, screen.X - size.X));
        var y = mouse.Y - size.Y - 16;
        if (y < 0)
            y = Math.Min(mouse.Y + 28, Math.Max(0, screen.Y - size.Y));
        panel.GlobalPosition = new Vector2(x, y);
    }

    /// <summary>
    /// Affiche la carte au-dessus de la souris, avec le nœud de carte du jeu (comme dans sa bibliothèque de cartes).
    /// Le nœud est centré sur sa position, d'où le décalage d'une demi-taille dans le conteneur.
    /// </summary>
    private void ShowCard(CardModel card)
    {
        try
        {
            if (_cardNode == null || !GodotObject.IsInstanceValid(_cardNode))
            {
                _cardNode = NCard.Create(card);
                if (_cardNode == null)
                    return;
                _cardNode.MouseFilter = Control.MouseFilterEnum.Ignore;
                _cardNode.Scale = Vector2.One * CardHoverScale;
                _cardNode.Position = _cardHover.Size / 2;
                _cardHover.AddChild(_cardNode);
            }
            else
            {
                _cardNode.Model = card;
            }
            _cardNode.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
            PlaceCard();
            _cardHover.Visible = true;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot show card {card.Id}: {e.Message}");
        }
    }

    /// <summary>
    /// Place la carte en haut à droite de la souris. Près du bord droit elle passe à gauche de la souris,
    /// près du bord haut elle descend juste assez pour rester dans l'écran.
    /// </summary>
    private void PlaceCard()
    {
        const float gap = 12;
        var size = _cardHover.Size;
        var screen = _root.GetViewportRect().Size;
        var mouse = _root.GetGlobalMousePosition();
        var x = mouse.X + gap;
        if (x + size.X > screen.X)
            x = Math.Max(0, mouse.X - size.X - gap);
        var y = Math.Clamp(mouse.Y - size.Y - gap, 0, Math.Max(0, screen.Y - size.Y));
        _cardHover.GlobalPosition = new Vector2(x, y);
    }

    private void HideHover()
    {
        _hover.Visible = false;
        _cardHover.Visible = false;
    }

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
