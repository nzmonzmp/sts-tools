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
    /// <summary>Onglets proposés : pas Path finder, qui ne s'ouvre ni en combat, ni aux récompenses, ni chez un marchand.</summary>
    public static readonly string[] Choices = ModOptions.Tabs.Where(t => t != "Path finder").ToArray();

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
