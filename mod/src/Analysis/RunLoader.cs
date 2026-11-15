using System.Text.Json;

namespace StsTools.Analysis;

public static class RunLoader
{
    /// <summary>
    /// Lit les fichiers .run de plusieurs dossiers d'historique. Une partie présente dans plusieurs dossiers
    /// (l'historique non moddé est copié vers le dossier moddé au premier lancement avec mods) n'est lue qu'une fois.
    /// </summary>
    /// <param name="localPlayerId">identifiant Steam du joueur, pour retrouver son personnage en coop</param>
    /// <param name="errors">fichiers illisibles, ignorés</param>
    public static List<Run> Load(IEnumerable<string> historyDirs, ulong localPlayerId, List<string>? errors = null)
    {
        var runs = new List<Run>();
        var seen = new HashSet<string>();
        foreach (var dir in historyDirs)
        {
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.run").Order())
            {
                if (!seen.Add(Path.GetFileName(file)))
                    continue;
                try
                {
                    var data = JsonDocument.Parse(File.ReadAllBytes(file)).RootElement;
                    var players = Run.Array(data, "players").ToList();
                    // En solo l'id vaut 1 ; en coop on retrouve son personnage via l'id Steam
                    JsonElement? me = players.Count == 1
                        ? players[0]
                        : players.Cast<JsonElement?>().FirstOrDefault(p =>
                            p!.Value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                            && id.TryGetUInt64(out var v) && v == localPlayerId);
                    if (me != null)
                        runs.Add(new Run(data, me.Value));
                }
                catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
                {
                    errors?.Add($"{file}: {e.Message}");
                }
            }
        }
        return runs;
    }
}
