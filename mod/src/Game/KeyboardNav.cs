using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

namespace StsTools.Game;

/// <summary>
/// Navigation au clavier (mode clavier seul du jeu, et manette) dans les menus du mod (écran Stats, panneau Run
/// stats), avec les touches du jeu : flèches, sélection, retour. Le jeu ne passe pas ces touches à Godot comme des
/// actions : chaque élément reçoit la touche brute, et le jeu n'en fait une action (ui_up…) que si personne ne l'a
/// utilisée ; l'action ne va alors qu'à l'élément qui a le focus (rien ne remonte vers l'écran), et Godot cherche
/// le voisin dans toute la fenêtre (le jeu caché derrière compris). Chaque élément de nos menus reconnaît donc
/// lui-même les touches choisies dans les options du jeu (et les actions, pour la manette) et déplace le focus dans
/// son menu seulement (<see cref="Handle"/>). Il faut aussi rendre les éléments atteignables, montrer lequel a le
/// focus, afficher au focus ce qui s'affiche au survol de la souris, et faire défiler les listes.
/// En clavier et souris, rien ne change : un clic ne laisse pas de focus visible.
/// Le jeu remet lui-même le focus sur le contrôle par défaut de « l'écran actif » (passage au clavier, changement
/// d'écran) : nos menus se déclarent écran actif quand ils sont ouverts (<see cref="Menu"/>), sinon le focus
/// repartirait sur le jeu en dessous.
/// </summary>
public static class KeyboardNav
{
    /// <summary>
    /// Menu du mod vu par le jeu comme un écran. Pas de focus à son ouverture : il ne vient qu'à la demande du joueur
    /// (une flèche ou la sélection dans l'écran Stats, la touche Focus run stats pour le panneau), sur le dernier
    /// contrôle du menu qui l'a eu, ou à défaut sur <paramref name="defaultFocus"/> (null : le premier du menu).
    /// </summary>
    public sealed class Menu(Control root, Func<Control?> defaultFocus) : IScreenContext
    {
        private Control? _last;

        /// <summary>Le joueur a demandé le focus dans le menu (depuis son ouverture).</summary>
        private bool _wantsFocus;

        /// <summary>Contrôle à qui donner le focus quand le joueur le demande (à défaut, le premier du menu).</summary>
        public Control? FocusTarget =>
            _last != null && GodotObject.IsInstanceValid(_last) && _last.IsVisibleInTree() && root.IsAncestorOf(_last)
                ? _last
                : defaultFocus() ?? Focusables(root).FirstOrDefault();

        /// <summary>Ce que le jeu utilise pour remettre le focus : rien tant que le joueur ne l'a pas demandé.</summary>
        public Control? DefaultFocusedControl => _wantsFocus ? FocusTarget : null;

        /// <summary>Le joueur demande le focus dans le menu : il y va, et y revient si le jeu le remet en place.</summary>
        public void TakeFocus()
        {
            _wantsFocus = true;
            FocusTarget?.GrabFocus();
        }

        /// <summary>Touches d'onglet du jeu (précédent : -1, suivant : +1).</summary>
        public Action<int>? SwitchTab { get; init; }

        /// <summary>Touche retour du jeu (fermer le menu).</summary>
        public Action? Cancel { get; init; }

        /// <summary>Menu ouvert.</summary>
        public Func<bool> IsOpen { get; init; } = () => true;

        /// <summary>
        /// Menu posé sur la partie sans la bloquer (panneau Run stats) : écran actif seulement depuis son ouverture
        /// et tant que le focus n'est pas parti sur le jeu ; sinon le jeu ne pourrait plus remettre le focus chez lui.
        /// </summary>
        public bool Overlay { get; init; }

        /// <summary>Panneau : écran actif seulement à la demande (Engage) ; écran plein : toujours.</summary>
        private bool _engaged;

        public bool IsActive => IsOpen() && (!Overlay || _engaged);

        /// <summary>Le focus est dans le menu.</summary>
        public bool HasFocus =>
            GodotObject.IsInstanceValid(root) && root.GetViewport()?.GuiGetFocusOwner() is { } owner
            && (owner == root || root.IsAncestorOf(owner));

        /// <summary>Écran actif, avec le focus (panneau : touche Focus run stats).</summary>
        public void Engage()
        {
            _engaged = true;
            TakeFocus();
            ScreenChanged();
        }

        /// <summary>Focus rendu au jeu : plus l'écran actif.</summary>
        public void Disengage()
        {
            _engaged = false;
            _wantsFocus = false;
            ScreenChanged();
        }

        /// <summary>Le focus est allé sur un autre contrôle : on retient le nôtre ; hors du menu, il n'est plus actif.</summary>
        internal void OnFocusChanged(Control focused)
        {
            if (!GodotObject.IsInstanceValid(root))
                return;
            if (root.IsAncestorOf(focused))
                _last = focused;
            else
            {
                _engaged = false;
                _wantsFocus = false;
            }
        }
    }

    private static readonly List<Menu> Menus = [];

    /// <summary>Racine de chaque menu déclaré, pour y garder la navigation.</summary>
    private static readonly Dictionary<Menu, Control> Roots = new();

    /// <summary>
    /// Déclare le menu (jusqu'à la sortie de <paramref name="root"/> de l'arbre) et prévient le jeu que l'écran actif
    /// a changé, à l'ouverture comme à la fermeture (<see cref="ScreenChanged"/>).
    /// </summary>
    public static void Register(Menu menu, Control root)
    {
        // En clavier seul, le jeu désactive la souris sur toute son interface (MouseBehaviorRecursive), donc sur nos
        // menus qui sont dedans : Godot ne leur envoie alors plus aucun événement, souris comme touches (ni nos
        // flèches, ni la sélection, ni Échap). Nos menus restent actifs quel que soit le mode, et peuvent prendre le
        // focus même si un parent du jeu l'interdit
        root.MouseBehaviorRecursive = Control.MouseBehaviorRecursiveEnum.Enabled;
        root.FocusBehaviorRecursive = Control.FocusBehaviorRecursiveEnum.Enabled;
        Menus.Add(menu);
        Roots[menu] = root;
        // La racine elle-même peut avoir le focus (écran Stats, pour Échap en clavier et souris)
        root.GuiInput += ev => Handle(root, ev);
        Callable onFocus = Callable.From<Control>(menu.OnFocusChanged);
        root.Ready += () => root.GetViewport().Connect(Viewport.SignalName.GuiFocusChanged, onFocus);
        root.TreeExiting += () =>
        {
            Menus.Remove(menu);
            Roots.Remove(menu);
            if (root.GetViewport() is { } viewport && viewport.IsConnected(Viewport.SignalName.GuiFocusChanged, onFocus))
                viewport.Disconnect(Viewport.SignalName.GuiFocusChanged, onFocus);
            ScreenChanged();
        };
    }

    /// <summary>
    /// Un menu s'ouvre ou se ferme : au clavier, le jeu remet le focus sur l'écran actif (le nôtre, ou le sien).
    /// </summary>
    public static void ScreenChanged() => Callable.From(() => ActiveScreenContext.Instance.Update()).CallDeferred();

    /// <summary>Le dernier menu ouvert passe devant les écrans du jeu.</summary>
    [HarmonyPatch(typeof(ActiveScreenContext), nameof(ActiveScreenContext.GetCurrentScreen))]
    public static class CurrentScreenPatch
    {
        public static void Postfix(ref IScreenContext? __result)
        {
            for (int i = Menus.Count - 1; i >= 0; i--)
                if (Menus[i].IsActive)
                {
                    __result = Menus[i];
                    return;
                }
        }
    }

    /// <summary>Le joueur navigue au clavier ou à la manette (mode du jeu).</summary>
    public static bool Active => ModHotkeys.CurrentMode != InputType.MouseAndKeyboard;

    private static readonly StyleBoxFlat FocusRing = new()
    {
        DrawCenter = false,
        BorderColor = StsColors.gold,
        BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
        CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        ExpandMarginLeft = 3, ExpandMarginTop = 3, ExpandMarginRight = 3, ExpandMarginBottom = 3,
    };

    // ---- Touches du jeu ----

    /// <summary>
    /// Touche du jeu pour <paramref name="action"/> : la touche brute choisie dans ses options pour le mode actuel,
    /// ou l'action elle-même (manette, ou touche que le jeu a déjà transformée en action).
    /// </summary>
    private static bool Is(InputEvent ev, StringName action, bool echo = false)
    {
        if (ev.IsActionPressed(action, allowEcho: echo))
            return true;
        return ev is InputEventKey { Pressed: true } key && (echo || !key.Echo)
            && NInputManager.Instance?.GetCurrentHotkey(action) is { } bound && bound != Key.None && key.Keycode == bound;
    }

    private static Vector2? Direction(InputEvent ev) =>
        Is(ev, MegaInput.up, echo: true) ? Vector2.Up
        : Is(ev, MegaInput.down, echo: true) ? Vector2.Down
        : Is(ev, MegaInput.left, echo: true) ? Vector2.Left
        : Is(ev, MegaInput.right, echo: true) ? Vector2.Right
        : null;

    private static bool IsSelect(InputEvent ev) => Is(ev, MegaInput.select);

    private static bool IsCancel(InputEvent ev) => Is(ev, MegaInput.cancel);

    private static Menu? MenuOf(Control control) =>
        Roots.FirstOrDefault(r => GodotObject.IsInstanceValid(r.Value) && (r.Value == control || r.Value.IsAncestorOf(control))).Key;

    /// <summary>
    /// Touche reçue par un élément du menu qui a le focus : sélection (activer), retour (fermer le menu), flèches
    /// (déplacer le focus dans le menu ; dans une liste, entrer dedans ou la faire défiler). La touche est alors
    /// consommée : le jeu n'en fait pas une action, et Godot ne déplace pas le focus ailleurs.
    /// </summary>
    private static void Handle(Control control, InputEvent ev)
    {
        if (!Active || ev is not (InputEventKey { Pressed: true } or InputEventAction { Pressed: true } or InputEventJoypadButton { Pressed: true }))
            return;
        var menu = MenuOf(control);
        if (menu == null)
            return;
        var root = Roots[menu];
        if (IsSelect(ev))
        {
            if (Activate(control))
                control.AcceptEvent();
        }
        else if (IsCancel(ev))
        {
            if (menu.Cancel != null)
            {
                control.AcceptEvent();
                SwallowRelease(ev);
                menu.Cancel();
            }
        }
        else if (Direction(ev) is { } direction)
        {
            control.AcceptEvent();
            if (control is ScrollContainer scroll && Scroll(scroll, direction))
                return;
            Move(root, control == root ? null : control, direction);
        }
    }

    /// <summary>
    /// Flèche haut ou bas sur une liste qui a le focus : entrer dans la liste (premier élément visible), ou la faire
    /// défiler si elle n'a pas d'éléments atteignables. False : rien à faire (en haut, en bas, gauche, droite).
    /// </summary>
    private static bool Scroll(ScrollContainer scroll, Vector2 direction)
    {
        if (direction.Y == 0)
            return false;
        if (Focusables(scroll) is { Count: > 0 } items)
        {
            var view = scroll.GetGlobalRect();
            (items.FirstOrDefault(c => view.Intersects(c.GetGlobalRect())) ?? items[0]).GrabFocus();
            return true;
        }
        const int step = 80;
        var bar = scroll.GetVScrollBar();
        if (direction.Y > 0 && scroll.ScrollVertical < bar.MaxValue - bar.Page)
            scroll.ScrollVertical += step;
        else if (direction.Y < 0 && scroll.ScrollVertical > 0)
            scroll.ScrollVertical -= step;
        else
            return false;
        return true;
    }

    /// <summary>
    /// Rend <paramref name="control"/> atteignable au clavier, avec un cadre doré quand il a le focus, activable avec
    /// la touche de sélection du jeu (bouton, case à cocher, liste déroulante, nombre).
    /// </summary>
    /// <param name="fallback">non utilisé (gardé pour les appels existants)</param>
    /// <param name="ring">cadre de focus propre à l'élément (bouton dont le dessin ne remplit pas tout le rectangle)</param>
    public static void Focusable(Control control, Control? fallback = null, StyleBox? ring = null)
    {
        control.FocusMode = Control.FocusModeEnum.All;
        // Cadre seulement au clavier : un clic de souris donne aussi le focus, sans rien montrer. (Retirer le focus
        // après un clic annulerait le clic : un bouton oublie qu'il est enfoncé quand il perd le focus.)
        control.FocusEntered += () => control.AddThemeStyleboxOverride("focus", Active ? ring ?? FocusRing : NoRing);
        // Texte, ligne avec une image (cartes, reliques, rencontres, étages) : Godot ne dessine pas de cadre de focus
        // sur ces éléments, on le dessine nous-mêmes par-dessus
        if (control is not (BaseButton or LineEdit or ScrollContainer))
        {
            control.Draw += () =>
            {
                if (Active && control.HasFocus())
                    control.DrawStyleBox(ring ?? FocusRing, new Rect2(Vector2.Zero, control.Size));
            };
            control.FocusEntered += control.QueueRedraw;
            control.FocusExited += control.QueueRedraw;
        }
        control.GuiInput += ev => Handle(control, ev);
    }

    private static readonly StyleBoxEmpty NoRing = new();

    /// <summary>Cadre de focus décalé vers le bas (bannière dessinée dans le bas de son bouton : Close).</summary>
    public static StyleBox RingShiftedDown(float pixels) => new StyleBoxFlat
    {
        DrawCenter = false,
        BorderColor = StsColors.gold,
        BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
        CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        ExpandMarginLeft = 3, ExpandMarginRight = 3, ExpandMarginTop = 3 - 2 * pixels, ExpandMarginBottom = 3,
    };

    /// <summary>
    /// Comme <see cref="Focusable"/>, pour un élément qui affiche quelque chose au survol (carte, détails) : au
    /// clavier, l'affichage suit le focus, placé contre l'élément au lieu de la souris.
    /// </summary>
    public static void FocusableHover(Control control, Action show, Action hide, Control? fallback = null)
    {
        Focusable(control, fallback);
        control.FocusEntered += () =>
        {
            if (!Active)
                return;
            _anchor = control;
            show();
        };
        control.FocusExited += () =>
        {
            if (_anchor != control)
                return;
            _anchor = null;
            hide();
        };
    }

    /// <summary>Élément qui a le focus clavier et dont on affiche le survol.</summary>
    private static Control? _anchor;

    /// <summary>
    /// Point près duquel placer un affichage de survol : la souris, ou au clavier le haut de l'élément qui a le focus.
    /// </summary>
    public static Vector2 PointerPosition(CanvasItem item)
    {
        if (_anchor is { } anchor && GodotObject.IsInstanceValid(anchor) && anchor.HasFocus())
        {
            var rect = anchor.GetGlobalRect();
            return new Vector2(rect.Position.X + Math.Min(rect.Size.X, 240) / 2, rect.Position.Y);
        }
        return item.GetGlobalMousePosition();
    }

    /// <summary>
    /// Zone de défilement atteignable : avec le focus, haut et bas entrent dans la liste ou la font défiler
    /// (<see cref="Scroll"/>) ; le défilement suit ensuite le focus dans la liste.
    /// </summary>
    public static void ScrollWithKeys(ScrollContainer scroll, Control? fallback = null)
    {
        Focusable(scroll, fallback);
        scroll.FollowFocus = true;
    }

    /// <summary>
    /// Donne le focus à l'élément du menu le plus proche dans la direction : le plus près dans l'axe, en pénalisant
    /// l'écart sur le côté (une ligne plus bas plutôt qu'un élément loin en diagonale). Rien dans cette direction :
    /// le focus ne bouge pas. Sans point de départ dans le menu : le premier élément.
    /// </summary>
    private static void Move(Control root, Control? from, Vector2 direction)
    {
        var candidates = Focusables(root);
        if (from == null || !root.IsAncestorOf(from))
        {
            candidates.FirstOrDefault()?.GrabFocus();
            return;
        }
        var origin = from.GetGlobalRect();
        Control? best = null;
        float bestScore = float.MaxValue;
        foreach (var c in candidates)
        {
            if (c == from || c.IsAncestorOf(from) || from.IsAncestorOf(c))
                continue;
            // Liste qui a ses propres éléments atteignables : on va directement sur eux
            if (c is ScrollContainer && Focusables(c).Count > 0)
                continue;
            var rect = c.GetGlobalRect();
            // Écart dans la direction, mesuré entre les bords qui se font face : il faut être vraiment de ce côté
            float along = direction.X > 0 ? rect.Position.X - origin.End.X
                : direction.X < 0 ? origin.Position.X - rect.End.X
                : direction.Y > 0 ? rect.Position.Y - origin.End.Y
                : origin.Position.Y - rect.End.Y;
            if (along < -1)
                continue;
            // Écart sur le côté : 0 si les deux se chevauchent sur l'autre axe
            float side = direction.X != 0
                ? Math.Max(0, Math.Max(rect.Position.Y - origin.End.Y, origin.Position.Y - rect.End.Y))
                : Math.Max(0, Math.Max(rect.Position.X - origin.End.X, origin.Position.X - rect.End.X));
            float score = Math.Max(0, along) + 3 * side;
            if (score < bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        best?.GrabFocus();
    }

    /// <summary>
    /// Touche retour consommée par un menu : son relâchement doit l'être aussi. Sinon le jeu, qui n'a pas vu
    /// l'appui, transforme le relâchement en action « retour » (menu principal : écran des profils ; partie : pause).
    /// </summary>
    private static readonly HashSet<Key> SwallowedKeys = [];

    private static void SwallowRelease(InputEvent ev)
    {
        if (ev is InputEventKey key)
            SwallowedKeys.Add(key.Keycode);
    }

    /// <summary>
    /// Avant le jeu, pour toute touche :
    /// - relâchement d'une touche retour déjà traitée par un menu : rien de plus ;
    /// - retour pendant qu'un menu plein écran est ouvert (écran Stats), en clavier et souris comme au clavier : il
    ///   ferme ce menu seulement, le jeu derrière ne le reçoit pas ;
    /// - au clavier, focus perdu (sur rien, ou sur le jeu derrière) pendant qu'un menu est l'écran actif : une flèche
    ///   ou la sélection le remet dans le menu, le retour ferme le menu.
    /// </summary>
    [HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
    public static class GlobalInputPatch
    {
        public static void Postfix(NGame __instance, InputEvent inputEvent)
        {
            var viewport = __instance.GetViewport();
            if (inputEvent is InputEventKey { Pressed: false } released && SwallowedKeys.Remove(released.Keycode))
            {
                viewport?.SetInputAsHandled();
                return;
            }
            if (inputEvent is not (InputEventKey { Pressed: true } or InputEventAction { Pressed: true }))
                return;
            bool cancel = IsCancel(inputEvent) || Is(inputEvent, MegaInput.pauseAndBack);
            if (cancel && Menus.LastOrDefault(m => m.IsOpen() && !m.Overlay) is { Cancel: { } close })
            {
                viewport?.SetInputAsHandled();
                SwallowRelease(inputEvent);
                close();
                return;
            }
            // Touches d'onglet du jeu : écran Stats ouvert (tous modes), ou panneau qui a le focus
            int tab = Is(inputEvent, MegaInput.viewDeckAndTabLeft) ? -1 : Is(inputEvent, MegaInput.viewExhaustPileAndTabRight) ? 1 : 0;
            if (tab != 0 && Menus.LastOrDefault(m => m.IsOpen() && (!m.Overlay || m.HasFocus)) is { SwitchTab: { } switchTab })
            {
                viewport?.SetInputAsHandled();
                SwallowRelease(inputEvent);
                switchTab(tab);
                return;
            }
            if (!Active)
                return;
            var menu = Menus.LastOrDefault(m => m.IsActive);
            if (menu == null || !Roots.TryGetValue(menu, out var root) || !GodotObject.IsInstanceValid(root))
                return;
            var owner = viewport?.GuiGetFocusOwner();
            if (owner != null && (owner == root || root.IsAncestorOf(owner)))
                return;
            if (Direction(inputEvent) != null || IsSelect(inputEvent))
            {
                menu.TakeFocus();
                if (!menu.HasFocus)
                    Focusables(root).FirstOrDefault()?.GrabFocus();
                viewport?.SetInputAsHandled();
                SwallowRelease(inputEvent);
            }
            else if (cancel && menu.Cancel != null)
            {
                viewport?.SetInputAsHandled();
                SwallowRelease(inputEvent);
                menu.Cancel();
            }
        }
    }

    /// <summary>
    /// Ce que fait la touche de sélection : appuyer sur un bouton, cocher, passer au choix suivant d'une liste
    /// (sans ouvrir sa fenêtre, que le jeu ne pilote pas au clavier), augmenter un nombre (revient au minimum après
    /// le maximum).
    /// </summary>
    private static bool Activate(Control control)
    {
        switch (control)
        {
            case OptionButton { ItemCount: > 0 } option:
                int next = (option.Selected + 1) % option.ItemCount;
                option.Select(next);
                option.EmitSignal(OptionButton.SignalName.ItemSelected, next);
                return true;
            case BaseButton { ToggleMode: true } toggle:
                toggle.ButtonPressed = !toggle.ButtonPressed;
                return true;
            case BaseButton button:
                button.EmitSignal(BaseButton.SignalName.Pressed);
                return true;
            case LineEdit { } edit when edit.GetParent() is SpinBox spin:
                spin.Value = spin.Value + spin.Step > spin.MaxValue ? spin.MinValue : spin.Value + spin.Step;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Contrôles atteignables sous <paramref name="root"/>, dans l'ordre de l'arbre.</summary>
    private static List<Control> Focusables(Node root)
    {
        var list = new List<Control>();
        void Walk(Node node)
        {
            foreach (var child in node.GetChildren())
            {
                if (child is Control { FocusMode: Control.FocusModeEnum.All } c && c.IsVisibleInTree())
                    list.Add(c);
                Walk(child);
            }
        }
        Walk(root);
        return list;
    }

    /// <summary>
    /// Reconstruit le contenu de <paramref name="container"/> sans perdre la place du focus clavier : s'il était
    /// dedans, il revient sur l'élément au même rang (ou le dernier), une fois la nouvelle mise en page faite.
    /// </summary>
    public static void KeepFocus(Control container, Action rebuild)
    {
        int index = -1;
        if (Active && container.GetViewport()?.GuiGetFocusOwner() is { } owner && container.IsAncestorOf(owner))
            index = Focusables(container).IndexOf(owner);
        rebuild();
        if (index < 0)
            return;
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(container))
                return;
            var list = Focusables(container);
            if (list.Count > 0)
                list[Math.Min(index, list.Count - 1)].GrabFocus();
        }).CallDeferred();
    }
}
