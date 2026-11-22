#if TESTBRIDGE
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace StsTools.Game;

/// <summary>
/// Pont de test, seulement dans une build de test (<c>dotnet build -p:TestBridge=true</c>, jamais dans le mod donné aux
/// joueurs) : Claude pilote le jeu pour tester le mod. Il dépose une commande dans
/// <c>user://StsTools/test/&lt;id&gt;.cmd</c> (une ligne : « click Max », « screenshot carte »…), le pont l'exécute
/// dans le jeu et écrit la réponse dans <c>&lt;id&gt;.out</c> (première ligne « ok » ou « error »). Les clics et les
/// touches passent par l'entrée du jeu (Input.ParseInputEvent), comme une vraie souris ou un vrai clavier.
/// Outil côté Claude : <c>mod/tools/sts_test.py</c>.
/// </summary>
public static class TestBridge
{
    private static bool _started;
    private static bool _busy;
    private static ulong _lastPoll;
    private static string Dir => ProjectSettings.GlobalizePath("user://StsTools/test");

    private static readonly string Help = """
        Commands (coordinates: game viewport pixels, as in tree/points):
          ping                      game state in one line
          state                     JSON: screen, run, GUI, map, path finder
          screenshot [name] [scale] PNG of the game in the test folder (scale 0.1-1, default 1)
          tree [text]               visible clickable controls (type, text, center), filtered by text
          click <text>              click the visible clickable control with this text (exact, else contains)
          clickat <x> <y>           left click at a position;  rclick <x> <y>: right click
          move <x> <y>              move the mouse (hover)
          scroll <x> <y> <up|down> [n]
          key <name> [n]            press a key (Godot names: Escape, Enter, M, F7, Ctrl+Z...), n times
          console <command>         dev console command (solo)
          gui open|close            the mod's GUI;  gui tab <title>[/<sub-tab>]: select a GUI tab
          map open|close            the map screen
          points                    map points: coord, type, state, center
          hoverpoint <col> <row>    hover a map point;  clickpoint <col> <row>: click it
          nodes <path> [depth]      node tree under a node path (type, name, global rect, visible), default depth 2
          roompreview <col> <row>   text of the mod's room preview for this map point (without hovering it)
          encounterimage <ID>       PNG of the room preview's monster image for this encounter (e.g. CUBEX_CONSTRUCT_NORMAL)
          combat                    player, hand (index, cost, target, playable, text), enemies (index, HP, intents)
          play <card> [enemy]       play the hand card at this index (on this enemy index if it needs a target)
          aim <card> [enemy]        like play, without the last click: the card stays aimed (mod preview); rclick cancels
          endturn                   click the end turn button
          deletehistory             delete every run history file of the profile (local and Steam Cloud)
          savels <dir>              files of a save folder, through the game's save store (e.g. profile1/saves)
          saveput <path> <file>     write a save file through the game's save store (local and Steam Cloud) from
                                    an absolute file path: restores a save after a test
          savedel <path>            delete a save file through the game's save store (local and Steam Cloud)
          fakemp <char> [<char>...] NEW test run, saves blocked (one player per character, you are the first; the
                                    others never act). Saves come back at the main menu (the test run is gone)
          graph                     JSON: every map point (coord, type, children), visited coords, free moves
          cards                     visible cards outside the hand (rewards, choices, shop): index, title, text
          pickcard <i>              click the visible card with this index (see cards)
        """;

    private static readonly AccessTools.FieldRef<NDevConsole, MegaCrit.Sts2.Core.DevConsole.DevConsole> ConsoleField =
        AccessTools.FieldRefAccess<NDevConsole, MegaCrit.Sts2.Core.DevConsole.DevConsole>("_devConsole");

    public static void Start()
    {
        if (_started || Engine.GetMainLoop() is not SceneTree tree)
            return;
        _started = true;
        Directory.CreateDirectory(Dir);
        foreach (var stale in Directory.GetFiles(Dir, "*.cmd"))
            File.Delete(stale);
        tree.ProcessFrame += Poll;
        Log.Warn($"[{ModEntry.Id}] TEST BRIDGE ENABLED: commands read from {Dir}");
        BugReport.Note("test bridge enabled (test build)");
    }

    private static void Poll()
    {
        ulong now = Time.GetTicksMsec();
        if (_busy || now - _lastPoll < 100)
            return;
        _lastPoll = now;
        var next = Directory.GetFiles(Dir, "*.cmd").OrderBy(File.GetCreationTimeUtc).FirstOrDefault();
        if (next == null)
            return;
        string line;
        try
        {
            line = File.ReadAllText(next).Trim();
            File.Delete(next);
        }
        catch (IOException)
        {
            return; // encore en cours d'écriture
        }
        _busy = true;
        _ = Run(Path.ChangeExtension(next, ".out"), line);
    }

    private static async Task Run(string outPath, string line)
    {
        string result;
        try
        {
            BugReport.Note($"test bridge: {line}");
            result = "ok\n" + await Execute(line);
        }
        catch (Exception e)
        {
            result = $"error\n{e.Message}\n{e.StackTrace}";
        }
        finally
        {
            _busy = false;
        }
        var tmp = outPath + ".tmp";
        File.WriteAllText(tmp, result);
        File.Move(tmp, outPath, overwrite: true);
    }

    private static SceneTree Tree => (SceneTree)Engine.GetMainLoop();

    private static async Task Frames(int n = 1)
    {
        for (int i = 0; i < n; i++)
            await Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
    }

    private static async Task<string> Execute(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return Help;
        string cmd = parts[0].ToLowerInvariant(), rest = parts.Length > 1 ? parts[1].Trim() : "";
        var args = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (cmd)
        {
            case "help":
                return Help;
            case "ping":
                return $"pong; {Summary()}";
            case "state":
                return JsonSerializer.Serialize(State(), new JsonSerializerOptions { WriteIndented = true });
            case "screenshot":
                return await Screenshot(args.ElementAtOrDefault(0) ?? $"shot-{DateTime.Now:HHmmss}",
                    args.Length > 1 ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1f);
            case "tree":
                return string.Join("\n", Clickables()
                    .Where(c => rest.Length == 0 || c.Text.Contains(rest, StringComparison.OrdinalIgnoreCase))
                    .Select(c => $"{c.Control.GetType().Name} \"{c.Text}\" at {Fmt(Center(c.Control))}{(InsideScrolls(c.Control) ? "" : " [scrolled out]")} ({c.Control.GetPath()})"));
            case "click":
            {
                var target = Find(rest) ?? throw new InvalidOperationException($"no visible clickable control with text \"{rest}\"");
                // Dans une zone de défilement, d'abord amené dans la partie visible
                await ScrollIntoView(target.Control);
                // Tout lu avant le clic : le contrôle peut être libéré par le clic (GUI redessiné)
                var at = Center(target.Control);
                string what = $"{target.Control.GetType().Name} \"{target.Text}\"";
                await Click(at, MouseButton.Left);
                return $"clicked {what} at {Fmt(at)}";
            }
            case "clickat":
                await Click(Pos(args), MouseButton.Left);
                return $"clicked at {Fmt(Pos(args))}";
            case "rclick":
                await Click(Pos(args), MouseButton.Right);
                return $"right clicked at {Fmt(Pos(args))}";
            case "move":
                await Move(Pos(args));
                return $"mouse at {Fmt(Pos(args))}";
            case "scroll":
            {
                var at = Pos(args);
                bool up = args.ElementAtOrDefault(2)?.ToLowerInvariant() != "down";
                int n = args.Length > 3 ? int.Parse(args[3]) : 1;
                await Move(at);
                for (int i = 0; i < n; i++)
                {
                    var button = up ? MouseButton.WheelUp : MouseButton.WheelDown;
                    Send(new InputEventMouseButton { ButtonIndex = button, Pressed = true, Position = at });
                    Send(new InputEventMouseButton { ButtonIndex = button, Pressed = false, Position = at });
                    await Frames();
                }
                return $"scrolled {(up ? "up" : "down")} x{n} at {Fmt(at)}";
            }
            case "key":
            {
                int n = args.Length > 1 ? int.Parse(args[1]) : 1;
                for (int i = 0; i < n; i++)
                    await Key(args.ElementAtOrDefault(0) ?? throw new ArgumentException("key name missing"));
                return $"pressed {args[0]} x{n}";
            }
            case "console":
            {
                var console = NDevConsole.InstanceOrNull ?? throw new InvalidOperationException("no dev console");
                var res = ConsoleField(console).ProcessCommand(rest);
                if (res.task != null)
                    await res.task;
                await Frames(2);
                return $"{(res.success ? "success" : "failed")}: {res.msg}";
            }
            case "gui":
                switch (args.ElementAtOrDefault(0)?.ToLowerInvariant())
                {
                    case "open":
                        if (!RunPanel.IsOpen)
                            RunPanel.Toggle();
                        break;
                    case "close":
                        RunPanel.Close();
                        break;
                    case "tab":
                        if (!RunPanel.SelectTabByTitle(string.Join(' ', args.Skip(1))))
                            throw new InvalidOperationException("no such tab available");
                        break;
                    default:
                        throw new ArgumentException("gui open|close|tab <title>[/<sub-tab>]");
                }
                await Frames(3);
                return $"GUI {(RunPanel.IsOpen ? "open" : "closed")}";
            case "map":
            {
                var screen = NMapScreen.Instance ?? throw new InvalidOperationException("no map screen (not in a run)");
                if (args.ElementAtOrDefault(0)?.ToLowerInvariant() == "close")
                    screen.Close();
                else
                    screen.Open(isOpenedFromTopBar: true);
                await Frames(10);
                return $"map {(screen.IsOpen ? "open" : "closed")}";
            }
            case "points":
                return string.Join("\n", MapPoints().Select(p =>
                    $"{p.Point.coord.col},{p.Point.coord.row} {p.Point.PointType} {p.State} at {Fmt(Center(p))}"));
            case "hoverpoint":
            case "clickpoint":
            {
                int col = int.Parse(args[0]), row = int.Parse(args[1]);
                var point = MapPoints().FirstOrDefault(p => p.Point.coord.col == col && p.Point.coord.row == row)
                            ?? throw new InvalidOperationException($"no map point {col},{row}");
                var at = Center(point);
                string what = $"{col},{row} {point.Point.PointType}";
                if (cmd == "hoverpoint")
                    await Move(at);
                else
                    await Click(at, MouseButton.Left);
                return $"{cmd} {what} at {Fmt(at)}";
            }
            case "encounterimage":
            {
                var encounter = MegaCrit.Sts2.Core.Models.ModelDb.GetByIdOrNull<MegaCrit.Sts2.Core.Models.EncounterModel>(
                                    new MegaCrit.Sts2.Core.Models.ModelId("ENCOUNTER", args[0]))
                                ?? throw new ArgumentException($"no encounter {args[0]}");
                var texture = EncounterVisuals.Thumbnail(encounter);
                for (int i = 0; texture == null && i < 300; i++)
                {
                    await Frames(1);
                    texture = EncounterVisuals.Thumbnail(encounter);
                }
                if (texture == null)
                    return "no image";
                string path = System.IO.Path.Combine(Dir, $"encounter-{args[0]}.png");
                texture.GetImage().SavePng(path);
                return path;
            }
            case "nodes":
            {
                var root = Tree.Root.GetNodeOrNull(args.ElementAtOrDefault(0) ?? "/root")
                           ?? throw new ArgumentException($"no node {args.ElementAtOrDefault(0)}");
                int depth = args.Length > 1 ? int.Parse(args[1]) : 2;
                var lines = new List<string>();
                void Walk(Node node, int level)
                {
                    string rect = node is Control c ? $" rect {Fmt(c.GlobalPosition)} size {Fmt(c.Size)}{(c.IsVisibleInTree() ? "" : " hidden")}"
                        : node is Node2D n2 ? $" at {Fmt(n2.GlobalPosition)}{(n2.IsVisibleInTree() ? "" : " hidden")}" : "";
                    lines.Add($"{new string(' ', level * 2)}{node.GetType().Name} {node.Name}{rect}");
                    if (level < depth)
                        foreach (var child in node.GetChildren())
                            Walk(child, level + 1);
                }
                Walk(root, 0);
                return string.Join("\n", lines);
            }
            case "roompreview":
            {
                var player = CardRunStats.LocalPlayer ?? throw new InvalidOperationException("not in a run");
                var state = (RunState)player.RunState;
                int col = int.Parse(args[0]), row = int.Parse(args[1]);
                var point = state.Map.GetAllMapPoints().Append(state.Map.BossMapPoint)
                                .FirstOrDefault(p => p.coord.col == col && p.coord.row == row)
                            ?? throw new InvalidOperationException($"no map point {col},{row}");
                var rows = MapRoomPreview.Rows(state, player, point);
                return MapRoomPreview.Debug(state, point) + "\n"
                       + (rows == null ? "(no preview)" : string.Join("\n", rows.Select(r => string.Join(" | ", r))));
            }
            case "combat":
                return CombatText();
            case "play":
            {
                var hand = HandHolders();
                int index = int.Parse(args[0]);
                if (index < 0 || index >= hand.Count)
                    throw new ArgumentException($"no card {index} in hand ({hand.Count} cards)");
                var card = hand[index].CardNode!.Model!;
                string name = card.Title;
                // Haut visible de la carte : son centre peut être sous le bas de l'écran
                var box = hand[index].Hitbox.GetGlobalRect();
                var from = new Vector2(box.GetCenter().X, Math.Min(box.GetCenter().Y, Tree.Root.GetVisibleRect().Size.Y - 90));
                Vector2 to;
                if (args.Length > 1)
                {
                    var enemies = Enemies();
                    int target = int.Parse(args[1]);
                    to = Center(enemies[target].Hitbox);
                }
                else
                    to = new Vector2(960, 420); // zone de jeu, au-dessus de la main
                // Comme un joueur : cliquer la carte, l'amener sur la cible (aperçu du mod au survol), cliquer
                await Click(from, MouseButton.Left);
                await Move(to);
                await Frames(10);
                await Click(to, MouseButton.Left);
                await Frames(60);
                return $"played {index} {name}\n{CombatText()}";
            }
            case "fakemp":
                return await FakeMultiplayer(args);
            case "graph":
            {
                var state = CardRunStats.LocalPlayer?.RunState ?? throw new InvalidOperationException("not in a run");
                var map = state.Map;
                var points = map.GetAllMapPoints().Append(map.StartingMapPoint).Append(map.BossMapPoint)
                    .Concat(map.SecondBossMapPoint is { } second ? [second] : []).Distinct();
                return JsonSerializer.Serialize(new
                {
                    rows = map.GetRowCount(),
                    start = $"{map.StartingMapPoint.coord.col},{map.StartingMapPoint.coord.row}",
                    current = state.CurrentMapPoint is { } c ? $"{c.coord.col},{c.coord.row}" : null,
                    visited = state.VisitedMapCoords.Select(v => $"{v.col},{v.row}").ToList(),
                    freeMoves = MapPaths.FreeMoves,
                    points = points.Select(p => new
                    {
                        coord = $"{p.coord.col},{p.coord.row}",
                        type = p.PointType.ToString(),
                        children = p.Children.Select(ch => $"{ch.coord.col},{ch.coord.row}").ToList(),
                    }).ToList(),
                });
            }
            case "focus":
            {
                // Contrôle qui a le focus : type, texte et position du curseur (zones de texte)
                var owner = Tree.Root.GuiGetFocusOwner();
                return owner switch
                {
                    null => "no focus",
                    TextEdit edit => $"TextEdit \"{edit.Text}\" caret line {edit.GetCaretLine()} column {edit.GetCaretColumn()}",
                    LineEdit lineEdit => $"LineEdit \"{lineEdit.Text}\" caret {lineEdit.CaretColumn}",
                    _ => $"{owner.GetType().Name} {owner.Name}",
                };
            }
            case "overlays":
            {
                // Surcouche de chaque carte de la main (affliction Hexed…) : pour vérifier qu'elle est affichée
                var lines = HandHolders().Select((h, i) =>
                {
                    var card = h.CardNode!;
                    var container = card.OverlayContainer;
                    string children = container == null ? "no container" : string.Join(",", container.GetChildren().Select(c => $"{c.Name}{(c is CanvasItem ci && !ci.Visible ? "(hidden)" : "")}"));
                    return $"{i}: {card.Model!.Title} affliction={card.Model.Affliction?.Id.Entry ?? "-"} hasOverlay={card.Model.Affliction?.HasOverlay} overlay=[{children}]";
                });
                return string.Join("\n", lines);
            }
            case "cards":
            {
                var cards = ScreenCards();
                return cards.Count == 0 ? "no card on screen" : string.Join("\n", cards.Select((c, i) =>
                    $"{i}: {c.CardNode!.Model!.Title} ({c.CardNode.Model.Type}, cost {(c.CardNode.Model.EnergyCost.CostsX ? "X" : c.CardNode.Model.EnergyCost.Canonical)}) at {Fmt(Center(c.Hitbox))} — {CardText(c.CardNode.Model)}"));
            }
            case "pickcard":
            {
                var cards = ScreenCards();
                int i = int.Parse(args[0]);
                if (i < 0 || i >= cards.Count)
                    throw new ArgumentException($"no card {i} ({cards.Count} on screen)");
                var at = Center(cards[i].Hitbox);
                string title = cards[i].CardNode!.Model!.Title;
                await Click(at, MouseButton.Left);
                return $"picked {i} {title} at {Fmt(at)}";
            }
            case "aim":
            {
                // Comme play, sans le dernier clic : la carte reste visée (aperçu du mod) ; rclick pour l'annuler
                var hand = HandHolders();
                int index = int.Parse(args[0]);
                if (index < 0 || index >= hand.Count)
                    throw new ArgumentException($"no card {index} in hand ({hand.Count} cards)");
                string name = hand[index].CardNode!.Model!.Title;
                var box = hand[index].Hitbox.GetGlobalRect();
                var from = new Vector2(box.GetCenter().X, Math.Min(box.GetCenter().Y, Tree.Root.GetVisibleRect().Size.Y - 90));
                var to = args.Length > 1 ? Center(Enemies()[int.Parse(args[1])].Hitbox) : new Vector2(960, 420);
                await Click(from, MouseButton.Left);
                await Move(to);
                await Frames(20);
                return $"aiming {index} {name} at {Fmt(to)}";
            }
            case "endturn":
            {
                var button = All(Tree.Root).OfType<MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton>().FirstOrDefault(b => b.IsVisibleInTree())
                             ?? throw new InvalidOperationException("no end turn button");
                await Click(Center(button), MouseButton.Left);
                await Frames(30);
                return "end turn clicked";
            }
            case "deletehistory":
                return DeleteHistory();
            case "savels":
                return string.Join("\n", Store().GetFilesInDirectory(args[0]));
            case "saveput":
                Store().WriteFile(args[0], File.ReadAllBytes(args[1]));
                return $"wrote {args[0]} via {Store().GetType().Name}";
            case "savedel":
                Store().DeleteFile(args[0]);
                return $"deleted {args[0]} via {Store().GetType().Name}";
            default:
                throw new ArgumentException($"unknown command \"{cmd}\"\n{Help}");
        }
    }

    /// <summary>
    /// Supprime toutes les parties de l'historique du profil courant (jeu non moddé et moddé) par le magasin de
    /// sauvegardes du jeu : CloudSaveStore.DeleteFile efface aussi la copie Steam Cloud, sinon le jeu la retélécharge.
    /// </summary>
    /// <summary>Magasin de sauvegardes du jeu (local et Steam Cloud).</summary>
    private static MegaCrit.Sts2.Core.Saves.ISaveStore Store()
    {
        var saves = MegaCrit.Sts2.Core.Saves.SaveManager.Instance;
        return (MegaCrit.Sts2.Core.Saves.ISaveStore)AccessTools.Field(saves.GetType(), "_saveStore").GetValue(saves)!;
    }

    private static string DeleteHistory()
    {
        var saves = MegaCrit.Sts2.Core.Saves.SaveManager.Instance;
        var store = (MegaCrit.Sts2.Core.Saves.ISaveStore)AccessTools.Field(saves.GetType(), "_saveStore").GetValue(saves)!;
        int profile = saves.CurrentProfileId, deleted = 0;
        foreach (var modded in new[] { false, true })
        {
            var dir = MegaCrit.Sts2.Core.Saves.Managers.RunHistorySaveManager.GetHistoryPath(profile, modded);
            if (!store.DirectoryExists(dir))
                continue;
            foreach (var file in store.GetFilesInDirectory(dir).Where(f => f.EndsWith(".run")))
            {
                store.DeleteFile(dir.PathJoin(file));
                deleted++;
            }
        }
        return $"deleted {deleted} run files via {store.GetType().Name}";
    }

    // ---- Entrées ----

    /// <summary>Position dans la fenêtre (ce qu'attend Input) d'une position dans la vue du jeu.</summary>
    private static Vector2 ToWindow(Vector2 viewport) => Tree.Root.GetFinalTransform() * viewport;

    private static void Send(InputEvent ev)
    {
        if (ev is InputEventMouse mouse)
        {
            _mouse = mouse.Position;
            _simulating = true;
            mouse.Position = ToWindow(mouse.Position);
            mouse.GlobalPosition = mouse.Position;
        }
        _sending = true;
        try
        {
            Input.ParseInputEvent(ev);
            Input.FlushBufferedEvents();
        }
        finally
        {
            _sending = false;
        }
    }

    private static Vector2 _mouse;

    /// <summary>
    /// Souris simulée active : le jeu lit la position de la souris directement (Viewport.GetMousePosition, curseur
    /// réel), pas seulement les événements ; tant que le pont pilote la souris, ces lectures renvoient sa position.
    /// Coupé dès que la vraie souris bouge.
    /// </summary>
    private static bool _simulating;

    private static bool _sending;

    [HarmonyPatch(typeof(Viewport), nameof(Viewport.GetMousePosition))]
    private static class MousePositionPatch
    {
        public static void Postfix(Viewport __instance, ref Vector2 __result)
        {
            if (_simulating && __instance == Tree.Root)
                __result = _mouse;
        }
    }

    [HarmonyPatch(typeof(CanvasItem), nameof(CanvasItem.GetGlobalMousePosition))]
    private static class GlobalMousePatch
    {
        public static void Postfix(CanvasItem __instance, ref Vector2 __result)
        {
            if (_simulating && __instance.GetViewport() == Tree.Root)
                __result = __instance.GetCanvasTransform().AffineInverse() * _mouse;
        }
    }

    [HarmonyPatch(typeof(CanvasItem), nameof(CanvasItem.GetLocalMousePosition))]
    private static class LocalMousePatch
    {
        public static void Postfix(CanvasItem __instance, ref Vector2 __result)
        {
            if (_simulating && __instance.GetViewport() == Tree.Root)
                __result = __instance.GetGlobalTransform().AffineInverse() * (__instance.GetCanvasTransform().AffineInverse() * _mouse);
        }
    }

    /// <summary>La vraie souris bouge : le joueur reprend la main.</summary>
    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.NGame), nameof(MegaCrit.Sts2.Core.Nodes.NGame._Input))]
    private static class RealMousePatch
    {
        public static void Prefix(InputEvent inputEvent)
        {
            if (!_sending && inputEvent is InputEventMouseMotion)
                _simulating = false;
        }
    }

    private static async Task Move(Vector2 to)
    {
        // Quelques étapes : le jeu voit la souris entrer dans les contrôles (survol)
        var from = _mouse;
        for (int i = 1; i <= 3; i++)
        {
            var at = from.Lerp(to, i / 3f);
            Send(new InputEventMouseMotion { Position = at, Relative = (to - from) / 3f });
            await Frames();
        }
        _mouse = to;
        await Frames(2);
    }

    private static async Task Click(Vector2 at, MouseButton button)
    {
        await Move(at);
        var mask = button == MouseButton.Left ? MouseButtonMask.Left : MouseButtonMask.Right;
        Send(new InputEventMouseButton { ButtonIndex = button, Pressed = true, Position = at, ButtonMask = mask });
        await Frames(2);
        Send(new InputEventMouseButton { ButtonIndex = button, Pressed = false, Position = at });
        await Frames(3);
    }

    private static async Task Key(string name)
    {
        var parts = name.Split('+');
        var keycode = OS.FindKeycodeFromString(parts[^1]);
        if (keycode == Godot.Key.None)
            throw new ArgumentException($"unknown key \"{parts[^1]}\"");
        bool Has(string m) => parts[..^1].Any(p => p.Equals(m, StringComparison.OrdinalIgnoreCase));
        // Lettre ou chiffre sans Ctrl/Alt : le caractère tapé, pour les zones de texte
        long unicode = parts[^1].Length == 1 && !Has("ctrl") && !Has("alt")
            ? (Has("shift") ? char.ToUpperInvariant(parts[^1][0]) : char.ToLowerInvariant(parts[^1][0])) : 0;
        InputEventKey Make(bool pressed) => new()
        {
            Keycode = keycode, PhysicalKeycode = keycode, Pressed = pressed, Unicode = pressed ? unicode : 0,
            CtrlPressed = Has("ctrl"), ShiftPressed = Has("shift"), AltPressed = Has("alt"),
        };
        Send(Make(true));
        await Frames(2);
        Send(Make(false));
        await Frames(3);
    }

    private static Vector2 Pos(string[] args) =>
        new(float.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));

    // ---- Recherche dans l'écran ----

    private static Vector2 Center(Control control) => control.GetGlobalRect().GetCenter();

    private static string Fmt(Vector2 v) => $"{v.X:0} {v.Y:0}";

    private static IEnumerable<Node> All(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            yield return child;
            foreach (var sub in All(child))
                yield return sub;
        }
    }

    /// <summary>Contrôles cliquables visibles (boutons Godot et du jeu), avec leur texte (le leur ou celui de leurs étiquettes).</summary>
    private static List<(Control Control, string Text)> Clickables()
    {
        var screen = Tree.Root.GetVisibleRect();
        return All(Tree.Root).OfType<Control>()
            .Where(c => (c is BaseButton or NClickableControl) && c.IsVisibleInTree()
                        && !(c is NMapPoint && NMapScreen.Instance is not { IsOpen: true })
                        && c.GetGlobalRect().Intersects(screen)
                        && !(c is BaseButton { Disabled: true }))
            .Select(c => (c, TextOf(c)))
            .ToList();
    }

    private static async Task ScrollIntoView(Control control)
    {
        for (var parent = control.GetParent(); parent != null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll)
            {
                scroll.EnsureControlVisible(control);
                await Frames(3);
            }
    }

    /// <summary>Le centre du contrôle est dans la partie visible de chaque zone de défilement qui le contient.</summary>
    private static bool InsideScrolls(Control control)
    {
        var center = control.GetGlobalRect().GetCenter();
        for (var parent = control.GetParent(); parent != null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll && !scroll.GetGlobalRect().HasPoint(center))
                return false;
        return true;
    }

    private static string TextOf(Control control)
    {
        if (control is Button { Text.Length: > 0 } button)
            return button.Text;
        var texts = All(control).Select(n => n switch
            {
                Label label when label.IsVisibleInTree() => label.Text,
                RichTextLabel rich when rich.IsVisibleInTree() => rich.GetParsedText(),
                _ => null,
            })
            .Where(t => !string.IsNullOrWhiteSpace(t));
        return string.Join(" ", texts!).Trim();
    }

    /// <summary>Texte exact (sans la casse), sinon contenu ; plusieurs : le dernier dans l'arbre, dessiné par-dessus.</summary>
    private static (Control Control, string Text)? Find(string text)
    {
        var all = Clickables();
        var exact = all.Where(c => c.Text.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
        var found = exact.Count > 0 ? exact : all.Where(c => c.Text.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        return found.Count > 0 ? found[^1] : null;
    }

    private static List<NMapPoint> MapPoints() =>
        NMapScreen.Instance?.GetNodeOrNull<Control>("TheMap/Points")?.GetChildren().OfType<NMapPoint>()
            .Where(p => GodotObject.IsInstanceValid(p)).OrderBy(p => p.Point.coord.row).ThenBy(p => p.Point.coord.col).ToList()
        ?? [];

    // ---- Multijoueur factice ----

    /// <summary>
    /// Partie de test à plusieurs joueurs en solo (comme le « fake multiplayer » du jeu) : toutes les sauvegardes du
    /// jeu sont bloquées jusqu'à la fermeture, pour ne pas écraser la vraie partie du joueur ni ses stats.
    /// </summary>
    private static bool _fakeRun;

    private static async Task<string> FakeMultiplayer(string[] args)
    {
        if (args.Length < 1)
            throw new ArgumentException("fakemp <character> [<character>...] (one: a solo test run; e.g. IRONCLAD SILENT)");
        var characters = args.Select(a => MegaCrit.Sts2.Core.Models.ModelDb.AllCharacters
                .FirstOrDefault(c => c.Id.Entry.Equals(a, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"unknown character {a}: " +
                string.Join(", ", MegaCrit.Sts2.Core.Models.ModelDb.AllCharacters.Select(c => c.Id.Entry)))).ToList();
        // Partie en cours (la vraie partie du joueur) : refusé avant de bloquer quoi que ce soit. Un échec laissait les
        // sauvegardes bloquées pendant que le joueur continuait sa vraie partie (2026-11-22)
        if (MegaCrit.Sts2.Core.Runs.RunManager.Instance.IsInProgress)
            throw new InvalidOperationException("a run is in progress: go back to the main menu first (saves untouched)");
        _fakeRun = true;
        CardRunStats.SavesBlocked = true;
        Log.Warn($"[{ModEntry.Id}] test bridge: fake multiplayer run, game saves blocked until the game closes");
        var unlocks = MegaCrit.Sts2.Core.Saves.SaveManager.Instance.GenerateUnlockStateFromProgress();
        var players = characters.Select((c, i) =>
            MegaCrit.Sts2.Core.Entities.Players.Player.CreateForNewRun(c, unlocks, (ulong)(i + 1))).ToList();
        var acts = MegaCrit.Sts2.Core.Models.ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList();
        var state = MegaCrit.Sts2.Core.Runs.RunState.CreateForNewRun(players, acts, [], MegaCrit.Sts2.Core.Runs.GameMode.Standard,
            isCustom: false, ascensionLevel: 0, MegaCrit.Sts2.Core.Helpers.SeedHelper.GetRandomSeed());
        var run = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
        run.SetUpNewSingleplayer(state, shouldSave: false);
        await MegaCrit.Sts2.Core.Assets.PreloadManager.LoadRunAssets(state.Players.Select(p => p.Character));
        await run.FinalizeStartingRelics();
        run.Launch();
        MegaCrit.Sts2.Core.Nodes.NGame.Instance.RootSceneContainer.SetCurrentScene(MegaCrit.Sts2.Core.Nodes.NRun.Create(state));
        await run.SetActInternal(0);
        run.RunLocationTargetedBuffer.OnLocationChanged(state.RunLocation);
        run.MapSelectionSynchronizer.OnLocationChanged(state.MapLocation);
        await run.EnterAct(0);
        await Frames(30);
        return $"fake multiplayer run: {string.Join(", ", characters.Select(c => c.Id.Entry))}; saves blocked";
    }

    /// <summary>
    /// Retour au menu principal : la partie de test est finie, les sauvegardes reviennent (sinon une vraie partie
    /// lancée ensuite dans la même session n'était pas sauvegardée).
    /// </summary>
    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu), nameof(MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu._Ready))]
    private static class EndFakeRunPatch
    {
        public static void Postfix()
        {
            if (!_fakeRun)
                return;
            _fakeRun = false;
            CardRunStats.SavesBlocked = false;
            Log.Warn($"[{ModEntry.Id}] test bridge: back to the main menu, game saves restored");
        }
    }

    /// <summary>
    /// Sauvegardes bloquées pendant une partie de test. Deux patchs : Harmony refuse un <c>__result</c> sur une méthode
    /// void, et l'échec d'une seule cible laissait toutes les suivantes sans patch (progression du profil écrite).
    /// </summary>
    [HarmonyPatch]
    private static class BlockSavesPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var type = typeof(MegaCrit.Sts2.Core.Saves.SaveManager);
            foreach (var name in new[] { "SaveProgressFile", "SaveRunHistory", "SaveProfile", "DeleteCurrentRun",
                         "DeleteCurrentMultiplayerRun", "SavePrefsFile", "SaveSettings" })
                if (AccessTools.Method(type, name) is { ReturnType: var ret } method && ret == typeof(void))
                    yield return method;
        }

        public static bool Prefix() => !_fakeRun;
    }

    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Saves.SaveManager), nameof(MegaCrit.Sts2.Core.Saves.SaveManager.SaveRun))]
    private static class BlockRunSavePatch
    {
        public static bool Prefix(ref Task __result)
        {
            if (!_fakeRun)
                return true;
            __result = Task.CompletedTask;
            return false;
        }
    }

    // ---- Combat ----

    /// <summary>
    /// Cartes de la main dans l'ordre du jeu (pile Hand), pas selon leur position : pendant que la main se réorganise
    /// après une carte jouée, l'ordre à l'écran change et une autre carte aurait été jouée.
    /// </summary>
    private static List<MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder> HandHolders()
    {
        var hand = MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand.Instance;
        if (hand == null || CardRunStats.LocalPlayer?.PlayerCombatState is not { } combat)
            return [];
        return combat.Hand.Cards.Select(c => hand.GetCardHolder(c))
            .OfType<MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder>()
            .Where(h => GodotObject.IsInstanceValid(h) && h.IsVisibleInTree() && h.CardNode?.Model != null).ToList();
    }

    private static List<MegaCrit.Sts2.Core.Nodes.Combat.NCreature> Enemies() =>
        MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance?.CreatureNodes
            .Where(c => GodotObject.IsInstanceValid(c) && c.Entity.IsEnemy && !c.Entity.IsDead)
            .OrderBy(c => c.GlobalPosition.X).ToList() ?? [];

    /// <summary>Cartes visibles hors de la main, de haut en bas puis de gauche à droite.</summary>
    private static List<MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder> ScreenCards()
    {
        var screen = Tree.Root.GetVisibleRect();
        return All(Tree.Root).OfType<MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder>()
            .Where(h => h is not MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder && h.IsVisibleInTree()
                        && h.CardNode?.Model != null && GodotObject.IsInstanceValid(h.Hitbox)
                        && h.Hitbox.GetGlobalRect().Intersects(screen))
            .OrderBy(h => Math.Round(h.Hitbox.GetGlobalRect().GetCenter().Y / 50)).ThenBy(h => h.Hitbox.GetGlobalRect().GetCenter().X)
            .ToList();
    }

    private static string CardText(MegaCrit.Sts2.Core.Models.CardModel card) =>
        System.Text.RegularExpressions.Regex.Replace(card.GetDescriptionForPile(MegaCrit.Sts2.Core.Entities.Cards.PileType.None), @"\[[^\]]*\]", "")
            .Replace("\n", " ");

    private static string Powers(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature) =>
        string.Join(", ", creature.Powers.Select(p => $"{p.Id.Entry} {p.Amount}"));

    private static string CombatText()
    {
        var player = CardRunStats.LocalPlayer ?? throw new InvalidOperationException("not in a run");
        if (player.PlayerCombatState is not { } combat || !MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress)
            return "not in combat";
        var me = player.Creature;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"player HP {me.CurrentHp}/{me.MaxHp} block {me.Block} energy {combat.Energy}/{combat.MaxEnergy}" +
                      $" powers [{Powers(me)}] draw {combat.DrawPile.Cards.Count} discard {combat.DiscardPile.Cards.Count}");
        var hand = HandHolders();
        for (int i = 0; i < hand.Count; i++)
        {
            var card = hand[i].CardNode!.Model!;
            string cost = card.EnergyCost.CostsX ? "X" : card.EnergyCost.Canonical.ToString();
            string text = System.Text.RegularExpressions.Regex.Replace(card.GetDescriptionForPile(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand), @"\[[^\]]*\]", "")
                .Replace("\n", " ");
            sb.AppendLine($"card {i}: {card.Title} cost {cost} target {card.TargetType} {(card.CanPlay() ? "playable" : "UNPLAYABLE")} — {text}");
        }
        var enemies = Enemies();
        for (int i = 0; i < enemies.Count; i++)
        {
            var e = enemies[i].Entity;
            var intents = e.Monster?.NextMove?.Intents.Select(intent => intent switch
            {
                MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent attack =>
                    $"attack {attack.GetSingleDamage([me], e)}{(attack.Repeats > 1 ? $"x{attack.Repeats}" : "")}",
                _ => intent.IntentType.ToString(),
            }) ?? [];
            sb.AppendLine($"enemy {i}: {e.Monster?.Id.Entry ?? e.ToString()} HP {e.CurrentHp}/{e.MaxHp} block {e.Block}" +
                          $" powers [{Powers(e)}] intent [{string.Join(", ", intents)}]");
        }
        return sb.ToString().TrimEnd();
    }

    // ---- État et captures ----

    private static async Task<string> Screenshot(string name, float scale)
    {
        // Capture du jeu : attend que l'image demandée soit dessinée avant de la lire
        var capture = await MegaCrit.Sts2.Core.Debug.ViewportCapture.GrabAsync();
        var image = capture.Image ?? throw new IOException($"no screenshot: {capture.Detail}");
        scale = Math.Clamp(scale, 0.1f, 1f);
        if (scale < 1)
            image.Resize((int)(image.GetWidth() * scale), (int)(image.GetHeight() * scale), Image.Interpolation.Bilinear);
        var file = Path.Combine(Dir, Path.GetFileName(name) + ".png");
        var error = image.SavePng(file);
        if (error != Error.Ok)
            throw new IOException($"cannot save the screenshot: {error}");
        return file;
    }

    private static string Summary()
    {
        var state = CardRunStats.LocalPlayer?.RunState;
        var map = NMapScreen.Instance;
        return state == null
            ? "not in a run"
            : $"in a run: act {state.CurrentActIndex + 1}, floor {state.VisitedMapCoords.Count}, " +
              $"map {(map is { IsOpen: true } ? "open" : "closed")}, GUI {(RunPanel.IsOpen ? "open" : "closed")}, " +
              $"{state.Players.Count} player(s)";
    }

    private static object State()
    {
        var state = CardRunStats.LocalPlayer?.RunState;
        var player = CardRunStats.LocalPlayer;
        return new
        {
            summary = Summary(),
            viewport = Fmt(Tree.Root.GetVisibleRect().Size),
            run = state == null ? null : new
            {
                act = state.CurrentActIndex + 1,
                floor = state.VisitedMapCoords.Count,
                current = state.CurrentMapPoint is { } p ? $"{p.coord.col},{p.coord.row} {p.PointType}" : null,
                players = state.Players.Count,
                hp = player == null ? null : $"{player.Creature.CurrentHp}/{player.Creature.MaxHp}",
                gold = player?.Gold,
            },
            gui = RunPanel.IsOpen,
            blockedAnimations = PlaySimulation.BlockedAnimations,
            map = NMapScreen.Instance is { IsOpen: true },
            pathFinder = new
            {
                // Classement : « Type:règle » (max, min, =n, >=n, sur tout l'acte) ; any : types hors du classement
                importance = MapPaths.Order.Select(t => $"{t}:{MapPaths.RuleOf(t)}").ToList(),
                freeMoves = MapPaths.FreeMoves,
                truncated = MapPaths.Truncated,
                any = MapPaths.Types.Where(t => !MapPaths.IsRanked(t.Type)).Select(t => t.Type.ToString()).ToList(),
                possible = MapPaths.Types.ToDictionary(t => t.Type.ToString(), t => MapPaths.Possible(t.Type)),
                showAll = MapPaths.ShowAll,
                index = MapPaths.Index,
                paths = MapPaths.Best.Select(path => string.Join(" ", path.Select(p => $"{p.coord.col},{p.coord.row}:{Short(p.PointType)}"))).ToList(),
            },
        };
    }

    private static string Short(MapPointType type) => type switch
    {
        MapPointType.Monster => "M",
        MapPointType.Elite => "E",
        MapPointType.RestSite => "R",
        MapPointType.Shop => "$",
        MapPointType.Treasure => "T",
        MapPointType.Unknown => "?",
        MapPointType.Boss => "B",
        MapPointType.Ancient => "A",
        _ => type.ToString(),
    };
}
#endif
