using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace StsTools.Game;

/// <summary>
/// Envoi d'un rapport de plantage (<see cref="BugReport"/>) dans un salon Discord, par un webhook : un message qui
/// résume le plantage, avec le dossier du rapport en zip. L'adresse du webhook est mise dans la DLL à la compilation
/// (CrashWebhook dans StsTools.local.props, jamais commité) ; sans elle, rien n'est envoyé.
/// Chaque rapport à envoyer porte un marqueur (upload.pending) jusqu'à son envoi (upload.sent) : un envoi raté est
/// retenté au lancement suivant.
/// </summary>
public static class CrashUpload
{
    private const string PendingMarker = "upload.pending";
    private const string SentMarker = "upload.sent";

    /// <summary>Taille maximale d'un fichier joint par un webhook Discord (10 Mo), avec de la marge.</summary>
    private const long MaxZip = 9_500_000;

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    /// <summary>Rapports en cours d'envoi : pas deux envois du même.</summary>
    private static readonly HashSet<string> Sending = [];

    private static string? Webhook => WebhookNamed("CrashWebhook");

    /// <summary>Adresse d'un webhook mise dans la DLL à la compilation (StsTools.local.props), sinon null.</summary>
    internal static string? WebhookNamed(string key) =>
        typeof(CrashUpload).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } url ? url : null;

    /// <summary>Le mod a été compilé avec un webhook : il peut envoyer ses rapports.</summary>
    public static bool Available => Webhook != null;

    /// <summary>
    /// Envoie le rapport en arrière-plan ; <paramref name="done"/> est appelé sur le fil du jeu avec le résultat.
    /// Le rapport reste marqué à envoyer tant que l'envoi n'a pas réussi.
    /// </summary>
    public static void Send(string report, Action<bool>? done = null)
    {
        if (Webhook is not { } url)
        {
            done?.Invoke(false);
            return;
        }
        lock (Sending)
            if (!Sending.Add(report))
                return;
        try
        {
            File.WriteAllText(Path.Combine(report, PendingMarker), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch (Exception)
        {
            // Sans marqueur, pas de nouvel essai au prochain lancement : l'envoi a lieu quand même
        }
        // Lu sur le fil du jeu : la plateforme (Steam) n'est pas appelée depuis l'envoi en arrière-plan
        var player = PlayerTag();
        Task.Run(async () =>
        {
            bool ok = false;
            try
            {
                ok = await Upload(url, report, player);
                if (ok)
                    File.WriteAllText(Path.Combine(report, SentMarker), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] cannot send the bug report {report}: {e.Message}");
            }
            finally
            {
                lock (Sending)
                    Sending.Remove(report);
            }
            BugReport.Note($"bug report {Path.GetFileName(report)} {(ok ? "sent" : "not sent")}");
            if (done != null)
                Callable.From(() => done(ok)).CallDeferred();
        });
    }

    /// <summary>Rapports que le joueur a choisi d'envoyer mais qui ne sont pas partis (pas de réseau…) : nouvel essai.</summary>
    public static void RetryPending(string? except)
    {
        if (!Available || !Directory.Exists(BugReport.Root))
            return;
        foreach (var report in Directory.GetDirectories(BugReport.Root, "crash-*"))
            if (report != except && File.Exists(Path.Combine(report, PendingMarker)) && !File.Exists(Path.Combine(report, SentMarker)))
                Send(report);
    }

    private static Task<bool> Upload(string url, string report, string? player) =>
        Post(url, Summary(report, player), Zip(report), $"{Path.GetFileName(report)}.zip");

    /// <summary>Un message dans le salon du webhook, avec un zip joint s'il y en a un.</summary>
    internal static async Task<bool> Post(string url, string content, byte[]? zip, string zipName)
    {
        var payload = JsonSerializer.Serialize(new
        {
            content,
            // Aucune mention (@everyone…) même si le journal en contient
            allowed_mentions = new { parse = Array.Empty<string>() },
        });
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
        if (zip != null)
        {
            var file = new ByteArrayContent(zip);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(file, "files[0]", zipName);
        }
        using var response = await Http.PostAsync(url + (url.Contains('?') ? "&" : "?") + "wait=true", form);
        if (response.IsSuccessStatusCode)
            return true;
        Log.Warn($"[{ModEntry.Id}] webhook upload refused: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return false;
    }

    /// <summary>Le dossier du rapport en zip, sous la taille permise (<see cref="ZipFiles"/>).</summary>
    private static byte[] Zip(string report) =>
        ZipFiles(Directory.GetFiles(report, "*", SearchOption.AllDirectories)
            .Select(path => (Path.GetRelativePath(report, path).Replace('\\', '/'), path))
            .Where(f => !f.Item1.StartsWith("upload.")));

    /// <summary>
    /// Fichiers en zip (nom dans le zip, chemin), sous la taille permise : tout ; sinon sans les sauvegardes (saves/) ;
    /// sinon avec seulement la fin des gros fichiers (la fin d'un log est ce qui compte).
    /// </summary>
    internal static byte[] ZipFiles(IEnumerable<(string Name, string Path)> files)
    {
        var list = files.Where(f => File.Exists(f.Path)).ToList();
        byte[] zip = BuildZip(list, saves: true, maxFile: null);
        if (zip.Length <= MaxZip)
            return zip;
        zip = BuildZip(list, saves: false, maxFile: null);
        foreach (long max in new long[] { 6_000_000, 3_000_000, 1_000_000, 300_000 })
        {
            if (zip.Length <= MaxZip)
                break;
            zip = BuildZip(list, saves: false, maxFile: max);
        }
        return zip;
    }

    private static byte[] BuildZip(List<(string Name, string Path)> files, bool saves, long? maxFile)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var (relative, path) in files)
            {
                if (!saves && relative.StartsWith("saves/"))
                    continue;
                var entry = archive.CreateEntry(relative, CompressionLevel.SmallestSize);
                using var output = entry.Open();
                using var input = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
                // Fichier trop gros : seulement sa fin
                if (maxFile is { } max && input.Length > max)
                {
                    input.Seek(-max, SeekOrigin.End);
                    output.Write(Encoding.UTF8.GetBytes($"[... start cut, last {max / 1000} KB only ...]\n"));
                }
                input.CopyTo(output);
            }
        }
        return memory.ToArray();
    }

    /// <summary>
    /// Message Discord : le rapport, ce qu'on sait du plantage (gel, aperçu non terminé), la partie et le combat
    /// en cours, et les dernières lignes du journal ; au plus 2000 caractères, la limite de Discord.
    /// </summary>
    private static string Summary(string report, string? player)
    {
        var journal = Path.Combine(report, "journal.log");
        var lines = JournalLines(journal);
        var text = new StringBuilder();
        text.AppendLine($"**Crash report** `{Path.GetFileName(report)}`" + (player != null ? $" from {player}" : ""));
        text.Append(Context(lines));
        if (File.Exists(Path.Combine(report, "freeze.txt")))
            text.AppendLine("Freeze detected (freeze.txt)");
        if (File.Exists(Path.Combine(report, "preview.log")))
            text.AppendLine("Card preview not finished (preview.log)");
        var tail = new StringBuilder();
        foreach (var line in lines.Reverse().Take(12).Reverse())
            tail.AppendLine(Clip(line.Replace("```", "'''"), 160));
        int room = 1990 - text.Length - 8;
        if (room > 100 && tail.Length > 0)
        {
            var last = tail.ToString();
            if (last.Length > room)
                last = last[^room..];
            text.Append("```\n").Append(last).Append("```");
        }
        var message = text.ToString();
        return message.Length > 2000 ? message[..2000] : message;
    }

    /// <summary>Dernières lignes d'un journal de session (<see cref="BugReport"/>).</summary>
    internal static string[] JournalLines(string journal) =>
        File.Exists(journal) ? ReadTail(journal, 200_000).Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];

    /// <summary>Contexte d'un journal : début de session (versions), dernière partie et dernier combat.</summary>
    internal static string Context(string[] lines)
    {
        var text = new StringBuilder();
        if (lines.FirstOrDefault(l => l.Contains("session start:")) is { } start)
            text.AppendLine(Clip(start, 300));
        if (lines.LastOrDefault(l => l.Contains(" run: ")) is { } run)
            text.AppendLine(Clip(run, 300));
        if (lines.LastOrDefault(l => l.Contains(" combat: ")) is { } combat)
            text.AppendLine(Clip(combat, 300));
        return text.ToString();
    }

    /// <summary>
    /// Joueur local sur sa plateforme (Steam) : pseudo et identifiant, pour regrouper les rapports d'une même personne ;
    /// null si la plateforme ne répond pas.
    /// </summary>
    internal static string? PlayerTag()
    {
        try
        {
            var platform = MegaCrit.Sts2.Core.Platform.PlatformUtil.PrimaryPlatform;
            ulong id = MegaCrit.Sts2.Core.Platform.PlatformUtil.GetLocalPlayerId(platform);
            if (id == 0)
                return null;
            string name = MegaCrit.Sts2.Core.Platform.PlatformUtil.GetPlayerNameRaw(platform, id).Replace("`", "'");
            return $"{platform} `{Clip(name, 40)}` id `{id}`";
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string Clip(string line, int max) => line.Length > max ? line[..max] + "…" : line;

    private static string ReadTail(string path, long max)
    {
        using var input = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
        if (input.Length > max)
            input.Seek(-max, SeekOrigin.End);
        using var reader = new StreamReader(input);
        return reader.ReadToEnd();
    }
}
