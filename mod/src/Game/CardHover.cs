using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
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
    private readonly Vector2 _cardSize;

    /// <summary>
    /// Tableau à droite de la carte (stats de la carte), masqué s'il est vide. Première ligne : en-têtes des
    /// colonnes ; première colonne : noms des lignes ; le reste : valeurs, alignées à droite.
    /// </summary>
    private readonly PanelContainer _info;
    private readonly GridContainer _infoRows;
    private const float InfoGap = 10;

    /// <summary>Centrer le curseur loupe sur le verre pendant le survol (l'écran Stats le fait déjà pour tout l'écran)</summary>
    private readonly bool _centerCursor;

    /// <param name="parent">nœud qui accueille la carte ; elle disparaît avec lui</param>
    /// <param name="centerCursor">
    /// centrer la loupe sur les noms survolés, puis rendre au jeu son curseur ; pour un affichage posé sur la
    /// partie, où le reste du jeu doit garder son curseur habituel
    /// </param>
    public CardHover(Node parent, float scale = 0.8f, bool centerCursor = false)
    {
        Scale = scale;
        _centerCursor = centerCursor;
        // TopLevel : la carte se place en coordonnées d'écran, hors de la mise en page du parent.
        // ZIndex : devant le reste de l'interface (barre du haut comprise, quand le parent est le panneau en partie)
        _cardSize = NCard.defaultSize * scale;
        _panel = new Control
        {
            Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, TopLevel = true, ZIndex = 100,
            Size = _cardSize,
        };
        parent.AddChild(_panel);

        _info = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _info.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.05f, 0.08f, 0.95f),
            BorderColor = new Color(StsColors.gold, 0.5f),
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 8, ContentMarginBottom = 8,
        });
        _infoRows = StatsTable.Create([]);
        _info.AddChild(_infoRows);
        _panel.AddChild(_info);
    }

    public float Scale { get; }

    public bool Visible => GodotObject.IsInstanceValid(_panel) && _panel.Visible;

    /// <summary>
    /// Fait afficher <paramref name="card"/> au survol de <paramref name="control"/> ; la carte suit la souris.
    /// </summary>
    /// <param name="info">tableau affiché à droite de la carte (en-têtes puis lignes), calculé à chaque survol</param>
    /// <param name="focusFallback">voir <see cref="KeyboardNav.Focusable"/> : au clavier, la carte suit aussi le focus</param>
    public void Attach(Control control, CardModel card, Func<IEnumerable<string[]>>? info = null, Control? focusFallback = null)
    {
        KeyboardNav.FocusableHover(control, () => Show(card, info?.Invoke()), Hide, focusFallback);
        control.MouseFilter = Control.MouseFilterEnum.Pass;
        control.MouseDefaultCursorShape = Control.CursorShape.Help;
        control.MouseEntered += () =>
        {
            if (_centerCursor)
                InspectCursor.CenterOnLens();
            Show(card, info?.Invoke());
        };
        control.MouseExited += () =>
        {
            if (_centerCursor)
                InspectCursor.Restore();
            Hide();
        };
        control.GuiInput += ev =>
        {
            if (ev is InputEventMouseMotion && Visible)
                Place();
        };
        // Le nom peut disparaître sous la souris (panneau redessiné, fermé) : la carte ne doit pas rester affichée
        // et le jeu doit retrouver son curseur
        control.TreeExiting += () =>
        {
            if (_centerCursor && Visible)
                InspectCursor.Restore();
            Hide();
        };
    }

    public void Show(CardModel card, IEnumerable<string[]>? info = null)
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
                _panel.AddChild(_card);
            }
            else
            {
                _card.Model = card;
            }
            _card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
            ShowInfo(info, _card);
            Place();
            _panel.Visible = true;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot show card {card.Id}: {e.Message}");
        }
    }

    /// <summary>
    /// Encadré de stats en haut à gauche, carte à sa droite ; le panneau englobe les deux pour rester dans l'écran.
    /// La carte est centrée sur sa position.
    /// </summary>
    private void ShowInfo(IEnumerable<string[]>? info, NCard card)
    {
        var rows = info?.ToList() ?? [];
        StatsTable.Fill(_infoRows, rows);
        _info.Visible = rows.Count > 0;
        if (rows.Count == 0)
        {
            card.Position = _cardSize / 2;
            _panel.Size = _cardSize;
            return;
        }
        _info.ResetSize();
        var infoSize = _info.GetCombinedMinimumSize();
        _info.Position = Vector2.Zero;
        _info.Size = infoSize;
        float cardLeft = infoSize.X + InfoGap;
        card.Position = new Vector2(cardLeft, 0) + _cardSize / 2;
        _panel.Size = new Vector2(cardLeft + _cardSize.X, Math.Max(_cardSize.Y, infoSize.Y));
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
        // Au clavier : contre l'élément qui a le focus
        var mouse = KeyboardNav.PointerPosition(panel);
        var x = mouse.X + gap;
        if (x + size.X > screen.X)
            x = Math.Max(0, mouse.X - size.X - gap);
        var y = Math.Clamp(mouse.Y - size.Y - gap, 0, Math.Max(0, screen.Y - size.Y));
        panel.GlobalPosition = new Vector2(x, y);
    }
}
