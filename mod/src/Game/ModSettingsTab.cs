using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace StsTools.Game;

/// <summary>
/// Options du jeu : un onglet « Mod » après « Input », avec les options du mod (<see cref="ModSettingsOptions"/>) puis
/// une ligne par touche du mod (<see cref="ModHotkeys"/>).
/// L'onglet est une copie de l'onglet Input, le panneau une copie (vidée) d'un panneau simple du jeu, et les lignes
/// des lignes de touches du jeu (même scène) dont on remplace le titre et la touche affichée. Un clic sur une ligne
/// attend la nouvelle touche, comme pour les touches du jeu.
/// </summary>
[HarmonyPatch(typeof(NSettingsTabManager), nameof(NSettingsTabManager._Ready))]
public static class ModSettingsTab
{
    private const string TabName = "StsToolsModTab";
    private const string EntryPrefix = "StsTools.";

    private static readonly AccessTools.FieldRef<NSettingsTabManager, Dictionary<NSettingsTab, NSettingsPanel>> Tabs =
        AccessTools.FieldRefAccess<NSettingsTabManager, Dictionary<NSettingsTab, NSettingsPanel>>("_tabs");

    /// <summary>
    /// Écrans d'options déjà créés avant les patchs (celui du menu principal, prêt avant le menu lui-même) : l'onglet y
    /// est ajouté après coup.
    /// </summary>
    public static void AddToExisting(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is NSettingsTabManager manager)
                Postfix(manager);
            else
                AddToExisting(child);
        }
    }

    public static void Postfix(NSettingsTabManager __instance)
    {
        try
        {
            var tabs = Tabs(__instance);
            if (tabs.Keys.Any(t => t.Name == TabName))
                return;
            var inputTab = __instance.GetNode<NSettingsTab>("Input");
            var tab = (NSettingsTab)inputTab.Duplicate();
            tab.Name = TabName;
            // Chaque onglet a son propre matériau (éclairage au survol) : sinon Input et Mod s'allumeraient ensemble
            var image = tab.GetNode<TextureRect>("TabImage");
            if (image.Material != null)
                image.Material = (Material)image.Material.Duplicate();
            inputTab.AddSibling(tab);
            tab.SetLabel("Mod");
            // Onglets placés à la main dans la scène (pas de conteneur) : le nôtre après Input, au même écart
            if (inputTab.GetParent() is not Container)
            {
                var sound = __instance.GetNode<Control>("Sound");
                tab.Position = inputTab.Position + (inputTab.Position - sound.Position);
            }

            // Panneau Sound copié puis vidé : un panneau d'options du jeu sans logique propre
            var model = __instance.GetNode<NSettingsPanel>("%SoundSettings");
            var panel = (NSettingsPanel)model.Duplicate();
            panel.Name = "StsToolsModSettings";
            panel.Visible = false;
            foreach (var child in panel.GetNode("VBoxContainer").GetChildren())
            {
                child.GetParent().RemoveChild(child);
                child.Free();
            }
            model.AddSibling(panel);
            var header = Header(__instance);
            // Deux sous-onglets : Options (GUI, rapports) et Input (touches du mod, sous l'en-tête de leurs colonnes)
            // Rangée : « ? » tout à gauche, sous-onglets au bord droit
            var row = new HBoxContainer();
            panel.Content.AddChild(row);
            row.AddThemeConstantOverride("separation", 24);
            var left = new Control { CustomMinimumSize = new Vector2(64, 64), MouseFilter = Control.MouseFilterEnum.Ignore };
            row.AddChild(left);
            row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
            var subTabs = new HBoxContainer();
            subTabs.AddThemeConstantOverride("separation", 24);
            row.AddChild(subTabs);
            var optionsTab = SubTab(inputTab, subTabs, "Options");
            var keysTab = SubTab(inputTab, subTabs, "Input");
            // Guide du mod, si l'option est cochée (dans ce même onglet)
            var help = ModGuide.GameHelpButton(64);
            help.Visible = ModOptions.HelpInSettings;
            left.AddChild(help);
            void HelpOption() => help.Visible = ModOptions.HelpInSettings;
            ModOptions.Changed += HelpOption;
            help.TreeExiting += () => ModOptions.Changed -= HelpOption;
            // Un peu d'espace sous les sous-onglets
            panel.Content.AddChild(new Control { CustomMinimumSize = new Vector2(0, 12), MouseFilter = Control.MouseFilterEnum.Ignore });

            int first = panel.Content.GetChildCount();
            ModSettingsOptions.AddTo(__instance, panel, text => SectionTitle(header, text));
            var optionRows = panel.Content.GetChildren().Skip(first).OfType<CanvasItem>().ToList();
            first = panel.Content.GetChildCount();
            if (header != null)
                panel.Content.AddChild(header);
            // Une section par groupe (GUI, écran Stats, partie), titre au style de l'en-tête des colonnes
            string? group = null;
            foreach (var hotkey in ModHotkeys.All)
            {
                if (hotkey.Group != group)
                {
                    group = hotkey.Group;
                    if (SectionTitle(header, group) is { } title)
                        panel.Content.AddChild(title);
                }
                panel.Content.AddChild(Entry(hotkey));
            }
            var keyRows = panel.Content.GetChildren().Skip(first).OfType<CanvasItem>().ToList();

            // « ? » au bout droit de la rangée, au centre vertical des sous-onglets dessinés (leur image dépasse leur
            // place dans la rangée)
            void PlaceHelp()
            {
                if (!GodotObject.IsInstanceValid(help) || !help.IsInsideTree())
                    return;
                help.Size = help.GetCombinedMinimumSize();
                // Image de l'onglet Options telle qu'elle est dessinée (décalée par rapport à sa place dans la rangée)
                var art = optionsTab.GetNodeOrNull<Control>("TabImage")?.GetGlobalRect() ?? optionsTab.GetGlobalRect();
                float centerY = art.Position.Y + art.Size.Y / 2 - left.GlobalPosition.Y;
                help.Position = new Vector2(0, centerY - help.Size.Y / 2);
            }
            left.Resized += () => Callable.From(PlaceHelp).CallDeferred();
            panel.VisibilityChanged += () => Callable.From(PlaceHelp).CallDeferred();

            // Même largeur pour les deux vues, celle de la plus large (Input, avec l'en-tête de ses colonnes) : le contenu
            // ne change plus de taille d'une vue à l'autre, et le jeu ne le recentre plus ailleurs (tout se décalait)
            void SameWidth()
            {
                if (!GodotObject.IsInstanceValid(panel))
                    return;
                float width = optionRows.Concat(keyRows).OfType<Control>().Max(r => r.GetCombinedMinimumSize().X);
                if (width > panel.Content.CustomMinimumSize.X)
                    panel.Content.CustomMinimumSize = new Vector2(width, 0);
                // Lignes d'options à la largeur de celles des panneaux du jeu (General…), calées à gauche : leurs cases
                // et choix tombent au même endroit que dans les autres onglets ; seuls les sous-onglets vont jusqu'au
                // bord du contenu, élargi par la vue Input
                if (ModSettingsOptions.NativeContent is { } native && GodotObject.IsInstanceValid(native) && native.Size.X > 0)
                    foreach (var row in optionRows.OfType<Control>())
                    {
                        row.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
                        row.CustomMinimumSize = new Vector2(native.Size.X, row.CustomMinimumSize.Y);
                    }
            }

            void Show(bool keys)
            {
                SameWidth();
                foreach (var row in optionRows)
                    row.Visible = !keys;
                foreach (var row in keyRows)
                    row.Visible = keys;
                if (keys)
                {
                    keysTab.Select();
                    optionsTab.Deselect();
                }
                else
                {
                    optionsTab.Select();
                    keysTab.Deselect();
                }
                AccessTools.Method(typeof(NSettingsPanel), "RefreshSize")?.Invoke(panel, null);
                // Navigation au clavier (mode clavier seul) et à la manette : le jeu chaîne les lignes visibles du
                // panneau entre elles (sous-onglets compris) et choisit la première comme focus à l'ouverture de l'onglet.
                // Taille du panneau refaite après la mise en page de la vue affichée
                Callable.From(() =>
                {
                    if (!GodotObject.IsInstanceValid(panel))
                        return;
                    AccessTools.Method(typeof(NSettingsPanel), "UpdateNavigation")?.Invoke(panel, null);
                    SameWidth();
                    AccessTools.Method(typeof(NSettingsPanel), "RefreshSize")?.Invoke(panel, null);
                }).CallDeferred();
            }
            optionsTab.Connect(NClickableControl.SignalName.Released, Callable.From<NSettingsTab>(_ => Show(false)));
            keysTab.Connect(NClickableControl.SignalName.Released, Callable.From<NSettingsTab>(_ => Show(true)));
            Show(false);

            tabs.Add(tab, panel);
            var switchTo = AccessTools.MethodDelegate<Action<NSettingsTab>>(
                AccessTools.Method(typeof(NSettingsTabManager), "SwitchTabTo"), __instance);
            tab.Connect(NClickableControl.SignalName.Released, Callable.From<NSettingsTab>(t => switchTo(t)));
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] cannot add the Mod settings tab: {e}");
        }
    }

    /// <summary>
    /// Sous-onglet de l'onglet Mod : copie d'un onglet des options du jeu (même aspect, même navigation au clavier et à
    /// la manette), posé dans une rangée du panneau.
    /// </summary>
    private static NSettingsTab SubTab(NSettingsTab model, Container parent, string text)
    {
        var tab = (NSettingsTab)model.Duplicate();
        tab.Name = "StsToolsSubTab" + text;
        var image = tab.GetNode<TextureRect>("TabImage");
        if (image.Material != null)
            image.Material = (Material)image.Material.Duplicate();
        // Taille de l'onglet du jeu (placé à la main) réservée dans la rangée ; même taille que lui (le jeu remet
        // l'échelle à 1 après le survol)
        var holder = new Control { CustomMinimumSize = model.Size, MouseFilter = Control.MouseFilterEnum.Ignore };
        parent.AddChild(holder);
        // Ancres de l'onglet du jeu centrées : dans sa case, il serait décalé de la moitié de sa taille
        tab.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        tab.Position = Vector2.Zero;
        holder.AddChild(tab);
        tab.SetLabel(text);
        return tab;
    }

    /// <summary>
    /// En-tête des colonnes (commande, clavier et souris, clavier seul, manette) : copie de celui de l'onglet Input,
    /// textes compris, sans ce qui n'est pas du texte (case à cocher du mode clavier seul…).
    /// </summary>
    private static Control? Header(NSettingsTabManager manager)
    {
        try
        {
            var row = manager.GetNode<NSettingsPanel>("%InputSettings").GetNode<Control>("%CommandHeader").GetParent<Control>();
            var copy = (Control)row.Duplicate();
            foreach (var child in copy.GetChildren())
                if (child is not (Label or RichTextLabel) && child.GetType() != typeof(Control))
                {
                    copy.RemoveChild(child);
                    child.Free();
                }
            return copy;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot copy the input settings header: {e.Message}");
            return null;
        }
    }

    /// <summary>Titre de section : copie d'un texte de l'en-tête des colonnes, en doré, avec de l'espace au-dessus.</summary>
    private static Control? SectionTitle(Control? header, string text)
    {
        try
        {
            if (header?.GetChildren().FirstOrDefault(c => c is Label or RichTextLabel) is not Control model)
                return null;
            var title = (Control)model.Duplicate();
            switch (title)
            {
                case MegaLabel mega:
                    mega.SetTextAutoSize(text);
                    break;
                case Label label:
                    label.Text = text;
                    break;
                case RichTextLabel rich:
                    rich.Text = text;
                    break;
            }
            title.Modulate = StsColors.gold;
            title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            title.CustomMinimumSize = new Vector2(0, 56);
            if (title is Label { } aligned)
            {
                aligned.HorizontalAlignment = HorizontalAlignment.Left;
                aligned.VerticalAlignment = VerticalAlignment.Bottom;
            }
            title.MouseFilter = Control.MouseFilterEnum.Ignore;
            title.FocusMode = Control.FocusModeEnum.None;
            return title;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot add the settings section {text}: {e.Message}");
            return null;
        }
    }

    private static NInputSettingsEntry Entry(ModHotkeys hotkey)
    {
        // Le jeu cherche le titre de chaque ligne dans ses traductions : on lui en donne un qui existe, remplacé
        // ensuite par le nôtre (EntryReadyPatch)
        NInputSettingsEntry.commandToLocTitle[EntryPrefix + hotkey.Id] = "confirm";
        var entry = NInputSettingsEntry.Create(EntryPrefix + hotkey.Id);
        // Comme dans l'onglet Input : la touche du mode actuel ; un message si l'action n'a pas de touche dans ce mode
        // (manette ; clavier et souris pour une action du clavier seul)
        entry.Connect(NClickableControl.SignalName.Released, Callable.From<NInputSettingsEntry>(e =>
        {
            if (!ModHotkeys.StartListening(hotkey, () => Refresh(e)))
                ShowCannotRemapToast(e, hotkey);
        }));
        // Options fermées ou onglet changé pendant l'attente : on abandonne
        entry.Connect(CanvasItem.SignalName.VisibilityChanged, Callable.From(() =>
        {
            if (!entry.IsVisibleInTree() && ModHotkeys.IsListening(hotkey))
                ModHotkeys.StopListening();
        }));
        entry.TreeExiting += () =>
        {
            if (ModHotkeys.IsListening(hotkey))
                ModHotkeys.StopListening();
        };
        return entry;
    }

    // ---- Lignes : titre et touche affichée ----

    private static readonly AccessTools.FieldRef<NInputSettingsEntry, MegaLabel> InputLabel =
        AccessTools.FieldRefAccess<NInputSettingsEntry, MegaLabel>("_inputLabel");
    private static readonly AccessTools.FieldRef<NInputSettingsEntry, MegaLabel> MKbLabel =
        AccessTools.FieldRefAccess<NInputSettingsEntry, MegaLabel>("_mKbBindingLabel");
    private static readonly AccessTools.FieldRef<NInputSettingsEntry, MegaLabel> KbOnlyLabel =
        AccessTools.FieldRefAccess<NInputSettingsEntry, MegaLabel>("_keyboardOnlyModeBindingLabel");
    private static readonly AccessTools.FieldRef<NInputSettingsEntry, Control> MissingControllerLabel =
        AccessTools.FieldRefAccess<NInputSettingsEntry, Control>("_missingControllerBindingLabel");
    private static readonly AccessTools.FieldRef<NInputSettingsEntry, TextureRect> ControllerIcon =
        AccessTools.FieldRefAccess<NInputSettingsEntry, TextureRect>("_controllerBindingIcon");

    private static ModHotkeys? HotkeyOf(NInputSettingsEntry entry)
    {
        string name = entry.InputName.ToString();
        return name.StartsWith(EntryPrefix) ? ModHotkeys.All.FirstOrDefault(h => EntryPrefix + h.Id == name) : null;
    }

    /// <summary>
    /// Touches affichées, comme le jeu : celle du clavier et souris et celle du clavier seul (« ... » pendant
    /// l'attente), grisées si absentes, en rouge pour le mode qui n'est pas actif ; aucune à la manette.
    /// </summary>
    private static void Refresh(NInputSettingsEntry entry)
    {
        if (HotkeyOf(entry) is not { } hotkey || !GodotObject.IsInstanceValid(entry) || !entry.IsNodeReady())
            return;
        foreach (var (label, mode) in new[] { (MKbLabel(entry), InputType.MouseAndKeyboard), (KbOnlyLabel(entry), InputType.KeyboardOnlyMode) })
        {
            label.Text = ModHotkeys.IsListening(hotkey, mode) ? "..." : hotkey.DisplayFor(mode);
            label.SelfModulate = label.Text == "-" ? StsColors.gray : Colors.White;
            label.Modulate = ModHotkeys.CurrentMode == mode ? Colors.White : StsColors.disabledRed;
        }
        MissingControllerLabel(entry).Visible = true;
        ControllerIcon(entry).Texture = null;
    }

    /// <summary>Même message que le jeu pour une touche qu'on ne peut pas changer à la manette.</summary>
    private static void ShowCannotRemapToast(Node entry, ModHotkeys hotkey)
    {
        try
        {
            Node? node = entry;
            while (node != null && node is not NSettingsScreen)
                node = node.GetParent();
            if (node is not NSettingsScreen screen)
                return;
            var message = new LocString("settings_ui", "TOAST_CANNOT_REMAP_INPUT");
            message.AddObj("Hotkey", hotkey.Title);
            // Mode dans lequel l'action n'a pas de touche : celui en cours
            var header = ModHotkeys.CurrentMode == InputType.Controller ? "INPUT_SETTINGS.CONTROLLER_HEADER" : "INPUT_SETTINGS.MOUSE_KEYBOARD_HEADER";
            message.AddObj("ControlType", new LocString("settings_ui", header).GetFormattedText());
            screen.ShowToast(message);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot show the remap toast: {e.Message}");
        }
    }

    [HarmonyPatch(typeof(NInputSettingsEntry), nameof(NInputSettingsEntry._Ready))]
    public static class EntryReadyPatch
    {
        public static void Postfix(NInputSettingsEntry __instance)
        {
            if (HotkeyOf(__instance) is not { } hotkey)
                return;
            InputLabel(__instance).SetTextAutoSize(hotkey.Title);
            Refresh(__instance);
        }
    }

    /// <summary>Le jeu remet ses propres valeurs (aucune touche connue) à chaque affichage : on remet les nôtres.</summary>
    [HarmonyPatch(typeof(NInputSettingsEntry), "UpdateInput")]
    public static class EntryUpdatePatch
    {
        public static void Postfix(NInputSettingsEntry __instance) => Refresh(__instance);
    }
}
