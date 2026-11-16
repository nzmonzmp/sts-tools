using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace StsTools.Game;

/// <summary>
/// Curseur loupe du jeu (forme Help) : par défaut son point actif est sur le bord de l'anneau ; sur les noms
/// survolables du mod, on le place au centre du verre, pour que la loupe soit centrée sur ce qu'on regarde.
/// </summary>
public static class InspectCursor
{
    private const string ImagePath = "res://images/packed/common_ui/cursor_inspect.png";

    /// <summary>Point actif du curseur loupe dans le jeu (NCursorManager._inspectHotSpot) : bord de l'anneau.</summary>
    private static readonly Vector2 GameHotSpot = new(12, 12);

    /// <summary>Centre du verre dans l'image 64 × 64 du curseur loupe (anneau doré de x = 5 à 40, y = 6 à 41).</summary>
    private static readonly Vector2 LensCenter = new(22, 23);

    public static void CenterOnLens() => SetHotSpot(LensCenter);

    public static void Restore() => SetHotSpot(GameHotSpot);

    /// <summary>Réapplique l'image du curseur loupe du jeu avec un autre point actif.</summary>
    private static void SetHotSpot(Vector2 hotSpot)
    {
        try
        {
            if (ResourceLoader.Load<Image>(ImagePath) is { } image)
                Input.SetCustomMouseCursor(image, Input.CursorShape.Help, hotSpot);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot set the inspect cursor: {e.Message}");
        }
    }
}
