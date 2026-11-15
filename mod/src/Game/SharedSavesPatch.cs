using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace StsTools.Game;

/// <summary>
/// Garde les sauvegardes normales quand le mod est actif.
/// Avec un mod chargé, le jeu range progression et historique dans un sous-dossier « modded »
/// (UserDataPathProvider.IsRunningModded, lu par GetAccountDir). Il y copie les sauvegardes
/// normales au premier lancement, mais la synchro Steam Cloud supprime ensuite ces copies :
/// on se retrouve avec une progression vide. Ce mod ne change rien au gameplay, donc on le
/// laisse utiliser les sauvegardes normales.
/// ModManager.IsRunningModded() reste vrai : le jeu se sait moddé pour les métriques,
/// Sentry et le multijoueur.
/// </summary>
[HarmonyPatch(typeof(UserDataPathProvider), nameof(UserDataPathProvider.IsRunningModded), MethodType.Setter)]
public static class SharedSavesPatch
{
    // Appelé par OneTimeInitialization juste après l'initialisation des mods
    public static void Prefix(ref bool value)
    {
        if (value)
            Log.Info($"[{ModEntry.Id}] using the unmodded save files");
        value = false;
    }
}

/// <summary>Plus besoin de copier les sauvegardes normales dans le dossier « modded ».</summary>
[HarmonyPatch(typeof(ModManager), nameof(ModManager.CopyUnmoddedSaveFilesIfNeeded))]
public static class SkipSaveCopyPatch
{
    public static bool Prefix() => false;
}
