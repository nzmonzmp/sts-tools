using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace StsTools.Game;

/// <summary>Point d'entrée appelé par le ModManager du jeu au démarrage.</summary>
[ModInitializer(nameof(Init))]
public static class ModEntry
{
    public const string Id = "StsTools";

    public static Harmony? Harmony { get; private set; }

    public static void Init()
    {
        // En premier : archive la session précédente si elle a planté, journal de celle-ci
        BugReport.Start();
        Harmony = new Harmony("nzmonzmp." + Id);
        // Seulement les patchs nécessaires avant le menu principal (EarlyPatch) ; les autres une fois le menu affiché
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(ModEntry).Assembly))
            if (type.IsDefined(typeof(EarlyPatchAttribute), false))
                Harmony.CreateClassProcessor(type).Patch();
        Log.Info($"[{Id}] initialized");
    }

    private static bool _latePatched;

    /// <summary>
    /// Tous les autres patchs, posés au premier affichage du menu principal : posés au démarrage, ils ralentissaient
    /// l'initialisation du jeu, qui se bloquait alors (écran noir) une fois sur trois ou quatre avant le menu.
    /// </summary>
    public static void ApplyLatePatches()
    {
        if (_latePatched || Harmony == null)
            return;
        _latePatched = true;
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(ModEntry).Assembly))
            if (type.IsDefined(typeof(HarmonyPatch), false) && !type.IsDefined(typeof(EarlyPatchAttribute), false))
                try
                {
                    Harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception e)
                {
                    Log.Warn($"[{Id}] cannot apply {type.Name}: {e.Message}");
                }
    }
}

/// <summary>
/// Patch posé dès le démarrage du jeu (sauvegardes, menu principal) ; les autres le sont une fois le menu affiché
/// (<see cref="ModEntry.ApplyLatePatches"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class EarlyPatchAttribute : Attribute;
