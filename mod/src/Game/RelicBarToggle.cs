using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace StsTools.Game;

/// <summary>
/// Touche <see cref="ModHotkeys.RelicBar"/> : cache ou réaffiche la barre des reliques sous la barre du haut (elle
/// peut couvrir l'écran avec beaucoup de reliques). Les reliques restent actives. Tant qu'elle est cachée, le jeu ne
/// la refait pas apparaître (il la montre et la cache lui-même autour de certains écrans).
/// </summary>
public static class RelicBarToggle
{
    private static bool _hidden;

    public static void Toggle()
    {
        try
        {
            if (NRun.Instance?.GlobalUi.RelicInventory is not { } inventory)
                return;
            _hidden = !_hidden;
            if (_hidden)
                inventory.AnimHide();
            else
                inventory.AnimShow();
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot toggle the relic bar: {e.Message}");
        }
    }

    /// <summary>Nouvelle partie ou partie reprise : la barre repart affichée.</summary>
    [HarmonyPatch(typeof(NRelicInventory), nameof(NRelicInventory.Initialize))]
    public static class ResetPatch
    {
        public static void Postfix() => _hidden = false;
    }

    [HarmonyPatch(typeof(NRelicInventory), nameof(NRelicInventory.AnimShow))]
    public static class AnimShowPatch
    {
        public static bool Prefix() => !_hidden;
    }

    [HarmonyPatch(typeof(NRelicInventory), nameof(NRelicInventory.ShowImmediately))]
    public static class ShowImmediatelyPatch
    {
        public static bool Prefix() => !_hidden;
    }
}
