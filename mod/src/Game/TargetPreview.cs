using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using static StsTools.Game.PlaySimulation;

namespace StsTools.Game;

/// <summary>
/// En jouant une carte : l'effet qu'elle aura, tout compris (<see cref="PlaySimulation"/>), dans un encadré à côté
/// de chaque créature concernée. À côté du joueur : ce qu'il gagne ou perd (Block, pouvoirs, énergie, pioche, PV
/// perdus aux épines…). À côté de chaque monstre : dégâts et part qui passe le Block, coup fatal, pouvoirs, et les
/// dégâts annoncés qu'il n'infligera pas (mort, Weak, Force retirée…). Pour une carte qui vise, au survol de la
/// cible ; pour les autres (compétences, attaques de zone), quand la carte entre dans la zone de jeu. Effet au
/// hasard : moyenne de plusieurs tirages. Seulement quand le GUI (<see cref="RunPanel"/>) est ouvert.
/// </summary>
public static class TargetPreview
{
    private sealed record Box(PanelContainer Panel, GridContainer Rows);

    private static readonly Dictionary<Creature, Box> Boxes = new();

    private static readonly Func<NCardPlay, CardModel?> CardOf =
        AccessTools.MethodDelegate<Func<NCardPlay, CardModel?>>(AccessTools.PropertyGetter(typeof(NCardPlay), "Card"));

    /// <summary>Carte qui vise : survol d'une cible.</summary>
    [HarmonyPatch(typeof(NCardPlay), "OnCreatureHover")]
    public static class HoverPatch
    {
        public static void Postfix(NCardPlay __instance, NCreature creature) => Track(__instance, creature.Entity);
    }

    /// <summary>Carte sans cible (ou visant tous les ennemis) entrée dans la zone de jeu.</summary>
    [HarmonyPatch(typeof(NCardPlay), "ShowMultiCreatureTargetingVisuals")]
    public static class NoTargetPatch
    {
        public static void Postfix(NCardPlay __instance) => Track(__instance, null);
    }

    [HarmonyPatch(typeof(NCardPlay), "OnCreatureUnhover")]
    public static class UnhoverPatch
    {
        public static void Postfix() => Forget();
    }

    [HarmonyPatch(typeof(NCardPlay), "HideTargetingVisuals")]
    public static class HideVisualsPatch
    {
        public static void Postfix() => Forget();
    }

    [HarmonyPatch(typeof(NCardPlay), "Cleanup")]
    public static class CleanupPatch
    {
        public static void Postfix() => Forget();
    }

    /// <summary>Carte en train d'être jouée et sa cible, pour l'aperçu quand le GUI s'ouvre ensuite.</summary>
    private static (NCardPlay Play, Creature? Target)? _current;

    private static void Track(NCardPlay play, Creature? target)
    {
        _current = (play, target);
        Show(play, target);
    }

    private static void Forget()
    {
        _current = null;
        Hide();
    }

    /// <summary>
    /// Le GUI s'ouvre (aperçu de la carte en train d'être jouée, s'il y en a une) ou se ferme (aperçu masqué).
    /// </summary>
    public static void GuiToggled(bool open)
    {
        if (!open)
            Hide();
        else if (_potion is { } potion)
            ShowPotion(potion.Model, potion.Target);
        else if (_choosing != null && NCombatRoom.Instance?.Ui.Hand is { IsInCardSelection: true } hand)
            RefreshChoice(hand);
        else if (_current is { } current && GodotObject.IsInstanceValid(current.Play) && current.Play.IsInsideTree())
            Show(current.Play, current.Target);
    }

    // ---- Potions en combat ----

    /// <summary>Potion visée ou survolée, et sa cible, pour l'aperçu (et quand le GUI s'ouvre ensuite).</summary>
    private static (PotionModel Model, Creature? Target)? _potion;

    /// <summary>Potion qui attend sa cible (le jeu prévient de la potion choisie), null sinon.</summary>
    private static PotionModel? _targetingPotion;

    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput.HoveredModelTracker),
        nameof(MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput.HoveredModelTracker.OnLocalPotionSelected))]
    public static class PotionSelectedPatch
    {
        public static void Postfix(PotionModel potionModel) => _targetingPotion = potionModel;
    }

    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput.HoveredModelTracker),
        nameof(MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput.HoveredModelTracker.OnLocalPotionDeselected))]
    public static class PotionDeselectedPatch
    {
        public static void Postfix()
        {
            _targetingPotion = null;
            _potion = null;
            Hide();
        }
    }

    /// <summary>Potion qui vise : survol d'une cible pendant le ciblage.</summary>
    [HarmonyPatch(typeof(NTargetManager), "OnCreatureHovered")]
    public static class PotionTargetPatch
    {
        public static void Postfix(NCreature creature)
        {
            if (_targetingPotion is not { } potion || !InCombat(potion))
                return;
            _potion = (potion, creature.Entity);
            ShowPotion(potion, creature.Entity);
        }
    }

    [HarmonyPatch(typeof(NTargetManager), "OnCreatureUnhovered")]
    public static class PotionUntargetPatch
    {
        public static void Postfix()
        {
            if (_targetingPotion == null)
                return;
            _potion = null;
            Hide();
        }
    }

    /// <summary>
    /// Potion sans cible (Block, Force…) : elle s'utilise d'un clic, l'aperçu s'affiche donc au survol (ou au
    /// focus clavier) de son emplacement.
    /// </summary>
    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder), "OnFocus")]
    public static class PotionFocusPatch
    {
        public static void Postfix(MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder __instance)
        {
            if (__instance.Potion?.Model is not { } potion || !InCombat(potion) || _targetingPotion != null || NeedsTarget(potion))
                return;
            _potion = (potion, null);
            ShowPotion(potion, null);
        }
    }

    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder), "OnUnfocus")]
    public static class PotionUnfocusPatch
    {
        public static void Postfix()
        {
            if (_targetingPotion != null)
                return;
            _potion = null;
            Hide();
        }
    }

    private static bool InCombat(PotionModel potion) =>
        MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress && potion.Owner?.Creature.CombatState != null
        && MegaCrit.Sts2.Core.Context.LocalContext.IsMe(potion.Owner);

    /// <summary>Comme le jeu : la potion attend une cible (ennemi, ou allié à qui la lancer).</summary>
    private static bool NeedsTarget(PotionModel potion) =>
        potion.TargetType is TargetType.AnyEnemy or TargetType.TargetedNoCreature || potion.CanThrowAtAlly();

    // ---- Choix en main pendant qu'une carte se joue (défausse, épuisement…) ----

    /// <summary>Carte qui demande le choix en cours, et rang de ce choix parmi ses choix en main.</summary>
    private static CardModel? _choosing;

    private static int _choiceIndex;

    private static readonly Dictionary<CardModel, int> HandChoices = new(ReferenceEqualityComparer.Instance);

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
    public static class PlayStartPatch
    {
        public static void Prefix(CardModel __instance)
        {
            if (!PlaySimulation.Active)
                HandChoices[__instance] = 0;
        }
    }

    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd), nameof(MegaCrit.Sts2.Core.Commands.CardSelectCmd.FromHand))]
    public static class HandChoicePatch
    {
        public static void Prefix(AbstractModel source, MegaCrit.Sts2.Core.Entities.Players.Player player)
        {
            if (PlaySimulation.Active || !MegaCrit.Sts2.Core.Context.LocalContext.IsMe(player))
                return;
            if (source is CardModel card)
            {
                _choosing = card;
                _choiceIndex = HandChoices.GetValueOrDefault(card);
                HandChoices[card] = _choiceIndex + 1;
            }
            else
                _choosing = null;
        }
    }

    private static readonly AccessTools.FieldRef<NPlayerHand, List<CardModel>> SelectedCards =
        AccessTools.FieldRefAccess<NPlayerHand, List<CardModel>>("_selectedCards");

    [HarmonyPatch(typeof(NPlayerHand), "SelectCardInSimpleMode")]
    public static class SelectPatch
    {
        public static void Postfix(NPlayerHand __instance) => RefreshChoice(__instance);
    }

    [HarmonyPatch(typeof(NPlayerHand), nameof(NPlayerHand.DeselectCard))]
    public static class DeselectPatch
    {
        public static void Postfix(NPlayerHand __instance) => RefreshChoice(__instance);
    }

    [HarmonyPatch(typeof(NPlayerHand), "AfterCardsSelected")]
    public static class SelectionDonePatch
    {
        public static void Postfix()
        {
            _choosing = null;
            Hide();
        }
    }

    /// <summary>
    /// Écran de choix en main : effet combiné de la carte qui le demande avec les cartes choisies (Sly défaussées
    /// qui se jouent, Feel No Pain sur les cartes épuisées…), à partir de ce choix.
    /// </summary>
    private static void RefreshChoice(NPlayerHand hand)
    {
        try
        {
            if (_choosing is not { } card || !hand.IsInCardSelection)
                return;
            var selected = SelectedCards(hand).ToList();
            if (selected.Count == 0)
                Hide();
            else
                ShowFor(card, card.CurrentTarget, selected, _choiceIndex);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot preview the choice: {e.Message}");
        }
    }

    public static void Hide()
    {
        foreach (var box in Boxes.Values)
            if (GodotObject.IsInstanceValid(box.Panel))
                box.Panel.Visible = false;
        foreach (var skull in Skulls)
            if (GodotObject.IsInstanceValid(skull))
                skull.QueueFree();
        Skulls.Clear();
    }

    /// <summary>Têtes de mort posées par l'aperçu en cours, retirées avec lui.</summary>
    private static readonly List<TextureRect> Skulls = [];

    /// <summary>La carte tuerait la créature à coup sûr : la tête de mort rouge qui clignote par-dessus.</summary>
    private static void ShowSkull(NCombatRoom room, Creature creature, Color color)
    {
        if (room.GetCreatureNode(creature) is not { } node)
            return;
        var box = node.Hitbox;
        var skull = LethalWarning.Skull(Math.Clamp(Math.Min(box.Size.X, box.Size.Y), 140, 280), color);
        node.AddChild(skull);
        skull.GlobalPosition = box.GetGlobalRect().GetCenter() - skull.Size / 2;
        Skulls.Add(skull);
    }

    private static void Show(NCardPlay play, Creature? target)
    {
        if (CardOf(play) is { } card)
            ShowFor(card, target);
        else
            Hide();
    }

    /// <param name="selection">cartes choisies dans l'écran de sélection en cours, pour la carte qui le demande</param>
    private static void ShowFor(CardModel card, Creature? target, IReadOnlyList<CardModel>? selection = null, int selectionIndex = 0) =>
        ShowOutcome(card.Owner.Creature, card.Title, () => PlaySimulation.Run(card, target, selection, selectionIndex));

    private static void ShowPotion(PotionModel potion, Creature? target) =>
        ShowOutcome(potion.Owner.Creature, potion.Title.GetFormattedText(), () => PlaySimulation.RunPotion(potion, target));

    /// <param name="user">créature qui joue la carte ou boit la potion : son encadré porte le nom de l'action</param>
    private static void ShowOutcome(Creature user, string title, Func<Outcome?> simulate)
    {
        try
        {
            Hide();
            // Seulement avec le GUI ouvert
            if (!RunPanel.IsOpen || !ModOptions.CardPreview || NCombatRoom.Instance is not { } room)
                return;
            var outcome = WithoutVfx(simulate);
            if (outcome == null)
                return;
            Placed.Clear();
            foreach (var (creature, stats) in outcome.Creatures)
            {
                if (stats.GetValueOrDefault(Stat.Lethal) >= 0.999 && outcome.Max.GetValueOrDefault(creature)?.ContainsKey(Stat.Lethal) != true)
                    ShowSkull(room, creature, LethalWarning.SkullRed);
                else if (stats.GetValueOrDefault(Stat.LethalLate) >= 0.999 && outcome.Max.GetValueOrDefault(creature)?.ContainsKey(Stat.LethalLate) != true)
                    ShowSkull(room, creature, LethalWarning.SkullViolet);
                var rows = Rows(creature, stats, outcome).ToList();
                if (rows.Count == 0 || room.GetCreatureNode(creature) is not { } node)
                    continue;
                string note = outcome.Incomplete ? "partial"
                    : outcome.Max.ContainsKey(creature) ? "range: your choice"
                    : outcome.Runs > 1 ? $"average of {outcome.Runs}" : "";
                rows.Insert(0, [creature == user ? title : creature.Name, note]);
                ShowBox(room, creature, node, rows, outcome);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot preview the card: {e.Message}");
            Hide();
        }
    }

    /// <summary>
    /// Effets visuels qu'une carte ajoute elle-même à l'écran (Neutralize…), hors commandes : retirés aussitôt
    /// après la simulation, avant d'être dessinés.
    /// </summary>
    private static T? WithoutVfx<T>(Func<T?> run) where T : class
    {
        var container = NCombatRoom.Instance?.CombatVfxContainer;
        var before = container?.GetChildren().ToHashSet() ?? [];
        try
        {
            return run();
        }
        finally
        {
            if (container != null && GodotObject.IsInstanceValid(container))
                foreach (var child in container.GetChildren())
                    if (!before.Contains(child))
                        child.QueueFree();
        }
    }

    // ---- Contenu ----

    private const string AddedRow = "Incoming damage added";

    /// <summary>Dégâts évités moins PV perdus par la carte : positif, la carte fait gagner des PV au bout du compte.</summary>
    private const string NetRow = "Net HP";

    /// <summary>Caractéristiques sans signe (des quantités, pas des gains ou des pertes).</summary>
    private static readonly HashSet<string> Unsigned =
        [Stat.Damage, Stat.ThroughBlock, Stat.HpLost, Stat.Draw, Stat.Created, Stat.Avoided, Stat.Prevented,
            Stat.ArrowsShot, Stat.ArrowsRetrieved, Stat.Upgraded, Stat.Transformed, Stat.Discarded, Stat.SlyPlayed];

    /// <summary>Lignes d'une créature, dans l'ordre où les effets arrivent ; les dégâts d'abord.</summary>
    /// <summary>
    /// Lignes d'une créature, dans l'ordre où les effets arrivent ; les dégâts d'abord. Une valeur qui dépend d'un
    /// choix du joueur s'affiche en fourchette (« 0–18 »).
    /// </summary>
    private static IEnumerable<string[]> Rows(Creature creature, Dictionary<string, double> stats, Outcome outcome)
    {
        var max = outcome.Max.GetValueOrDefault(creature);
        var spread = outcome.Spread.GetValueOrDefault(creature);
        double? MaxOf(string stat) => max != null && max.TryGetValue(stat, out var m) ? m : null;
        // Valeur ; fourchette si elle dépend d'un choix ; fourchette et espérance si elle dépend du hasard
        string Value(string stat, double value, Func<double, string> format) =>
            MaxOf(stat) is { } high ? $"{format(value)}–{format(high)}"
            : spread != null && spread.TryGetValue(stat, out var range)
                ? $"{format(range.Low)}–{format(range.High)} ({format(value)})"
            : format(value);

        var order = stats.Keys.OrderBy(k => k switch
        {
            Stat.Damage => 0,
            Stat.ThroughBlock => 1,
            Stat.Lethal or Stat.LethalLate => 2,
            Stat.Stunned => 3,
            Stat.HpLost => 4,
            Stat.Prevented => 5,
            Stat.EndsTurn => 8,
            _ when k.StartsWith(Stat.EndOfTurn) => 7,
            _ => 6,
        });
        foreach (var stat in order)
        {
            double value = stats[stat];
            if (Math.Abs(value) < 0.05 && MaxOf(stat) == null || stat == Stat.Hits)
                continue;
            switch (stat)
            {
                case Stat.Damage:
                    double hits = stats.GetValueOrDefault(Stat.Hits);
                    var damage = Value(stat, value, Format);
                    yield return [stat, hits > 1.05 ? $"{damage}, {Format(hits)} hits" : damage];
                    break;
                case Stat.ThroughBlock:
                    // Seulement si le Block de la cible en arrête une partie
                    if (Math.Abs(value - stats.GetValueOrDefault(Stat.Damage)) >= 0.05
                        || Math.Abs((MaxOf(stat) ?? value) - (MaxOf(Stat.Damage) ?? stats.GetValueOrDefault(Stat.Damage))) >= 0.05)
                        yield return [stat, Value(stat, value, Format)];
                    break;
                case Stat.Lethal or Stat.LethalLate when value >= 0.999 && MaxOf(stat) == null:
                    // Mort certaine : la tête de mort sur la créature à la place de la ligne
                    break;
                case Stat.Lethal or Stat.LethalLate or Stat.Stunned:
                    yield return [stat, MaxOf(stat) != null ? "depends" : outcome.Runs > 1 ? $"{value * 100:0}%" : "✔"];
                    break;
                case Stat.Prevented:
                    yield return value >= 0 ? [stat, Value(stat, value, Format)] : ["Damage added", Format(-value)];
                    // PV perdus par la carte (Breakthrough…) mais dégâts évités : le bilan, juste après
                    if (stats.GetValueOrDefault(Stat.HpLost) is var lost and > 0)
                    {
                        double net = value - lost, netMax = (MaxOf(stat) ?? value) - (MaxOf(Stat.HpLost) ?? lost);
                        yield return [NetRow, Math.Abs(netMax - net) >= 0.05 ? $"{Signed(net)}–{Signed(netMax)}" : Signed(net)];
                    }
                    break;
                case Stat.EndsTurn:
                    yield return [stat, MaxOf(stat) != null ? "depends" : ""];
                    break;
                case Stat.Avoided:
                    yield return value >= 0 ? [stat, Value(stat, value, Format)] : [AddedRow, Format(-value)];
                    break;
                default:
                    if (stat.StartsWith(Stat.Channel))
                        yield return [stat, MaxOf(stat) != null ? "depends" : value > 1.05 ? $"×{Format(value)}" : ""];
                    else
                        yield return [stat, Value(stat, value, Unsigned.Contains(stat) || stat.StartsWith(Stat.MovedTo) ? Format : Signed)];
                    break;
            }
        }
    }

    private static string Format(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.05 ? Math.Round(value).ToString("0") : value.ToString("0.0");

    private static string Signed(double value) => value > 0 ? "+" + Format(value) : Format(value);

    private static readonly Dictionary<string, string> RowIcons = new()
    {
        [Stat.Damage] = CardRunStats.Damage,
        [Stat.ThroughBlock] = CardRunStats.SelfDamage,
        [Stat.Lethal] = CardRunStats.Kills,
        [Stat.LethalLate] = CardRunStats.Kills,
        [Stat.Stunned] = CardRunStats.Stunned,
        [Stat.Block] = CardRunStats.Block,
        [Stat.HpLost] = CardRunStats.SelfDamage,
        [Stat.Healed] = CardRunStats.Healed,
        [Stat.Energy] = CardRunStats.EnergyGained,
        [Stat.Stars] = CardRunStats.StarsGained,
        [Stat.Draw] = CardRunStats.CardsDrawn,
        [Stat.Created] = CardRunStats.CardsCreated,
        [Stat.Avoided] = CardRunStats.Prevented,
        [Stat.Prevented] = CardRunStats.Prevented,
        [Stat.Forge] = CardRunStats.Forged,
        [Stat.Summon] = CardRunStats.Summoned,
        [Stat.Discarded] = CardRunStats.CardsDrawn,
        ["Damage added"] = CardRunStats.Damage,
        [AddedRow] = CardRunStats.Damage,
        [NetRow] = CardRunStats.Healed,
    };

    private static Texture2D? IconOf(string name, Outcome outcome) =>
        RowIcons.TryGetValue(name, out var stat) ? CardRunStats.IconFor(stat)
        : name.StartsWith(Stat.Channel) ? CardRunStats.IconFor(CardRunStats.Orbs)
        : outcome.PowerIcons.TryGetValue(name.StartsWith(Stat.EndOfTurn) ? name[Stat.EndOfTurn.Length..] : name, out var path)
            ? StatsTable.LoadIcon(path)
        : null;

    // ---- Affichage ----

    private static void ShowBox(NCombatRoom room, Creature creature, NCreature node, List<string[]> rows, Outcome outcome)
    {
        if (!Boxes.TryGetValue(creature, out var box) || !GodotObject.IsInstanceValid(box.Panel))
        {
            var panel = new PanelContainer
            {
                Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, TopLevel = true, ZIndex = 100,
            };
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(0.04f, 0.05f, 0.08f, 0.92f),
                BorderColor = new Color(StsColors.gold, 0.5f),
                BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
                CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
                ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 8, ContentMarginBottom = 8,
            });
            var grid = StatsTable.Create([]);
            panel.AddChild(grid);
            room.AddChild(panel);
            Boxes[creature] = box = new Box(panel, grid);
            // Créatures d'un combat fini : on oublie leurs encadrés (détruits avec la salle)
            foreach (var gone in Boxes.Where(b => !GodotObject.IsInstanceValid(b.Value.Panel)).Select(b => b.Key).ToList())
                Boxes.Remove(gone);
        }
        StatsTable.Fill(box.Rows, rows, name => IconOf(name, outcome));
        box.Panel.ResetSize();
        box.Panel.Size = box.Panel.GetCombinedMinimumSize();
        Place(box.Panel, node, playerSide: creature.Side == CombatSide.Player);
        box.Panel.Visible = true;
    }

    /// <summary>Encadrés déjà placés pour l'aperçu en cours : les suivants ne les chevauchent pas.</summary>
    private static readonly List<Rect2> Placed = [];

    /// <summary>
    /// Côté joueur : à droite de la créature ; côté monstres : à gauche (vers le joueur). En haut de sa silhouette,
    /// de l'autre côté si la place manque, toujours dans l'écran. Sans chevaucher un encadré déjà placé : décalé
    /// en dessous de lui, ou au-dessus s'il n'y a plus de place en bas.
    /// </summary>
    private static void Place(Control panel, NCreature node, bool playerSide)
    {
        const float gap = 16, margin = 8;
        var box = node.Hitbox.GetGlobalRect();
        var screen = panel.GetViewportRect().Size;
        var size = panel.Size;
        float left = box.Position.X - size.X - gap, right = box.End.X + gap;
        float x = playerSide
            ? right + size.X <= screen.X ? right : Math.Max(0, left)
            : left >= 0 ? left : Math.Min(right, screen.X - size.X);
        float maxY = Math.Max(0, screen.Y - size.Y);
        float start = Math.Clamp(box.Position.Y, 0, maxY);
        var y = Free(x, start, size, margin, down: true) is { } below && below <= maxY ? below
            : Free(x, start, size, margin, down: false) is { } above && above >= 0 ? above
            : start;
        panel.GlobalPosition = new Vector2(x, y);
        Placed.Add(new Rect2(new Vector2(x, y), size));
    }

    /// <summary>Première hauteur libre en partant de <paramref name="y"/>, vers le bas ou vers le haut.</summary>
    private static float? Free(float x, float y, Vector2 size, float margin, bool down)
    {
        for (int i = 0; i <= Placed.Count; i++)
        {
            var rect = new Rect2(new Vector2(x, y), size).Grow(margin / 2);
            var hit = Placed.FirstOrDefault(p => p.Grow(margin / 2).Intersects(rect));
            if (hit == default)
                return y;
            y = down ? hit.End.Y + margin : hit.Position.Y - size.Y - margin;
        }
        return null;
    }
}
