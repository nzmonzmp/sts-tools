using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace StsTools.Game;

/// <summary>
/// Touches du mod, réglables dans les options du jeu (onglet Mod, <see cref="ModSettingsTab"/>) et gardées dans les
/// réglages du mod. Comme les touches du jeu, une par mode de contrôle : clavier et souris, clavier seul (pas de
/// touche à la manette). Seule celle du mode actif se déclenche, et c'est elle qu'on change dans les options.
/// Écoutées au niveau du jeu entier : au menu principal comme en partie. Le choix d'une nouvelle touche se fait ici
/// aussi (<see cref="StartListening"/>).
/// </summary>
public sealed class ModHotkeys
{
    /// <summary>Ouvre ou ferme le panneau de stats en partie.</summary>
    public static readonly ModHotkeys RunStats = new("RunStats", "Run stats panel", Key.F7, RunPanel.Toggle);

    /// <summary>Ouvre ou ferme l'écran Stats (historique des parties).</summary>
    public static readonly ModHotkeys StatsScreenKey = new("StatsScreen", "Stats screen", Key.F6, StatsScreen.Toggle);

    /// <summary>
    /// Clavier seul : met le focus dans le panneau de stats en partie (l'ouvre s'il est fermé), ou le rend au jeu.
    /// Sans touche en clavier et souris : la souris suffit pour aller dans le panneau.
    /// </summary>
    public static readonly ModHotkeys FocusRunStats =
        new("FocusRunStats", "Focus run stats", Key.F8, RunPanel.ToggleFocus) { KeyboardOnly = true };

    public static readonly ModHotkeys[] All = [RunStats, StatsScreenKey, FocusRunStats];

    /// <summary>Action du mode clavier seul : pas de touche (ni réglable) en clavier et souris.</summary>
    public bool KeyboardOnly { get; private init; }

    /// <summary>Une touche peut être choisie pour ce mode.</summary>
    public bool CanBind(InputType mode) =>
        mode == InputType.KeyboardOnlyMode || mode == InputType.MouseAndKeyboard && !KeyboardOnly;

    /// <summary>Nom de l'action, aussi clé dans les réglages et nom de la ligne dans les options du jeu.</summary>
    public string Id { get; }

    public string Title { get; }

    private readonly Key _default;
    private readonly Action _action;

    private ModHotkeys(string id, string title, Key defaultKey, Action action)
    {
        Id = id;
        Title = title;
        _default = defaultKey;
        _action = action;
    }

    private const string MousePrefix = "Mouse:";

    /// <summary>Boutons de souris acceptés (clavier et souris seulement) : pas les clics gauche et droit ni la molette.</summary>
    private static readonly MouseButton[] MouseButtons = [MouseButton.Middle, MouseButton.Xbutton1, MouseButton.Xbutton2];

    /// <summary>Mode de contrôle actuel du jeu.</summary>
    public static InputType CurrentMode => NControllerManager.Instance?.InputType ?? InputType.MouseAndKeyboard;

    private readonly Dictionary<InputType, string> _bindings = new();

    /// <summary>Clé de sauvegarde : l'id pour clavier et souris (comme avant), « id.KbOnly » pour clavier seul.</summary>
    private string SaveKey(InputType mode) => mode == InputType.KeyboardOnlyMode ? Id + ".KbOnly" : Id;

    /// <summary>
    /// Touche ou bouton de souris d'un mode, tel que sauvegardé : nom de touche Godot (« F7 ») ou « Mouse: » suivi du
    /// nom du bouton (« Mouse:Xbutton1 »). Null à la manette.
    /// </summary>
    private string? Binding(InputType mode)
    {
        if (!CanBind(mode))
            return null;
        if (!_bindings.TryGetValue(mode, out var binding))
            _bindings[mode] = binding = SavedSettings.Load().Hotkeys.GetValueOrDefault(SaveKey(mode)) ?? _default.ToString();
        return binding;
    }

    private void SetBinding(InputType mode, string value)
    {
        _bindings[mode] = value;
        // Relit les réglages avant de les modifier : l'écran Stats et le panneau les sauvegardent aussi
        var settings = SavedSettings.Load();
        settings.Hotkeys[SaveKey(mode)] = value;
        settings.Save();
    }

    /// <summary>Nom affiché pour un mode : « F7 », « Mouse 4 »…, « - » sans touche.</summary>
    public string DisplayFor(InputType mode) => Binding(mode) switch
    {
        null => "-",
        MousePrefix + nameof(MouseButton.Xbutton1) => "Mouse 4",
        MousePrefix + nameof(MouseButton.Xbutton2) => "Mouse 5",
        MousePrefix + nameof(MouseButton.Middle) => "Middle click",
        var b => b,
    };

    /// <summary>Touche du mode actuel (à la manette, celle du clavier et souris, pour l'afficher quelque part).</summary>
    public string Display => DisplayFor(CurrentMode == InputType.Controller ? InputType.MouseAndKeyboard : CurrentMode);

    private bool Matches(InputEvent inputEvent)
    {
        var binding = Binding(CurrentMode);
        return binding != null && inputEvent switch
        {
            InputEventKey { Pressed: true, Echo: false } key =>
                !binding.StartsWith(MousePrefix) && Enum.TryParse<Key>(binding, out var bound) && key.Keycode == bound,
            InputEventMouseButton { Pressed: true } mouse => binding == MousePrefix + mouse.ButtonIndex,
            _ => false,
        };
    }

    /// <summary>Action dont on attend la nouvelle touche (options, onglet Mod), mode concerné, et quoi faire une fois choisie.</summary>
    private static (ModHotkeys Hotkey, InputType Mode, Action Done)? _listening;

    public static bool IsListening(ModHotkeys hotkey, InputType mode) => _listening is { } l && l.Hotkey == hotkey && l.Mode == mode;

    public static bool IsListening(ModHotkeys hotkey) => _listening?.Hotkey == hotkey;

    /// <summary>
    /// La prochaine touche pressée devient celle de <paramref name="hotkey"/> pour le mode actuel (en clavier et
    /// souris, aussi un bouton de souris : milieu, côtés). Échap ou un clic ailleurs annule. Rien à la manette.
    /// </summary>
    /// <returns>false si l'action n'a pas de touche dans ce mode (manette ; clavier et souris pour une action du clavier seul)</returns>
    public static bool StartListening(ModHotkeys hotkey, Action done)
    {
        if (!hotkey.CanBind(CurrentMode))
            return false;
        var previous = _listening;
        _listening = (hotkey, CurrentMode, done);
        // Comme le jeu pendant le choix d'une touche : ses raccourcis (onglets…) ne réagissent pas
        NControllerManager.Instance?.StartListeningForRebind();
        previous?.Done();
        done();
        return true;
    }

    public static void StopListening()
    {
        var previous = _listening;
        _listening = null;
        NControllerManager.Instance?.StopListeningForRebind();
        previous?.Done();
    }

    /// <summary>
    /// Touches et boutons pressés, partout dans le jeu, sauf pendant la saisie d'un texte (console, recherche) ; ou
    /// choix d'une nouvelle touche dans les options.
    /// </summary>
    [HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
    public static class InputPatch
    {
        public static void Postfix(NGame __instance, InputEvent inputEvent)
        {
            if (_listening is { } listening)
            {
                Listen(__instance, listening.Hotkey, listening.Mode, inputEvent);
                return;
            }
            if (__instance.GetViewport()?.GuiGetFocusOwner() is LineEdit or TextEdit)
                return;
            foreach (var hotkey in All)
                if (hotkey.Matches(inputEvent))
                {
                    try
                    {
                        hotkey._action();
                    }
                    catch (Exception e)
                    {
                        Log.Warn($"[{ModEntry.Id}] hotkey {hotkey.Id} failed: {e.Message}");
                    }
                    return;
                }
        }

        /// <summary>
        /// Nouvelle touche : une touche du clavier (Échap annule, et ne ferme pas les options) ou, en clavier et
        /// souris, un bouton de souris accepté. Un clic gauche ou droit ailleurs annule, sans empêcher le clic.
        /// </summary>
        private static void Listen(NGame game, ModHotkeys hotkey, InputType mode, InputEvent inputEvent)
        {
            switch (inputEvent)
            {
                case InputEventKey { Pressed: true, Echo: false } key:
                    if (key.Keycode != Key.Escape)
                        hotkey.SetBinding(mode, key.Keycode.ToString());
                    break;
                case InputEventMouseButton { Pressed: true } mouse
                    when mode == InputType.MouseAndKeyboard && MouseButtons.Contains(mouse.ButtonIndex):
                    hotkey.SetBinding(mode, MousePrefix + mouse.ButtonIndex);
                    break;
                case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right }:
                    StopListening();
                    return;
                default:
                    return;
            }
            game.GetViewport()?.SetInputAsHandled();
            StopListening();
        }
    }
}
