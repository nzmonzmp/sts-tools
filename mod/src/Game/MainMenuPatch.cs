using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace StsTools.Game;

/// <summary>Ajoute un bouton « Stats » au menu principal, sous « Compendium ».</summary>
[EarlyPatch]
[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
public static class MainMenuPatch
{
    private const string ButtonName = "StsToolsButton";
    private const string ButtonText = "Stats";
    private const string BugButtonName = "StsToolsBugButton";

    // Avant _Ready : le bouton profite ainsi de l'animation de survol branchée par le menu sur tous ses boutons
    public static void Prefix(NMainMenu __instance)
    {
        try
        {
            var buttons = __instance.GetNode<Control>("MainMenuTextButtons");
            if (buttons.HasNode(ButtonName))
                return;
            // Copie du bouton Compendium pour garder le style du jeu
            var model = buttons.GetNode<NMainMenuTextButton>("CompendiumButton");
            var button = (NMainMenuTextButton)model.Duplicate();
            button.Name = ButtonName;
            buttons.AddChild(button);
            buttons.MoveChild(button, model.GetIndex() + 1);
            // « Report a bug » sous « Stats », seulement si le mod sait envoyer les rapports
            if (BugReportDialog.Available)
            {
                var bug = (NMainMenuTextButton)model.Duplicate();
                bug.Name = BugButtonName;
                buttons.AddChild(bug);
                buttons.MoveChild(bug, button.GetIndex() + 1);
            }
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] cannot add the main menu button: {e}");
        }
    }

    public static void Postfix(NMainMenu __instance)
    {
        ModEntry.ApplyLatePatches();
        BugReport.WatchFrames();
        BugReport.ShowNotice(__instance);
        try
        {
            var button = __instance.GetNodeOrNull<NMainMenuTextButton>("MainMenuTextButtons/" + ButtonName);
            if (button == null)
                return;
            button.Visible = true;
            button.Enable();
            button.GetChild<Label>(0).Text = ButtonText;
            button.Connect(NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ => StatsScreen.Open(__instance)));
            if (__instance.GetNodeOrNull<NMainMenuTextButton>("MainMenuTextButtons/" + BugButtonName) is { } bug)
            {
                bug.Visible = true;
                bug.Enable();
                bug.GetChild<Label>(0).Text = BugReportDialog.ButtonText;
                bug.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => BugReportDialog.Open()));
            }
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] cannot set up the main menu button: {e}");
        }
    }
}
