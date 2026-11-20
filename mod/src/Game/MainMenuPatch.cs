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
        // Options du menu principal prêtes avant les patchs : onglet Mod ajouté maintenant
        ModSettingsTab.AddToExisting(__instance.GetTree().Root);
        ModGuide.ShowOnFirstLaunch();
        Recenter(__instance);
        DiscordButton.Add(__instance);
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

    /// <summary>Haut et bas du logo du jeu à sa place d'origine (px d'écran 1920 × 1080, mesurés sur une capture).</summary>
    private const float LogoTop = 230, LogoBottom = 580;

    /// <summary>
    /// Avec les boutons du mod (Stats, Report a bug), la liste des boutons descendait presque au bas de l'écran. L'espace
    /// libre en hauteur (écran moins logo moins liste) est réparti : 3/4 au-dessus du logo « Slay the Spire II », 1/8
    /// entre le logo et la liste, 1/8 sous la liste. Le jeu garde la liste centrée sur ce point quand des
    /// boutons apparaissent (Continue, Abandon Run) : seules les marges de ses ancres changent.
    /// </summary>
    private static void Recenter(NMainMenu menu)
    {
        const string done = "ststools_recentered";
        try
        {
            if (menu.HasMeta(done))
                return;
            menu.SetMeta(done, true);
            if (menu.GetNodeOrNull<Control>("MainMenuTextButtons") is not { } buttons)
                return;
            float height = menu.GetViewportRect().Size.Y;
            float listHeight = buttons.GetCombinedMinimumSize().Y;
            float free = Math.Max(0, height - (LogoBottom - LogoTop) - listHeight);
            float logoTop = free * 3 / 4;
            if (menu.GetNodeOrNull("MainMenuBg")?.FindChild("Logo", true, false) is Node2D logo)
                logo.Position -= new Vector2(0, LogoTop - logoTop);
            float listTop = logoTop + (LogoBottom - LogoTop) + free / 8;
            // Centre d'après les ancres et leurs marges (la mise en page de la liste n'est pas encore faite ici)
            float center = ((buttons.AnchorTop + buttons.AnchorBottom) * height + buttons.OffsetTop + buttons.OffsetBottom) / 2;
            float shift = listTop + listHeight / 2 - center;
            buttons.OffsetTop += shift;
            buttons.OffsetBottom += shift;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot recenter the main menu: {e.Message}");
        }
    }
}
