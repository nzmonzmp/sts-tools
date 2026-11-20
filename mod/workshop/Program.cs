// Usage : Workshop <dossier du mod> <image d'aperçu> <titre> <fichier description> <fichier id> <public|friends|private|unlisted> [note de version]
// Crée l'item la première fois (son id est gardé dans <fichier id>), puis le met à jour.
// Ou : Workshop subscribe|unsubscribe|status <fichier id> (abonnement du compte Steam connecté, pour tester).
using Steamworks;

if (args[0] is "subscribe" or "unsubscribe" or "status")
    return Subscription(args[0], new PublishedFileId_t(ulong.Parse(File.ReadAllText(args[1]).Trim())));

var (contentDir, preview, title, descFile, idFile, visArg) = (Path.GetFullPath(args[0]), Path.GetFullPath(args[1]),
    args[2], args[3], args[4], args[5]);
var changeNote = args.Length > 6 ? args[6] : "";
var appId = new AppId_t(2868840);

var init = SteamAPI.InitEx(out var initMsg);
if (init != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
{
    Console.Error.WriteLine($"SteamAPI.Init a échoué ({init} : {initMsg}) : Steam est-il lancé et connecté ?");
    return 1;
}
Console.WriteLine($"Connecté à Steam : {SteamFriends.GetPersonaName()} ({SteamUser.GetSteamID()})");

T Wait<T>(SteamAPICall_t call) where T : struct
{
    T? result = null;
    var cr = CallResult<T>.Create((r, failed) =>
    {
        if (failed) throw new Exception($"Appel Steam {typeof(T).Name} en échec");
        result = r;
    });
    cr.Set(call);
    while (result == null)
    {
        SteamAPI.RunCallbacks();
        Thread.Sleep(100);
    }
    return result.Value;
}

PublishedFileId_t id;
if (File.Exists(idFile))
{
    id = new PublishedFileId_t(ulong.Parse(File.ReadAllText(idFile).Trim()));
    Console.WriteLine($"Mise à jour de l'item {id}");
}
else
{
    var created = Wait<CreateItemResult_t>(SteamUGC.CreateItem(appId, EWorkshopFileType.k_EWorkshopFileTypeCommunity));
    if (created.m_eResult != EResult.k_EResultOK)
    {
        Console.Error.WriteLine($"CreateItem : {created.m_eResult}");
        return 1;
    }
    id = created.m_nPublishedFileId;
    File.WriteAllText(idFile, id + "\n");
    Console.WriteLine($"Item créé : {id}");
    if (created.m_bUserNeedsToAcceptWorkshopLegalAgreement)
        Console.WriteLine("ACCORD: il faut accepter l'accord du Workshop pour que l'item soit visible.");
}

var vis = visArg switch
{
    "public" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic,
    "friends" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly,
    "unlisted" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityUnlisted,
    _ => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPrivate,
};

var h = SteamUGC.StartItemUpdate(appId, id);
SteamUGC.SetItemTitle(h, title);
SteamUGC.SetItemDescription(h, File.ReadAllText(descFile));
SteamUGC.SetItemContent(h, contentDir);
SteamUGC.SetItemPreview(h, preview);
SteamUGC.SetItemVisibility(h, vis);
SteamUGC.SetItemTags(h, new List<string> { "Mod" });

var submitted = SubmitAndWait(h);
Console.WriteLine($"SubmitItemUpdate : {submitted.m_eResult}");
if (submitted.m_bUserNeedsToAcceptWorkshopLegalAgreement)
    Console.WriteLine("ACCORD: il faut accepter l'accord du Workshop pour que l'item soit visible.");
Console.WriteLine($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}");
SteamAPI.Shutdown();
return submitted.m_eResult == EResult.k_EResultOK ? 0 : 1;

SubmitItemUpdateResult_t SubmitAndWait(UGCUpdateHandle_t handle)
{
    SubmitItemUpdateResult_t? result = null;
    var cr = CallResult<SubmitItemUpdateResult_t>.Create((r, failed) => result = r);
    cr.Set(SteamUGC.SubmitItemUpdate(handle, changeNote));
    var last = "";
    while (result == null)
    {
        SteamAPI.RunCallbacks();
        var st = SteamUGC.GetItemUpdateProgress(handle, out var done, out var total);
        var line = $"{st} {done}/{total}";
        if (line != last) Console.WriteLine(last = line);
        Thread.Sleep(200);
    }
    return result.Value;
}

static int Subscription(string action, PublishedFileId_t id)
{
    var init = SteamAPI.InitEx(out var initMsg);
    if (init != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
    {
        Console.Error.WriteLine($"SteamAPI.Init a échoué ({init} : {initMsg})");
        return 1;
    }
    EResult? result = null;
    CallResult<RemoteStorageSubscribePublishedFileResult_t>? sub = null;
    CallResult<RemoteStorageUnsubscribePublishedFileResult_t>? unsub = null;
    if (action == "subscribe")
    {
        sub = CallResult<RemoteStorageSubscribePublishedFileResult_t>.Create((r, _) => result = r.m_eResult);
        sub.Set(SteamUGC.SubscribeItem(id));
    }
    else if (action == "unsubscribe")
    {
        unsub = CallResult<RemoteStorageUnsubscribePublishedFileResult_t>.Create((r, _) => result = r.m_eResult);
        unsub.Set(SteamUGC.UnsubscribeItem(id));
    }
    else result = EResult.k_EResultOK;
    while (result == null)
    {
        SteamAPI.RunCallbacks();
        Thread.Sleep(100);
    }
    Console.WriteLine($"{action} {id} : {result}");
    // Après un abonnement, attend que Steam ait téléchargé l'item
    for (int i = 0; i < 600; i++)
    {
        var state = (EItemState)SteamUGC.GetItemState(id);
        var installed = SteamUGC.GetItemInstallInfo(id, out var size, out var folder, 1024, out _);
        if (action != "subscribe" || (state.HasFlag(EItemState.k_EItemStateInstalled) && !state.HasFlag(EItemState.k_EItemStateNeedsUpdate)))
        {
            Console.WriteLine($"état : {state}" + (installed ? $", installé dans {folder} ({size} octets)" : ""));
            break;
        }
        if (i == 0) SteamUGC.DownloadItem(id, true);
        SteamAPI.RunCallbacks();
        Thread.Sleep(500);
    }
    SteamAPI.Shutdown();
    return result == EResult.k_EResultOK ? 0 : 1;
}
