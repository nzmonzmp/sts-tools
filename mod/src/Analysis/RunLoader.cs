using System.Text.Json;
using System.Text.Json.Nodes;

namespace StsTools.Analysis;

public static class RunLoader
{
    /// <summary>
    /// Lit les fichiers .run de plusieurs dossiers d'historique. Une partie présente dans plusieurs dossiers
    /// (l'historique non moddé est copié vers le dossier moddé au premier lancement avec mods) n'est lue qu'une fois.
    /// </summary>
    /// <param name="localPlayerId">identifiant Steam du joueur, pour retrouver son personnage en coop</param>
    /// <param name="errors">fichiers illisibles, ignorés</param>
    /// <param name="renames">identifiants renommés par le jeu (ancien → nouveau), appliqués dans l'ordre comme ses
    /// migrations : sans eux, une carte renommée (Rapid Shot → Twin Shot) serait inconnue dans les anciennes parties</param>
    public static List<Run> Load(IEnumerable<string> historyDirs, ulong localPlayerId, List<string>? errors = null,
        IReadOnlyList<IReadOnlyDictionary<string, string>>? renames = null)
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
                    var data = Parse(File.ReadAllBytes(file), renames);
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

    private static JsonElement Parse(byte[] bytes, IReadOnlyList<IReadOnlyDictionary<string, string>>? renames)
    {
        if (renames is { Count: > 0 })
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            if (renames.Any(r => r.Keys.Any(old => text.Contains($"\"{old}\"", StringComparison.Ordinal))))
            {
                var node = JsonNode.Parse(text);
                foreach (var r in renames)
                    node = Rename(node, r);
                return JsonDocument.Parse(node!.ToJsonString()).RootElement;
            }
        }
        return JsonDocument.Parse(bytes).RootElement;
    }

    /// <summary>Comme le jeu (SharedMigrationHelper.ReplaceModelIds) : toutes les valeurs texte, pas les clés.</summary>
    private static JsonNode? Rename(JsonNode? node, IReadOnlyDictionary<string, string> renames)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s) && renames.TryGetValue(s, out var renamed):
                return JsonValue.Create(renamed);
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    var child = a[i];
                    var replaced = Rename(child, renames);
                    if (!ReferenceEquals(child, replaced))
                        a[i] = replaced;
                }
                break;
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    var child = o[key];
                    var replaced = Rename(child, renames);
                    if (!ReferenceEquals(child, replaced))
                        o[key] = replaced;
                }
                break;
        }
        return node;
    }
}
