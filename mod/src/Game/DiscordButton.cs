using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace StsTools.Game;

/// <summary>
/// Menu principal : logo Discord dans le coin en haut à droite, à droite du bouton des notes de version (même taille,
/// même écart) ; un clic ouvre l'invitation du serveur du mod dans le navigateur.
/// </summary>
public static class DiscordButton
{
    public const string Invite = "https://discord.gg/wEB5NVwZPX";

    private const string NodeName = "StsToolsDiscordButton";
    private const string Shifted = "ststools_shifted";

    /// <summary>Logo Discord (Clyde), tracé officiel en 24 × 24.</summary>
    private const string Svg =
        """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path fill="#ffffff" d="M20.317 4.3698a19.7913 19.7913 0 00-4.8851-1.5152.0741.0741 0 00-.0785.0371c-.211.3753-.4447.8648-.6083 1.2495-1.8447-.2762-3.68-.2762-5.4868 0-.1636-.3933-.4058-.8742-.6177-1.2495a.077.077 0 00-.0785-.037 19.7363 19.7363 0 00-4.8852 1.515.0699.0699 0 00-.0321.0277C.5334 9.0458-.319 13.5799.0992 18.0578a.0824.0824 0 00.0312.0561c2.0528 1.5076 4.0413 2.4228 5.9929 3.0294a.0777.0777 0 00.0842-.0276c.4616-.6304.8731-1.2952 1.226-1.9942a.076.076 0 00-.0416-.1057c-.6528-.2476-1.2743-.5495-1.8722-.8923a.077.077 0 01-.0076-.1277c.1258-.0943.2517-.1923.3718-.2914a.0743.0743 0 01.0776-.0105c3.9278 1.7933 8.18 1.7933 12.0614 0a.0739.0739 0 01.0785.0095c.1202.099.246.1981.3728.2924a.077.077 0 01-.0066.1276 12.2986 12.2986 0 01-1.873.8914.0766.0766 0 00-.0407.1067c.3604.698.7719 1.3628 1.225 1.9932a.076.076 0 00.0842.0286c1.961-.6067 3.9495-1.5219 6.0023-3.0294a.077.077 0 00.0313-.0552c.5004-5.177-.8382-9.6739-3.5485-13.6604a.061.061 0 00-.0312-.0286zM8.02 15.3312c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9555-2.4189 2.157-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.9555 2.4189-2.1569 2.4189zm7.9748 0c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9554-2.4189 2.1569-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.946 2.4189-2.1568 2.4189Z"/></svg>""";

    /// <summary>Couleur des icônes du jeu au repos ; violet Discord au survol.</summary>
    private static readonly Color Idle = new(0.91f, 0.86f, 0.74f);
    private static readonly Color Hover = new(0.345f, 0.396f, 0.949f);

    private static ImageTexture? _texture;

    private static ImageTexture? Texture()
    {
        if (_texture != null)
            return _texture;
        var image = new Image();
        if (image.LoadSvgFromString(Svg, 4) != Error.Ok)
            return null;
        return _texture = ImageTexture.CreateFromImage(image);
    }

    public static void Add(NMainMenu menu)
    {
        try
        {
            if (menu.GetNodeOrNull<Control>("%PatchNotesButton") is not { } notes)
                return;
            var parent = notes.GetParent<Control>();
            parent.GetNodeOrNull(NodeName)?.Free();
            if (Texture() is not { } texture)
            {
                Log.Warn($"[{ModEntry.Id}] cannot load the Discord logo");
                return;
            }

            const float side = 64;
            var button = new Button
            {
                Name = NodeName,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = "Join the STS Tools Discord",
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            };
            foreach (var state in new[] { "normal", "hover", "pressed", "focus", "disabled" })
                button.AddThemeStyleboxOverride(state, new StyleBoxEmpty());
            // Dans le coin, à la place du bouton des notes ; celui-ci et la version du jeu (écrite juste à sa gauche)
            // passent d'un cran à gauche
            button.AnchorLeft = notes.AnchorLeft;
            button.AnchorRight = notes.AnchorRight;
            button.AnchorTop = notes.AnchorTop;
            button.AnchorBottom = notes.AnchorBottom;
            button.OffsetLeft = notes.OffsetLeft;
            button.OffsetRight = notes.OffsetRight;
            button.OffsetTop = notes.OffsetTop;
            button.OffsetBottom = notes.OffsetBottom;
            if (!notes.HasMeta(Shifted))
            {
                notes.SetMeta(Shifted, true);
                float width = notes.OffsetRight - notes.OffsetLeft;
                float gap = notes.AnchorRight >= 1 ? -notes.OffsetRight : 16;
                foreach (var control in new[] { notes, menu.GetNodeOrNull<Control>("%ReleaseInfo") ?? menu.GetNodeOrNull<Control>("ReleaseInfo") })
                    if (control != null)
                    {
                        control.OffsetLeft -= width + gap;
                        control.OffsetRight -= width + gap;
                    }
            }

            var holder = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            holder.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            button.AddChild(holder);
            TextureRect Logo(Color color, Vector2 offset)
            {
                var rect = new TextureRect
                {
                    Texture = texture,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    SelfModulate = color,
                };
                rect.SetAnchorsPreset(Control.LayoutPreset.Center);
                rect.OffsetLeft = rect.OffsetTop = -side / 2;
                rect.OffsetRight = rect.OffsetBottom = side / 2;
                rect.Position += offset;
                holder.AddChild(rect);
                return rect;
            }
            Logo(new Color(0, 0, 0, 0.6f), new Vector2(2, 3));
            var logo = Logo(Idle, Vector2.Zero);

            // Retour au survol des boutons du jeu : un peu plus grand ; et couleur de Discord
            // Agrandi autour de son centre (taille connue seulement une fois le bouton placé)
            button.MouseEntered += () =>
            {
                logo.SelfModulate = Hover;
                holder.PivotOffset = holder.Size / 2;
                holder.Scale = Vector2.One * 1.1f;
            };
            button.MouseExited += () => { logo.SelfModulate = Idle; holder.Scale = Vector2.One; };
            button.Pressed += () =>
            {
                BugReport.Note("discord button");
                OS.ShellOpen(Invite);
            };
            parent.AddChild(button);
            parent.MoveChild(button, notes.GetIndex() + 1);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot add the Discord button: {e.Message}");
        }
    }
}
