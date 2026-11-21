using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace StsTools.Game;

/// <summary>
/// Contexte du rapport de gel : quel code du mod tourne en ce moment (patchs et mises à jour suivis, imbriqués), le
/// dernier fini, combien de fois chacun a été appelé depuis la dernière image (une boucle sans fin s'y voit : des
/// milliers d'appels), et où en est le combat (tour, camp, action du jeu en cours). Sans le mod dans la pile, le
/// gel vient du jeu.
/// </summary>
public static class FreezeTrace
{
    private static readonly object Gate = new();

    /// <summary>Code du mod en cours, du plus extérieur au plus intérieur.</summary>
    private static readonly List<string> Running = [];

    private static string? _last;

    /// <summary>Appels depuis la dernière image, par code suivi.</summary>
    private static readonly Dictionary<string, int> Calls = [];

    private static bool _installed;

    /// <summary>
    /// Mises à jour du mod lancées hors patch (image suivante, redessin différé, abonnements aux événements du jeu) :
    /// suivies comme les patchs.
    /// </summary>
    private static readonly (Type Type, string Method)[] Entries =
    [
        (typeof(RunPanel), "Refresh"),
        (typeof(RunPanel), "FitHeight"),
        (typeof(RunPanel), "OnChanged"),
        (typeof(RunPanel), "AutoSelectTab"),
        (typeof(LethalWarning), "Update"),
        (typeof(MapRoomPreview), "Update"),
        (typeof(MapPaths), "Update"),
        (typeof(PlaySimulation), nameof(PlaySimulation.PredictTurnEnd)),
        (typeof(PlaySimulation), nameof(PlaySimulation.Run)),
        (typeof(PlaySimulation), nameof(PlaySimulation.RunPotion)),
        (typeof(TargetPreview), "Update"),
        (typeof(CardHover), "Update"),
    ];

    private static readonly HashSet<string> EntryNames = Entries.Select(e => $"{e.Type.Name}.{e.Method}").ToHashSet();

    /// <summary>Une fois tous les patchs posés (menu principal) : chaque patch du mod et chaque mise à jour suivis.</summary>
    public static void Install(Harmony harmony)
    {
        if (_installed)
            return;
        _installed = true;
        var enter = new HarmonyMethod(AccessTools.Method(typeof(FreezeTrace), nameof(Enter)));
        var exit = new HarmonyMethod(AccessTools.Method(typeof(FreezeTrace), nameof(Exit)));
        var methods = new HashSet<MethodInfo>();
        foreach (var original in harmony.GetPatchedMethods().ToList())
            if (Harmony.GetPatchInfo(original) is { } info)
                foreach (var patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                    if (patch.owner == harmony.Id && patch.PatchMethod.DeclaringType != typeof(FreezeTrace))
                        methods.Add(patch.PatchMethod);
        foreach (var (type, name) in Entries)
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(m => m.Name == name && !m.IsAbstract && !m.ContainsGenericParameters))
                methods.Add(method);
        int failed = 0;
        foreach (var method in methods)
            try
            {
                harmony.Patch(method, prefix: enter, finalizer: exit);
            }
            catch (Exception)
            {
                failed++;
            }
        BugReport.Note($"freeze trace: {methods.Count - failed} mod methods followed" + (failed > 0 ? $", {failed} not" : ""));
    }

    /// <summary>Nom lisible : Type.Méthode (classe englobante pour les patchs, Patch.Postfix seul ne dit rien).</summary>
    private static readonly Dictionary<MethodBase, string> Names = [];

    private static string NameOf(MethodBase method)
    {
        if (Names.TryGetValue(method, out var name))
            return name;
        var type = method.DeclaringType;
        name = type?.DeclaringType is { } outer ? $"{outer.Name}.{type.Name}.{method.Name}" : $"{type?.Name}.{method.Name}";
        return Names[method] = name;
    }

    public static void Enter(MethodBase __originalMethod)
    {
        lock (Gate)
        {
            var name = NameOf(__originalMethod);
            Running.Add(name);
            Calls[name] = Calls.GetValueOrDefault(name) + 1;
        }
    }

    public static Exception? Exit(MethodBase __originalMethod, Exception? __exception)
    {
        lock (Gate)
        {
            var name = NameOf(__originalMethod);
            int index = Running.LastIndexOf(name);
            if (index >= 0)
                Running.RemoveRange(index, Running.Count - index);
            _last = name;
        }
        return __exception;
    }

    /// <summary>À chaque image : les appels comptent de zéro.</summary>
    public static void Frame()
    {
        lock (Gate)
            Calls.Clear();
    }

    /// <summary>Lignes du rapport de gel (depuis le fil de surveillance, le jeu étant figé).</summary>
    public static string Describe()
    {
        var lines = new List<string>();
        // Sans attendre indéfiniment : si le fil principal tenait le verrou, le rapport est écrit quand même
        if (Monitor.TryEnter(Gate, 500))
            try
            {
                lines.Add($"Mod code running: {(Running.Count > 0 ? string.Join(" > ", Running) : "none")}");
                lines.Add($"Last mod code finished: {_last ?? "none"}");
                var top = Calls.OrderByDescending(c => c.Value).Take(10).Select(c => $"{c.Key} ×{c.Value}").ToList();
                lines.Add($"Mod calls since the last frame: {(top.Count > 0 ? string.Join(", ", top) : "none")}");
                // Mises à jour du mod (redessins…) même peu appelées : un redessin relancé sans fin s'y voit
                var updates = Calls.Where(c => EntryNames.Contains(c.Key)).Select(c => $"{c.Key} ×{c.Value}").ToList();
                if (updates.Count > 0)
                    lines.Add($"Mod updates since the last frame: {string.Join(", ", updates)}");
            }
            finally
            {
                Monitor.Exit(Gate);
            }
        else
            lines.Add("Mod code running: unknown (busy)");
        try
        {
            if (CombatManager.Instance.DebugOnlyGetState() is { } combat && CombatManager.Instance.IsInProgress)
                lines.Add($"Combat: round {combat.RoundNumber}, side {combat.CurrentSide}, "
                    + string.Join(", ", combat.Creatures.Select(c => $"{c.Name} {c.CurrentHp}/{c.MaxHp}")));
        }
        catch (Exception e)
        {
            lines.Add($"Combat: unknown ({e.GetType().Name})");
        }
        try
        {
            if (RunManager.Instance.ActionExecutor is { } executor)
                lines.Add($"Game action running: {executor.CurrentlyRunningAction?.ToString() ?? "none"}"
                    + $" (executor {(executor.IsRunning ? "running" : "idle")}{(executor.IsPaused ? ", paused" : "")})");
        }
        catch (Exception e)
        {
            lines.Add($"Game action running: unknown ({e.GetType().Name})");
        }
        return string.Join("\n", lines);
    }
}
