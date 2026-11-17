using System.Diagnostics;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;

namespace StsTools.Game;

/// <summary>
/// Rapport de bug automatique. Pendant une session de jeu, un journal est écrit sur le disque au fil de l'eau (il
/// survit à un plantage) : contexte (versions, mods, partie, combat), toutes les lignes du log du jeu, ce que fait le
/// mod (aperçus, touches, GUI), exceptions non gérées, et un rapport de gel si le jeu cesse d'afficher des images
/// (boucle infinie, blocage). Partie terminée ou jeu fermé normalement : tout est effacé, ça ne sert plus. Sinon
/// (plantage, gel puis fermeture forcée), au lancement suivant, la session est archivée dans un dossier de rapport,
/// avec le log complet du jeu de cette session et les sauvegardes de la partie en cours (pour la rejouer).
/// </summary>
public static class BugReport
{
    /// <summary>Dossier des rapports : user://StsTools/reports.</summary>
    public static string Root { get; private set; } = "";

    private static string Session => Path.Combine(Root, "session");
    private static string Journal => Path.Combine(Session, "journal.log");
    private static string Lock => Path.Combine(Session, "running");
    private static string FreezeFile => Path.Combine(Session, "freeze.txt");

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static long _written;

    /// <summary>Dernières lignes, pour le rapport de gel.</summary>
    private static readonly Queue<string> Recent = new();

    private const int RecentLines = 300;
    private const long MaxJournal = 8 * 1024 * 1024;
    private const int KeptReports = 10;

    /// <summary>Rapport archivé à ce lancement (session précédente non terminée), affiché au menu principal.</summary>
    public static string? Archived { get; private set; }

    /// <summary>Ce que fait le mod en ce moment (aperçu en cours…), lu par le rapport de gel ; null : rien.</summary>
    public static volatile string? Activity;

    /// <summary>Au démarrage du mod : archive la session précédente si elle n'a pas été fermée, en ouvre une nouvelle.</summary>
    public static void Start()
    {
        try
        {
            Root = ProjectSettings.GlobalizePath("user://StsTools/reports");
            Directory.CreateDirectory(Root);
            if (File.Exists(Lock))
                Archive();
            else if (Directory.Exists(Session))
                Directory.Delete(Session, true);
            Directory.CreateDirectory(Session);
            File.WriteAllText(Lock, $"{System.Environment.ProcessId} {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Open();
            Note($"session start: mod {typeof(BugReport).Assembly.GetName().Version} built {BuildTime():yyyy-MM-dd HH:mm}, "
                + $"{RuntimeInformation()}");
            Log.LogCallback += OnGameLog;
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Note($"UNHANDLED EXCEPTION: {e.ExceptionObject}");
            // Fermeture sans passer par le menu du jeu : arrêt depuis Steam ou kill (SIGTERM), fin du processus
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ClosedWithoutCrash("process exit");
            foreach (var signal in new[] { System.Runtime.InteropServices.PosixSignal.SIGTERM,
                         System.Runtime.InteropServices.PosixSignal.SIGINT, System.Runtime.InteropServices.PosixSignal.SIGHUP })
                try
                {
                    Signals.Add(System.Runtime.InteropServices.PosixSignalRegistration.Create(signal, _ => ClosedWithoutCrash(signal.ToString())));
                }
                catch (Exception)
                {
                    // Signal non géré sur ce système (Windows : la fermeture passe par le menu du jeu)
                }
            TaskScheduler.UnobservedTaskException += (_, e) => Note($"unobserved task exception: {e.Exception}");
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] bug reports disabled: {e.Message}");
        }
    }

    private static DateTime BuildTime()
    {
        try
        {
            return File.GetLastWriteTime(typeof(BugReport).Assembly.Location);
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static string RuntimeInformation() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {System.Environment.Version}";

    private static void Open()
    {
        var stream = new FileStream(Journal, FileMode.Append, System.IO.FileAccess.Write, FileShare.ReadWrite);
        _written = stream.Length;
        // Chaque ligne part tout de suite vers le système : elle reste même si le jeu plante
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    /// <summary>Ajoute une ligne au journal (et à la mémoire des dernières lignes).</summary>
    public static void Note(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {text}";
        lock (Gate)
        {
            Recent.Enqueue(line);
            while (Recent.Count > RecentLines)
                Recent.Dequeue();
            if (_writer == null)
                return;
            try
            {
                // Journal trop gros : la moitié la plus ancienne part dans journal.1.log
                if (_written > MaxJournal)
                {
                    _writer.Dispose();
                    File.Move(Journal, Path.Combine(Session, "journal.1.log"), true);
                    Open();
                }
                _writer.WriteLine(line);
                _written += line.Length + 1;
            }
            catch (Exception)
            {
                // Journal seulement
            }
        }
    }

    private static void OnGameLog(LogLevel level, string text, int _) => Note($"[game {level}] {text}");

    // ---- Trace pas à pas d'un aperçu de carte (preview.log) ----

    private static StreamWriter? _preview;

    private static string PreviewFile => Path.Combine(Session, "preview.log");

    /// <summary>
    /// Début d'un aperçu : sa trace pas à pas (événements, réactions, commandes) va dans preview.log, écrit au fil
    /// de l'eau. Le fichier ne garde que l'aperçu en cours : s'il reste après un plantage, c'est là que ça s'est
    /// passé, à la dernière ligne près.
    /// </summary>
    public static void BeginPreview(string name)
    {
        Activity = $"card preview: {name}";
        lock (Gate)
        {
            try
            {
                _preview?.Dispose();
                _preview = Directory.Exists(Session) ? new StreamWriter(PreviewFile, false) { AutoFlush = true } : null;
                _preview?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} preview {name}");
            }
            catch (Exception)
            {
                _preview = null;
            }
        }
    }

    public static void PreviewStep(string text)
    {
        lock (Gate)
        {
            try
            {
                _preview?.WriteLine(text);
            }
            catch (Exception)
            {
                // Trace seulement
            }
        }
    }

    /// <summary>Aperçu terminé sans problème : sa trace ne sert plus.</summary>
    public static void EndPreview(string summary)
    {
        Activity = null;
        Note(summary);
        lock (Gate)
        {
            try
            {
                _preview?.Dispose();
                _preview = null;
                File.Delete(PreviewFile);
            }
            catch (Exception)
            {
                // Trace seulement
            }
        }
    }

    // ---- Fin normale : effacé ----

    /// <summary>Gardées pour que les signaux restent interceptés.</summary>
    private static readonly List<System.Runtime.InteropServices.PosixSignalRegistration> Signals = [];

    /// <summary>
    /// Jeu fermé sans plantage, mais sans passer par son menu (arrêt depuis Steam, kill) : comme une fermeture
    /// normale, sauf si le jeu était figé (rapport de gel écrit) — c'est alors justement un jeu bloqué qu'on ferme,
    /// le rapport est gardé.
    /// </summary>
    private static void ClosedWithoutCrash(string how)
    {
        try
        {
            if (File.Exists(FreezeFile))
            {
                Note($"game closed ({how}) after a freeze: the report is kept");
                return;
            }
        }
        catch (Exception)
        {
            // Dans le doute, fermeture normale
        }
        CleanExit();
    }

    /// <summary>Partie terminée (victoire, défaite, abandon) : rien à garder, nouveau journal.</summary>
    public static void RunEnded()
    {
        lock (Gate)
        {
            try
            {
                _writer?.Dispose();
                _writer = null;
                foreach (var file in Directory.GetFiles(Session).Where(f => f != Lock))
                    File.Delete(file);
                Open();
            }
            catch (Exception)
            {
                // Journal seulement
            }
        }
        Note("previous run ended normally: its data was cleared");
    }

    /// <summary>Jeu fermé normalement : la session ne sert plus, tout est effacé.</summary>
    public static void CleanExit()
    {
        lock (Gate)
        {
            try
            {
                Log.LogCallback -= OnGameLog;
                _writer?.Dispose();
                _writer = null;
                if (Directory.Exists(Session))
                    Directory.Delete(Session, true);
            }
            catch (Exception)
            {
                // Rien de plus à faire en quittant
            }
        }
    }

    [EarlyPatch]
    [HarmonyPatch(typeof(NGame), nameof(NGame.Quit))]
    public static class QuitPatch
    {
        public static void Prefix() => CleanExit();
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded))]
    public static class RunEndPatch
    {
        public static void Postfix(bool isVictory)
        {
            Note($"run ended ({(isVictory ? "victory" : "defeat or abandon")})");
            RunEnded();
        }
    }

    // ---- Session précédente non terminée : archivée ----

    private static void Archive()
    {
        string started = File.ReadAllText(Lock).Split(' ', 2).ElementAtOrDefault(1) ?? "";
        var stamp = DateTime.TryParse(started, out var date) ? date : Directory.GetLastWriteTime(Session);
        var report = Path.Combine(Root, $"crash-{stamp:yyyyMMdd-HHmmss}");
        if (Directory.Exists(report))
            Directory.Delete(report, true);
        Directory.Move(Session, report);
        File.Delete(Path.Combine(report, "running"));

        // Log complet du jeu de cette session : le plus récent des logs archivés par le jeu au lancement
        var logs = ProjectSettings.GlobalizePath("user://logs");
        var previousLog = Directory.Exists(logs)
            ? Directory.GetFiles(logs, "godot*.log").Where(f => Path.GetFileName(f) != "godot.log")
                .OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
            : null;
        if (previousLog != null)
            File.Copy(previousLog, Path.Combine(report, "game.log"), true);

        // Sauvegardes de la partie en cours, pour la rejouer
        var saves = Path.Combine(report, "saves");
        foreach (var save in Directory.GetFiles(ProjectSettings.GlobalizePath("user://"), "current_run*.save", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(saves);
            var profile = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(save)) ?? "");
            File.Copy(save, Path.Combine(saves, $"{profile}-{Path.GetFileName(save)}"), true);
        }

        File.WriteAllText(Path.Combine(report, "README.txt"),
            $"""
            StsTools bug report: the game session started {started} did not end normally
            (crash, freeze then forced close, or killed).

            journal.log   what happened, line by line, until the end (game log lines, mod actions, errors)
            freeze.txt    present if the game stopped drawing frames: what the mod was doing at that moment
            preview.log   present if a card preview did not finish: its step-by-step trace (last line = where it stopped)
            game.log      the game's full log of that session
            saves/        the run saves at the time of the report, to replay it
            """);
        Archived = report;
        Log.Warn($"[{ModEntry.Id}] the previous game session did not end normally: bug report saved in {report}");

        // Seulement les plus récents
        foreach (var old in Directory.GetDirectories(Root, "crash-*").OrderByDescending(d => d).Skip(KeptReports))
            Directory.Delete(old, true);
    }

    /// <summary>Au menu principal, après une session qui ne s'est pas terminée normalement : où est le rapport.</summary>
    public static void ShowNotice(Control menu)
    {
        if (Archived is not { } report || menu.GetNodeOrNull("StsToolsBugReport") != null)
            return;
        var label = new Label
        {
            Name = "StsToolsBugReport",
            Text = $"STS Tools: the last session ended abnormally. Bug report saved in:\n{report}",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(24, 24),
        };
        label.AddThemeColorOverride("font_color", StsColors.gold);
        label.AddThemeFontSizeOverride("font_size", 18);
        menu.AddChild(label);
        var timer = menu.GetTree().CreateTimer(20);
        timer.Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(label))
                label.QueueFree();
        };
        Archived = null;
    }

    // ---- Gel : le jeu n'affiche plus d'images ----

    private static long _lastFrame;
    private static bool _watching;

    /// <summary>
    /// Surveille l'affichage depuis un autre fil : plus d'image depuis 10 s (boucle infinie, blocage), un rapport de
    /// gel est écrit tout de suite (ce que fait le mod, dernières lignes), sans attendre une fermeture forcée.
    /// </summary>
    public static void WatchFrames()
    {
        if (_watching || Engine.GetMainLoop() is not SceneTree tree)
            return;
        _watching = true;
        Interlocked.Exchange(ref _lastFrame, Stopwatch.GetTimestamp());
        tree.ProcessFrame += () => Interlocked.Exchange(ref _lastFrame, Stopwatch.GetTimestamp());
        var thread = new Thread(Watch) { IsBackground = true, Name = "StsTools frame watchdog" };
        thread.Start();
    }

    private static void Watch()
    {
        bool frozen = false;
        while (true)
        {
            Thread.Sleep(1000);
            double seconds = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastFrame)).TotalSeconds;
            if (seconds >= 10 && !frozen)
            {
                frozen = true;
                WriteFreeze(seconds);
            }
            else if (seconds < 2 && frozen)
            {
                frozen = false;
                Note("the game draws frames again (freeze over)");
            }
        }
    }

    private static void WriteFreeze(double seconds)
    {
        try
        {
            string[] recent;
            lock (Gate)
                recent = Recent.ToArray();
            var text = $"""
                Freeze detected at {DateTime.Now:yyyy-MM-dd HH:mm:ss}: no frame for {seconds:0} s.
                Mod activity: {Activity ?? "none"}
                Card preview: {PlaySimulation.DebugState ?? "none"}
                In combat: {CombatManager.Instance?.IsInProgress}

                Last journal lines:
                {string.Join("\n", recent)}
                """;
            File.WriteAllText(FreezeFile, text);
            Note($"FREEZE: no frame for {seconds:0} s; mod activity: {Activity ?? "none"}; preview: {PlaySimulation.DebugState ?? "none"}");
        }
        catch (Exception)
        {
            // Rapport seulement
        }
    }

    // ---- Contexte : partie, combat ----

    /// <summary>Partie ouverte (nouvelle ou reprise) : personnages, ascension, mode, seed, étage, mods.</summary>
    public static void RunStarted(IRunState runState)
    {
        try
        {
            var players = string.Join(", ", runState.Players.Select(p => p.Character.Id.Entry));
            var mods = string.Join(", ", MegaCrit.Sts2.Core.Modding.ModManager.GetLoadedMods().Select(m => m.ToString()));
            Note($"run: {players}, ascension {runState.AscensionLevel}, mode {runState.GameMode}, seed {runState.Rng.StringSeed}, "
                + $"act {runState.CurrentActIndex + 1} floor {runState.TotalFloor}, mods: {mods}");
        }
        catch (Exception e)
        {
            Note($"run: cannot describe it ({e.Message})");
        }
    }

    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
    public static class CombatPatch
    {
        public static void Postfix(CombatState state)
        {
            try
            {
                Note($"combat: {state.Encounter?.Id.Entry}, "
                    + string.Join(", ", state.Creatures.Select(c => $"{c.Name} {c.CurrentHp}/{c.MaxHp}")));
            }
            catch (Exception e)
            {
                Note($"combat: cannot describe it ({e.Message})");
            }
        }
    }
}
