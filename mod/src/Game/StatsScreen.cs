using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
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
    private static readonly (string Label, SortKey Key)[] Sorts = [("Winrate", SortKey.Win), ("Runs", SortKey.N), ("Pickrate", SortKey.Pick)];
    private const int DefaultAscension = 10;

    private static StatsScreen? _current;

    private readonly CanvasLayer _layer;
    private readonly Control _root;
    private readonly VBoxContainer _content;
    private readonly ScrollContainer _scroll;
    private readonly Label _status;
    private readonly PanelContainer _hover;
    /// <summary>Carte affichée au survol de son nom, créée à la première utilisation.</summary>
    private readonly CardHover _cardHover;

    /// <summary>Monstres d'une rencontre affichés au survol de son nom (onglet Losses).</summary>
    private readonly Panel _monsterHover;
    private const float MonsterHoverHeight = 260, MonsterHoverMaxWidth = 900, MonsterHoverPadding = 16;
    private readonly Label _hoverText;
    private readonly OptionButton _mode = new(), _ascension = new(), _character = new(), _gameMode = new(), _sort = new();
    private readonly CheckBox _ancientRelics = new() { Text = "Ancient relics", ButtonPressed = true };
    private readonly SpinBox _min = new() { MinValue = 1, MaxValue = 999, Value = 1 };
    private readonly Dictionary<Control, Tab[]> _filterTabs = new();
    private Tab _tab = Tab.Winrate;
    private readonly List<(Tab Tab, TabButton Button)> _tabButtons = new();

    /// <summary>En-tête épinglé en haut de la zone de défilement quand l'en-tête d'origine en est sorti.</summary>
    private readonly PanelContainer _sticky;
    private string? _stickyKey;

    /// <summary>Tableaux affichés : grille des lignes, clé de leurs colonnes, et le tableau.</summary>
    private readonly List<(Control Rows, string Key, Table Table)> _tables = new();

    /// <summary>Par clé de colonnes, l'en-tête affiché au-dessus du premier tableau qui les utilise.</summary>
    private readonly Dictionary<string, Control> _firstHeaders = new();

    /// <summary>Largeur commune de chaque colonne dans l'onglet, pour aligner tous les tableaux sur un seul en-tête.</summary>
    private readonly Dictionary<string, float> _columnWidths = new();
    private float _nameColumnWidth;

    private const int CellFontSize = 20;
    private const int TableMargin = 10;
    private const int ColumnSeparation = 28;

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

    /// <summary>
    /// Touche du mod : ferme l'écran s'il est ouvert, sinon l'ouvre par-dessus le menu principal ou la partie.
    /// </summary>
    public static void Toggle()
    {
        if (_current != null && GodotObject.IsInstanceValid(_current._layer))
        {
            _current.Close();
            return;
        }
        if (NGame.Instance is not { } game)
            return;
        if (game.MainMenu is { } menu)
            Open(menu);
        else
            Open(game, FindAnyFont(game));
    }

    /// <summary>Police d'un texte du jeu affiché (barre du haut en partie), à défaut de celle du menu principal.</summary>
    private static Font? FindAnyFont(Node root)
    {
        try
        {
            return root.FindChildren("*", "Label", true, false).OfType<Label>()
                .FirstOrDefault(l => l.IsVisibleInTree() && l.HasThemeFontOverride("font"))?.GetThemeFont("font");
        }
        catch (Exception)
        {
            return null;
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
                // Anciens noms de colonnes : « Win% picked » et « Win rate picked » sont devenus « Winrate picked »
                _hiddenColumns.Add((tab, h is "%" or "Rate" ? "%" : h.Replace("%", "rate").Replace(" rate", "rate")));

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
        foreach (var side in new[] { "left", "right", "bottom" })
            margin.AddThemeConstantOverride("margin_" + side, PageMargin);
        // Marge haute réduite, compensée par une barre d'onglets plus haute (PlaceActions) : les filtres ne bougent
        // pas, et le titre et les onglets, centrés dans la barre, tombent au milieu entre le haut et les filtres
        margin.AddThemeConstantOverride("margin_top", PageMarginTop);
        _root.AddChild(margin);

        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 16);
        margin.AddChild(page);

        // En-tête : titre, onglets, boutons
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        page.AddChild(header);
        var title = MakeLabel("STS Tools", 34, StsColors.gold);
        title.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        header.AddChild(title);
        header.AddChild(new Control { CustomMinimumSize = new Vector2(32, 0) });
        for (int i = 0; i < TabNames.Length; i++)
        {
            var tab = (Tab)i;
            var tabButton = new TabButton(TabNames[i]);
            tabButton.Button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            tabButton.Button.Pressed += () =>
            {
                _tab = tab;
                foreach (var (t, other) in _tabButtons)
                    other.SetSelected(t == _tab);
                Refresh();
            };
            // Pas atteignable au clavier : les onglets changent avec les touches d'onglet du jeu (KeyboardNav)
            _tabButtons.Add((tab, tabButton));
            header.AddChild(tabButton.Button);
        }
        foreach (var (t, tabButton) in _tabButtons)
            tabButton.SetSelected(t == _tab);
        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        // Les pointes de sa bannière (à mi-hauteur) au milieu de la bannière rouge de Close. Celle-ci est dessinée
        // dans le bas de son bouton : mesuré sur une capture, son milieu est 8 px sous celui du bouton.
        var reload = new Button { Text = "Reload" };
        StyleReloadButton(reload);
        reload.Pressed += () => { Load(); Refresh(); };
        KeyboardNav.Focusable(reload, _root);
        var reloadOffset = new MarginContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        reloadOffset.AddThemeConstantOverride("margin_top", 2 * ReloadDropY);
        reloadOffset.AddChild(reload);
        var close = new Button { Text = "Close", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        StyleCloseButton(close);
        close.Pressed += Close;
        // Cadre de focus centré sur la bannière, dessinée dans le bas du bouton (ReloadDropY sous son centre)
        KeyboardNav.Focusable(close, _root, KeyboardNav.RingShiftedDown(ReloadDropY));
        // Reload et Close sont placés à part (PlaceActions) ; une case vide de même taille garde leur place
        // dans la barre, pour que les onglets et les filtres ne bougent pas
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 12);
        // Guide du mod, si l'option est cochée (options du jeu, onglet Mod)
        if (ModOptions.HelpInStats)
        {
            var help = ModGuide.GameHelpButton(44);
            KeyboardNav.Focusable(help, _root);
            // Même décalage que Reload : centré sur les bannières, dessinées dans le bas des boutons
            var helpOffset = new MarginContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            helpOffset.AddThemeConstantOverride("margin_top", 2 * ReloadDropY);
            helpOffset.AddChild(help);
            actions.AddChild(helpOffset);
        }
        actions.AddChild(reloadOffset);
        actions.AddChild(close);
        var actionsPlaceholder = new Control();
        header.AddChild(actionsPlaceholder);

        // Filtres
        var filters = new HBoxContainer();
        filters.AddThemeConstantOverride("separation", 8);
        page.AddChild(filters);
        _root.AddChild(actions);
        void Place() => PlaceActions(actions, actionsPlaceholder, filters);
        filters.Resized += Place;
        _root.Resized += Place;
        header.Resized += Place;
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
        // Filtres au clavier : la touche de sélection passe au choix suivant, augmente le nombre, coche
        foreach (var option in new[] { _mode, _ascension, _character, _gameMode, _sort })
            KeyboardNav.Focusable(option, _root);
        KeyboardNav.Focusable(_min.GetLineEdit(), _root);
        KeyboardNav.Focusable(_ancientRelics, _root);

        _status = MakeLabel("", 18, StsColors.cream with { A = 0.6f });
        page.AddChild(_status);

        // Zone de défilement, avec l'en-tête épinglé posé par-dessus en haut
        var scrollArea = new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        page.AddChild(scrollArea);
        _scroll = new ScrollContainer();
        _scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        KeyboardNav.ScrollWithKeys(_scroll, _root);
        scrollArea.AddChild(_scroll);
        _sticky = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _sticky.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.10f, 1f),
            BorderColor = StsColors.gold with { A = 0.5f },
            BorderWidthBottom = 2,
            ContentMarginLeft = TableMargin, ContentMarginRight = 8, ContentMarginTop = 4, ContentMarginBottom = 6,
        });
        scrollArea.AddChild(_sticky);
        // L'en-tête épinglé est mis à jour à chaque image (voir plus bas) : au signal de la barre de défilement,
        // le contenu n'a pas encore été déplacé et les positions lues seraient celles d'avant le défilement
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

        _cardHover = new CardHover(_root);

        // ClipContents : les particules de certains monstres (Soul Nexus) ne débordent pas du panneau
        _monsterHover = new Panel { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, TopLevel = true, ClipContents = true };
        _monsterHover.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.13f, 0.98f),
            BorderColor = StsColors.gold,
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        });
        _root.AddChild(_monsterHover);

        // Écran actif pour le jeu tant qu'il est ouvert : au clavier, le jeu y met le focus (onglet affiché)
        // Focus par défaut : le premier élément (premier filtre)
        var menu = new KeyboardNav.Menu(_root, () => null)
        {
            Cancel = Close,
            // Touches d'onglet du jeu (comme ses propres menus) : seul moyen de changer d'onglet au clavier ; onglet
            // précédent ou suivant, sans boucler
            SwitchTab = delta =>
            {
                int index = Math.Clamp(_tabButtons.FindIndex(t => t.Tab == _tab) + delta, 0, _tabButtons.Count - 1);
                if (_tabButtons[index].Tab != _tab)
                    _tabButtons[index].Button.Button.EmitSignal(BaseButton.SignalName.Pressed);
            },
        };
        KeyboardNav.Register(menu, _root);
        parent.AddChild(_layer);
        KeyboardNav.ScreenChanged();
        // Au clavier, la liste défile pour montrer l'élément qui a le focus, mais l'en-tête épinglé recouvre le haut
        // de la liste : l'élément ne doit pas rester dessous
        var viewport = _root.GetViewport();
        Callable onFocus = Callable.From<Control>(KeepBelowSticky);
        viewport.Connect(Viewport.SignalName.GuiFocusChanged, onFocus);
        _layer.TreeExiting += () =>
        {
            if (viewport.IsConnected(Viewport.SignalName.GuiFocusChanged, onFocus))
                viewport.Disconnect(Viewport.SignalName.GuiFocusChanged, onFocus);
        };
        var tree = _root.GetTree();
        Action updateSticky = UpdateSticky;
        tree.ProcessFrame += updateSticky;
        _layer.TreeExiting += () => tree.ProcessFrame -= updateSticky;
        // Positions fiables seulement après la première mise en page
        _root.GetTree().CreateTimer(0).Timeout += Place;
        // Loupe centrée sur le pointeur tant que l'écran est ouvert ; curseur du jeu rétabli à la fermeture
        InspectCursor.CenterOnLens();
        _layer.TreeExiting += InspectCursor.Restore;
        Load();
        Refresh();
        // Pas de focus à l'ouverture : au clavier, il vient à la première flèche ou sélection (KeyboardNav)
    }

    /// <param name="label">libellé devant le contrôle, ou null (case à cocher, qui a son propre texte)</param>
    private void AddFilter(HBoxContainer box, string? label, Control control, Tab[] tabs)
    {
        var item = new HBoxContainer();
        item.AddThemeConstantOverride("separation", 8);
        if (label != null)
        {
            var text = MakeLabel(label, 20, StsColors.cream);
            if (GameFont(FilterFontPath) is { } font)
                text.AddThemeFontOverride("font", font);
            text.VerticalAlignment = VerticalAlignment.Center;
            item.AddChild(text);
        }
        StyleFilterControl(control);
        item.AddChild(control);
        item.AddChild(new Control { CustomMinimumSize = new Vector2(16, 0) });
        box.AddChild(item);
        _filterTabs[item] = tabs;
    }

    // ---- style des filtres : listes déroulantes des paramètres du jeu (scenes/screens/settings_dropdown.tscn) ----

    private const string FilterFontPath = "res://themes/kreon_bold_glyph_space_one.tres";
    private const string DropdownArrowPath = "res://images/packed/common_ui/settings_tiny_left_arrow.png";
    private static readonly Color DropdownFieldColor = new(0.172549f, 0.262745f, 0.309804f);
    private static readonly Color DropdownHoverColor = new(0.172549f, 0.345098f, 0.439216f);
    private static readonly Color DropdownListColor = new(0.0705882f, 0.129412f, 0.160784f);
    private static readonly Color DropdownValueColor = new(0.937255f, 0.784314f, 0.317647f);
    private static Texture2D? _dropdownArrow;

    private static Font? GameFont(string path) =>
        ResourceLoader.Exists(path) ? ResourceLoader.Load<Font>(path) : null;

    private static StyleBoxFlat FilterBox(Color color, int marginX = 12, int? marginRight = null) => new()
    {
        BgColor = color,
        ContentMarginLeft = marginX, ContentMarginRight = marginRight ?? marginX, ContentMarginTop = 4, ContentMarginBottom = 4,
    };

    private const int ArrowSize = 18, ArrowMargin = 10, ArrowSpace = ArrowSize + 2 * ArrowMargin;
    private const double ArrowTurnDuration = 0.12;

    /// <summary>
    /// Flèche de la liste déroulante : vers la droite quand la liste est fermée, vers le bas quand elle est ouverte
    /// (quart de tour dans le sens horaire), avec une courte rotation entre les deux.
    /// La flèche intégrée d'OptionButton ne peut pas tourner : elle est remplacée par une image transparente, et
    /// une image de la flèche du jeu est posée à droite du champ.
    /// </summary>
    private static void AddSpinningArrow(OptionButton option)
    {
        option.AddThemeIconOverride("arrow", ImageTexture.CreateFromImage(Image.CreateEmpty(1, 1, false, Image.Format.Rgba8)));
        if (DropdownArrow() is not { } texture)
            return;
        var arrow = new TextureRect
        {
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(ArrowSize, ArrowSize),
            PivotOffset = new Vector2(ArrowSize, ArrowSize) / 2,
        };
        // Centré verticalement, collé au bord droit du champ
        arrow.SetAnchorsPreset(Control.LayoutPreset.CenterRight);
        arrow.OffsetLeft = -ArrowMargin - ArrowSize;
        arrow.OffsetRight = -ArrowMargin;
        arrow.OffsetTop = -ArrowSize / 2f;
        arrow.OffsetBottom = ArrowSize / 2f;
        option.AddChild(arrow);
        Tween? tween = null;
        void Turn(float degrees)
        {
            if (!arrow.IsInsideTree())
                return;
            tween?.Kill();
            tween = arrow.CreateTween();
            tween.TweenProperty(arrow, "rotation_degrees", degrees, ArrowTurnDuration)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        }
        var popup = option.GetPopup();
        popup.AboutToPopup += () => Turn(90);
        popup.PopupHide += () => Turn(0);
    }

    /// <summary>Flèche du jeu (vers la gauche) retournée vers la droite, pour les listes déroulantes.</summary>
    private static Texture2D? DropdownArrow()
    {
        if (_dropdownArrow != null || !ResourceLoader.Exists(DropdownArrowPath))
            return _dropdownArrow;
        try
        {
            var image = ResourceLoader.Load<Texture2D>(DropdownArrowPath).GetImage();
            if (image.IsCompressed())
                image.Decompress();
            image.Rotate180();
            image.Resize(18, 18, Image.Interpolation.Lanczos);
            _dropdownArrow = ImageTexture.CreateFromImage(image);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot load the dropdown arrow: {e.Message}");
        }
        return _dropdownArrow;
    }

    /// <summary>
    /// Filtres dans le style des listes déroulantes des paramètres du jeu : case bleu-vert foncé, valeur dorée en
    /// Kreon Bold, flèche du jeu ; liste dépliée bleu nuit, entrées crème, surbrillance bleue.
    /// </summary>
    private static void StyleFilterControl(Control control)
    {
        var font = GameFont(FilterFontPath);
        switch (control)
        {
            case OptionButton option:
                // Marge droite élargie : la place de la flèche animée (AddSpinningArrow)
                option.AddThemeStyleboxOverride("normal", FilterBox(DropdownFieldColor, 12, ArrowSpace));
                option.AddThemeStyleboxOverride("hover", FilterBox(DropdownHoverColor, 12, ArrowSpace));
                option.AddThemeStyleboxOverride("pressed", FilterBox(DropdownHoverColor, 12, ArrowSpace));
                option.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
                if (font != null)
                    option.AddThemeFontOverride("font", font);
                option.AddThemeFontSizeOverride("font_size", 22);
                option.AddThemeColorOverride("font_color", DropdownValueColor);
                option.AddThemeColorOverride("font_hover_color", StsColors.gold);
                option.AddThemeColorOverride("font_pressed_color", StsColors.gold);
                option.AddThemeColorOverride("font_focus_color", DropdownValueColor);
                AddSpinningArrow(option);
                var popup = option.GetPopup();
                popup.AddThemeStyleboxOverride("panel", FilterBox(DropdownListColor, 6));
                popup.AddThemeStyleboxOverride("hover", FilterBox(DropdownHoverColor, 6));
                if (font != null)
                    popup.AddThemeFontOverride("font", font);
                popup.AddThemeFontSizeOverride("font_size", 22);
                popup.AddThemeColorOverride("font_color", StsColors.cream);
                popup.AddThemeColorOverride("font_hover_color", StsColors.cream);
                popup.AddThemeConstantOverride("v_separation", 8);
                break;
            case SpinBox spin:
                var edit = spin.GetLineEdit();
                edit.AddThemeStyleboxOverride("normal", FilterBox(DropdownFieldColor));
                edit.AddThemeStyleboxOverride("focus", FilterBox(DropdownHoverColor));
                if (font != null)
                    edit.AddThemeFontOverride("font", font);
                edit.AddThemeFontSizeOverride("font_size", 22);
                edit.AddThemeColorOverride("font_color", DropdownValueColor);
                edit.AddThemeColorOverride("caret_color", StsColors.cream);
                break;
            case CheckBox check:
                if (font != null)
                    check.AddThemeFontOverride("font", font);
                check.AddThemeColorOverride("font_hover_color", StsColors.gold);
                check.AddThemeColorOverride("font_pressed_color", StsColors.cream);
                check.AddThemeColorOverride("font_hover_pressed_color", StsColors.gold);
                break;
        }
    }

    private void OnInput(InputEvent ev)
    {
        // Échap qui vient de fermer le guide du mod : l'écran reste ouvert
        if (ev.IsActionPressed("ui_cancel") && !ModGuide.BlocksInput)
        {
            _root.AcceptEvent();
            // Le relâchement d'Échap ouvrirait l'écran des profils du menu principal (bouton Profil, touche Retour)
            KeyboardNav.SwallowRelease(ev);
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
        // Après la mise en page des nouveaux tableaux, à la frame suivante
        _scroll.GetTree().CreateTimer(0).Timeout += () =>
        {
            if (!GodotObject.IsInstanceValid(_scroll))
                return;
            if (keepScroll)
                _scroll.ScrollVertical = scroll;
            UpdateSticky();
        };
        _tables.Clear();
        _firstHeaders.Clear();
        _stickyKey = null;
        _sticky.Visible = false;
        foreach (var child in _sticky.GetChildren())
            child.QueueFree();
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
        ComputeColumnWidths(report);
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

    /// <summary>
    /// Clé des colonnes d'un tableau, sans la première (les noms) : dans Losses, les tableaux « Killed by » et
    /// « Floor » partagent ainsi un seul en-tête.
    /// </summary>
    private static string ColumnsKey(Table table) => string.Join("\u001f", table.Headers.Skip(1));

    /// <summary>Par clé de colonnes, le titre de la colonne des noms s'il est le même dans tous les tableaux, sinon null.</summary>
    private readonly Dictionary<string, string?> _nameHeaders = new();

    /// <summary>
    /// Largeur de chaque colonne, mesurée sur tous les tableaux de l'onglet (en-têtes et cellules), pour que
    /// les tableaux s'alignent sur un seul en-tête. La colonne des noms a une largeur commune à tous les tableaux.
    /// </summary>
    private void ComputeColumnWidths(Report report)
    {
        _columnWidths.Clear();
        _nameHeaders.Clear();
        foreach (var table in report.Sections.SelectMany(s => s.Tables))
        {
            var key = ColumnsKey(table);
            _nameHeaders[key] = !_nameHeaders.TryGetValue(key, out var name) || name == table.Headers[0] ? table.Headers[0] : null;
        }
        var font = _root.GetThemeFont("font", "Label");
        float Measure(string text) => font.GetStringSize(text, HorizontalAlignment.Left, -1, CellFontSize).X + 12;
        _nameColumnWidth = 200;
        foreach (var table in report.Sections.SelectMany(s => s.Tables))
        {
            _nameColumnWidth = Math.Max(_nameColumnWidth, Measure(table.Headers[0]));
            for (int r = 0; r < table.Rows.Count; r++)
            {
                // Image devant le nom (personnage, relique, Ancien) : 32 px plus l'espacement
                var icon = table.RowItems?.ElementAtOrDefault(r) is { Kind: not ItemKind.Card } ? 40 : 0;
                _nameColumnWidth = Math.Max(_nameColumnWidth, Measure(table.Rows[r][0]) + icon);
            }
            for (int i = 1; i < table.Headers.Length; i++)
            {
                var width = Math.Max(Measure(table.Headers[i]), CheckBoxSize);
                foreach (var row in table.Rows)
                    width = Math.Max(width, Measure(row[i]));
                _columnWidths[table.Headers[i]] = Math.Max(_columnWidths.GetValueOrDefault(table.Headers[i]), width);
            }
        }
    }

    private float ColumnWidth(Table table, int column) =>
        column == 0 ? _nameColumnWidth : _columnWidths.GetValueOrDefault(table.Headers[column], 90);

    /// <summary>Ordre d'affichage : la colonne des noms, les colonnes affichées, puis les colonnes masquées à droite.</summary>
    private (bool[] Hidden, List<int> Order) ColumnOrder(Table table)
    {
        var hidden = table.Headers.Select(h => _hiddenColumns.Contains((_tab, h))).ToArray();
        hidden[0] = false;
        var order = Enumerable.Range(0, table.Headers.Length).OrderBy(i => i == 0 ? 0 : hidden[i] ? 2 : 1).ToList();
        return (hidden, order);
    }

    private static GridContainer NewGrid(int columns)
    {
        var grid = new GridContainer { Columns = columns };
        grid.AddThemeConstantOverride("h_separation", ColumnSeparation);
        grid.AddThemeConstantOverride("v_separation", 2);
        return grid;
    }

    /// <summary>
    /// En-tête d'un tableau : une case à cocher sans texte, centrée au-dessus de chaque colonne (sauf celle des
    /// noms), puis les noms des colonnes. Mêmes largeurs que les lignes des tableaux.
    /// </summary>
    /// <param name="sticky">copie épinglée en haut : ses cases ne sont pas atteignables au clavier (celles de l'original le sont)</param>
    private GridContainer BuildHeader(Table table, bool sticky = false)
    {
        var (hidden, order) = ColumnOrder(table);
        var grid = NewGrid(table.Headers.Length);
        foreach (var i in order)
        {
            if (i == 0)
            {
                grid.AddChild(new Control { CustomMinimumSize = new Vector2(ColumnWidth(table, 0), 0) });
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
            if (!sticky)
                KeyboardNav.Focusable(box, _root);
            box.Toggled += shown =>
            {
                if (shown)
                    _hiddenColumns.Remove((_tab, header));
                else
                    _hiddenColumns.Add((_tab, header));
                // Différé : la case est détruite par la reconstruction de l'écran
                // Au clavier, le focus revient sur la case reconstruite
                Callable.From(() => KeyboardNav.KeepFocus(_content, () => Refresh(keepScroll: true))).CallDeferred();
            };
            grid.AddChild(box);
        }
        foreach (var i in order)
        {
            // Colonne des noms : titre seulement s'il est commun à tous les tableaux qui partagent cet en-tête
            var title = i == 0 ? _nameHeaders.GetValueOrDefault(ColumnsKey(table)) ?? "" : table.Headers[i];
            grid.AddChild(Cell(title, i, hidden[i] ? StsColors.gold with { A = 0.35f } : StsColors.gold,
                ColumnWidth(table, i)));
        }
        return grid;
    }

    private static MarginContainer WithTableMargin(Control child)
    {
        // Marge à gauche : le contour des cartes rares déborde du texte et serait coupé par la zone de défilement
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", TableMargin);
        margin.AddChild(child);
        return margin;
    }

    private void RenderTable(Table table)
    {
        if (table.Title != null)
        {
            _content.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
            _content.AddChild(MakeLabel(table.Title, 21, StsColors.gold));
        }
        if (table.Rows.Count == 0)
        {
            if (table.Subtitle != null)
                _content.AddChild(MakeLabel(table.Subtitle, 21, StsColors.gold));
            _content.AddChild(MakeLabel(Stats.NoRows, 18, StsColors.cream with { A = 0.6f }));
            return;
        }
        // En-tête affiché seulement au-dessus du premier tableau qui a ces colonnes,
        // avant le sous-titre (« Killed by ») pour que celui-ci reste collé aux lignes
        var key = ColumnsKey(table);
        if (!_firstHeaders.ContainsKey(key))
        {
            var header = WithTableMargin(BuildHeader(table));
            _firstHeaders[key] = header;
            _content.AddChild(header);
        }
        if (table.Subtitle != null)
            _content.AddChild(MakeLabel(table.Subtitle, 21, StsColors.gold));

        var (hidden, order) = ColumnOrder(table);
        var grid = NewGrid(table.Headers.Length);
        foreach (var (row, r) in table.Rows.Select((row, r) => (row, r)))
            foreach (var i in order)
            {
                var width = ColumnWidth(table, i);
                if (hidden[i])
                {
                    // Colonne masquée : cellule vide, qui garde la hauteur de la ligne
                    grid.AddChild(Cell("", i, StsColors.cream, width));
                    continue;
                }
                var item = i == 0 ? table.RowItems?.ElementAtOrDefault(r) : null;
                var label = Cell(row[i], i, StsColors.cream, item == null ? width : 0);
                var control = item == null ? label : WithIcon(item, 32, label);
                // Largeur fixe de la colonne, image comprise
                control.CustomMinimumSize = new Vector2(width, control.CustomMinimumSize.Y);
                if (item is { Kind: ItemKind.Card } card && _game?.Card(card.Id) is { } cardModel)
                {
                    _cardHover.Attach(control, cardModel, focusFallback: _root);
                }
                if (item is { Kind: ItemKind.Encounter } encounter)
                {
                    control.MouseFilter = Control.MouseFilterEnum.Pass;
                    control.MouseDefaultCursorShape = Control.CursorShape.Help;
                    control.MouseEntered += () => ShowMonsters(encounter.Id);
                    control.MouseExited += HideHover;
                    KeyboardNav.FocusableHover(control, () => ShowMonsters(encounter.Id), HideHover, _root);
                    control.GuiInput += ev =>
                    {
                        if (ev is InputEventMouseMotion && _monsterHover.Visible)
                            PlaceCard(_monsterHover);
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
                    KeyboardNav.FocusableHover(control, () => ShowHover(tooltip), HideHover, _root);
                    // Le panneau suit la souris tant qu'elle reste sur le nom
                    control.GuiInput += ev =>
                    {
                        if (ev is InputEventMouseMotion && _hover.Visible)
                            PlaceHover();
                    };
                }
                // Toute ligne atteignable au clavier par son nom, même sans rien à afficher au survol (étages des
                // défaites, personnages…) : la liste défile ainsi jusqu'à elle
                if (i == 0 && control.FocusMode != Control.FocusModeEnum.All)
                    KeyboardNav.Focusable(control, _root);
                grid.AddChild(control);
            }
        _content.AddChild(WithTableMargin(grid));
        _tables.Add((grid, key, table));
    }

    /// <summary>
    /// Élément de la liste qui reçoit le focus (clavier) : une fois que la liste a défilé jusqu'à lui et que
    /// l'en-tête épinglé est à jour, s'il est caché sous l'en-tête, la liste remonte d'autant.
    /// </summary>
    private void KeepBelowSticky(Control focused)
    {
        if (!KeyboardNav.Active || !GodotObject.IsInstanceValid(_scroll) || !_scroll.IsAncestorOf(focused))
            return;
        _scroll.GetTree().CreateTimer(0).Timeout += () =>
        {
            if (!GodotObject.IsInstanceValid(focused) || !GodotObject.IsInstanceValid(_sticky) || !focused.HasFocus())
                return;
            UpdateSticky();
            if (!_sticky.Visible)
                return;
            float hidden = _sticky.GetGlobalRect().End.Y - focused.GetGlobalRect().Position.Y;
            if (hidden > 0)
                _scroll.ScrollVertical -= (int)Math.Ceiling(hidden) + 4;
        };
    }

    /// <summary>
    /// Épingle en haut de la zone de défilement l'en-tête du tableau en cours de lecture, dès que son en-tête
    /// d'origine (au-dessus du premier tableau qui a ces colonnes) est sorti de l'écran par le haut.
    /// </summary>
    private void UpdateSticky()
    {
        if (!GodotObject.IsInstanceValid(_sticky))
            return;
        var top = _scroll.GetGlobalRect().Position.Y;
        // Dernier tableau dont les lignes ont commencé au-dessus du bas de l'en-tête épinglé. Entre deux tableaux
        // (titre de section, bandeau d'acte...), on garde ainsi les colonnes du tableau précédent.
        var probe = top + (_sticky.Visible ? _sticky.Size.Y : 40);
        (Control Rows, string Key, Table Table)? current = null;
        foreach (var entry in _tables)
        {
            if (!GodotObject.IsInstanceValid(entry.Rows))
                continue;
            if (entry.Rows.GetGlobalRect().Position.Y <= probe)
                current = entry;
        }
        if (current is not { } table || !_firstHeaders.TryGetValue(table.Key, out var header)
            || !GodotObject.IsInstanceValid(header) || header.GetGlobalRect().Position.Y >= top)
        {
            _sticky.Visible = false;
            return;
        }
        if (_stickyKey != table.Key)
        {
            foreach (var child in _sticky.GetChildren())
                child.QueueFree();
            _sticky.AddChild(BuildHeader(table.Table, sticky: true));
            _stickyKey = table.Key;
        }
        _sticky.Visible = true;
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
            ItemKind.Encounter => _game.EncounterIcon(item.Id),
            ItemKind.Event => _game.EventIcon(),
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

    /// <summary>
    /// Bouton Close dans le style du bouton retour du jeu (NBackButton) : sa texture rouge en fond, sa flèche
    /// en icône, plus clair et texte doré au survol. Fond rouge uni si la texture est introuvable.
    /// </summary>
    internal static void StyleCloseButton(Button button)
    {
        StyleBox Box(Color modulate)
        {
            if (ResourceLoader.Exists(BackButtonTexture) && ResourceLoader.Load<Texture2D>(BackButtonTexture) is { } texture)
                return new StyleBoxTexture
                {
                    Texture = texture,
                    ModulateColor = modulate,
                    // Plus de marge en haut : la bannière de la texture est dessinée dans sa partie basse
                    ContentMarginLeft = 39, ContentMarginRight = 51, ContentMarginTop = 21, ContentMarginBottom = 6,
                };
            return new StyleBoxFlat
            {
                BgColor = new Color("A8352C") * modulate,
                CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
                ContentMarginLeft = 33, ContentMarginRight = 33, ContentMarginTop = 9, ContentMarginBottom = 9,
            };
        }
        try
        {
            button.AddThemeStyleboxOverride("normal", Box(Colors.White));
            button.AddThemeStyleboxOverride("hover", Box(new Color(1.2f, 1.2f, 1.2f)));
            button.AddThemeStyleboxOverride("pressed", Box(Colors.Gray));
            button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            // Même police que Reload (Kreon Bold du bouton Refresh du jeu), contour rouge foncé assorti à la bannière
            if (ResourceLoader.Exists(RefreshButtonFont) && ResourceLoader.Load<Font>(RefreshButtonFont) is { } font)
                button.AddThemeFontOverride("font", font);
            button.AddThemeColorOverride("font_outline_color", new Color(0.28f, 0.07f, 0.06f));
            button.AddThemeConstantOverride("outline_size", 11);
            // Bouton une fois et demie plus grand que les autres boutons de l'écran
            button.AddThemeFontSizeOverride("font_size", 33);
            button.AddThemeColorOverride("font_color", StsColors.cream);
            button.AddThemeColorOverride("font_hover_color", StsColors.gold);
            button.AddThemeColorOverride("font_pressed_color", StsColors.cream);
            if (ResourceLoader.Exists(BackArrowTexture) && ResourceLoader.Load<Texture2D>(BackArrowTexture) is { } arrow)
            {
                button.Icon = arrow;
                button.AddThemeConstantOverride("icon_max_width", 39);
                button.AddThemeColorOverride("icon_normal_color", StsColors.cream);
                button.AddThemeColorOverride("icon_hover_color", StsColors.gold);
                button.AddThemeColorOverride("icon_pressed_color", StsColors.cream);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot style the close button: {e.Message}");
        }
    }

    /// <summary>
    /// Bouton Reload dans le style du bouton Refresh du jeu (écran pour rejoindre un ami) : bannière teintée
    /// bleu-gris, texte crème en Kreon Bold avec contour bleu-vert foncé et ombre. Même taille que Close.
    /// </summary>
    private static void StyleReloadButton(Button button)
    {
        var tint = new Color(0.430037f, 0.544539f, 0.572528f);
        StyleBox Box(float brightness)
        {
            var color = new Color(tint.R * brightness, tint.G * brightness, tint.B * brightness);
            if (ResourceLoader.Exists(RefreshButtonTexture) && ResourceLoader.Load<Texture2D>(RefreshButtonTexture) is { } texture)
                return new StyleBoxTexture
                {
                    Texture = texture,
                    ModulateColor = color,
                    ContentMarginLeft = 36, ContentMarginRight = 36, ContentMarginTop = ReloadMarginY, ContentMarginBottom = ReloadMarginY,
                };
            return new StyleBoxFlat
            {
                BgColor = color,
                CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
                ContentMarginLeft = 33, ContentMarginRight = 33, ContentMarginTop = 9, ContentMarginBottom = 9,
            };
        }
        try
        {
            button.AddThemeStyleboxOverride("normal", Box(1f));
            button.AddThemeStyleboxOverride("hover", Box(1.25f));
            button.AddThemeStyleboxOverride("pressed", Box(0.7f));
            button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            if (ResourceLoader.Exists(RefreshButtonFont) && ResourceLoader.Load<Font>(RefreshButtonFont) is { } font)
                button.AddThemeFontOverride("font", font);
            button.AddThemeFontSizeOverride("font_size", 33);
            button.AddThemeColorOverride("font_color", StsColors.cream);
            button.AddThemeColorOverride("font_hover_color", StsColors.gold);
            button.AddThemeColorOverride("font_pressed_color", StsColors.cream);
            button.AddThemeColorOverride("font_outline_color", new Color(0.0672f, 0.23389333f, 0.28f));
            button.AddThemeConstantOverride("outline_size", 11);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot style the reload button: {e.Message}");
        }
    }

    private const int ReloadMarginY = 12;

    /// <summary>Décalage vers le bas de Reload pour l'aligner sur la bannière rouge de Close (mesuré : 8 px).</summary>
    private const int ReloadDropY = 8;
    private const int PageMargin = 48, PageMarginTop = 16;

    /// <summary>
    /// Place Reload et Close à droite, centrés verticalement entre le haut de la fenêtre et les filtres. Le centre
    /// visible de la bannière rouge de Close est ReloadDropY sous le centre de son bouton (mesuré sur une capture).
    /// </summary>
    private void PlaceActions(Control actions, Control placeholder, Control filters)
    {
        if (!GodotObject.IsInstanceValid(actions) || !filters.IsInsideTree())
            return;
        // Taille naturelle des boutons, pour le conteneur comme pour la case vide de la barre
        var size = actions.GetCombinedMinimumSize();
        actions.Size = size;
        // Case vide : largeur des boutons, et hauteur de barre agrandie de ce que la marge haute a perdu
        var placeholderSize = new Vector2(size.X, size.Y + (PageMargin - PageMarginTop));
        if (placeholder.CustomMinimumSize != placeholderSize)
            placeholder.CustomMinimumSize = placeholderSize;
        var middle = (filters.GetGlobalRect().Position.Y - _root.GetGlobalRect().Position.Y) / 2;
        var right = placeholder.GetGlobalRect().End.X - _root.GetGlobalRect().Position.X;
        actions.Position = new Vector2(right - actions.Size.X, middle - actions.Size.Y / 2 - ReloadDropY);
    }

    private const string RefreshButtonTexture = "res://images/ui/reward_screen/reward_skip_button.png";
    private const string RefreshButtonFont = "res://themes/kreon_bold_glyph_space_one.tres";
    private const string BackButtonTexture = "res://images/atlases/ui_atlas.sprites/back_button.tres";
    private const string BackArrowTexture = "res://images/atlases/compressed_atlas.sprites/back_button_arrow.tres";

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
        var mouse = KeyboardNav.PointerPosition(_root);
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
    private static void PlaceCard(Control panel) => CardHover.PlaceNearMouse(panel);

    /// <summary>Numéro du survol en cours, pour ignorer une mise en page arrivée après la fin du survol.</summary>
    private int _monsterHoverId;

    /// <summary>
    /// Affiche la rencontre au-dessus à droite de la souris, avec les visuels de combat de ses monstres.
    /// Rencontre avec une scène d'emplacements (Decimillipede, Kaiser Crab...) : la scène est rendue en entier,
    /// chaque monstre posé sur son emplacement comme en combat. Sinon, chaque monstre est rendu à part puis
    /// placé côte à côte. Le rendu se fait dans des scènes à fond transparent (SubViewport) : on mesure ensuite
    /// les pixels réellement dessinés et on n'affiche que cette zone, toujours animée.
    /// </summary>
    private void ShowMonsters(string encounterId)
    {
        ClearMonsters();
        if (_game?.EncounterPreview(encounterId) is not { } preview)
            return;
        var hoverId = ++_monsterHoverId;
        var viewports = new List<SubViewport>();
        try
        {
            viewports.AddRange(EncounterVisuals.Render(NewMonsterViewport, encounterId, preview.Encounter, preview.Monsters));
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot show encounter {encounterId}: {e.Message}");
        }
        if (viewports.Count == 0)
            return;
        // Quelques images pour que Spine pose les monstres et que les scènes soient rendues
        _monsterHover.GetTree().CreateTimer(0.15).Timeout += () =>
        {
            if (hoverId == _monsterHoverId && GodotObject.IsInstanceValid(_monsterHover))
                LayoutMonsters(viewports);
        };
    }

    private SubViewport NewMonsterViewport(Vector2I size)
    {
        var viewport = new SubViewport
        {
            TransparentBg = true,
            Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Size = size,
        };
        _monsterHover.AddChild(viewport);
        return viewport;
    }

    /// <summary>
    /// Découpe chaque scène sur ses pixels dessinés, met chaque monstre à l'échelle (réduit s'il dépasse la
    /// hauteur maximale, jamais agrandi), les aligne par les pieds et ajuste le panneau à son contenu.
    /// </summary>
    private void LayoutMonsters(List<SubViewport> viewports)
    {
        var shown = new List<(Texture2D Texture, Vector2 Size, float Scale)>();
        foreach (var viewport in viewports)
        {
            if (!GodotObject.IsInstanceValid(viewport))
                continue;
            try
            {
                var used = EncounterVisuals.OpaqueRect(viewport.GetTexture().GetImage());
                if (used.Size.X <= 0 || used.Size.Y <= 0)
                    continue;
                // Petite marge pour ne pas rogner le contour
                var region = new Rect2(used.Position - new Vector2I(4, 4), used.Size + new Vector2I(8, 8))
                    .Intersection(new Rect2(Vector2.Zero, viewport.Size));
                var texture = new AtlasTexture { Atlas = viewport.GetTexture(), Region = region };
                shown.Add((texture, region.Size, Math.Min(1f, MonsterHoverHeight / region.Size.Y)));
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] cannot measure monster: {e.Message}");
            }
        }
        if (shown.Count == 0)
            return;

        var width = shown.Sum(m => m.Size.X * m.Scale) + MonsterHoverPadding * (shown.Count - 1);
        var fit = Math.Min(1f, MonsterHoverMaxWidth / width);
        var height = shown.Max(m => m.Size.Y * m.Scale) * fit;
        var x = MonsterHoverPadding;
        foreach (var (texture, size, scale) in shown)
        {
            var drawn = size * scale * fit;
            _monsterHover.AddChild(new TextureRect
            {
                Texture = texture,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                // Bas du dessin posé sur le bas du panneau
                Position = new Vector2(x, MonsterHoverPadding + height - drawn.Y),
                Size = drawn,
            });
            x += drawn.X + MonsterHoverPadding;
        }
        _monsterHover.Size = new Vector2(x, height + 2 * MonsterHoverPadding);
        PlaceCard(_monsterHover);
        _monsterHover.Visible = true;
    }

    private void ClearMonsters()
    {
        foreach (var child in _monsterHover.GetChildren())
        {
            _monsterHover.RemoveChild(child);
            child.QueueFree();
        }
    }

    private void HideHover()
    {
        _monsterHoverId++;
        ClearMonsters();
        _monsterHover.Visible = false;
        _hover.Visible = false;
        _cardHover.Hide();
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

    /// <param name="width">largeur fixe de la colonne ; 0 si elle est donnée au conteneur de la cellule</param>
    private static Label Cell(string text, int column, Color color, float width)
    {
        var label = MakeLabel(text, CellFontSize, color);
        label.HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        label.CustomMinimumSize = new Vector2(width, 0);
        return label;
    }

    private static Label MakeLabel(string text, int size, Color color)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    /// <summary>
    /// Onglet dans le style des onglets des paramètres du jeu (NSettingsTab, scenes/screens/settings_tab.tscn) :
    /// plaque sombre, contour cyan lumineux quand il est sélectionné, texte Kreon Bold crème (à moitié transparent
    /// s'il n'est pas sélectionné, doré au survol). Bouton transparent qui contient les images et le texte.
    /// </summary>
    private sealed class TabButton
    {
        private const string PlatePath = "res://images/packed/common_ui/settings_tab_selected.png";
        private const string StrokePath = "res://images/packed/common_ui/settings_tab_stroke.png";
        private const string FontPath = "res://themes/kreon_bold_glyph_space_two.tres";
        private static readonly Color StrokeColor = new(0.3648f, 0.9104f, 0.96f, 0.752941f);
        private static readonly Color DimPlate = new(0.9f, 0.9f, 0.9f), BrightPlate = new(1.2f, 1.2f, 1.2f);

        public Button Button { get; }
        private readonly TextureRect _stroke, _plate;
        private readonly Label _label;
        private bool _selected, _hovered;

        public TabButton(string text)
        {
            Button = new Button
            {
                CustomMinimumSize = new Vector2(176, 62),
                FocusMode = Control.FocusModeEnum.None,
                PivotOffset = new Vector2(88, 31),
            };
            foreach (var state in new[] { "normal", "hover", "pressed", "focus", "disabled" })
                Button.AddThemeStyleboxOverride(state, new StyleBoxEmpty());
            _stroke = Layer(StrokePath);
            _stroke.Modulate = StrokeColor;
            _stroke.Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
            _plate = Layer(PlatePath);
            _label = new Label
            {
                Text = text,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            if (ResourceLoader.Exists(FontPath) && ResourceLoader.Load<Font>(FontPath) is { } font)
                _label.AddThemeFontOverride("font", font);
            _label.AddThemeFontSizeOverride("font_size", 26);
            _label.AddThemeColorOverride("font_color", StsColors.cream);
            _label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.25098f));
            _label.AddThemeConstantOverride("outline_size", 12);
            Button.AddChild(_stroke);
            Button.AddChild(_plate);
            Button.AddChild(_label);
            Button.MouseEntered += () => { _hovered = true; Update(); };
            Button.MouseExited += () => { _hovered = false; Update(); };
            Update();
        }

        private static TextureRect Layer(string path)
        {
            var rect = new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            if (ResourceLoader.Exists(path))
                rect.Texture = ResourceLoader.Load<Texture2D>(path);
            rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            return rect;
        }

        public void SetSelected(bool selected)
        {
            _selected = selected;
            Update();
        }

        private void Update()
        {
            _stroke.Visible = _selected;
            _plate.Modulate = _hovered ? BrightPlate : DimPlate;
            _label.Modulate = _hovered ? StsColors.gold : _selected ? StsColors.cream : StsColors.halfTransparentCream;
            Button.Scale = _hovered ? Vector2.One * 1.05f : Vector2.One;
        }
    }
}
