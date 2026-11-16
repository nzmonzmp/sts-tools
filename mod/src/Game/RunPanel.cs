using Godot;
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

    /// <param name="subTab">sous-onglet affiché, parmi <see cref="SubTabs"/> ; null s'il n'y en a pas</param>
    void Fill(VBoxContainer content, Player player, string? subTab);
}

/// <summary>
/// Panneau de stats en partie, ancré à droite sous la barre du haut. Il ne bloque pas le jeu : on continue à jouer
/// pendant qu'il est ouvert. S'ouvre et se ferme avec le bouton de la barre du haut ou la touche <see cref="Hotkey"/> ;
/// il est fermé en début de partie et à l'ouverture du menu Échap. L'onglet choisi est gardé d'une partie à l'autre.
/// Sa hauteur est limitée à l'écran : au-delà, le contenu défile.
/// Construit, comme <see cref="StatsScreen"/>, uniquement avec des nœuds Godot standards.
/// </summary>
public sealed class RunPanel
{
    public const Key Hotkey = Key.F7;
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
    private readonly List<Button> _tabButtons = [];
    private readonly HBoxContainer _subTabBar;
    private readonly List<Button> _subTabButtons = [];
    /// <summary>Sous-onglet choisi dans chaque onglet, par titre d'onglet</summary>
    private readonly Dictionary<string, string> _subTabs;
    private readonly Action _changed;
    private int _tab;

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
        var cardHover = new CardHover(_panel);
        _tabs = [new DeckTab(cardHover), new ComingSoonTab("Combat"), new ComingSoonTab("Relics")];
        var saved = SavedSettings.Load();
        _tab = Math.Max(0, Array.FindIndex(_tabs, t => t.Title == saved.RunPanelTab));
        _subTabs = new Dictionary<string, string>(saved.RunPanelSubTabs);
        _changed = OnChanged;

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

        var header = new HBoxContainer();
        page.AddChild(header);
        header.AddChild(Label("Run stats", 24, StsColors.gold));
        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        header.AddChild(Label($"[{Hotkey}]", 16, StsColors.halfTransparentCream));
        var close = new Button { Text = "✕", FocusMode = Control.FocusModeEnum.None, Flat = true };
        close.AddThemeColorOverride("font_color", StsColors.cream);
        close.AddThemeColorOverride("font_hover_color", StsColors.gold);
        close.Pressed += () => SetOpen(false);
        header.AddChild(close);

        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 6);
        page.AddChild(tabs);
        for (int i = 0; i < _tabs.Length; i++)
        {
            int index = i;
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

        _subTabBar = new HBoxContainer();
        _subTabBar.AddThemeConstantOverride("separation", 6);
        page.AddChild(_subTabBar);

        // Défilement vertical seulement : la largeur du contenu reste celle du panneau
        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        page.AddChild(_scroll);
        _content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _content.AddThemeConstantOverride("separation", 6);
        _scroll.AddChild(_content);

        // Sous les autres enfants de la barre, pour ne pas passer devant ses infobulles
        topBar.AddChild(_panel);
        topBar.MoveChild(_panel, 0);

        foreach (var tab in _tabs)
            tab.Subscribe(_player, _changed);
        _panel.TreeExiting += () =>
        {
            foreach (var tab in _tabs)
                tab.Unsubscribe(_player, _changed);
            if (_current == this)
                _current = null;
        };

        BuildSubTabs();
        StyleTabs();
        SetOpen(false);
    }

    private void SetOpen(bool open)
    {
        _panel.Visible = open;
        // Toujours à l'ouverture : certains changements (carte améliorée) ne passent par aucun événement suivi
        if (open)
            Refresh();
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

    private void SelectTab(int index)
    {
        _tab = index;
        Save(s => s.RunPanelTab = _tabs[index].Title);
        BuildSubTabs();
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

    /// <summary>Boutons des sous-onglets de l'onglet courant ; la barre disparaît s'il n'en a pas.</summary>
    private void BuildSubTabs()
    {
        RemoveChildren(_subTabBar);
        _subTabButtons.Clear();
        var subTabs = _tabs[_tab].SubTabs;
        _subTabBar.Visible = subTabs.Length > 0;
        foreach (var name in subTabs)
        {
            var button = new Button { Text = name, FocusMode = Control.FocusModeEnum.None };
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
        if (!GodotObject.IsInstanceValid(_panel))
            return;
        if (_panel.Visible)
            Callable.From(Refresh).CallDeferred();
    }

    private void Refresh()
    {
        if (!GodotObject.IsInstanceValid(_panel))
            return;
        RemoveChildren(_content);
        try
        {
            _tabs[_tab].Fill(_content, _player, CurrentSubTab);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] cannot fill the run panel tab {_tabs[_tab].Title}: {e}");
            _content.AddChild(Label("Error, see the game log.", 18, StsColors.red));
        }
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
        float wanted = _content.GetCombinedMinimumSize().Y;
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
        _panel.OffsetLeft = -16 - Width;
        _panel.OffsetRight = -16;
        _panel.OffsetTop = _panel.OffsetBottom = TopBarHeight + 12;
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
        foreach (var state in new[] { "normal", "hover", "pressed", "focus" })
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

    public string[] SubTabs => ["Types", "Cards"];

    public void Subscribe(Player player, Action changed) => PileType.Deck.GetPile(player).ContentsChanged += changed;

    public void Unsubscribe(Player player, Action changed) => PileType.Deck.GetPile(player).ContentsChanged -= changed;

    public void Fill(VBoxContainer content, Player player, string? subTab)
    {
        var cards = PileType.Deck.GetPile(player).Cards;
        content.AddChild(RunPanel.Label($"{cards.Count} cards", 20, StsColors.cream));
        if (subTab == "Cards")
            FillCards(content, cards, cardHover);
        else
            FillTypes(content, cards);
    }

    /// <summary>Une ligne par type, « Attacks : 12 (48%) », dans la couleur du type.</summary>
    private static void FillTypes(VBoxContainer content, IReadOnlyList<CardModel> cards)
    {
        foreach (var (type, name, color) in Types)
        {
            int count = cards.Count(c => c.Type == type);
            if (count == 0 && type is not (CardType.Attack or CardType.Skill or CardType.Power))
                continue;
            float share = cards.Count > 0 ? (float)count / cards.Count : 0;
            content.AddChild(RunPanel.Label($"{name} : {count} ({share:P0})", 20, color));
        }
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
                // La carte du deck elle-même : améliorations et enchantements de la partie compris
                cardHover.Attach(row, card);
                list.AddChild(row);
            }
        }
        content.AddChild(list);
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

/// <summary>Onglet réservé à une fonctionnalité du TODO pas encore faite.</summary>
internal sealed class ComingSoonTab(string title) : IRunPanelTab
{
    public string Title => title;

    public void Subscribe(Player player, Action changed) { }

    public void Unsubscribe(Player player, Action changed) { }

    public void Fill(VBoxContainer content, Player player, string? subTab) =>
        content.AddChild(RunPanel.Label("Coming soon.", 18, StsColors.halfTransparentCream));
}
