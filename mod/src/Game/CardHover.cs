using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace StsTools.Game;

/// <summary>
/// Carte du jeu (NCard) affichée près de la souris au survol d'un nom de carte. Utilisée par l'écran Stats
/// et par le panneau de stats en partie. Le nœud de la carte est créé au premier survol puis réutilisé.
/// </summary>
public sealed class CardHover
{
    private readonly Control _panel;
    private NCard? _card;

    /// <param name="parent">nœud qui accueille la carte ; elle disparaît avec lui</param>
    public CardHover(Node parent, float scale = 0.8f)
    {
        Scale = scale;
        // TopLevel : la carte se place en coordonnées d'écran, hors de la mise en page du parent.
        // ZIndex : devant le reste de l'interface (barre du haut comprise, quand le parent est le panneau en partie)
        _panel = new Control
        {
            Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, TopLevel = true, ZIndex = 100,
            Size = NCard.defaultSize * scale,
        };
        parent.AddChild(_panel);
    }

    public float Scale { get; }

    public bool Visible => GodotObject.IsInstanceValid(_panel) && _panel.Visible;

    /// <summary>
    /// Fait afficher <paramref name="card"/> au survol de <paramref name="control"/> ; la carte suit la souris.
    /// </summary>
    public void Attach(Control control, CardModel card)
    {
        control.MouseFilter = Control.MouseFilterEnum.Pass;
        control.MouseDefaultCursorShape = Control.CursorShape.Help;
        control.MouseEntered += () => Show(card);
        control.MouseExited += Hide;
        control.GuiInput += ev =>
        {
            if (ev is InputEventMouseMotion && Visible)
                Place();
        };
        // Le nom peut disparaître sous la souris (panneau redessiné, fermé) : la carte ne doit pas rester affichée
        control.TreeExiting += Hide;
    }

    public void Show(CardModel card)
    {
        try
        {
            if (_card == null || !GodotObject.IsInstanceValid(_card))
            {
                _card = NCard.Create(card);
                if (_card == null)
                    return;
                _card.MouseFilter = Control.MouseFilterEnum.Ignore;
                _card.Scale = Vector2.One * Scale;
                _card.Position = _panel.Size / 2;
                _panel.AddChild(_card);
            }
            else
            {
                _card.Model = card;
            }
            _card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
            Place();
            _panel.Visible = true;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot show card {card.Id}: {e.Message}");
        }
    }

    public void Hide()
    {
        if (GodotObject.IsInstanceValid(_panel))
            _panel.Visible = false;
    }

    public void Place() => PlaceNearMouse(_panel);

    /// <summary>
    /// Place <paramref name="panel"/> en haut à droite de la souris. Près du bord droit il passe à gauche de la
    /// souris, près du bord haut il descend juste assez pour rester dans l'écran.
    /// </summary>
    public static void PlaceNearMouse(Control panel)
    {
        const float gap = 12;
        var size = panel.Size;
        var screen = panel.GetViewportRect().Size;
        var mouse = panel.GetGlobalMousePosition();
        var x = mouse.X + gap;
        if (x + size.X > screen.X)
            x = Math.Max(0, mouse.X - size.X - gap);
        var y = Math.Clamp(mouse.Y - size.Y - gap, 0, Math.Max(0, screen.Y - size.Y));
        panel.GlobalPosition = new Vector2(x, y);
    }
}
