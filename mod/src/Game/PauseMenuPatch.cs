using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;

namespace StsTools.Game;

/// <summary>Ajoute un bouton « Stats » au menu pause, sous « Compendium », avec le style des boutons du menu.</summary>
[HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu._Ready))]
public static class PauseMenuPatch
{
    private const string ButtonName = "StsToolsButton";
    private const string ButtonText = "Stats";

    // Avant _Ready, comme pour le menu principal : le bouton existe quand le menu se met en place
    public static void Prefix(NPauseMenu __instance)
    {
        try
        {
            var buttons = __instance.GetNode<Control>("%ButtonContainer");
            if (buttons.HasNode(ButtonName))
                return;
            // Copie du bouton Compendium ; le conteneur (VBoxContainer) la range sous l'original
            var model = buttons.GetNode<NPauseMenuButton>("Compendium");
            var button = (NPauseMenuButton)model.Duplicate();
            button.Name = ButtonName;
            // Chaque bouton a son propre matériau (éclairage au survol) : sinon les deux s'allumeraient ensemble
            var image = button.GetNode<TextureRect>("ButtonImage");
            if (image.Material != null)
                image.Material = (Material)image.Material.Duplicate();
            buttons.AddChild(button);
            buttons.MoveChild(button, model.GetIndex() + 1);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] cannot add the pause menu button: {e}");
        }
    }

    public static void Postfix(NPauseMenu __instance)
    {
        try
        {
            var buttons = __instance.GetNode<Control>("%ButtonContainer");
            var button = buttons.GetNodeOrNull<NPauseMenuButton>(ButtonName);
            if (button == null)
                return;
            var label = button.GetNode<MegaLabel>("Label");
            label.SetTextAutoSize(ButtonText);
            var font = label.GetThemeFont("font");
            button.Connect(NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ => StatsScreen.Open(__instance, font)));
            LinkFocus(buttons);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] cannot set up the pause menu button: {e}");
        }
    }

    /// <summary>
    /// Navigation à la manette : le menu chaîne ses six boutons entre eux ; on refait la chaîne avec le nôtre,
    /// sur les boutons visibles, de haut en bas et en boucle.
    /// </summary>
    private static void LinkFocus(Control buttons)
    {
        var visible = buttons.GetChildren().OfType<NPauseMenuButton>().Where(b => b.Visible).ToList();
        for (int i = 0; i < visible.Count; i++)
        {
            visible[i].FocusNeighborTop = visible[(i - 1 + visible.Count) % visible.Count].GetPath();
            visible[i].FocusNeighborBottom = visible[(i + 1) % visible.Count].GetPath();
        }
    }
}
