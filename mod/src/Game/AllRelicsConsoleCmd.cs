using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace StsTools.Game;

/// <summary>
/// Commande de la console du jeu (active quand le jeu tourne avec des mods) pour tester les stats des reliques :
/// donne toutes les reliques du jeu qu'on n'a pas encore. Par défaut sans leur effet de prise (pas d'écran de
/// choix de carte à retirer, d'amélioration… pour des centaines de reliques) ; « allrelics obtain » (ou « gained »)
/// les prend comme en jeu, une par une.
/// </summary>
public sealed class AllRelicsConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "allrelics";

    public override string Args => "[obtain|gained]";

    public override string Description => "Gives every relic not owned yet (obtain/gained: with their pickup effects)";

    public override bool IsNetworked => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (issuingPlayer == null || !RunManager.Instance.IsInProgress)
            return new CmdResult(success: false, "A run does not appear to be in progress");
        var owned = issuingPlayer.Relics.Select(r => r.Id).ToHashSet();
        var relics = ModelDb.AllRelics.Where(r => !owned.Contains(r.Id)).ToList();
        if (args.Length > 0 && args[0].ToLowerInvariant() is "obtain" or "gained")
        {
            TaskHelper.RunSafely(ObtainAll(relics, issuingPlayer));
            return new CmdResult(success: true, $"Obtaining {relics.Count} relics");
        }
        int added = 0;
        foreach (var relic in relics)
        {
            try
            {
                issuingPlayer.AddRelicInternal(relic.ToMutable());
                added++;
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] cannot add relic {relic.Id}: {e.Message}");
            }
        }
        return new CmdResult(success: true, $"Added {added} relics (without pickup effects)");
    }

    private static async Task ObtainAll(List<RelicModel> relics, Player player)
    {
        foreach (var relic in relics)
        {
            try
            {
                await RelicCmd.Obtain(relic.ToMutable(), player);
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] cannot obtain relic {relic.Id}: {e.Message}");
            }
        }
    }
}
