using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace StsTools.Game;

/// <summary>
/// Un onglet du panneau de stats en partie. Chaque onglet dit quand il doit être redessiné (Subscribe) et
/// remplit son contenu à partir de l'état actuel du joueur (Fill) ; le panneau s'occupe du reste, y compris
/// des sous-onglets s'il en déclare.
/// </summary>
public interface IRunPanelTab
{
    string Title { get; }

    /// <summary>Noms des sous-onglets, affichés sous les onglets ; vide s'il n'y en a pas.</summary>
    string[] SubTabs => [];

    /// <summary>Branche <paramref name="changed"/> sur les événements du jeu qui changent les stats de l'onglet.</summary>
    void Subscribe(Player player, Action changed);

    void Unsubscribe(Player player, Action changed);

    /// <summary>L'onglet sait montrer l'équipe entière (totaux de tous les joueurs).</summary>
    bool HasTeam => false;

    /// <param name="players">
    /// joueur affiché (choisi dans la rangée des joueurs en multijoueur), ou tous les joueurs pour l'équipe
    /// (seulement si <see cref="HasTeam"/>)
    /// </param>
    /// <param name="subTab">sous-onglet affiché, parmi <see cref="SubTabs"/> ; null s'il n'y en a pas</param>
    void Fill(VBoxContainer content, IReadOnlyList<Player> players, string? subTab);
}

/// <summary>
/// Panneau de stats en partie, ancré à droite sous la barre du haut. Il ne bloque pas le jeu : on continue à jouer
/// pendant qu'il est ouvert. S'ouvre et se ferme avec le bouton de la barre du haut ou la touche <see cref="ModHotkeys.RunStats"/> ;
/// il est fermé en début de partie et à l'ouverture du menu Échap. L'onglet choisi est gardé d'une partie à l'autre.
/// Sa hauteur est limitée à l'écran : au-delà, le contenu défile.
/// Construit, comme <see cref="StatsScreen"/>, uniquement avec des nœuds Godot standards.
/// </summary>
public sealed class RunPanel
{
    public const string NodeName = "StsToolsRunPanel";

    private const float Width = 400;
    private const float TopBarHeight = 80;
    private const string FontPath = "res://themes/kreon_bold_glyph_space_two.tres";

    private static RunPanel? _current;

    private readonly IRunPanelTab[] _tabs;
    private readonly Player _player;
    private readonly PanelContainer _panel;
    private readonly VBoxContainer _content;
    private readonly ScrollContainer _scroll;

    /// <summary>Tout sous l'en-tête : masqué quand le panneau est replié (flèche de l'en-tête).</summary>
    private readonly VBoxContainer _body;

    private readonly Button _collapse;
    private readonly List<Button> _tabButtons = [];
    private readonly HBoxContainer _subTabBar;

    /// <summary>Rangée des joueurs (multijoueur seulement) : un bouton par joueur, et l'équipe si l'onglet la montre.</summary>
    private readonly HBoxContainer _playerBar;
    private readonly List<(Button Button, Player? Player)> _playerButtons = [];

    /// <summary>Joueur affiché ; null : l'équipe entière.</summary>
    private Player? _viewed;

    /// <summary>Joueurs de la partie, le joueur local en premier.</summary>
    private IReadOnlyList<Player> Players =>
        _player.RunState.Players.OrderBy(p => p == _player ? 0 : 1).ToList();
    private readonly List<Button> _subTabButtons = [];
    /// <summary>Sous-onglet choisi dans chaque onglet, par titre d'onglet</summary>
    private readonly Dictionary<string, string> _subTabs;
    /// <summary>Par onglet : redessine le panneau si c'est l'onglet affiché qui a changé</summary>
    private readonly Action[] _changed;
    private int _tab;
    private readonly KeyboardNav.Menu _menu;

    /// <summary>Crée le panneau dans la barre du haut d'une partie, fermé ou ouvert selon le dernier état.</summary>
    public static void Attach(Control topBar, Player player)
    {
        try
        {
            topBar.GetNodeOrNull(NodeName)?.Free();
            _current = new RunPanel(topBar, player);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] cannot create the run panel: {e}");
        }
    }

    public static void Toggle()
    {
        if (_current != null && GodotObject.IsInstanceValid(_current._panel))
            _current.SetOpen(!_current._panel.Visible);
    }

    /// <summary>
    /// Touche « Focus run stats » (clavier seul) : panneau fermé, il s'ouvre avec le focus dedans ; ouvert sans le focus,
    /// le focus y va ; focus déjà dedans, il est rendu au jeu (le panneau reste ouvert).
    /// </summary>
    public static void ToggleFocus()
    {
        if (_current is not { } panel || !GodotObject.IsInstanceValid(panel._panel))
            return;
        if (!panel._panel.Visible)
        {
            panel.SetOpen(true);
            panel._menu.Engage();
            return;
        }
        if (panel._panel.GetViewport()?.GuiGetFocusOwner() is { } owner && panel._panel.IsAncestorOf(owner))
        {
            owner.ReleaseFocus();
            panel._menu.Disengage();
            return;
        }
        panel._menu.Engage();
        panel._menu.DefaultFocusedControl?.GrabFocus();
    }

    /// <summary>Touche « Collapse GUI » : replie ou déplie le panneau ; fermé, il s'ouvre déplié.</summary>
    public static void ToggleCollapse()
    {
        if (_current is not { } panel || !GodotObject.IsInstanceValid(panel._panel))
            return;
        if (!panel._panel.Visible)
        {
            panel.SetCollapsed(false);
            panel.SetOpen(true);
            return;
        }
        panel.SetCollapsed(panel._body.Visible);
    }

    /// <summary>Le panneau de stats est ouvert (les infobulles des reliques n'affichent leurs stats qu'à ce moment).</summary>
    public static bool IsOpen => _current != null && GodotObject.IsInstanceValid(_current._panel) && _current._panel.Visible;

    public static void Close()
    {
        if (_current != null && GodotObject.IsInstanceValid(_current._panel))
            _current.SetOpen(false);
    }

    private RunPanel(Control topBar, Player player)
    {
        _player = player;
        _panel = new PanelContainer { Name = NodeName, MouseFilter = Control.MouseFilterEnum.Stop };
        // Carte affichée au survol d'un nom de carte, comme dans l'écran Stats ; masquée avec le panneau
        var cardHover = new CardHover(_panel, centerCursor: true);
        _tabs = [new DeckTab(cardHover), new GameTab(), new CombatTab()];
        var saved = SavedSettings.Load();
        _right = saved.RunPanelRight ?? DefaultRight;
        _top = saved.RunPanelTop ?? DefaultTop;
        _height = saved.RunPanelHeight;
        _tab = Math.Max(0, Array.FindIndex(_tabs, t => t.Title == saved.RunPanelTab));
        _subTabs = new Dictionary<string, string>(saved.RunPanelSubTabs);
        _changed = _tabs.Select((_, i) => (Action)(() =>
        {
            // Pas de redessin pour un autre onglet : il ferait disparaître la carte survolée dans l'onglet affiché
            if (_tab == i)
                OnChanged();
        })).ToArray();

        var theme = new Theme { DefaultFontSize = 20 };
        if (ResourceLoader.Exists(FontPath) && ResourceLoader.Load<Font>(FontPath) is { } font)
            theme.DefaultFont = font;

        _panel.Theme = theme;
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.05f, 0.08f, 0.92f),
            BorderColor = new Color(StsColors.gold, 0.5f),
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 10, ContentMarginBottom = 14,
        });
        _panel.AnchorLeft = _panel.AnchorRight = 1;
        _panel.GrowHorizontal = Control.GrowDirection.Begin;
        _panel.GrowVertical = Control.GrowDirection.End;
        Place();

        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 10);
        _panel.AddChild(page);

        // L'en-tête sert de poignée pour déplacer le panneau
        var header = new HBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.Move,
            TooltipText = "Drag to move",
        };
        header.GuiInput += OnHeaderInput;
        page.AddChild(header);
        // La marge du panneau au-dessus et à gauche de l'en-tête sert aussi de poignée
        _panel.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true } press && press.GlobalPosition.Y > header.GetGlobalRect().End.Y)
                return;
            OnHeaderInput(ev);
        };
        header.AddChild(Label("GUI", 24, StsColors.gold));
        // Espace vide de l'en-tête : laisse passer les clics (poignée de déplacement)
        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        header.AddChild(Label($"[{ModHotkeys.RunStats.Display}]", 16, StsColors.halfTransparentCream));
        // Replier : il ne reste que l'en-tête (titre, flèche pour déplier, croix)
        _collapse = new Button { Flat = true, TooltipText = "Show or hide the panel content" };
        KeyboardNav.Focusable(_collapse);
        _collapse.AddThemeColorOverride("font_color", StsColors.cream);
        _collapse.AddThemeColorOverride("font_hover_color", StsColors.gold);
        _collapse.Pressed += () => SetCollapsed(_body!.Visible);
        header.AddChild(_collapse);
        var close = new Button { Text = "✕", Flat = true };
        KeyboardNav.Focusable(close);
        close.AddThemeColorOverride("font_color", StsColors.cream);
        close.AddThemeColorOverride("font_hover_color", StsColors.gold);
        close.Pressed += () => SetOpen(false);
        header.AddChild(close);

        _body = new VBoxContainer();
        _body.AddThemeConstantOverride("separation", 10);
        page.AddChild(_body);

        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 6);
        _body.AddChild(tabs);
        for (int i = 0; i < _tabs.Length; i++)
        {
            int index = i;
            // Pas atteignable au clavier : les onglets changent avec les touches d'onglet du jeu (KeyboardNav)
            var button = new Button
            {
                Text = _tabs[i].Title,
                FocusMode = Control.FocusModeEnum.None,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            button.Pressed += () => SelectTab(index);
            _tabButtons.Add(button);
            tabs.AddChild(button);
        }

        _playerBar = new HBoxContainer();
        _playerBar.AddThemeConstantOverride("separation", 6);
        _body.AddChild(_playerBar);
        _viewed = player;

        _subTabBar = new HBoxContainer();
        _subTabBar.AddThemeConstantOverride("separation", 6);
        _body.AddChild(_subTabBar);

        // Défilement vertical seulement : la largeur du contenu reste celle du panneau
        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        KeyboardNav.ScrollWithKeys(_scroll);

        _body.AddChild(_scroll);
        _content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _content.AddThemeConstantOverride("separation", 6);
        _scroll.AddChild(_content);

        // Poignée du bas : glisser pour fixer la hauteur, double-clic pour revenir à la hauteur du contenu
        var grip = new Panel
        {
            CustomMinimumSize = new Vector2(0, 10),
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.Vsize,
            TooltipText = "Drag to resize, double-click for automatic height",
        };
        grip.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(StsColors.gold, 0.25f),
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
            ContentMarginTop = 4, ContentMarginBottom = 4,
        });
        grip.GuiInput += OnGripInput;
        _body.AddChild(grip);

        // Pour le jeu, écran actif depuis son ouverture tant que le focus n'est pas reparti sur le jeu
        // Focus par défaut : premier sous-onglet, sinon premier élément de la liste, sinon le bouton de fermeture
        _menu = new KeyboardNav.Menu(_panel, () =>
            _subTabButtons.FirstOrDefault(b => b.IsVisibleInTree())
            ?? _content.FindChildren("*", "Control", true, false).OfType<Control>()
                .FirstOrDefault(c => c.FocusMode == Control.FocusModeEnum.All && c.IsVisibleInTree())
            ?? (_body.Visible ? _scroll : _collapse))
        {
            IsOpen = () => GodotObject.IsInstanceValid(_panel) && _panel.Visible,
            Overlay = true,
            // Au clavier, le retour du jeu ferme le panneau quand il a le focus
            Cancel = () => SetOpen(false),
            // Touches d'onglet du jeu, seulement quand le panneau a le focus : seul moyen de changer d'onglet au
            // clavier ; onglet précédent ou suivant, sans boucler. Le focus reste dans le panneau
            SwitchTab = delta =>
            {
                int index = Math.Clamp(_tab + delta, 0, _tabs.Length - 1);
                if (index == _tab)
                    return;
                bool subTabFocused = _subTabButtons.Any(b => b.HasFocus());
                SelectTab(index);
                KeepFocusInPanel(subTabFocused);
            },
        };
        KeyboardNav.Register(_menu, _panel);

        // Sous les autres enfants de la barre, pour ne pas passer devant ses infobulles
        topBar.AddChild(_panel);
        topBar.MoveChild(_panel, 0);

        for (int i = 0; i < _tabs.Length; i++)
            _tabs[i].Subscribe(_player, _changed[i]);
        _panel.TreeExiting += () =>
        {
            for (int i = 0; i < _tabs.Length; i++)
                _tabs[i].Unsubscribe(_player, _changed[i]);
            if (_current == this)
                _current = null;
        };

        BuildSubTabs();
        BuildPlayers();
        StyleTabs();
        SetCollapsed(saved.RunPanelCollapsed, save: false);
        SetOpen(false);
    }

    /// <summary>Replie le panneau (seulement l'en-tête) ou le déplie ; gardé dans les réglages.</summary>
    private void SetCollapsed(bool collapsed, bool save = true)
    {
        bool focusInside = _body.GetViewport()?.GuiGetFocusOwner() is { } owner && _body.IsAncestorOf(owner);
        _body.Visible = !collapsed;
        _collapse.Text = collapsed ? "▼" : "▲";
        if (save)
            Save(settings => settings.RunPanelCollapsed = collapsed);
        // Le focus clavier ne reste pas sur un élément caché
        if (collapsed && focusInside)
            _collapse.GrabFocus();
        if (collapsed)
        {
            _panel.ResetSize();
            Place();
        }
        else
            Callable.From(FitHeight).CallDeferred();
    }

    /// <summary>Le panneau vient de s'ouvrir ou de se fermer.</summary>
    public static event Action? OpenChanged;

    private void SetOpen(bool open)
    {
        bool changed = _panel.Visible != open;
        if (changed)
            BugReport.Note($"GUI {(open ? "opened" : "closed")}, focus on {_panel.GetViewport()?.GuiGetFocusOwner()?.Name ?? "nothing"}");
        // Fermé avec le focus dedans : on le rend au jeu
        if (!open && _panel.GetViewport()?.GuiGetFocusOwner() is { } owner && _panel.IsAncestorOf(owner))
            owner.ReleaseFocus();
        _panel.Visible = open;
        if (changed)
        {
            OpenChanged?.Invoke();
            TargetPreview.GuiToggled(open);
        }
        // Toujours à l'ouverture : certains changements (carte améliorée) ne passent par aucun événement suivi
        if (open)
            Refresh();
        // Pas de focus à l'ouverture (touche Focus run stats pour l'y mettre) ; fermé : le jeu reprend le focus
        if (changed && !open)
        {
            _menu.Disengage();
            KeyboardNav.RestoreGameFocus();
        }
    }

    /// <summary>
    /// Relit les réglages avant de les modifier : l'écran Stats les sauvegarde aussi, il ne faut pas écraser
    /// ses filtres avec une copie ancienne.
    /// </summary>
    private static void Save(Action<SavedSettings> change)
    {
        var settings = SavedSettings.Load();
        change(settings);
        settings.Save();
    }

    /// <summary>
    /// Après un changement d'onglet au clavier : sous-onglets refaits (le focus y était) ou focus perdu avec la liste
    /// redessinée, le focus revient sur le premier élément du panneau.
    /// </summary>
    private void KeepFocusInPanel(bool subTabFocused)
    {
        if (subTabFocused || !_menu.HasFocus)
            _menu.TakeFocus();
    }

    private void SelectTab(int index)
    {
        _tab = index;
        Save(s => s.RunPanelTab = _tabs[index].Title);
        BuildSubTabs();
        BuildPlayers();
        StyleTabs();
        Refresh();
    }

    /// <summary>Sous-onglet affiché dans l'onglet courant : le dernier choisi, sinon le premier.</summary>
    private string? CurrentSubTab
    {
        get
        {
            var subTabs = _tabs[_tab].SubTabs;
            if (subTabs.Length == 0)
                return null;
            return _subTabs.TryGetValue(_tabs[_tab].Title, out var name) && subTabs.Contains(name) ? name : subTabs[0];
        }
    }

    /// <summary>Nom d'un joueur comme le jeu l'affiche ; « You » pour le joueur local.</summary>
    private string NameOf(Player player)
    {
        if (player == _player)
            return "You";
        try
        {
            return MegaCrit.Sts2.Core.Platform.PlatformUtil.GetPlayerName(
                MegaCrit.Sts2.Core.Runs.RunManager.Instance.NetService.Platform, player.NetId);
        }
        catch (Exception)
        {
            return $"Player {player.NetId}";
        }
    }

    /// <summary>
    /// Rangée des joueurs : seulement à plusieurs. « You », chaque coéquipier, puis « Team » si l'onglet la montre ;
    /// l'équipe choisie dans un onglet qui ne la montre pas revient au joueur local.
    /// </summary>
    private void BuildPlayers()
    {
        RemoveChildren(_playerBar);
        _playerButtons.Clear();
        var players = Players;
        _playerBar.Visible = players.Count > 1;
        if (players.Count <= 1)
        {
            _viewed = _player;
            return;
        }
        bool team = _tabs[_tab].HasTeam;
        if (_viewed == null && !team || _viewed != null && !players.Contains(_viewed))
            _viewed = _player;
        foreach (var p in team ? players.Cast<Player?>().Append(null) : players.Cast<Player?>())
        {
            var viewed = p;
            // Icône du personnage au-dessus du nom du joueur ; l'équipe : son nom seul, à la même hauteur
            var button = new Button
            {
                Text = viewed == null ? "Team" : NameOf(viewed),
                ClipText = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                IconAlignment = HorizontalAlignment.Center,
                VerticalIconAlignment = VerticalAlignment.Top,
                CustomMinimumSize = new Vector2(0, 64),
            };
            if (viewed != null)
            {
                try
                {
                    button.Icon = viewed.Character.IconTexture;
                }
                catch (Exception e)
                {
                    Log.Warn($"[{ModEntry.Id}] cannot load the character icon: {e.Message}");
                }
            }
            button.AddThemeConstantOverride("icon_max_width", 36);
            KeyboardNav.Focusable(button);
            button.AddThemeFontSizeOverride("font_size", 15);
            button.Pressed += () =>
            {
                _viewed = viewed;
                StyleTabs();
                Refresh();
            };
            _playerButtons.Add((button, viewed));
            _playerBar.AddChild(button);
        }
    }

    /// <summary>Boutons des sous-onglets de l'onglet courant ; la barre disparaît s'il n'en a pas.</summary>
    private void BuildSubTabs()
    {
        RemoveChildren(_subTabBar);
        _subTabButtons.Clear();
        var subTabs = _tabs[_tab].SubTabs;
        _subTabBar.Visible = subTabs.Length > 0;
        foreach (var name in subTabs)
        {
            var button = new Button { Text = name };
            KeyboardNav.Focusable(button);
            button.AddThemeFontSizeOverride("font_size", 17);
            button.Pressed += () => SelectSubTab(name);
            _subTabButtons.Add(button);
            _subTabBar.AddChild(button);
        }
    }

    private void SelectSubTab(string name)
    {
        var tab = _tabs[_tab].Title;
        _subTabs[tab] = name;
        Save(s => s.RunPanelSubTabs[tab] = name);
        StyleTabs();
        Refresh();
    }

    /// <summary>Les stats ont changé : redessine si le panneau est ouvert (sinon, ce sera fait à l'ouverture).</summary>
    private void OnChanged()
    {
        if (!GodotObject.IsInstanceValid(_panel) || !_panel.Visible || _refreshQueued)
            return;
        // Un seul redessin par image, même si beaucoup de stats changent d'un coup (une attaque sur tous les ennemis)
        _refreshQueued = true;
        Callable.From(() =>
        {
            _refreshQueued = false;
            Refresh();
        }).CallDeferred();
    }

    private bool _refreshQueued;

    private void Refresh()
    {
        if (!GodotObject.IsInstanceValid(_panel))
            return;
        // Redessin pendant la navigation au clavier : le focus reste au même rang dans la liste
        KeyboardNav.KeepFocus(_content, () =>
        {
            RemoveChildren(_content);
            try
            {
                _tabs[_tab].Fill(_content, _viewed is { } viewed ? [viewed] : Players, CurrentSubTab);
            }
            catch (Exception e)
            {
                Log.Error($"[{ModEntry.Id}] cannot fill the run panel tab {_tabs[_tab].Title}: {e}");
                _content.AddChild(Label("Error, see the game log.", 18, StsColors.red));
            }
        });
        _scroll.ScrollVertical = 0;
        Callable.From(FitHeight).CallDeferred();
    }

    /// <summary>
    /// La hauteur suit le contenu, sans dépasser le bas de l'écran : la zone de défilement prend la hauteur du
    /// contenu, limitée à la place restante. Appelé après la mise en page, quand les tailles sont connues.
    /// </summary>
    private void FitHeight()
    {
        if (!GodotObject.IsInstanceValid(_panel))
            return;
        const float bottomMargin = 32;
        float available = _panel.GetViewportRect().Size.Y - _scroll.GlobalPosition.Y - bottomMargin;
        // Hauteur choisie par le joueur (poignée du bas), sinon celle du contenu
        float wanted = _height ?? _content.GetCombinedMinimumSize().Y;
        _scroll.CustomMinimumSize = new Vector2(0, Math.Max(60, Math.Min(wanted, available)));
        // ResetSize ramène la hauteur à celle du contenu mais garde le bord gauche ; Place recolle ensuite
        // le panneau au bord droit avec sa largeur fixe
        _panel.ResetSize();
        Place();
    }

    /// <summary>
    /// Place le panneau en haut à droite sous la barre, avec sa largeur fixe ; s'il doit s'élargir pour son
    /// contenu, il s'étend vers la gauche et reste collé au bord droit.
    /// </summary>
    private void Place()
    {
        // Toujours dans l'écran, où que le joueur l'ait mis
        var screen = _panel.IsInsideTree() ? _panel.GetViewportRect().Size : Vector2.Zero;
        if (screen.X > 0)
        {
            // Largeur réelle : le panneau s'élargit vers la gauche si son contenu le demande
            float width = Math.Max(Width, _panel.Size.X);
            _right = Math.Clamp(_right, 0, Math.Max(0, screen.X - width));
            // Jamais sous la barre du haut du jeu, ni plus bas que le bas de l'écran (le panneau entier y tient)
            const float bottomMargin = 8;
            float height = Math.Max(_panel.Size.Y, 60);
            _top = Math.Clamp(_top, TopBarHeight, Math.Max(TopBarHeight, screen.Y - height - bottomMargin));
        }
        _panel.OffsetLeft = -_right - Width;
        _panel.OffsetRight = -_right;
        _panel.OffsetTop = _panel.OffsetBottom = _top;
    }

    // ---- Position et hauteur choisies par le joueur ----

    private const float DefaultRight = 16;
    private const float DefaultTop = TopBarHeight + 12;

    /// <summary>Écart au bord droit de l'écran, haut du panneau ; hauteur de la zone de défilement (null : automatique).</summary>
    private float _right, _top;

    private float? _height;

    private Vector2? _dragFrom;
    private (float Right, float Top) _dragStart;
    private float? _resizeFrom;
    private float _resizeStart;

    /// <summary>Glisser l'en-tête déplace le panneau ; relâcher garde la position.</summary>
    private void OnHeaderInput(InputEvent ev)
    {
        switch (ev)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                _dragFrom = press.GlobalPosition;
                _dragStart = (_right, _top);
                _panel.AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _dragFrom != null:
                _dragFrom = null;
                Save(s => (s.RunPanelRight, s.RunPanelTop) = (_right, _top));
                break;
            case InputEventMouseMotion motion when _dragFrom is { } from:
                var delta = motion.GlobalPosition - from;
                (_right, _top) = (_dragStart.Right - delta.X, _dragStart.Top + delta.Y);
                FitHeight();
                _panel.AcceptEvent();
                break;
        }
    }

    /// <summary>Glisser la poignée du bas change la hauteur ; double-clic : de nouveau celle du contenu.</summary>
    private void OnGripInput(InputEvent ev)
    {
        switch (ev)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true }:
                _resizeFrom = null;
                _height = null;
                Save(s => s.RunPanelHeight = null);
                FitHeight();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                _resizeFrom = press.GlobalPosition.Y;
                _resizeStart = _scroll.Size.Y;
                _panel.AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _resizeFrom != null:
                _resizeFrom = null;
                Save(s => s.RunPanelHeight = _height);
                break;
            case InputEventMouseMotion motion when _resizeFrom is { } from:
                _height = Math.Max(60, _resizeStart + motion.GlobalPosition.Y - from);
                FitHeight();
                _panel.AcceptEvent();
                break;
        }
    }

    /// <summary>
    /// Retire tout de suite les enfants de l'arbre avant de les libérer : avec QueueFree seul, ils restent
    /// jusqu'à la fin de l'image et comptent encore dans la taille mesurée (panneau trop haut ou trop large).
    /// </summary>
    private static void RemoveChildren(Node parent)
    {
        foreach (var child in parent.GetChildren())
        {
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }

    private void StyleTabs()
    {
        for (int i = 0; i < _tabButtons.Count; i++)
            StyleTab(_tabButtons[i], i == _tab, small: false);
        var subTab = CurrentSubTab;
        foreach (var button in _subTabButtons)
            StyleTab(button, button.Text == subTab, small: true);
        foreach (var (button, viewed) in _playerButtons)
            StyleTab(button, viewed == _viewed, small: true);
    }

    /// <summary>Plaque sombre, contour cyan si sélectionné ; les sous-onglets sont plus petits et sans fond.</summary>
    private static void StyleTab(Button button, bool selected, bool small)
    {
        var box = new StyleBoxFlat
        {
            BgColor = small ? new Color(0, 0, 0, 0) : selected ? new Color(0.16f, 0.2f, 0.26f) : new Color(0.09f, 0.11f, 0.15f),
            BorderColor = selected ? new Color(0.3648f, 0.9104f, 0.96f, 0.75f) : new Color(1, 1, 1, small ? 0 : 0.08f),
            BorderWidthBottom = 2, BorderWidthTop = small ? 0 : 2, BorderWidthLeft = small ? 0 : 2, BorderWidthRight = small ? 0 : 2,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = small ? 0 : 6, CornerRadiusBottomRight = small ? 0 : 6,
            ContentMarginTop = small ? 2 : 4, ContentMarginBottom = small ? 2 : 4, ContentMarginLeft = 10, ContentMarginRight = 10,
        };
        // Pas « focus » : c'est le cadre de la navigation au clavier (KeyboardNav), dessiné par-dessus
        foreach (var state in new[] { "normal", "hover", "pressed" })
            button.AddThemeStyleboxOverride(state, box);
        button.AddThemeColorOverride("font_color", selected ? StsColors.cream : StsColors.halfTransparentCream);
        button.AddThemeColorOverride("font_hover_color", StsColors.gold);
        button.AddThemeColorOverride("font_pressed_color", StsColors.cream);
    }

    internal static Label Label(string text, int size, Color color)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    /// <summary>Ligne « nom, barre proportionnelle, nombre, pourcentage », la brique commune des onglets.</summary>
    internal static Control BarRow(string name, int count, int total, Color color)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        var label = Label(name, 20, StsColors.cream);
        label.CustomMinimumSize = new Vector2(90, 0);
        row.AddChild(label);

        float share = total > 0 ? (float)count / total : 0;
        var track = new Panel { CustomMinimumSize = new Vector2(0, 14), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore };
        track.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.06f),
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3 });
        var fill = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, AnchorBottom = 1, AnchorRight = share };
        fill.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = color,
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3 });
        track.AddChild(fill);
        row.AddChild(track);

        var n = Label(count.ToString(), 20, StsColors.cream);
        n.CustomMinimumSize = new Vector2(36, 0);
        n.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(n);
        var pct = Label($"{share:P0}", 20, StsColors.halfTransparentCream);
        pct.CustomMinimumSize = new Vector2(56, 0);
        pct.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(pct);
        return row;
    }
}

/// <summary>Deck actuel : sa composition par type (Types) ou la liste de ses cartes (Cards).</summary>
internal sealed class DeckTab(CardHover cardHover) : IRunPanelTab
{
    private static readonly (CardType Type, string Name, Color Color)[] Types =
    [
        (CardType.Attack, "Attacks", StsColors.red),
        (CardType.Skill, "Skills", StsColors.blue),
        (CardType.Power, "Powers", StsColors.gold),
        (CardType.Status, "Statuses", StsColors.gray),
        (CardType.Curse, "Curses", StsColors.purple),
        (CardType.Quest, "Quests", StsColors.green),
    ];

    public string Title => "Deck";

    public string[] SubTabs => ["Stats", "Cards"];

    // Le deck de chaque joueur : on peut afficher celui d'un coéquipier
    public void Subscribe(Player player, Action changed)
    {
        foreach (var p in player.RunState.Players)
            PileType.Deck.GetPile(p).ContentsChanged += changed;
    }

    public void Unsubscribe(Player player, Action changed)
    {
        foreach (var p in player.RunState.Players)
            PileType.Deck.GetPile(p).ContentsChanged -= changed;
    }

    public void Fill(VBoxContainer content, IReadOnlyList<Player> players, string? subTab)
    {
        var player = players[0];
        var cards = PileType.Deck.GetPile(player).Cards;
        content.AddChild(RunPanel.Label($"{cards.Count} cards", 20, StsColors.cream));
        if (subTab == "Cards")
            FillCards(content, cards, cardHover);
        else
            FillStats(content, cards, player.Relics);
    }

    /// <summary>Nombre de cartes du deck par type et par effet (Block, dégâts, pioche, pouvoirs…), zéros compris.</summary>
    private static void FillStats(VBoxContainer content, IReadOnlyList<CardModel> cards, IReadOnlyList<RelicModel> relics)
    {
        var rows = DeckStats.Compute(cards, relics);
        // Première ligne : en-têtes ; puis chaque groupe sous son titre. Relics et Total : effets et pouvoirs seulement
        List<string[]> table = [["", "Cards", "Relics", "Total"]];
        // Lignes dont toutes les colonnes sont à 0 masquées, et les groupes vides avec elles
        var shown = rows.Where(r => r.Count != 0 || (r.Relics ?? 0) != 0 || (r.Total ?? 0) != 0);
        foreach (var group in shown.GroupBy(r => r.Group))
        {
            table.Add([group.Key]);
            // Total « - » quand aucune carte n'a l'effet (ligne affichée pour une relique seulement)
            table.AddRange(group.Select(r => new[]
            {
                r.Name, r.Count.ToString(), r.Relics?.ToString() ?? "",
                // « 38+ » : total minimum, certaines cartes n'ont pas de valeur fixe ; « ? » : aucune n'en a
                r.Total == null ? "" : r.Count == 0 ? "-"
                    : r.Approx && r.Total == 0 ? "?"
                    : r.Total.Value.ToString("0.##") + (r.Approx ? "+" : ""),
            }));
        }
        // Lignes des types dans la couleur du type, comme dans Cards
        content.AddChild(StatsTable.Create(table, name => DeckStats.IconFor(rows, name),
            name => Array.Find(Types, t => t.Name == name) is { Name: not null } t ? t.Color : null));
    }

    /// <summary>
    /// Les cartes du deck sur une seule colonne : « 4× Strike », une ligne par carte (les versions améliorées et
    /// enchantées à part, avec l'icône de l'enchantement), dans la couleur de leur type, rangées par type puis par
    /// nombre d'exemplaires.
    /// Au survol d'un nom, la carte s'affiche.
    /// </summary>
    private static void FillCards(VBoxContainer content, IReadOnlyList<CardModel> cards, CardHover cardHover)
    {
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        foreach (var byType in cards.GroupBy(c => c.Type).OrderBy(g => TypeOrder(g.Key)))
        {
            var color = TypeColor(byType.Key);
            // Une carte enchantée est comptée à part de ses copies sans enchantement (ou avec un autre)
            var groups = byType
                .GroupBy(c => (c.Title, Enchantment: EnchantmentKey(c)))
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key.Title)
                .ThenBy(g => g.Key.Enchantment);
            foreach (var group in groups)
            {
                var card = group.First();
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 6);
                row.AddChild(RunPanel.Label($"{group.Count()}× {group.Key.Title}", 18, color));
                if (card.Enchantment is { } enchantment)
                    AddEnchantmentIcon(row, enchantment);
                // La carte du deck elle-même : améliorations et enchantements de la partie compris ;
                // à côté, les stats de la partie pour toutes les copies de la ligne
                var copies = group.ToList();
                cardHover.Attach(row, card, () => StatsRows(card, CardRunStats.Sum(copies), copies.Count));
                list.AddChild(row);
            }
        }
        content.AddChild(list);
    }

    /// <summary>
    /// Stats de la partie affichées au survol d'une ligne de cartes : pour chaque stat, le total de la partie, la
    /// moyenne par combat, et l'apport moyen par combat d'une seule carte (total ÷ combats ÷ copies de la ligne).
    /// D'abord les stats liées à la carte (<see cref="StatRelevance"/>), puis, sous « Other », les
    /// autres stats non nulles (enchantement, effet inhabituel).
    /// </summary>
    private static IEnumerable<string[]> StatsRows(CardModel card, CardRunStats s, int copies)
    {
        string Format(decimal value) => value.ToString("0.##");
        string PerCombat(decimal total) => s.Combats > 0 ? Format(total / s.Combats) : "-";
        string PerCard(decimal total) => s.Combats > 0 && copies > 0 ? Format(total / s.Combats / copies) : "-";
        // Stats hors combat (cartes ajoutées au deck…) : pas de moyennes par combat
        string[] Row(string name) => CardRunStats.OutOfCombat.Contains(name)
            ? [name, Format(s[name]), "", ""]
            : [name, Format(s[name]), PerCombat(s[name]), PerCard(s[name])];

        var (related, others) = StatRelevance.For(card).Arrange(s);
        // Aucune stat : pas de tableau, la carte s'affiche seule
        if (related.Count == 0 && others.Count == 0)
            yield break;
        yield return ["", "Total", "Per combat", "Per card"];
        foreach (var name in related)
            yield return Row(name);
        if (others.Count == 0)
            yield break;
        yield return ["Other"];
        foreach (var name in others)
            yield return Row(name);
    }

    private static string EnchantmentKey(CardModel card) =>
        card.Enchantment is { } e ? $"{e.Id.Entry}:{(e.ShowAmount ? e.DisplayAmount : 0)}" : "";

    /// <summary>Icône de l'enchantement après le nom de la carte, avec son nombre s'il en affiche un.</summary>
    private static void AddEnchantmentIcon(HBoxContainer row, EnchantmentModel enchantment)
    {
        try
        {
            row.AddChild(new TextureRect
            {
                Texture = enchantment.Icon,
                CustomMinimumSize = new Vector2(22, 22),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
            if (enchantment.ShowAmount)
                row.AddChild(RunPanel.Label(enchantment.DisplayAmount.ToString(), 16, StsColors.cream));
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot show enchantment {enchantment.Id}: {e.Message}");
        }
    }

    private static int TypeOrder(CardType type)
    {
        int index = Array.FindIndex(Types, t => t.Type == type);
        return index < 0 ? Types.Length : index;
    }

    private static Color TypeColor(CardType type) =>
        Array.Find(Types, t => t.Type == type) is { Name: not null } t ? t.Color : StsColors.cream;
}

/// <summary>
/// Combat en cours : les mêmes stats que pour une carte, pour toutes les cartes du joueur ensemble, avec le
/// total du combat, le tour en cours et la moyenne par tour.
/// </summary>
internal sealed class CombatTab : IRunPanelTab
{
    public string Title => "Combat";

    public void Subscribe(Player player, Action changed) => CardRunStats.Changed += changed;

    public void Unsubscribe(Player player, Action changed) => CardRunStats.Changed -= changed;

    public bool HasTeam => true;

    public void Fill(VBoxContainer content, IReadOnlyList<Player> players, string? subTab)
    {
        // Un joueur, ou l'équipe : la somme de tous (tours : ceux du joueur le plus avancé)
        var totals = players.Select(CardRunStats.TotalsOf).ToList();
        var combat = CardRunStats.Merge(totals.Select(t => t.Combat));
        var turn = CardRunStats.Merge(totals.Select(t => t.Turn));
        int turns = totals.Max(t => t.CombatTurns);
        var player = players.Count == 1 ? players[0] : null;
        string Format(decimal value) => value.ToString("0.##");
        var rows = combat.DisplayNames()
            .Select(name => new[] { name, Format(combat[name]), Format(turn[name]), turns > 0 ? Format(combat[name] / turns) : "-" })
            .ToList();
        bool inCombat = CombatManager.Instance.IsInProgress;
        if (rows.Count == 0 && !inCombat)
        {
            content.AddChild(RunPanel.Label("No stats for this combat yet.", 18, StsColors.halfTransparentCream));
            return;
        }
        content.AddChild(RunPanel.Label(turns > 0 ? $"Turn {turns}" : inCombat ? "Combat" : "Last combat", 18, StsColors.halfTransparentCream));
        // Dégâts annoncés : calculés par le jeu contre le joueur local seulement
        if (inCombat && player != null && player == CardRunStats.LocalPlayer && IncomingDamage(player) is { } incoming)
        {
            // Icône d'attaque des intentions, libellé atténué, nombre en clair : comme une ligne du tableau
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 6);
            line.AddChild(new TextureRect
            {
                Texture = StatsTable.LoadIcon("res://images/packed/intents/attack/intent_attack_1.png"),
                CustomMinimumSize = new Vector2(22, 22),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
            line.AddChild(RunPanel.Label("Unblocked incoming damage", 18, StsColors.halfTransparentCream));
            line.AddChild(RunPanel.Label(incoming.ToString(), 18, StsColors.cream));
            content.AddChild(line);
        }
        if (rows.Count > 0)
            content.AddChild(StatsTable.Create([["", "Total", "This turn", "Per turn"], .. rows]));
    }

    /// <summary>
    /// Dégâts que le joueur va perdre si rien ne change : ceux annoncés par les intentions d'attaque des ennemis en
    /// vie pour leur prochain tour, comme les chiffres affichés au-dessus d'eux (coups multiples compris, avec la
    /// Force des ennemis, Weak, Vulnerable… calculés par le jeu contre le joueur local), moins son Block actuel.
    /// Null s'il n'y a pas de combat.
    /// </summary>
    private static int? IncomingDamage(Player player)
    {
        try
        {
            if (player.Creature.CombatState is not CombatState state)
                return null;
            var targets = state.Allies;
            int total = 0;
            foreach (var enemy in state.Enemies)
            {
                if (enemy.IsDead || enemy.Monster?.NextMove is not { } move)
                    continue;
                foreach (var intent in move.Intents)
                    if (intent is AttackIntent attack && attack.DamageCalc != null)
                        total += attack.GetTotalDamage(targets, enemy);
            }
            return Math.Max(0, total - player.Creature.Block);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot compute incoming damage: {e.Message}");
            return null;
        }
    }
}

/// <summary>
/// Partie entière : les mêmes stats que l'onglet Combat, sur toute la partie, avec le total, la moyenne par combat
/// et la moyenne par tour (tous combats confondus).
/// </summary>
internal sealed class GameTab : IRunPanelTab
{
    public string Title => "Game";

    public void Subscribe(Player player, Action changed) => CardRunStats.Changed += changed;

    public void Unsubscribe(Player player, Action changed) => CardRunStats.Changed -= changed;

    public bool HasTeam => true;

    public void Fill(VBoxContainer content, IReadOnlyList<Player> players, string? subTab)
    {
        // Un joueur, ou l'équipe : la somme de tous (combats et tours : ceux du joueur le plus avancé)
        var totals = players.Select(CardRunStats.TotalsOf).ToList();
        var run = CardRunStats.Merge(totals.Select(t => t.Run));
        int combats = totals.Max(t => t.RunCombats), turns = totals.Max(t => t.RunTurns);
        string Format(decimal value) => value.ToString("0.##");
        var rows = run.DisplayNames()
            .Select(name => new[]
            {
                name, Format(run[name]),
                combats > 0 ? Format(run[name] / combats) : "-",
                turns > 0 ? Format(run[name] / turns) : "-",
            })
            .ToList();
        if (rows.Count == 0)
        {
            content.AddChild(RunPanel.Label("No stats for this game yet.", 18, StsColors.halfTransparentCream));
            return;
        }
        content.AddChild(RunPanel.Label($"{combats} combats, {turns} turns", 18, StsColors.halfTransparentCream));
        content.AddChild(StatsTable.Create([["", "Total", "Per combat", "Per turn"], .. rows]));
    }
}
