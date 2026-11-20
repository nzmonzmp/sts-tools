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
    /// Bouton de la taille de ceux du jeu, avec l'icône du mod (le jeu n'a pas d'icône « statistiques »). Le conteneur
    /// de droite (HBoxContainer) le range avant la carte.
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
            CustomMinimumSize = new Vector2(IconSize, IconSize),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            PivotOffset = new Vector2(IconSize / 2, IconSize / 2),
        };
        icon.SetAnchorsPreset(Control.LayoutPreset.Center);
        icon.OffsetLeft = icon.OffsetTop = -IconSize / 2;
        icon.OffsetRight = icon.OffsetBottom = IconSize / 2;
        icon.Draw += () => DrawIcon(icon);
        button.AddChild(icon);

        // Même retour au survol que les boutons du jeu : plus clair et un peu plus grand
        icon.Modulate = new Color(0.9f, 0.9f, 0.9f);
        button.MouseEntered += () => { icon.Modulate = new Color(1.15f, 1.15f, 1.15f); icon.Scale = Vector2.One * 1.1f; };
        button.MouseExited += () => { icon.Modulate = new Color(0.9f, 0.9f, 0.9f); icon.Scale = Vector2.One; };
        button.Pressed += RunPanel.Toggle;

        row.AddChild(button);
        row.MoveChild(button, map.GetIndex());
    }

    private const float IconSize = 46;

    private static readonly Color Outline = new(0.1f, 0.06f, 0.02f);
    private static readonly Color BarColor = new(0.91f, 0.86f, 0.74f);
    private static readonly Color LineColor = new(1f, 0.62f, 0.12f);
    private static readonly Color DotColor = new(1f, 0.62f, 0.12f);

    /// <summary>
    /// Même dessin que l'icône du serveur Discord (<c>assets/icon.png</c>) sans son fond : des barres et une courbe de
    /// suivi qui monte, avec ses points ; contour sombre comme les icônes de la barre du haut du jeu.
    /// </summary>
    private static void DrawIcon(Control icon)
    {
        const float s = IconSize;
        // Coordonnées de l'icône (0..1) ramenées à la zone utile x 0.15..0.85, y 0.20..0.82
        static Vector2 P(float x, float y) => new((x - 0.15f) / 0.7f * s, (y - 0.17f) / 0.7f * s);
        const float baseY = 0.76f;
        foreach (var (cx, h) in new[] { (0.27f, 0.20f), (0.42f, 0.30f), (0.58f, 0.26f), (0.73f, 0.42f) })
            icon.DrawStyleBox(Box(BarColor, 1, 2), new Rect2(P(cx - 0.065f, baseY - h), P(cx + 0.065f, baseY) - P(cx - 0.065f, baseY - h)));
        icon.DrawStyleBox(Box(BarColor, 1, 2), new Rect2(P(0.17f, baseY), P(0.83f, baseY + 0.07f) - P(0.17f, baseY)));
        Vector2[] points = [P(0.22f, 0.58f), P(0.40f, 0.46f), P(0.58f, 0.52f), P(0.78f, 0.26f)];
        icon.DrawPolyline(points, Outline, 6.5f, antialiased: true);
        icon.DrawPolyline(points, LineColor, 4.5f, antialiased: true);
        const float r = 4.5f;
        foreach (var p in points)
            icon.DrawStyleBox(Box(DotColor, 1, r), new Rect2(p - new Vector2(r, r), new Vector2(2 * r, 2 * r)));
    }

    private static StyleBoxFlat Box(Color color, int border, float radius) => new()
    {
        BgColor = color,
        BorderColor = Outline,
        BorderWidthLeft = border, BorderWidthTop = border, BorderWidthRight = border, BorderWidthBottom = border,
        CornerRadiusTopLeft = (int)radius, CornerRadiusTopRight = (int)radius,
        CornerRadiusBottomLeft = (int)radius, CornerRadiusBottomRight = (int)radius,
        AntiAliasing = true,
    };
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
