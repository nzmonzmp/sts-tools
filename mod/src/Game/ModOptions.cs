namespace StsTools.Game;

/// <summary>
/// Options du mod réglées dans les options du jeu (onglet Mod, <see cref="ModSettingsTab"/>), gardées dans les
/// réglages (<see cref="SavedSettings"/>). Lues une fois puis gardées en mémoire : plusieurs sont consultées à chaque
/// image. Changées seulement par <see cref="Set"/>, qui les sauvegarde et prévient le GUI.
/// </summary>
public static class ModOptions
{
    private static SavedSettings? _current;

    private static SavedSettings Current => _current ??= SavedSettings.Load();

    /// <summary>Onglets du GUI : titres, dans l'ordre du panneau (Support seulement à plusieurs).</summary>
    public static readonly string[] Tabs = ["Deck", "Game", "Combat", "Support", "Path finder"];

    public static bool OpenOnRunStart => Current.OpenOnRunStart;
    public static bool HelpInGui => Current.HelpInGui;
    public static bool HelpInStats => Current.HelpInStats;
    public static bool HelpInSettings => Current.HelpInSettings;

    /// <summary>Une option a changé (boutons « ? » de l'onglet Mod, par exemple).</summary>
    public static event Action? Changed;
    public static bool CardPreview => Current.CardPreview;
    public static bool Skulls => Current.Skulls;

    /// <summary>Choix proposés pour <see cref="RandomDraws"/>.</summary>
    public static readonly int[] RandomDrawChoices = [4, 8, 12, 16, 24];

    /// <summary>Simulations moyennées quand le hasard intervient (une valeur proposée, sinon 12).</summary>
    public static int RandomDraws => RandomDrawChoices.Contains(Current.RandomDraws) ? Current.RandomDraws : 12;
    public static bool RelicStats => Current.RelicStats;
    public static bool RoomPreview => Current.RoomPreview;
    public static string? OpenTab => Current.OpenTab;
    public static string? TabOnCombatStart => Current.TabOnCombatStart;
    public static string? TabOnCombatEnd => Current.TabOnCombatEnd;
    public static string? TabOnMerchant => Current.TabOnMerchant;
    public static string OpenPanel => Current.OpenPanel;
    public static string PanelGrow => Current.PanelGrow;
    public static string CrashReports => SavedSettings.Load().CrashReports;
    public static string? BugReportName => SavedSettings.Load().BugReportName;
    public static bool BugReportAttach => Current.BugReportAttach;

    public static bool IsTabHidden(string title) => Current.HiddenTabs.Contains(title);

    /// <summary>Change une option : sauvegardée tout de suite (avec les autres réglages, relus), GUI mis à jour.</summary>
    public static void Set(Action<SavedSettings> change)
    {
        var settings = SavedSettings.Load();
        change(settings);
        settings.Save();
        _current = settings;
        RunPanel.OptionsChanged();
        Changed?.Invoke();
    }
}
