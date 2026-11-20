using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace StsTools.Game;

/// <summary>
/// Garde-fous des rapports envoyés sur Discord (<see cref="CrashUpload"/>, <see cref="BugReportDialog"/>), gardés dans
/// user://StsTools/reports/sent.json :
/// <list type="bullet">
/// <item>pas de spam : au plus <see cref="BugsPerHour"/> rapports de bug par heure, <see cref="BugDelay"/> entre deux, pas
/// deux fois la même description en 24 h ; au plus <see cref="CrashesPerHour"/> plantages envoyés par heure (les autres
/// partent au lancement suivant) ;</item>
/// <item>pas de doublon : un plantage déjà signalé (même signature : version du mod, dernière exception ou gel, aperçu
/// non terminé, combat) n'envoie pas un nouveau message avec son zip, il incrémente un compteur sur le premier message
/// (modifié par le webhook) ;</item>
/// <item>regroupement : dans un salon forum, chaque joueur a son fil (créé au premier envoi, nommé d'après son pseudo et
/// son id Steam), où arrivent tous ses rapports ; dans un salon ordinaire, messages ordinaires.</item>
/// </list>
/// Limites côté joueur seulement : un webhook récupéré dans la DLL peut toujours être utilisé à la main.
/// </summary>
public static class ReportLimits
{
    public const int BugsPerHour = 5;
    public static readonly TimeSpan BugDelay = TimeSpan.FromSeconds(60);
    public const int CrashesPerHour = 3;

    private sealed class Sent
    {
        public string Kind { get; set; } = "";
        public DateTime Time { get; set; }
        /// <summary>Empreinte de la description (rapports de bug)</summary>
        public string? Hash { get; set; }
    }

    private sealed class CrashMessage
    {
        public string MessageId { get; set; } = "";
        public string? ThreadId { get; set; }
        public string Content { get; set; } = "";
        public int Count { get; set; } = 1;
        public DateTime Last { get; set; }
    }

    private sealed class State
    {
        public List<Sent> History { get; set; } = [];
        /// <summary>Signature d'un plantage → son message (par webhook : « CrashWebhook|signature »)</summary>
        public Dictionary<string, CrashMessage> Crashes { get; set; } = [];
        /// <summary>Fil du joueur dans le forum de chaque webhook</summary>
        public Dictionary<string, string> Threads { get; set; } = [];
        /// <summary>Webhooks dont le salon n'est pas un forum (pas de fil)</summary>
        public List<string> NoForum { get; set; } = [];
    }

    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    private static string FilePath => ProjectSettings.GlobalizePath("user://StsTools/reports/sent.json");

    private static State Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath), Json) ?? new State();
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot read sent reports, starting over: {e.Message}");
        }
        return new State();
    }

    private static void Update(Action<State> change)
    {
        lock (Lock)
        {
            var state = Load();
            change(state);
            // Historique d'un jour suffit pour les limites
            state.History.RemoveAll(s => s.Time < DateTime.Now.AddDays(-1));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(state, Json));
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] cannot save sent reports: {e.Message}");
            }
        }
    }

    private static State Read()
    {
        lock (Lock)
            return Load();
    }

    // ---- Limites ----

    private static string Hash(string text) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text.Trim().ToLowerInvariant())))[..16];

    /// <summary>Pourquoi ce rapport de bug ne peut pas partir maintenant (texte pour le joueur), ou null.</summary>
    public static string? BugRefusal(string description)
    {
        var now = DateTime.Now;
        var bugs = Read().History.Where(s => s.Kind == "bug").ToList();
        if (bugs.Any(s => s.Hash == Hash(description) && s.Time > now.AddDays(-1)))
            return "This report was already sent. Thank you!";
        if (bugs.Count(s => s.Time > now.AddHours(-1)) >= BugsPerHour)
            return $"You sent {BugsPerHour} reports in the last hour. Please try again later.";
        if (bugs.Count > 0 && bugs.Max(s => s.Time) is var last && now - last < BugDelay)
            return $"Please wait {(int)Math.Ceiling((BugDelay - (now - last)).TotalSeconds)} s before sending another report.";
        return null;
    }

    /// <summary>
    /// Envoi commencé : compté tout de suite (pas une fois parti), pour qu'un deuxième appui pendant l'envoi soit refusé.
    /// </summary>
    public static void BugStarted(string description) =>
        Update(s => s.History.Add(new Sent { Kind = "bug", Time = DateTime.Now, Hash = Hash(description) }));

    /// <summary>Envoi raté : il ne compte pas (le joueur peut réessayer).</summary>
    public static void BugFailed(string description) =>
        Update(s =>
        {
            var hash = Hash(description);
            if (s.History.FindLastIndex(x => x.Kind == "bug" && x.Hash == hash) is var index and >= 0)
                s.History.RemoveAt(index);
        });

    /// <summary>Trop de plantages envoyés dans l'heure : celui-ci attend le lancement suivant.</summary>
    public static bool CrashLimited() =>
        Read().History.Count(s => s.Kind == "crash" && s.Time > DateTime.Now.AddHours(-1)) >= CrashesPerHour;

    // ---- Doublons de plantages ----

    private static readonly Regex Numbers = new(@"0x[0-9a-fA-F]+|\d+", RegexOptions.Compiled);

    /// <summary>
    /// Ce qui distingue un plantage d'un autre, d'après son rapport : version du mod, dernière exception (sans les
    /// nombres), gel et état de l'aperçu, aperçu de carte non terminé, combat en cours.
    /// </summary>
    public static string CrashSignature(string report)
    {
        var lines = CrashUpload.JournalLines(Path.Combine(report, "journal.log"));
        string Clean(string text) => Numbers.Replace(text, "#").Trim();
        var parts = new List<string>();
        if (lines.FirstOrDefault(l => l.Contains("session start:")) is { } start)
            parts.Add(Regex.Replace(start, @"^\S+\s+", ""));
        if (lines.LastOrDefault(l => l.Contains("UNHANDLED EXCEPTION") || l.Contains("unobserved task exception")) is { } error)
            parts.Add(Clean(error[(error.IndexOf(':') + 1)..]).Split(" at ")[0]);
        if (File.Exists(Path.Combine(report, "freeze.txt")))
            parts.Add("freeze " + Clean(lines.LastOrDefault(l => l.Contains("FREEZE:"))?.Split("mod activity:").ElementAtOrDefault(1) ?? ""));
        if (File.Exists(Path.Combine(report, "preview.log")))
            parts.Add("preview not finished");
        if (lines.LastOrDefault(l => l.Contains(" combat: ")) is { } combat)
            parts.Add(combat[(combat.IndexOf(" combat: ") + 9)..].Split(',')[0]);
        if (parts.Count <= 1)
            parts.Add("silent " + Clean(lines.LastOrDefault(l => !l.Contains("[game ")) ?? ""));
        return Hash(string.Join(" | ", parts));
    }

    /// <summary>
    /// Plantage déjà signalé par ce joueur : le compteur de son premier message est mis à jour (pas de nouveau message ni
    /// de zip). Faux s'il est nouveau, ou si le premier message n'a pas pu être modifié (supprimé…).
    /// </summary>
    public static async Task<bool> CountAgain(string key, string url, string signature)
    {
        if (!Read().Crashes.TryGetValue($"{key}|{signature}", out var known))
            return false;
        int count = known.Count + 1;
        var content = $"{known.Content}\n**Seen again**: {count} times in all, last {DateTime.Now:yyyy-MM-dd HH:mm}";
        if (content.Length > 2000)
            content = content[..2000];
        var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Patch,
            $"{url}/messages/{known.MessageId}" + (known.ThreadId != null ? $"?thread_id={known.ThreadId}" : ""))
        {
            Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new
            {
                content,
                allowed_mentions = new { parse = Array.Empty<string>() },
            }), Encoding.UTF8, "application/json"),
        };
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warn($"[{ModEntry.Id}] cannot update the crash message: {(int)response.StatusCode}");
            Update(s => s.Crashes.Remove($"{key}|{signature}"));
            return false;
        }
        Update(s =>
        {
            if (s.Crashes.TryGetValue($"{key}|{signature}", out var crash))
            {
                crash.Count = count;
                crash.Last = DateTime.Now;
            }
            s.History.Add(new Sent { Kind = "crash-repeat", Time = DateTime.Now });
        });
        return true;
    }

    public static void CrashSent(string key, string signature, PostResult result, string content) =>
        Update(s =>
        {
            s.History.Add(new Sent { Kind = "crash", Time = DateTime.Now });
            if (result.MessageId != null)
                s.Crashes[$"{key}|{signature}"] = new CrashMessage
                {
                    MessageId = result.MessageId, ThreadId = result.ThreadId, Content = content, Last = DateTime.Now,
                };
        });

    // ---- Envoi, dans le fil du joueur si le salon est un forum ----

    public readonly record struct PostResult(bool Ok, string? MessageId, string? ThreadId);

    /// <summary>
    /// Un message par le webhook <paramref name="key"/>, avec un zip s'il y en a un. Salon forum : dans le fil du joueur
    /// (créé au premier envoi sous le nom <paramref name="threadName"/>) ; salon ordinaire : message ordinaire (refus du
    /// fil retenu pour les envois suivants).
    /// </summary>
    public static async Task<PostResult> Post(string key, string url, string content, byte[]? zip, string zipName, string? threadName)
    {
        var state = Read();
        bool forum = threadName != null && !state.NoForum.Contains(key);
        state.Threads.TryGetValue(key, out var thread);
        var (status, body) = await Send(url, content, zip, zipName, forum ? thread : null, forum && thread == null ? threadName : null);
        // Fil supprimé : on en recrée un
        if (forum && thread != null && status is 400 or 404)
        {
            Update(s => s.Threads.Remove(key));
            (status, body) = await Send(url, content, zip, zipName, null, threadName);
            thread = null;
        }
        // Salon ordinaire : pas de fil, message ordinaire
        if (forum && thread == null && status == 400)
        {
            Update(s =>
            {
                if (!s.NoForum.Contains(key))
                    s.NoForum.Add(key);
            });
            (status, body) = await Send(url, content, zip, zipName, null, null);
            forum = false;
        }
        if (status is < 200 or >= 300)
        {
            Log.Warn($"[{ModEntry.Id}] webhook upload refused: {status} {body}");
            return new PostResult(false, null, null);
        }
        string? messageId = null, channelId = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            messageId = json.RootElement.GetProperty("id").GetString();
            channelId = json.RootElement.GetProperty("channel_id").GetString();
        }
        catch (Exception)
        {
            // Réponse inattendue : le message est parti, sans id
        }
        // Premier message dans le forum : son salon est le fil créé pour ce joueur
        if (forum && thread == null && channelId != null)
            Update(s => s.Threads[key] = channelId);
        return new PostResult(true, messageId, forum ? channelId : null);
    }

    private static async Task<(int Status, string Body)> Send(string url, string content, byte[]? zip, string zipName,
        string? threadId, string? threadName)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["content"] = content,
            // Aucune mention (@everyone…) même si le journal en contient
            ["allowed_mentions"] = new { parse = Array.Empty<string>() },
            ["thread_name"] = threadName,
        }.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value));
        using var form = new System.Net.Http.MultipartFormDataContent();
        form.Add(new System.Net.Http.StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
        if (zip != null)
        {
            var file = new System.Net.Http.ByteArrayContent(zip);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(file, "files[0]", zipName);
        }
        var query = "wait=true" + (threadId != null ? $"&thread_id={threadId}" : "");
        using var response = await Http.PostAsync(url + (url.Contains('?') ? "&" : "?") + query, form);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Plantage envoyé par le relais : il compte pour la limite par heure.</summary>
    public static void CrashCounted() => Update(s => s.History.Add(new Sent { Kind = "crash", Time = DateTime.Now }));

    /// <summary>
    /// Un rapport envoyé au relais (relay/) : il vérifie, limite, regroupe et compte les doublons, puis le transmet à
    /// Discord. Faux s'il le refuse (429 : trop de rapports ; le plantage repart au lancement suivant).
    /// </summary>
    public static async Task<bool> PostRelay(string relay, string kind, string content, byte[]? zip, string zipName,
        (string Platform, string Name, ulong Id)? player, string? signature)
    {
        var payload = JsonSerializer.Serialize(new
        {
            kind,
            content,
            player = player is { } p ? new { id = p.Id.ToString(), name = p.Name } : null,
            signature,
        });
        using var form = new System.Net.Http.MultipartFormDataContent();
        // Noms entre guillemets : Cloudflare ne lit pas le formulaire sinon (.NET les écrit sans)
        form.Add(new System.Net.Http.StringContent(payload, Encoding.UTF8, "application/json"), "\"payload_json\"");
        if (zip != null)
        {
            var file = new System.Net.Http.ByteArrayContent(zip);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(file, "\"files[0]\"", $"\"{zipName}\"");
        }
        using var response = await Http.PostAsync(relay, form);
        var body = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
        {
            if (body.Contains("\"duplicate\":true") || body.Contains("\"duplicate\": true"))
                BugReport.Note($"{kind} report counted by the relay as already reported");
            return true;
        }
        Log.Warn($"[{ModEntry.Id}] report relay refused: {(int)response.StatusCode} {body}");
        return false;
    }

    /// <summary>Nom du fil d'un joueur dans un forum : pseudo et id de la plateforme (100 caractères au plus).</summary>
    public static string? ThreadName()
    {
        if (CrashUpload.PlayerIdentity() is not { } player)
            return null;
        var name = $"{player.Name} ({player.Id})";
        return name.Length > 100 ? name[..100] : name;
    }
}
