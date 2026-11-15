using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace StsTools.Game;

/// <summary>Point d'entrée appelé par le ModManager du jeu au démarrage.</summary>
[ModInitializer(nameof(Init))]
public static class ModEntry
{
    public const string Id = "StsTools";

    public static void Init()
    {
        new Harmony("nzmonzmp." + Id).PatchAll(typeof(ModEntry).Assembly);
        Log.Info($"[{Id}] initialisé");
    }
}
