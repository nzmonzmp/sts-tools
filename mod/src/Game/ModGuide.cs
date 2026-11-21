using Godot;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace StsTools.Game;

/// <summary>
/// Guide du mod : ce qu'il sait faire et où le régler. Affiché tout seul au premier menu principal avec le mod, puis
/// rouvert par les boutons « ? » du GUI, de l'écran Stats et de l'onglet Mod des options (chacun visible si son option
/// est cochée dans l'onglet Mod). Fenêtre au-dessus de tout, fermée par Close ou Échap.
/// </summary>
public static class ModGuide
{
    private const string FontPath = "res://themes/kreon_bold_glyph_space_two.tres";

    private static CanvasLayer? _layer;

    public static bool IsOpen => _layer != null && GodotObject.IsInstanceValid(_layer);

    private static ulong _closedFrame = ulong.MaxValue;

    /// <summary>
    /// Contrôle qui avait le focus avant le guide : à la fermeture, le focus revient à l'écran qui le contient. Sinon la
    /// touche Échap suivante n'arrive plus à l'écran derrière (Stats) mais au menu principal, qui ouvrait l'écran des
    /// profils.
    /// </summary>
    private static Control? _previousFocus;

    /// <summary>
    /// Ouvert, ou fermé pendant cette image : la touche qui l'a fermé (Échap) ne doit pas aussi fermer ce qu'il y a
    /// derrière (écran Stats, GUI).
    /// </summary>
    public static bool BlocksInput => IsOpen || Engine.GetProcessFrames() == _closedFrame;

    /// <summary>Premier menu principal avec le mod : le guide s'ouvre une fois.</summary>
    public static void ShowOnFirstLaunch()
    {
        var settings = SavedSettings.Load();
        if (settings.GuideSeen)
            return;
        settings.GuideSeen = true;
        settings.Save();
        Callable.From(Open).CallDeferred();
    }

    public static void Open()
    {
        if (IsOpen || Engine.GetMainLoop() is not SceneTree tree)
            return;
        try
        {
            _previousFocus = tree.Root.GuiGetFocusOwner();
            Build(tree);
            BugReport.Note("Mod guide opened");
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] cannot open the mod guide: {e}");
            Close();
        }
    }

    /// <summary>
    /// Fermé par une touche (Échap) : son relâchement est avalé, sinon il agirait aussi derrière (le bouton Profil du
    /// menu principal réagit au relâchement de la touche Retour).
    /// </summary>
    public static void CloseByKey(InputEvent ev)
    {
        KeyboardNav.SwallowRelease(ev);
        Close();
    }

    /// <summary>Raccourcis du jeu bloqués jusqu'à ce temps (ms) : le relâchement de la touche qui a fermé le guide.</summary>
    private static ulong _hotkeysBlockedUntil;

    /// <summary>
    /// Raccourcis des boutons du jeu (NHotkeyManager) : rien pendant que le guide est ouvert, ni juste après sa
    /// fermeture. Le relâchement d'Échap qui le ferme arrivait sinon au bouton Profil du menu principal, derrière.
    /// </summary>
    /// <summary>
    /// Touches du guide prises avant le gestionnaire des modes de contrôle du jeu : en mode « clavier seul », la première
    /// flèche après la souris ne sert qu'à repasser au clavier (focus ailleurs, souris coupée) et n'arrivait jamais au
    /// guide.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.CommonUi.NControllerManager), "_Input")]
    public static class ControllerPatch
    {
        public static bool Prefix(Node __instance, InputEvent inputEvent)
        {
            if (!IsOpen || !HandleKey(inputEvent))
                return true;
            __instance.GetViewport()?.SetInputAsHandled();
            return false;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.CommonUi.NHotkeyManager), "_UnhandledInput")]
    public static class HotkeyPatch
    {
        public static bool Prefix() => !IsOpen && Time.GetTicksMsec() >= _hotkeysBlockedUntil;
    }

    public static void Close()
    {
        if (IsOpen)
        {
            _layer!.QueueFree();
            _closedFrame = Engine.GetProcessFrames();
            _hotkeysBlockedUntil = Time.GetTicksMsec() + 500;
            _showPage = null;
            // Pas le bouton « ? » lui-même (une touche suivante rouvrirait le guide) : l'écran qui le contient
            Node? target = _previousFocus;
            while (target is BaseButton || target is Control { FocusMode: not Control.FocusModeEnum.All })
                target = target.GetParent();
            if (target is Control control && GodotObject.IsInstanceValid(control) && control.IsVisibleInTree())
                control.GrabFocus();
        }
        _previousFocus = null;
        _layer = null;
    }

    /// <summary>Bouton « ? » qui ouvre le guide, pour les en-têtes du GUI, de l'écran Stats et de l'onglet Mod.</summary>
    public static Button HelpButton(int fontSize = 22)
    {
        var button = new Button { Text = "?", TooltipText = "Mod guide: what the mod does and where to change it" };
        button.AddThemeFontSizeOverride("font_size", fontSize);
        button.AddThemeColorOverride("font_color", StsColors.gold);
        button.AddThemeColorOverride("font_hover_color", StsColors.cream);
        button.AddThemeColorOverride("font_pressed_color", StsColors.cream);
        button.AddThemeColorOverride("font_focus_color", StsColors.gold);
        // Pastille ronde à bord doré, plus claire au survol
        int side = (int)(fontSize * 1.5f);
        button.CustomMinimumSize = new Vector2(side, side);
        // Rond même dans une rangée plus haute que lui
        button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        StyleBoxFlat Round(Color bg, float border) => new()
        {
            BgColor = bg,
            BorderColor = new Color(StsColors.gold, border),
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = side, CornerRadiusTopRight = side, CornerRadiusBottomLeft = side, CornerRadiusBottomRight = side,
            ContentMarginLeft = 0, ContentMarginRight = 0, ContentMarginTop = 0, ContentMarginBottom = 0,
        };
        button.AddThemeStyleboxOverride("normal", Round(new Color(0.06f, 0.08f, 0.12f, 0.9f), 0.7f));
        button.AddThemeStyleboxOverride("hover", Round(new Color(0.16f, 0.2f, 0.26f, 0.95f), 1f));
        button.AddThemeStyleboxOverride("pressed", Round(new Color(0.16f, 0.2f, 0.26f, 0.95f), 1f));
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        button.Pressed += Open;
        return button;
    }

    /// <summary>
    /// Bouton « ? » dessiné comme le jeu : le point d'interrogation de son écran de statistiques, plus lumineux et un
    /// peu plus grand au survol. Repli : la pastille ronde de <see cref="HelpButton"/>.
    /// </summary>
    public static Button GameHelpButton(int size)
    {
        var texture = new[]
        {
            "res://images/atlases/stats_screen_atlas.sprites/stats_questionmark.tres",
            "res://images/ui/emote/question.png",
        }.Select(StatsTable.LoadIcon).FirstOrDefault(t => t != null);
        if (texture == null)
            return HelpButton(size / 2);
        var button = new Button
        {
            Flat = true, TooltipText = "Mod guide: what the mod does and where to change it",
            CustomMinimumSize = new Vector2(size, size), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        var image = new TextureRect
        {
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            PivotOffset = new Vector2(size, size) / 2,
        };
        image.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        button.AddChild(image);
        // Comme les boutons du jeu : plus clair et un peu plus grand au survol
        void Hover(bool on)
        {
            image.Modulate = on ? new Color(1.25f, 1.25f, 1.25f) : Colors.White;
            image.Scale = Vector2.One * (on ? 1.1f : 1f);
        }
        button.MouseEntered += () => Hover(true);
        button.MouseExited += () => Hover(false);
        button.FocusEntered += () => Hover(true);
        button.FocusExited += () => Hover(false);
        button.Pressed += Open;
        return button;
    }

    // ---- Contenu ----

    /// <summary>Une page du guide : titre de son onglet, icône du jeu (chemins possibles séparés par |), paragraphes (BBCode, voir <see cref="Gold"/>, <see cref="Key"/>).</summary>
    private sealed record Page(string Tab, string Icon, string Title, string[] Lines);

    private static string Hex(Color color) => "#" + color.ToHtml(false);

    /// <summary>Nom d'un élément du mod ou du jeu, en doré comme les mots-clés des cartes.</summary>
    private static string Gold(string text) => $"[color={Hex(StsColors.gold)}]{text}[/color]";

    /// <summary>Mot-clé de combat (Block, Vulnerable…), en bleu comme dans les descriptions du jeu.</summary>
    private static string Blue(string text) => $"[color={Hex(StsColors.blue)}]{text}[/color]";

    /// <summary>Touche ou bouton à presser, en relief clair sur fond sombre.</summary>
    private static string Key(string text) =>
        $"[bgcolor=#2a3342][color={Hex(StsColors.cream)}] {text} [/color][/bgcolor]";

    private static List<Page> Pages()
    {
        string gui = Key(ModHotkeys.RunStats.Display);
        string stats = Key(ModHotkeys.StatsScreenKey.Display);
        string collapse = Key(ModHotkeys.CollapseGui.Display);
        return
        [
            new("GUI", "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_deck.tres", "The GUI, your run at a glance", [
                $"Open it in a run with {gui} or the {Gold("GUI")} button of the top bar. Drag its header to move it, {collapse} or {Gold("▲")} to collapse it, its bottom edge to resize it.",
                $"{Gold("Deck")}: what each card of your deck did this run ({Blue("damage")}, {Blue("Block")}, energy, cards drawn…), with the card on hover.",
                $"{Gold("Game")}: totals of the run.",
                $"{Gold("Combat")}: this combat, turn by turn, and the damage the enemies will deal to each player.",
                $"{Gold("Support")} (multiplayer): what you gave your allies.",
                $"{Gold("Path finder")} (map open): the best paths to the boss by room type, drawn on the map (see the {Gold("Map")} page).",
            ]),
            new("Combat", "res://images/packed/intents/attack/intent_attack_1.png", "In combat, while the GUI is open", [
                $"Aim a card or a potion: a box next to each creature shows what it will do ({Blue("damage")}, {Blue("Block")}, powers, kills, damage avoided).",
                $"Skulls: on the {Gold("End turn")} button when ending the turn kills you, on the enemies that will die.",
                $"Relic tooltips: what each relic did this run.",
            ]),
            new("Map", "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_map.tres", "On the map", [
                $"Hover a room (GUI open): the enemies you can meet there, the rewards (gold, potion, cards, relics) and, for a {Gold("?")} room, the odds of each outcome.",
                $"{Gold("Path finder")} tab: rank the room types (shops, elites, rest sites, ?, fights) and the best paths to the boss are drawn on the map.",
                $"In multiplayer, {Gold("Draw")} draws them with the game's map pen, for everyone to see.",
            ]),
            new("Stats", "res://images/atlases/stats_screen_atlas.sprites/stats_achievements.tres|res://images/atlases/stats_screen_atlas.sprites/stats_cards.tres", "Stats screen", [
                $"Open it with {stats}, or {Gold("Stats")} in the main menu and the pause menu.",
                $"Win rates, cards, relics, ancients and losses from your whole run history, with filters by mode, ascension and character.",
            ]),
            new("Settings", "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_settings.tres", "Where to change things", [
                $"{Gold("Settings > Mod > Options")}: GUI tabs, how the GUI opens, tabs switched automatically (combat start and end, merchant), the features above, crash and bug reports, and the {Gold("?")} buttons.",
                $"{Gold("Settings > Mod > Input")}: the mod's keys.",
                $"{Gold("Report a bug")}, in the main menu and the pause menu, sends your description (and, if you want, your logs and run) to the mod's author. After a crash, the main menu offers to send the crash report.",
                $"Open this guide again with the {Gold("?")} buttons of the GUI, the Stats screen and {Gold("Settings > Mod")}.",
            ]),
        ];
    }

    private static int _page;

    /// <summary>Change de page dans le guide ouvert (null : fermé).</summary>
    private static Action<int>? _showPage;

    /// <summary>
    /// Touche pendant que le guide est ouvert (ModHotkeys, avant le reste du jeu) : Échap le ferme, gauche et droite
    /// changent de page. Vrai si la touche a servi.
    /// </summary>
    public static bool HandleKey(InputEvent ev)
    {
        if (!IsOpen)
            return false;
        if (ev.IsActionPressed("ui_cancel"))
        {
            CloseByKey(ev);
            return true;
        }
        // Actions du jeu (clavier, manette) ou touches flèches
        bool left = ev.IsActionPressed(MegaInput.left) || ev is InputEventKey { Pressed: true, Echo: false, Keycode: Godot.Key.Left };
        bool right = ev.IsActionPressed(MegaInput.right) || ev is InputEventKey { Pressed: true, Echo: false, Keycode: Godot.Key.Right };
        if (_showPage != null && (left || right))
        {
            _showPage(_page + (left ? -1 : 1));
            return true;
        }
        return false;
    }

    private static void Build(SceneTree tree)
    {
        // Au-dessus de l'écran Stats et de la fenêtre de rapport de bug (couche 100)
        _layer = new CanvasLayer { Layer = 110, Name = "StsToolsModGuide", ProcessMode = Node.ProcessModeEnum.Always };
        var theme = new Theme { DefaultFontSize = 20 };
        if (ResourceLoader.Exists(FontPath) && ResourceLoader.Load<Font>(FontPath) is { } font)
            theme.DefaultFont = font;

        // Écran plein, assombri comme derrière les options du jeu
        var screen = new ColorRect { Color = new Color(0.02f, 0.03f, 0.05f, 0.97f), MouseFilter = Control.MouseFilterEnum.Stop, Theme = theme };
        screen.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(screen);
        var models = GameModels(tree.Root);

        // Contenu centré dans l'écran
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        screen.AddChild(center);
        var column = new VBoxContainer { CustomMinimumSize = new Vector2(1300, 0) };
        column.AddThemeConstantOverride("separation", 18);
        center.AddChild(column);

        // Titre, puis les onglets des pages : copies des onglets des options du jeu (sinon de simples boutons)
        var heading = Label("STS Tools  ·  Mod guide", 30, StsColors.gold, wrap: false);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(heading);
        var tabsRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        tabsRow.AddThemeConstantOverride("separation", 12);
        column.AddChild(tabsRow);
        var pages = Pages();
        var select = new List<Action<bool>>();
        var labels = new List<Action>();
        // Changement de page, branché sur les onglets avant que la page existe (assigné plus bas)
        Action<int> showPage = _ => { };
        foreach (var (page, index) in pages.Select((p, i) => (p, i)))
        {
            if (models.Tab is { } model)
            {
                var tab = (NSettingsTab)model.Duplicate();
                tab.Name = "StsToolsGuideTab" + index;
                var image = tab.GetNode<TextureRect>("TabImage");
                if (image.Material != null)
                    image.Material = (Material)image.Material.Duplicate();
                var holder = new Control { CustomMinimumSize = model.Size, MouseFilter = Control.MouseFilterEnum.Ignore };
                tabsRow.AddChild(holder);
                // Ancres de l'onglet du jeu centrées : dans sa case, il serait décalé de la moitié de sa taille
                tab.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                tab.Position = Vector2.Zero;
                holder.AddChild(tab);
                // Texte mis une fois l'onglet prêt (dans la scène) : son étiquette n'existe qu'après _Ready
                labels.Add(() => tab.SetLabel(page.Tab));
                tab.Connect(NClickableControl.SignalName.Released, Callable.From<NSettingsTab>(_ => showPage(index)));
                select.Add(on =>
                {
                    // Select puis Deselect : la copie peut garder le contour de l'onglet copié sans se croire choisie
                    tab.Select();
                    if (!on)
                        tab.Deselect();
                });
            }
            else
            {
                var tab = new Button { Text = page.Tab, FocusMode = Control.FocusModeEnum.None };
                tab.AddThemeFontSizeOverride("font_size", 24);
                tab.Pressed += () => showPage(index);
                tabsRow.AddChild(tab);
                select.Add(on => RunPanel.StyleTab(tab, on, small: false));
            }
        }

        // Page : icône à gauche, titre et lignes à droite, séparées par un trait comme les lignes des options
        var body = new HBoxContainer { CustomMinimumSize = new Vector2(0, 560) };
        body.AddThemeConstantOverride("separation", 32);
        column.AddChild(body);
        var icon = new TextureRect
        {
            CustomMinimumSize = new Vector2(128, 128),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
        };
        body.AddChild(icon);
        var text = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", 0);
        body.AddChild(text);
        var title = Label("", 32, StsColors.gold);
        title.CustomMinimumSize = new Vector2(0, 56);
        text.AddChild(title);

        // Pied : flèches de page du jeu autour du numéro
        var footer = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        footer.AddThemeConstantOverride("separation", 28);
        column.AddChild(footer);
        var previous = Arrow(models.LeftArrow, "◀");
        var number = Label("", 26, StsColors.cream, wrap: false);
        number.VerticalAlignment = VerticalAlignment.Center;
        var next = Arrow(models.RightArrow, "▶");
        previous.Pressed += () => showPage(_page - 1);
        next.Pressed += () => showPage(_page + 1);
        footer.AddChild(previous);
        footer.AddChild(number);
        footer.AddChild(next);

        // Retour en bas à gauche, à la place du bouton retour des options du jeu
        var close = new Button { Text = "Close" };
        StatsScreen.StyleCloseButton(close);
        close.Position = new Vector2(40, 1080 - 150);
        close.Pressed += Close;
        // Le bouton a le focus : Échap ferme, gauche et droite changent de page (aussi dans ModHotkeys)
        close.GuiInput += ev =>
        {
            if (HandleKey(ev))
                close.AcceptEvent();
        };
        screen.AddChild(close);

        var rows = new List<Control>();
        void ShowPage(int index)
        {
            _page = (index + pages.Count) % pages.Count;
            var page = pages[_page];
            // Première image du jeu trouvée parmi celles proposées (séparées par |)
            icon.Texture = page.Icon.Split('|').Select(StatsTable.LoadIcon).FirstOrDefault(t => t != null);
            title.Text = page.Title;
            foreach (var row in rows)
            {
                text.RemoveChild(row);
                row.QueueFree();
            }
            rows.Clear();
            foreach (var line in page.Lines)
            {
                var row = new VBoxContainer();
                row.AddThemeConstantOverride("separation", 12);
                row.AddChild(new ColorRect { Color = new Color(StsColors.cream, 0.12f), CustomMinimumSize = new Vector2(0, 2) });
                row.AddChild(Rich(line, models.Description));
                row.AddChild(new Control { CustomMinimumSize = new Vector2(0, 2) });
                text.AddChild(row);
                rows.Add(row);
            }
            number.Text = $"{_page + 1} / {pages.Count}";
            for (int i = 0; i < select.Count; i++)
                select[i](i == _page);
        }
        showPage = ShowPage;
        _showPage = ShowPage;

        tree.Root.AddChild(_layer);
        foreach (var label in labels)
            label();
        ShowPage(0);
        close.GrabFocus();
    }

    /// <summary>Texte d'une ligne : BBCode, police de la description des infobulles du jeu si trouvée.</summary>
    private static RichTextLabel Rich(string bbcode, RichTextLabel? model)
    {
        var label = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false, Text = bbcode,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        if (model?.GetThemeFont("normal_font") is { } normal)
            label.AddThemeFontOverride("normal_font", normal);
        if (model?.GetThemeFont("bold_font") is { } bold)
            label.AddThemeFontOverride("bold_font", bold);
        label.AddThemeFontSizeOverride("normal_font_size", 26);
        label.AddThemeFontSizeOverride("bold_font_size", 26);
        label.AddThemeColorOverride("default_color", StsColors.cream);
        label.AddThemeConstantOverride("line_separation", 6);
        return label;
    }

    /// <summary>Flèche de page : image d'une flèche des choix ◀ ▶ des options du jeu, sinon le caractère.</summary>
    private static Button Arrow(TextureRect? model, string fallback)
    {
        var button = new Button { Flat = true, FocusMode = Control.FocusModeEnum.None };
        if (model?.Texture is { } texture)
        {
            button.CustomMinimumSize = new Vector2(56, 56);
            // Image de la flèche du jeu ; celle de droite est la même image retournée dans le jeu
            var image = new TextureRect
            {
                Texture = texture,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                FlipH = model.FlipH || model.Scale.X < 0,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            image.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            button.AddChild(image);
            button.MouseEntered += () => image.Modulate = new Color(1.3f, 1.3f, 1.3f);
            button.MouseExited += () => image.Modulate = Colors.White;
        }
        else
        {
            button.Text = fallback;
            button.AddThemeFontSizeOverride("font_size", 30);
            button.AddThemeColorOverride("font_color", StsColors.gold);
            button.AddThemeColorOverride("font_hover_color", StsColors.cream);
        }
        return button;
    }

    /// <summary>
    /// Modèles pris dans les options du jeu déjà créées (menu principal, ou partie après une ouverture des options) :
    /// un onglet, les images des flèches de choix ; et la description d'une infobulle du jeu (police du texte).
    /// </summary>
    private static (NSettingsTab? Tab, TextureRect? LeftArrow, TextureRect? RightArrow, RichTextLabel? Description) GameModels(Node root)
    {
        NSettingsTab? tab = null;
        TextureRect? left = null, right = null;
        try
        {
            if (Find<NSettingsTabManager>(root) is { } manager)
                tab = manager.GetNodeOrNull<NSettingsTab>("Input");
            if (Find<NPaginator>(root) is { } paginator)
            {
                left = paginator.GetNodeOrNull<TextureRect>("LeftArrow/Image");
                right = paginator.GetNodeOrNull<TextureRect>("RightArrow/Image");
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] mod guide: no settings models: {e.Message}");
        }
        RichTextLabel? description = null;
        try
        {
            if (ResourceLoader.Load<PackedScene>("res://scenes/ui/hover_tip.tscn")?.Instantiate() is Node tip)
            {
                if (tip.FindChild("Description", true, false) is RichTextLabel found)
                    description = (RichTextLabel)found.Duplicate();
                tip.QueueFree();
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] mod guide: no tooltip font: {e.Message}");
        }
        return (tab, left, right, description);
    }

    private static T? Find<T>(Node node) where T : class
    {
        foreach (var child in node.GetChildren())
        {
            if (child is T found)
                return found;
            if (Find<T>(child) is { } deeper)
                return deeper;
        }
        return null;
    }

    private static Label Label(string text, int size, Color color, bool wrap = true)
    {
        var label = new Label { Text = text, AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }
}
