using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Runs;

namespace StsTools.Game;

/// <summary>
/// En partie : ajoute à la barre du haut un bouton qui ouvre le panneau de stats (<see cref="RunPanel"/>),
/// juste avant le bouton de la carte, et crée le panneau pour le joueur local.
/// </summary>
[HarmonyPatch(typeof(NTopBar), nameof(NTopBar.Initialize))]
public static class TopBarPatch
{
    private const string ButtonName = "StsToolsRunPanelButton";

    public static void Postfix(NTopBar __instance, IRunState runState)
    {
        try
        {
            var player = LocalContext.GetMe(runState);
            if (player == null)
                return;
            CardRunStats.Track(runState, player);
            BugReport.RunStarted(runState);
            AddButton(__instance);
            RunPanel.Attach(__instance, player);
            MapPaths.Install();
            MapRoomPreview.Install();
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] cannot set up the top bar: {e}");
        }
    }

    /// <summary>
    /// Bouton de la taille de ceux du jeu, avec une petite icône en barres dorées (le jeu n'a pas d'icône
    /// « statistiques »). Le conteneur de droite (HBoxContainer) le range avant la carte.
    /// </summary>
    private static void AddButton(NTopBar topBar)
    {
        var map = topBar.Map;
        var row = map.GetParent();
        row.GetNodeOrNull(ButtonName)?.Free();

        float side = map.Size.Y > 0 ? map.Size.Y : 80;
        var button = new Button
        {
            Name = ButtonName,
            CustomMinimumSize = new Vector2(side, side),
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = $"GUI ({ModHotkeys.RunStats.Display})\nShow or hide relics ({ModHotkeys.RelicBar.Display})",
        };
        foreach (var state in new[] { "normal", "hover", "pressed", "focus", "disabled" })
            button.AddThemeStyleboxOverride(state, new StyleBoxEmpty());

        var icon = new Control
        {
            CustomMinimumSize = new Vector2(40, 40),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            PivotOffset = new Vector2(20, 20),
        };
        icon.SetAnchorsPreset(Control.LayoutPreset.Center);
        icon.OffsetLeft = icon.OffsetTop = -20;
        icon.OffsetRight = icon.OffsetBottom = 20;
        float[] heights = [0.45f, 0.75f, 1f];
        for (int i = 0; i < heights.Length; i++)
        {
            var bar = new Panel
            {
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Position = new Vector2(2 + i * 13, 40 * (1 - heights[i])),
                Size = new Vector2(10, 40 * heights[i]),
            };
            bar.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = StsColors.gold,
                BorderColor = new Color(0.1f, 0.06f, 0.02f),
                BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
                CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2,
            });
            icon.AddChild(bar);
        }
        button.AddChild(icon);

        // Même retour au survol que les boutons du jeu : plus clair et un peu plus grand
        icon.Modulate = new Color(0.9f, 0.9f, 0.9f);
        button.MouseEntered += () => { icon.Modulate = new Color(1.15f, 1.15f, 1.15f); icon.Scale = Vector2.One * 1.1f; };
        button.MouseExited += () => { icon.Modulate = new Color(0.9f, 0.9f, 0.9f); icon.Scale = Vector2.One; };
        button.Pressed += RunPanel.Toggle;

        row.AddChild(button);
        row.MoveChild(button, map.GetIndex());
    }
}

/// <summary>Ferme le panneau de stats quand le menu Échap (menu pause) s'ouvre.</summary>
[HarmonyPatch(typeof(NCapstoneSubmenuStack), nameof(NCapstoneSubmenuStack.ShowScreen))]
public static class PauseMenuOpenPatch
{
    public static void Postfix(CapstoneSubmenuType type)
    {
        if (type == CapstoneSubmenuType.PauseMenu)
            RunPanel.Close();
    }
}
