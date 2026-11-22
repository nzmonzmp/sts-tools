using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace StsTools.Game;

/// <summary>
/// Onglet du GUI choisi tout seul au début d'un combat, à sa fin, au début de chaque tour du joueur et en arrivant
/// chez un marchand, selon les options du mod (« None » par défaut : rien ne change).
/// </summary>
public static class AutoTabs
{
    /// <summary>
    /// Onglets proposés : pas Path finder, qui ne s'ouvre ni en combat, ni aux récompenses, ni chez un marchand. Puis
    /// les sous-onglets qu'on peut cacher, en « Onglet/Sous-onglet » (« Combat/End turn »).
    /// </summary>
    public static readonly string[] Choices =
    [
        .. ModOptions.Tabs.Where(t => t != "Path finder"),
        .. ModOptions.SubTabs.Select(s => $"{s.Tab}/{s.SubTab}"),
    ];

    /// <summary>Texte d'un choix dans les options : « Combat: End turn » pour un sous-onglet.</summary>
    public static string Label(string choice) => choice.Replace("/", ": ");

    /// <summary>Onglet et sous-onglet d'un choix (sous-onglet null : celui de l'onglet ne change pas).</summary>
    public static (string Tab, string? SubTab) Split(string choice) =>
        choice.IndexOf('/') is var i and >= 0 ? (choice[..i], choice[(i + 1)..]) : (choice, null);

    /// <summary>Onglet réglé, s'il fait partie des choix (une valeur gardée d'avant, Path finder, est ignorée).</summary>
    private static string? Valid(string? tab) => tab != null && Choices.Contains(tab) ? tab : null;

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatStart))]
    public static class CombatStartPatch
    {
        public static void Prefix(IRunState runState, ICombatState? combatState)
        {
            if (!PlaySimulation.Active)
                RunPanel.AutoSelectTab(Valid(ModOptions.TabOnCombatStart), "combat start");
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCombatEnd))]
    public static class CombatEndPatch
    {
        public static void Prefix(IRunState runState, ICombatState? combatState, CombatRoom room)
        {
            if (!PlaySimulation.Active)
                RunPanel.AutoSelectTab(Valid(ModOptions.TabOnCombatEnd), "combat end");
        }
    }

    /// <summary>Début du tour du joueur local (pas ceux des coéquipiers, ni ceux d'une simulation).</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
    public static class TurnStartPatch
    {
        public static void Prefix(ICombatState combatState, Player player)
        {
            if (!PlaySimulation.Active && ModOptions.TabOnTurnStart != null && LocalContext.GetMe(player.RunState) == player)
                RunPanel.AutoSelectTab(Valid(ModOptions.TabOnTurnStart), "turn start");
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterRoomEntered))]
    public static class MerchantPatch
    {
        public static void Prefix(IRunState runState, AbstractRoom room)
        {
            if (room is MerchantRoom && !PlaySimulation.Active)
                RunPanel.AutoSelectTab(Valid(ModOptions.TabOnMerchant), "merchant");
        }
    }
}
