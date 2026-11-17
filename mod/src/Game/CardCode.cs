using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace StsTools.Game;

/// <summary>
/// Ce que fait une carte (ou une relique, un pouvoir, un enchantement) d'après son code : les commandes du jeu
/// qu'elle appelle (attaquer, gagner du Block, piocher, appliquer tel pouvoir…) et les calculs du jeu qu'elle
/// modifie (méthodes redéfinies : ModifyBlockAdditive…). Plus fiable que ses valeurs affichées : la valeur
/// « Cards » sert à piocher comme à défausser, et certains effets n'ont pas de valeur du tout.
/// </summary>
public static class CardCode
{
    /// <param name="PowerTypes">pouvoirs appliqués, par type</param>
    /// <param name="Methods">méthodes déclarées par la classe elle-même (redéfinitions des écouteurs du jeu)</param>
    public sealed record Effects(HashSet<string> Commands, HashSet<string> Powers, bool Readable,
        HashSet<Type> PowerTypes, HashSet<string> Methods);

    private static readonly Dictionary<Type, Effects> Cache = new();

    private const BindingFlags AllDeclared =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>
    /// Commandes appelées par le code de la classe de la carte et de ses classes internes (méthodes async et
    /// lambdas compilées), sous la forme « CardPileCmd.Draw » ; pouvoirs appliqués par nom (« Strength »).
    /// </summary>
    public static Effects Of(CardModel card) => Of(card.GetType());

    /// <summary>Même lecture pour n'importe quelle classe du jeu (relique, pouvoir, enchantement…), sans ses classes parentes.</summary>
    public static Effects Of(Type type)
    {
        if (Cache.TryGetValue(type, out var cached))
            return cached;
        var commands = new HashSet<string>();
        var powers = new HashSet<string>();
        var powerTypes = new HashSet<Type>();
        var methods = type.GetMethods(AllDeclared).Select(m => m.Name).ToHashSet();
        bool readable = true;
        try
        {
            foreach (var method in MethodsOf(type))
            {
                if (method.GetMethodBody() == null)
                    continue;
                foreach (var instruction in PatchProcessor.GetOriginalInstructions(method))
                {
                    if (instruction.operand is not MethodInfo called || called.DeclaringType is not { } owner)
                        continue;
                    commands.Add($"{owner.Name}.{called.Name}");
                    // PowerCmd.Apply<StrengthPower>(…) : le pouvoir est l'argument de type
                    if (owner == typeof(PowerCmd) && called.IsGenericMethod)
                        foreach (var arg in called.GetGenericArguments().Where(t => typeof(PowerModel).IsAssignableFrom(t)))
                        {
                            powers.Add(arg.Name.EndsWith("Power") ? arg.Name[..^"Power".Length] : arg.Name);
                            powerTypes.Add(arg);
                        }
                }
            }
        }
        catch (Exception e)
        {
            readable = false;
            Log.Warn($"[{ModEntry.Id}] cannot read the code of {type.Name}: {e.Message}");
        }
        return Cache[type] = new Effects(commands, powers, readable, powerTypes, methods);
    }

    public static bool Calls(CardModel card, params string[] commands)
    {
        var effects = Of(card);
        return commands.Any(c => c.EndsWith(".*")
            ? effects.Commands.Any(x => x.StartsWith(c[..^1]))
            : effects.Commands.Contains(c));
    }

    /// <summary>Méthodes de la classe et de toutes ses classes internes (machines à états des async, lambdas).</summary>
    private static IEnumerable<MethodBase> MethodsOf(Type type)
    {
        foreach (var method in type.GetMethods(AllDeclared))
            yield return method;
        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (var method in MethodsOf(nested))
                yield return method;
    }
}
