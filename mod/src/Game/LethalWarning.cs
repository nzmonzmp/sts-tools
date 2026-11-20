using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace StsTools.Game;

/// <summary>
/// GUI ouvert : si le joueur mourrait en terminant son tour maintenant (fin de tour, puis attaques des ennemis
/// moins son Block, d'après <see cref="PlaySimulation.PredictTurnEnd"/>), le bouton de fin de tour le signale, comme le
/// jeu signale une mort annoncée : texte du bouton recoloré avec un contour sombre (comme les PV d'une créature
/// que le poison va tuer), icône « coup fatal » des intentions ennemies, et les chiffres au-dessus du bouton. Sauf
/// si un effet empêche la mort (Lizard Tail…). Recalculé quand le combat change (une fois par image au plus).
/// </summary>
public static class LethalWarning
{
    private const string IconName = "StsToolsLethalIcon";
    private const string DeathIcon = "res://images/packed/intents/intent_death_blow.png";

    /// <summary>Tête de mort du jeu (emote multijoueur), pour les monstres condamnés.</summary>
    private const string SkullIcon = "res://images/ui/emote/skull.png";

    /// <summary>Couleurs d'une mort annoncée, sur le modèle des PV en danger du jeu : texte vif, contour sombre.</summary>
    private static readonly Color LethalColor = new("FF5A4D");
    private static readonly Color LethalOutline = new("470000");

    private static readonly AccessTools.FieldRef<NEndTurnButton, MegaLabel> Label =
        AccessTools.FieldRefAccess<NEndTurnButton, MegaLabel>("_label");

    private static readonly AccessTools.FieldRef<NEndTurnButton, CombatState?> State =
        AccessTools.FieldRefAccess<NEndTurnButton, CombatState?>("_combatState");

    private static readonly AccessTools.FieldRef<NEndTurnButton, Control> Visuals =
        AccessTools.FieldRefAccess<NEndTurnButton, Control>("_visuals");

    private static bool _scheduled, _subscribed;

    /// <summary>Le combat a changé (carte jouée, Block, intentions…) : nouveau calcul à la prochaine image.</summary>
    public static void Schedule()
    {
        if (_scheduled || PlaySimulation.Active)
            return;
        _scheduled = true;
        Callable.From(() =>
        {
            _scheduled = false;
            Update();
        }).CallDeferred();
    }

    [HarmonyPatch(typeof(NEndTurnButton), nameof(NEndTurnButton.Initialize))]
    public static class InitPatch
    {
        public static void Postfix()
        {
            if (!_subscribed)
            {
                _subscribed = true;
                CardRunStats.Changed += Schedule;
                RunPanel.OpenChanged += Schedule;
            }
            Schedule();
        }
    }

    private static void Update()
    {
        try
        {
            if (NCombatRoom.Instance?.Ui.EndTurnButton is not { } button || !GodotObject.IsInstanceValid(button))
                return;
            var turn = Predict(State(button), out var combat, out var me);
            Show(button, turn is { } t && t.Taken >= t.Hp
                // Mort évitée par un effet (Lizard Tail…) : pas une mort, sauf mort imposée (Sandpit…)
                && (t.Forced || Hook.ShouldDie(combat!.RunState, combat, me!.Creature, out _)) ? (t.Taken, t.Hp, t.Forced) : null);
            ShowExtraTurn(button, turn?.ExtraTurn);
            ShowDoomed(turn?.DeadEnemies ?? [], turn?.LateDeadEnemies ?? []);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot check if ending the turn is lethal: {e.Message}");
        }
    }

    /// <summary>Ce qui se passerait si le joueur terminait son tour maintenant (GUI ouvert, son tour) ; sinon null.</summary>
    private static (int Taken, int Hp, bool Forced, MegaCrit.Sts2.Core.Models.AbstractModel? ExtraTurn,
        IReadOnlyList<MegaCrit.Sts2.Core.Entities.Creatures.Creature> DeadEnemies,
        IReadOnlyList<MegaCrit.Sts2.Core.Entities.Creatures.Creature> LateDeadEnemies)? Predict(
        CombatState? state, out CombatState? combat, out MegaCrit.Sts2.Core.Entities.Players.Player? me)
    {
        combat = state;
        me = null;
        if (!RunPanel.IsOpen || !ModOptions.Skulls || !CombatManager.Instance.IsInProgress || CombatManager.Instance.IsOverOrEnding
            || combat == null || combat.CurrentSide != CombatSide.Player
            || LocalContext.GetMe(combat) is not { } player || player.Creature.IsDead)
            return null;
        me = player;
        return PlaySimulation.PredictTurnEnd(player);
    }

    private const string DoomedName = "StsToolsDoomed";

    /// <summary>Ennemis marqués comme condamnés (pour retirer la marque quand ils ne le sont plus).</summary>
    private static readonly HashSet<MegaCrit.Sts2.Core.Entities.Creatures.Creature> Doomed = [];

    /// <summary>
    /// Ennemis qui meurent d'ici leur attaque sans que le joueur fasse rien (poison, Doom, effets de fin de tour…) :
    /// une tête de mort en transparence par-dessus, qui clignote.
    /// </summary>
    /// <param name="late">ceux qui meurent après avoir agi (Doom à la fin de leur tour) : tête de mort violette</param>
    private static void ShowDoomed(IReadOnlyList<MegaCrit.Sts2.Core.Entities.Creatures.Creature> early,
        IReadOnlyList<MegaCrit.Sts2.Core.Entities.Creatures.Creature> late)
    {
        if (NCombatRoom.Instance is not { } room)
            return;
        var dead = early.Concat(late).ToList();
        // Marque à refaire si la couleur change (mort avant ou après avoir agi)
        foreach (var creature in Doomed.Where(c => !dead.Contains(c)
                     || room.GetCreatureNode(c)?.GetNodeOrNull<TextureRect>(DoomedName) is { } m && m.SelfModulate != (late.Contains(c) ? SkullViolet : SkullRed)).ToList())
        {
            // Retirée tout de suite (pas seulement à la fin de l'image) : la nouvelle marque prend sa place aussitôt
            if (room.GetCreatureNodeIncludingDying(creature)?.GetNodeOrNull(DoomedName) is { } mark)
            {
                mark.GetParent().RemoveChild(mark);
                mark.QueueFree();
            }
            Doomed.Remove(creature);
        }
        foreach (var creature in dead)
        {
            if (room.GetCreatureNode(creature) is not { } node || node.GetNodeOrNull(DoomedName) != null)
                continue;
            var box = node.Hitbox;
            float side = Math.Clamp(Math.Min(box.Size.X, box.Size.Y), 140, 280);
            var mark = Skull(side, late.Contains(creature) ? SkullViolet : SkullRed);
            mark.Name = DoomedName;
            node.AddChild(mark);
            // Au centre de la silhouette, en coordonnées d'écran (la créature peut être retournée ou mise à l'échelle)
            mark.GlobalPosition = box.GetGlobalRect().GetCenter() - mark.Size / 2;
            Doomed.Add(creature);
        }
    }

    private const string ExtraIconName = "StsToolsExtraTurnIcon";
    private const string ExtraTextName = "StsToolsExtraTurnText";

    /// <summary>
    /// Terminer le tour maintenant donnerait un tour supplémentaire (Pael's Eye sans carte jouée ce tour…) : icône de
    /// ce qui l'accorde à droite du bouton, et « Extra turn » en doré, comme une relique prête à se déclencher.
    /// </summary>
    private static void ShowExtraTurn(NEndTurnButton button, MegaCrit.Sts2.Core.Models.AbstractModel? source)
    {
        var visuals = Visuals(button);
        var icon = visuals.GetNodeOrNull<TextureRect>(ExtraIconName);
        var text = visuals.GetNodeOrNull<Label>(ExtraTextName);
        if (source == null)
        {
            if (icon != null)
                icon.Visible = false;
            if (text != null)
                text.Visible = false;
            return;
        }
        if (icon == null || text == null)
        {
            icon = new TextureRect
            {
                Name = ExtraIconName,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Size = new Vector2(64, 64),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            visuals.AddChild(icon);
            text = new Label { Name = ExtraTextName, MouseFilter = Control.MouseFilterEnum.Ignore };
            text.AddThemeFontSizeOverride("font_size", 20);
            text.AddThemeColorOverride("font_color", MegaCrit.Sts2.Core.Helpers.StsColors.gold);
            text.AddThemeColorOverride("font_outline_color", new Color(0.1f, 0.06f, 0.02f));
            text.AddThemeConstantOverride("outline_size", 8);
            visuals.AddChild(text);
        }
        icon.Texture = source switch
        {
            MegaCrit.Sts2.Core.Models.RelicModel relic => StatsTable.LoadIcon(relic.PackedIconPath),
            MegaCrit.Sts2.Core.Models.PowerModel power => StatsTable.LoadIcon(power.IconPath),
            _ => null,
        };
        var size = visuals.Size.X > 0 ? visuals.Size : button.Size;
        icon.Position = new Vector2(size.X - icon.Size.X * 0.4f, (size.Y - icon.Size.Y) / 2);
        icon.Visible = icon.Texture != null;
        text.Text = "Extra turn";
        text.ResetSize();
        text.Position = new Vector2((size.X - text.Size.X) / 2, size.Y + 2);
        text.Visible = true;
    }

    private static void Show(NEndTurnButton button, (int Taken, int Hp, bool Forced)? lethal)
    {
        var label = Label(button);
        var visuals = Visuals(button);
        var skull = visuals.GetNodeOrNull<TextureRect>(IconName);
        if (lethal == null)
        {
            if (skull == null)
                return;
            skull.QueueFree();
            label.RemoveThemeColorOverride("font_color");
            label.RemoveThemeColorOverride("font_outline_color");
            return;
        }
        // La tête de mort rouge qui clignote, sur le bouton (comme sur les monstres condamnés) ; elle laisse passer
        // les clics : elle prévient, n'empêche pas de terminer le tour
        if (skull == null)
        {
            skull = Skull(110);
            skull.Name = IconName;
            visuals.AddChild(skull);
        }
        var size = visuals.Size.X > 0 ? visuals.Size : button.Size;
        skull.Position = (size - skull.Size) / 2;
        label.AddThemeColorOverride("font_color", LethalColor);
        label.AddThemeColorOverride("font_outline_color", LethalOutline);
    }

    /// <summary>Tête de mort rouge en transparence, qui clignote : mort annoncée (bouton de fin de tour, monstre condamné).</summary>
    /// <summary>Rouge : mort avant d'agir. Violet (couleur de Doom dans les barres de vie) : mort après avoir agi.</summary>
    internal static readonly Color SkullRed = new(1, 0.12f, 0.08f), SkullViolet = new(0.78f, 0.3f, 1f);

    internal static TextureRect Skull(float side, Color? color = null)
    {
        var skull = new TextureRect
        {
            Texture = StatsTable.LoadIcon(SkullIcon) ?? StatsTable.LoadIcon(DeathIcon),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(side, side),
            Size = new Vector2(side, side),
            SelfModulate = color ?? SkullRed,
            Modulate = new Color(1, 1, 1, 0.35f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ZIndex = 50,
        };
        // Clignotement lancé une fois dans la scène (une animation a besoin de l'arbre des nœuds)
        skull.Ready += () =>
        {
            var tween = skull.CreateTween().SetLoops();
            tween.TweenProperty(skull, "modulate:a", 0.9f, 0.7).SetTrans(Tween.TransitionType.Sine);
            tween.TweenProperty(skull, "modulate:a", 0.35f, 0.7).SetTrans(Tween.TransitionType.Sine);
        };
        return skull;
    }
}
