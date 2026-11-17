using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace StsTools.Game;

/// <summary>
/// Stats de la partie d'une relique possédée, ajoutées sous sa description quand le jeu l'affiche au survol
/// (barre des reliques) pendant que le panneau de stats (<see cref="RunPanel"/>) est ouvert, et mises à jour tant
/// qu'elle reste affichée. Les reliques non possédées (boutique, coffre) gardent l'infobulle du jeu.
/// Les valeurs sont celles réellement obtenues : dégâts après Weak, Vulnerable et Block ennemi, sans ce qui dépasse
/// les PV de la cible.
/// </summary>
[HarmonyPatch(typeof(NHoverTipSet), nameof(NHoverTipSet.SetAlignmentForRelic))]
public static class RelicHover
{
    /// <summary>Colonne des infobulles texte du jeu, où s'empilent la description et les mots-clés.</summary>
    private static readonly AccessTools.FieldRef<NHoverTipSet, VFlowContainer> TextTips =
        AccessTools.FieldRefAccess<NHoverTipSet, VFlowContainer>("_textHoverTipContainer");

    /// <summary>Relique de la barre survolée en ce moment, dont l'infobulle est affichée.</summary>
    private static NRelicInventoryHolder? _hovered;

    [HarmonyPatch(typeof(NRelicInventoryHolder), "OnFocus")]
    public static class FocusPatch
    {
        public static void Postfix(NRelicInventoryHolder __instance) => _hovered = __instance;
    }

    [HarmonyPatch(typeof(NRelicInventoryHolder), "OnUnfocus")]
    public static class UnfocusPatch
    {
        public static void Postfix(NRelicInventoryHolder __instance)
        {
            if (_hovered == __instance)
                _hovered = null;
        }
    }

    static RelicHover() => RunPanel.OpenChanged += RefreshHovered;

    /// <summary>
    /// Panneau de stats ouvert ou fermé pendant le survol d'une relique : le jeu refait son infobulle (comme au
    /// survol), avec ou sans les stats.
    /// </summary>
    private static void RefreshHovered()
    {
        try
        {
            if (_hovered is not { } holder || !GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree())
                return;
            NHoverTipSet.CreateAndShow(holder, holder.Relic.Model.HoverTips)?.SetAlignmentForRelic(holder.Relic);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot refresh the relic tooltip: {e.Message}");
        }
    }

    /// <summary>Écart entre deux infobulles, comme dans le jeu.</summary>
    private const float Spacing = 5;

    /// <summary>Délai entre deux mises à jour de l'encadré affiché, pour ne pas le refaire à chaque événement.</summary>
    private const double RefreshSeconds = 0.25;

    /// <summary>
    /// Avant que le jeu place l'infobulle : l'encadré est ajouté à la colonne de la description, et le jeu le
    /// place avec elle (au-dessus des cartes liées, dans l'écran).
    /// </summary>
    public static void Prefix(NHoverTipSet __instance, NRelic relic)
    {
        try
        {
            // Seulement quand le panneau de stats est ouvert : sinon l'infobulle du jeu reste telle quelle
            if (!RunPanel.IsOpen)
                return;
            // Relique possédée sans stats encore : rien à afficher, l'infobulle du jeu reste telle quelle
            if (relic.Model is not { } model || model.IsCanonical || !OwnedByLocal(model))
                return;
            var stats = CardRunStats.Of(model) ?? new CardRunStats();
            var rows = Rows(model, stats);
            if (rows.Count == 0)
                return;
            var column = TextTips(__instance);
            var grid = StatsTable.Create(rows);
            var panel = Panel(grid);
            column.AddChild(panel);
            var size = panel.GetCombinedMinimumSize();
            panel.Size = size;
            column.Size = new Vector2(Math.Max(column.Size.X, size.X), column.Size.Y + size.Y + Spacing);
            KeepUpdated(model, stats, column, panel, grid);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot show relic stats: {e.Message}");
        }
    }

    private static bool OwnedByLocal(RelicModel model) =>
        CardRunStats.LocalPlayer is { } player && player.Relics.Contains(model);

    /// <summary>Refait le tableau quand une stat change, tant que l'infobulle est affichée ; la colonne suit sa taille.</summary>
    private static void KeepUpdated(RelicModel model, CardRunStats stats, VFlowContainer column, PanelContainer panel, GridContainer grid)
    {
        bool dirty = false;
        void OnChanged() => dirty = true;
        CardRunStats.AnyChanged += OnChanged;
        panel.TreeExiting += () => CardRunStats.AnyChanged -= OnChanged;

        var timer = new Godot.Timer { WaitTime = RefreshSeconds, Autostart = true };
        timer.Timeout += () =>
        {
            if (!dirty || !GodotObject.IsInstanceValid(panel))
                return;
            dirty = false;
            try
            {
                var before = panel.Size;
                // La relique a pu recevoir ses premières stats depuis l'affichage
                StatsTable.Fill(grid, Rows(model, CardRunStats.Of(model) ?? stats));
                panel.ResetSize();
                var after = panel.GetCombinedMinimumSize();
                panel.Size = after;
                column.Size = new Vector2(Math.Max(column.Size.X, after.X), column.Size.Y + after.Y - before.Y);
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] cannot update relic stats: {e.Message}");
            }
        };
        panel.AddChild(timer);
    }

    /// <summary>
    /// Pour chaque stat non nulle : total de la partie, moyennes par combat et par tour depuis que la relique est là,
    /// et ce combat (ou le dernier). Les dégâts et le Block bonus du combat sont suivis de leur part dans ceux du joueur.
    /// </summary>
    private static List<string[]> Rows(RelicModel model, CardRunStats s)
    {
        string Format(decimal value) => value.ToString("0.##");
        var combat = s.SinceCombatStart();
        bool local = model.Owner == CardRunStats.LocalPlayer;

        string Combat(string name)
        {
            if (combat == null)
                return "-";
            decimal value = combat[name];
            // Part du bonus dans les dégâts ou le Block du joueur pendant ce combat (cartes du joueur local)
            string? baseStat = name switch
            {
                CardRunStats.BonusDamage => CardRunStats.Damage,
                CardRunStats.BonusBlock => CardRunStats.Block,
                _ => null,
            };
            if (local && baseStat != null && value > 0 && CardRunStats.CombatTotals[baseStat] is > 0 and var total)
                return $"{Format(value)} ({Math.Round(100 * value / total)}%)";
            return Format(value);
        }

        string[] Row(string name) =>
        [
            name, Format(s[name]),
            s.Combats > 0 ? Format(s[name] / s.Combats) : "-",
            s.Turns > 0 ? Format(s[name] / s.Turns) : "-",
            Combat(name),
        ];

        // Stats non nulles : liées à la relique d'abord, puis, à part, les autres
        var (related, others) = StatRelevance.For(model).Arrange(s);
        var rows = new List<string[]>();
        if (related.Count == 0 && others.Count == 0)
            return rows;
        string combatTitle = CombatManager.Instance.IsInProgress ? "This combat" : "Last combat";
        rows.Add(["", "Total", "Per combat", "Per turn", combatTitle]);
        rows.AddRange(related.Select(Row));
        if (others.Count > 0)
        {
            rows.Add(["Other"]);
            rows.AddRange(others.Select(Row));
        }
        return rows;
    }

    /// <summary>Même encadré que les stats à côté d'une carte, au moins à la largeur des infobulles du jeu.</summary>
    private static PanelContainer Panel(GridContainer grid)
    {
        var panel = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(NHoverTipSet.hoverTipWidth, 0),
        };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.05f, 0.08f, 0.95f),
            BorderColor = new Color(StsColors.gold, 0.5f),
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 8, ContentMarginBottom = 8,
        });
        panel.AddChild(grid);
        return panel;
    }
}
