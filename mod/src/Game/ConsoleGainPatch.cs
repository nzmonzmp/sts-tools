using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace StsTools.Game;

/// <summary>
/// Console du jeu : taper le nom d'une relique ou d'une carte (« Lizard Tail », « lizardtail », « LIZARD_TAIL »)
/// au lieu d'une commande la fait gagner : la relique est prise comme en jeu (effet de prise compris), la carte
/// ajoutée au deck. Les vraies commandes passent avant.
/// </summary>
[HarmonyPatch(typeof(DevConsole), "ProcessCommand", [typeof(Player), typeof(string), typeof(string[])])]
public static class ConsoleGainPatch
{
    private static readonly AccessTools.FieldRef<DevConsole, Dictionary<string, MegaCrit.Sts2.Core.DevConsole.ConsoleCommands.AbstractConsoleCmd>> Commands =
        AccessTools.FieldRefAccess<DevConsole, Dictionary<string, MegaCrit.Sts2.Core.DevConsole.ConsoleCommands.AbstractConsoleCmd>>("_commands");

    /// <summary>Nom réduit à ses lettres et chiffres, en minuscules : « Lizard Tail » = « LIZARD_TAIL ».</summary>
    private static string Key(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public static bool Prefix(DevConsole __instance, Player? player, string cmdName, string[] args, ref CmdResult __result)
    {
        try
        {
            if (cmdName.Equals("help") || Commands(__instance).ContainsKey(cmdName.ToLowerInvariant()))
                return true;
            if (player == null || !RunManager.Instance.IsInProgress)
                return true;
            string key = Key(string.Join(" ", args.Prepend(cmdName)));
            if (key.Length == 0)
                return true;

            var relic = ModelDb.AllRelics.FirstOrDefault(r => Key(r.Id.Entry) == key || Key(SafeTitle(r)) == key);
            if (relic != null)
            {
                __result = new CmdResult(RelicCmd.Obtain(relic.ToMutable(), player), success: true, $"Obtained relic '{SafeTitle(relic)}'");
                return false;
            }
            var card = ModelDb.AllCards.FirstOrDefault(c => Key(c.Id.Entry) == key || Key(c.Title) == key);
            if (card != null)
            {
                var created = player.RunState.CreateCard(card, player);
                __result = new CmdResult(CardPileCmd.Add(created, PileType.Deck), success: true, $"Added card '{card.Title}' to the deck");
                return false;
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot gain from the console: {e.Message}");
        }
        return true;
    }

    private static string SafeTitle(RelicModel relic)
    {
        try
        {
            return relic.Title.GetFormattedText();
        }
        catch (Exception)
        {
            return relic.Id.Entry;
        }
    }
}
