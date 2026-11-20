using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace StsTools.Game;

/// <summary>
/// Lignes d'options de l'onglet Mod des options du jeu (<see cref="ModSettingsTab"/>) : réglages du GUI, des rapports
/// de plantage et de bug (<see cref="ModOptions"/>). Chaque ligne est une copie d'une ligne du jeu (case à cocher ou
/// choix ◀ ▶) dont on remplace le titre ; la case ou le choix copié garde la classe du jeu (Run Timer, FPS...), dont
/// les méthodes sont interceptées pour nos copies (repérées par une méta) : elles lisent et changent nos options,
/// jamais celles du jeu.
/// </summary>
public static class ModSettingsOptions
{
    private const string Meta = "ststools_option";

    private abstract record Option(string Id, string Title);

    /// <param name="CanUntick">faux : la case ne peut pas être décochée (elle se recoche)</param>
    private sealed record Toggle(string Id, string Title, Func<bool> Get, Action<bool> Set, Func<bool>? CanUntick = null)
        : Option(Id, Title);

    private sealed record Choice(string Id, string Title, string[] Labels, Func<int> Get, Action<int> Set) : Option(Id, Title);

    private sealed record Text(string Id, string Title, string Placeholder, Func<string> Get, Action<string> Set) : Option(Id, Title);

    private static readonly Dictionary<string, Option> Options = new();

    /// <summary>Sections d'options, dans l'ordre de l'onglet : titre, puis lignes.</summary>
    private static List<(string Section, List<Option> Options)> Sections()
    {
        static Toggle Feature(string id, string title, Func<bool> get, Action<SavedSettings, bool> set) =>
            new(id, title, get, v => ModOptions.Set(s => set(s, v)));
        // Deck, Game et Combat sont toujours proposés : au moins un reste visible
        static bool OtherAlwaysShown(string tab) =>
            new[] { "Deck", "Game", "Combat" }.Any(t => t != tab && !ModOptions.IsTabHidden(t));

        var sections = new List<(string, List<Option>)>
        {
            ("GUI tabs", ModOptions.Tabs.Select(tab => (Option)new Toggle($"tab.{tab}", $"Show the {tab} tab",
                () => !ModOptions.IsTabHidden(tab),
                v => ModOptions.Set(s =>
                {
                    s.HiddenTabs.Remove(tab);
                    if (!v)
                        s.HiddenTabs.Add(tab);
                }),
                tab is "Deck" or "Game" or "Combat" ? () => OtherAlwaysShown(tab) : null)).ToList()),
            ("Opening the GUI", [
                Feature("open.start", "Open the GUI when a run starts", () => ModOptions.OpenOnRunStart,
                    (s, v) => s.OpenOnRunStart = v),
                new Choice("open.tab", "Tab", ["Last used", .. ModOptions.Tabs],
                    () => ModOptions.OpenTab is { } t ? Array.IndexOf(ModOptions.Tabs, t) + 1 : 0,
                    i => ModOptions.Set(s => s.OpenTab = i == 0 ? null : ModOptions.Tabs[i - 1])),
                new Choice("open.panel", "Panel", ["As left", "Expanded", "Collapsed"],
                    () => Math.Max(0, Array.IndexOf(PanelValues, ModOptions.OpenPanel)),
                    i => ModOptions.Set(s => s.OpenPanel = PanelValues[i])),
            ]),
            ("While the GUI is open", [
                Feature("feature.cards", "Card and potion previews", () => ModOptions.CardPreview, (s, v) => s.CardPreview = v),
                Feature("feature.skulls", "Lethal warnings (skulls)", () => ModOptions.Skulls, (s, v) => s.Skulls = v),
                Feature("feature.relics", "Relic stats in tooltips", () => ModOptions.RelicStats, (s, v) => s.RelicStats = v),
                Feature("feature.rooms", "Map room previews", () => ModOptions.RoomPreview, (s, v) => s.RoomPreview = v),
            ]),
        };
        // Sans webhook (mod compilé sans), rien n'est envoyé : pas d'options
        if (CrashUpload.Available)
            sections.Add(("Crash reports", [
                new Choice("crash", "Send crash reports", ["Ask", "Always", "Never"],
                    () => Math.Max(0, Array.IndexOf(CrashValues, ModOptions.CrashReports)),
                    i => ModOptions.Set(s => s.CrashReports = CrashValues[i])),
            ]));
        if (BugReportDialog.Available)
            sections.Add(("Bug reports", [
                new Text("bug.name", "Default name", "Discord or Steam name", () => ModOptions.BugReportName ?? "",
                    v => ModOptions.Set(s => s.BugReportName = v.Trim())),
                Feature("bug.attach", "Attach logs and run save by default", () => ModOptions.BugReportAttach,
                    (s, v) => s.BugReportAttach = v),
            ]));
        return sections;
    }

    private static readonly string[] PanelValues = ["last", "expanded", "collapsed"];
    private static readonly string[] CrashValues = ["ask", "always", "never"];

    /// <summary>
    /// Ajoute les sections d'options au panneau, chaque titre fait par <paramref name="sectionTitle"/>. Rien si les
    /// lignes modèles du jeu sont introuvables (les touches restent).
    /// </summary>
    public static void AddTo(NSettingsTabManager manager, NSettingsPanel panel, Func<string, Control?> sectionTitle)
    {
        var root = (Node?)manager.GetParent() ?? manager;
        // De préférence des lignes simples du panneau General (Run Timer, Screenshake), sinon la première trouvée
        var tickRow = ModelRow<NSettingsTickbox>(root, "NRunTimerTickbox", out var tickbox);
        var choiceRow = ModelRow<NPaginator>(root, "NScreenshakePaginator", out var paginator);
        if (tickRow == null || tickbox == null)
        {
            Log.Warn($"[{ModEntry.Id}] no settings tickbox to copy: mod options not shown");
            return;
        }
        PatchModels(tickbox.GetType(), paginator?.GetType());
        // Ligne entière qui affiche l'infobulle du réglage du jeu (Run Timer…) : pas pour nos copies
        foreach (var row in new[] { tickRow, choiceRow })
            if (row != null && row.GetType().Name.Contains("HoverTip"))
                PatchHoverTip(row.GetType());
        foreach (var (section, options) in Sections())
        {
            if (sectionTitle(section) is { } title)
                panel.Content.AddChild(title);
            foreach (var option in options)
            {
                Options[option.Id] = option;
                var row = option switch
                {
                    Toggle => CopyRow(tickRow, tickbox, option),
                    Choice when choiceRow != null && paginator != null => CopyRow(choiceRow, paginator, option),
                    Text => TextRow(tickRow, tickbox, (Text)option),
                    _ => null,
                };
                if (row != null)
                    panel.Content.AddChild(row);
            }
        }
    }

    /// <summary>Première ligne du jeu contenant un <typeparamref name="T"/> : enfant direct du contenu d'un panneau.</summary>
    private static Control? ModelRow<T>(Node root, string preferred, out T? widget) where T : Control
    {
        widget = Find<T>(root, n => n.GetType().Name == preferred) ?? Find<T>(root, _ => true);
        Node? node = widget;
        while (node != null && node.GetParent() is { } parent && !(parent is VBoxContainer && parent.GetParent() is NSettingsPanel))
            node = parent;
        return node?.GetParent() is VBoxContainer ? node as Control : null;
    }

    private static T? Find<T>(Node node, Func<Node, bool> accept) where T : class
    {
        foreach (var child in node.GetChildren())
        {
            if (child is T found && !child.HasMeta(Meta) && accept(child))
                return found;
            if (Find<T>(child, accept) is { } deeper)
                return deeper;
        }
        return null;
    }

    /// <summary>Copie d'une ligne : notre titre, sans les infobulles du jeu, le contrôle copié marqué comme le nôtre.</summary>
    private static Control CopyRow(Control model, Control widget, Option option)
    {
        var row = (Control)model.Duplicate();
        row.Name = "StsToolsOption_" + option.Id.Replace('.', '_');
        var copy = row.GetNode<Control>(model.GetPathTo(widget));
        copy.SetMeta(Meta, option.Id);
        // Matériaux propres (éclairage au survol) : sinon la case du jeu s'allumerait avec la nôtre
        foreach (var item in Descendants(copy).OfType<CanvasItem>())
            if (item.Material is ShaderMaterial material)
                item.Material = (Material)material.Duplicate();
        RemoveHoverTips(row);
        SetTitle(row, copy, option.Title);
        return row;
    }

    /// <summary>Ligne de texte : une ligne de case à cocher dont la case est remplacée par un champ de saisie.</summary>
    private static Control TextRow(Control model, Control widget, Text option)
    {
        var row = (Control)model.Duplicate();
        row.Name = "StsToolsOption_" + option.Id.Replace('.', '_');
        var old = row.GetNode<Control>(model.GetPathTo(widget));
        var edit = new LineEdit
        {
            Text = option.Get(),
            PlaceholderText = option.Placeholder,
            CustomMinimumSize = new Vector2(420, 52),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            SizeFlagsHorizontal = old.SizeFlagsHorizontal,
            FocusMode = Control.FocusModeEnum.All,
        };
        edit.AddThemeFontSizeOverride("font_size", 26);
        edit.TextChanged += text => option.Set(text);
        // Échap ou Entrée : on quitte le champ (sinon il garde le focus et Échap ne ferme plus les options)
        edit.TextSubmitted += _ => edit.ReleaseFocus();
        edit.GuiInput += ev =>
        {
            if (ev.IsActionPressed("ui_cancel"))
            {
                edit.ReleaseFocus();
                edit.AcceptEvent();
            }
        };
        old.AddSibling(edit);
        old.GetParent().RemoveChild(old);
        old.QueueFree();
        RemoveHoverTips(row);
        SetTitle(row, edit, option.Title);
        return row;
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            yield return child;
            foreach (var deeper in Descendants(child))
                yield return deeper;
        }
    }

    private static void RemoveHoverTips(Node row)
    {
        if (row.GetType().Name.Contains("HoverTip"))
            row.SetMeta(Meta, "row");
        foreach (var tip in Descendants(row).Where(n => n.GetType().Name.Contains("HoverTip")).ToList())
        {
            tip.GetParent().RemoveChild(tip);
            tip.QueueFree();
        }
    }

    /// <summary>Premier texte de la ligne hors du contrôle : notre titre, sans traduction automatique.</summary>
    private static void SetTitle(Control row, Control widget, string title)
    {
        var label = Descendants(row).FirstOrDefault(n => n is Label or RichTextLabel && n != widget && !widget.IsAncestorOf(n));
        switch (label)
        {
            case MegaLabel mega:
                mega.AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled;
                mega.SetTextAutoSize(title);
                break;
            case Label plain:
                plain.AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled;
                plain.Text = title;
                break;
            case RichTextLabel rich:
                rich.AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled;
                rich.Text = title;
                break;
        }
    }

    // ---- Contrôles copiés : méthodes du jeu interceptées pour les nôtres ----

    private static readonly HashSet<MethodBase> Patched = [];

    private static void PatchModels(Type tickbox, Type? paginator)
    {
        if (ModEntry.Harmony is not { } harmony)
            return;
        void Patch(Type type, string name, string prefix)
        {
            if (AccessTools.Method(type, name) is not { } method || !Patched.Add(method))
                return;
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(ModSettingsOptions), prefix));
        }
        Patch(tickbox, "_Ready", nameof(TickboxReady));
        Patch(tickbox, "OnTick", nameof(TickboxTick));
        Patch(tickbox, "OnUntick", nameof(TickboxUntick));
        if (paginator == null)
            return;
        Patch(paginator, "_Ready", nameof(PaginatorReady));
        Patch(paginator, "OnIndexChanged", nameof(PaginatorChanged));
    }

    private static void PatchHoverTip(Type type)
    {
        if (ModEntry.Harmony is { } harmony && AccessTools.Method(type, "_Ready") is { } method && Patched.Add(method))
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(ModSettingsOptions), nameof(HoverTipReady)));
    }

    /// <summary>Nos lignes copiées d'une ligne-infobulle : rien au survol.</summary>
    public static bool HoverTipReady(Control __instance) => !__instance.HasMeta(Meta);

    private static T? Ours<T>(Node node) where T : Option =>
        node.HasMeta(Meta) && Options.TryGetValue(node.GetMeta(Meta).AsString(), out var option) ? option as T : null;

    private static readonly MethodInfo ConnectTickbox = AccessTools.Method(typeof(NTickbox), "ConnectSignals");
    private static readonly MethodInfo ConnectPaginator = AccessTools.Method(typeof(NPaginator), "ConnectSignals");
    private static readonly AccessTools.FieldRef<NPaginator, List<string>> PaginatorOptions =
        AccessTools.FieldRefAccess<NPaginator, List<string>>("_options");
    private static readonly AccessTools.FieldRef<NPaginator, int> PaginatorIndex =
        AccessTools.FieldRefAccess<NPaginator, int>("_currentIndex");
    private static readonly AccessTools.FieldRef<NPaginator, MegaLabel> PaginatorLabel =
        AccessTools.FieldRefAccess<NPaginator, MegaLabel>("_label");

    public static bool TickboxReady(NTickbox __instance)
    {
        if (Ours<Toggle>(__instance) is not { } option)
            return true;
        try
        {
            ConnectTickbox.Invoke(__instance, null);
            __instance.IsTicked = option.Get();
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] option {option.Id}: {e.Message}");
        }
        return false;
    }

    public static bool TickboxTick(NTickbox __instance)
    {
        if (Ours<Toggle>(__instance) is not { } option)
            return true;
        option.Set(true);
        return false;
    }

    public static bool TickboxUntick(NTickbox __instance)
    {
        if (Ours<Toggle>(__instance) is not { } option)
            return true;
        if (option.CanUntick?.Invoke() == false)
        {
            // Dernier onglet toujours proposé : il reste visible
            __instance.IsTicked = true;
            return false;
        }
        option.Set(false);
        return false;
    }

    public static bool PaginatorReady(NPaginator __instance)
    {
        if (Ours<Choice>(__instance) is not { } option)
            return true;
        try
        {
            ConnectPaginator.Invoke(__instance, null);
            var options = PaginatorOptions(__instance);
            options.Clear();
            options.AddRange(option.Labels);
            int index = Math.Clamp(option.Get(), 0, option.Labels.Length - 1);
            PaginatorIndex(__instance) = index;
            PaginatorLabel(__instance).SetTextAutoSize(option.Labels[index]);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] option {option.Id}: {e.Message}");
        }
        return false;
    }

    public static bool PaginatorChanged(NPaginator __instance, int __0)
    {
        if (Ours<Choice>(__instance) is not { } option)
            return true;
        if (__0 >= 0 && __0 < option.Labels.Length)
        {
            PaginatorLabel(__instance).SetTextAutoSize(option.Labels[__0]);
            option.Set(__0);
        }
        return false;
    }
}
