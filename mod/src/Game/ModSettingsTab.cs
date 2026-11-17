using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace StsTools.Game;

/// <summary>
/// Options du jeu : un onglet « Mod » après « Input », avec une ligne par touche du mod (<see cref="ModHotkeys"/>).
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
            if (Header(__instance) is { } header)
                panel.Content.AddChild(header);
            foreach (var hotkey in ModHotkeys.All)
                panel.Content.AddChild(Entry(hotkey));
            AccessTools.Method(typeof(NSettingsPanel), "RefreshSize")?.Invoke(panel, null);
            // Navigation au clavier (mode clavier seul) et à la manette : le jeu chaîne les lignes du panneau entre elles
            // et choisit la première comme focus à l'ouverture de l'onglet ; à refaire une fois nos lignes prêtes
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(panel))
                    AccessTools.Method(typeof(NSettingsPanel), "UpdateNavigation")?.Invoke(panel, null);
            }).CallDeferred();

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
