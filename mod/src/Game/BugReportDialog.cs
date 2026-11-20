using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;

namespace StsTools.Game;

/// <summary>
/// « Report a bug » (menu principal, menu pause) : le joueur décrit le bug qu'il a vu, donne son nom s'il veut être
/// recontacté, et peut joindre les logs et la partie en cours. Le rapport part dans le salon Discord des rapports de
/// bug par un webhook (BugWebhook dans StsTools.local.props, mis dans la DLL à la compilation) : le joueur n'a pas
/// besoin de voir ce salon, que seuls ceux qui ont les droits lisent. Sans webhook, le bouton n'apparaît pas.
/// Pendant que la fenêtre est ouverte, le jeu et les touches du mod ne reçoivent pas le clavier (on y écrit).
/// </summary>
public static class BugReportDialog
{
    public const string ButtonText = "Report a bug";
    private const string FontPath = "res://themes/kreon_bold_glyph_space_two.tres";

    private static string? Webhook => CrashUpload.WebhookNamed("BugWebhook");

    /// <summary>Le mod sait envoyer un rapport de bug (webhook présent).</summary>
    public static bool Available => CrashUpload.Relay != null || Webhook != null;

    private static CanvasLayer? _layer;
    private static Action? _close;

    /// <summary>Un rapport est en train de partir : pas de deuxième envoi.</summary>
    private static bool _sending;

    /// <summary>La fenêtre est ouverte : le clavier lui est réservé.</summary>
    public static bool IsOpen => _layer != null && GodotObject.IsInstanceValid(_layer);

    public static void Open()
    {
        if (IsOpen || !Available || Engine.GetMainLoop() is not SceneTree tree)
            return;
        try
        {
            Build(tree);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] cannot open the bug report window: {e}");
            Close();
        }
    }

    private static void Close()
    {
        if (IsOpen)
            _layer!.QueueFree();
        _layer = null;
        _close = null;
    }

    private static void Build(SceneTree tree)
    {
        _layer = new CanvasLayer { Layer = 100, Name = "StsToolsBugReportDialog" };
        _close = Close;
        var theme = new Theme { DefaultFontSize = 20 };
        if (ResourceLoader.Exists(FontPath) && ResourceLoader.Load<Font>(FontPath) is { } font)
            theme.DefaultFont = font;

        // Fond assombri sur tout l'écran : rien derrière ne reçoit la souris
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = Control.MouseFilterEnum.Stop, Theme = theme };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(dim);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        dim.AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(720, 0) };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.05f, 0.08f, 0.97f),
            BorderColor = new Color(StsColors.gold, 0.6f),
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 20, ContentMarginRight = 20, ContentMarginTop = 14, ContentMarginBottom = 16,
        });
        center.AddChild(panel);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        panel.AddChild(column);

        column.AddChild(Label("Report a bug", 26, StsColors.gold));
        column.AddChild(Label("What happened? What were you doing? What did you expect?", 18, StsColors.cream));
        var description = new TextEdit
        {
            CustomMinimumSize = new Vector2(0, 220),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            PlaceholderText = "Describe the bug…",
        };
        column.AddChild(description);

        column.AddChild(Label("Your name, to be contacted (optional)", 18, StsColors.cream));
        var name = new LineEdit { Text = SavedSettings.Load().BugReportName ?? "", PlaceholderText = "Discord or Steam name" };
        column.AddChild(name);

        var attach = new CheckBox { Text = "Attach the game logs and the current run save", ButtonPressed = ModOptions.BugReportAttach };
        column.AddChild(attach);

        var status = Label("", 18, StsColors.cream);
        status.Visible = false;
        column.AddChild(status);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 10);
        column.AddChild(buttons);
        var cancel = new Button { Text = "Cancel" };
        cancel.Pressed += Close;
        buttons.AddChild(cancel);
        var send = new Button { Text = "Send" };
        buttons.AddChild(send);

        send.Pressed += () =>
        {
            var text = description.Text.Trim();
            status.Visible = true;
            if (text.Length == 0)
            {
                status.Text = "Please describe the bug first.";
                status.AddThemeColorOverride("font_color", StsColors.red);
                return;
            }
            // Envoi déjà en cours (appuis répétés sur Send) : rien de plus
            if (_sending)
                return;
            // Pas de spam : délai entre deux rapports, nombre par heure, même texte déjà envoyé
            if (ReportLimits.BugRefusal(text) is { } refusal)
            {
                status.Text = refusal;
                status.AddThemeColorOverride("font_color", StsColors.red);
                return;
            }
            var settings = SavedSettings.Load();
            settings.BugReportName = name.Text.Trim();
            settings.Save();
            send.Disabled = cancel.Disabled = description.Editable = name.Editable = false;
            attach.Disabled = true;
            status.Text = "Sending…";
            status.AddThemeColorOverride("font_color", StsColors.cream);
            Send(text, name.Text.Trim(), attach.ButtonPressed, ok =>
            {
                if (!GodotObject.IsInstanceValid(status))
                    return;
                if (ok)
                {
                    status.Text = "Bug report sent, thank you!";
                    tree.CreateTimer(2).Timeout += Close;
                }
                else
                {
                    status.Text = "Could not send the report (no connection?). Try again later.";
                    status.AddThemeColorOverride("font_color", StsColors.red);
                    send.Disabled = cancel.Disabled = false;
                    description.Editable = name.Editable = true;
                    attach.Disabled = false;
                }
            });
        };

        tree.Root.AddChild(_layer);
        description.GrabFocus();
    }

    private static Label Label(string text, int size, Color color)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    /// <summary>
    /// Envoi en arrière-plan : un message (nom, contexte de la partie, description) et un zip avec la description
    /// complète et, si demandé, le journal de la session, le log du jeu et les sauvegardes de la partie en cours.
    /// </summary>
    private static void Send(string description, string name, bool attach, Action<bool> done)
    {
        if ((CrashUpload.Relay ?? Webhook) is not { } url)
        {
            done(false);
            return;
        }
        bool relay = CrashUpload.Relay != null;
        var identity = CrashUpload.PlayerIdentity();
        var stamp = DateTime.Now;
        _sending = true;
        ReportLimits.BugStarted(description);
        var journalLines = CrashUpload.JournalLines(BugReport.JournalPath);
        var files = attach ? BugReport.CurrentFiles().ToList() : [];
        var player = CrashUpload.PlayerTag();
        var threadName = ReportLimits.ThreadName();
        BugReport.Note($"bug report written by the player ({description.Length} characters, files: {attach})");
        Task.Run(async () =>
        {
            bool ok = false;
            try
            {
                var descriptionFile = Path.Combine(Path.GetTempPath(), $"StsTools-bug-{stamp:yyyyMMdd-HHmmss}.txt");
                await File.WriteAllTextAsync(descriptionFile,
                    $"From: {(name.Length > 0 ? name : "anonymous")}\nPlayer: {player ?? "unknown"}\n\n{description}\n");
                files.Insert(0, ("description.txt", descriptionFile));
                var zip = CrashUpload.ZipFiles(files);
                var message = Message(description, name, journalLines);
                var zipName = $"bug-{stamp:yyyyMMdd-HHmmss}.zip";
                ok = relay
                    ? await ReportLimits.PostRelay(url, "bug", message, zip, zipName, identity, null)
                    : (await ReportLimits.Post("BugWebhook", url, message, zip, zipName, threadName)).Ok;
                File.Delete(descriptionFile);
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] cannot send the bug report: {e.Message}");
            }
            BugReport.Note($"bug report {(ok ? "sent" : "not sent")}");
            if (!ok)
                ReportLimits.BugFailed(description);
            Callable.From(() =>
            {
                _sending = false;
                done(ok);
            }).CallDeferred();
        });
    }

    /// <summary>Message Discord : qui, le contexte (versions, partie, combat), puis la description, en 2000 caractères au plus.</summary>
    private static string Message(string description, string name, string[] journalLines)
    {
        var text = new StringBuilder();
        // Le nom tel quel, sans mise en forme Discord
        var who = name.Length > 0 ? name.Replace("`", "'") : "anonymous";
        text.AppendLine($"**Bug report** from `{CrashUpload.Clip(who, 60)}`" + (CrashUpload.PlayerTag() is { } player ? $", {player}" : ""));
        text.Append(CrashUpload.Context(journalLines));
        int room = 1990 - text.Length - 6;
        var quoted = description.Length > room ? description[..Math.Max(0, room - 40)] + "… (full text in description.txt)" : description;
        text.Append(">>> ").Append(quoted);
        var message = text.ToString();
        return message.Length > 2000 ? message[..2000] : message;
    }

    /// <summary>
    /// Fenêtre ouverte : le jeu ne traite pas le clavier (on écrit dans la fenêtre) ; Échap la ferme. La souris passe
    /// par l'interface, que le fond de la fenêtre arrête.
    /// </summary>
    [HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
    public static class InputPatch
    {
        public static bool Prefix(NGame __instance, InputEvent inputEvent)
        {
            if (!IsOpen)
                return true;
            if (inputEvent is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                __instance.GetViewport()?.SetInputAsHandled();
                Callable.From(() => _close?.Invoke()).CallDeferred();
            }
            return false;
        }
    }
}
