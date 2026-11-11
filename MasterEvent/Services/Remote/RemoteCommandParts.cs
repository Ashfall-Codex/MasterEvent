using System;
using MasterEvent.Communication.Remote;

namespace MasterEvent.Services.Remote;

// Error : null quand la commande a réussi ; Changed : elle a modifié la scène.
public readonly record struct RemoteOutcome(string? Error, bool Changed)
{
    public bool Ok => Error is null;
    public static RemoteOutcome Fail(string error) => new(error, false);
}

// Ce que les commandes des pages du MJ et du joueur font de la même façon, sur le thread du jeu.
internal static class RemoteCommandParts
{
    // Une erreur due à l'état reçu devient « internal », avec une ligne au journal. detail termine la ligne de débogage.
    public static RemoteOutcome Run(string logPrefix, RemoteCommand command, Func<RemoteOutcome> execute, string detail = "")
    {
        RemoteOutcome outcome;
        try
        {
            outcome = execute();
        }
        // Ce que l'état reçu du relais peut provoquer : marqueur ou entrée de tour hors bornes, collection
        // modifiée, formule de dé du modèle illisible ou trop grande (int.Parse), borne de dé incohérente.
        catch (InvalidOperationException ex) { return Interrupted(logPrefix, command, ex); }
        catch (ArgumentException ex) { return Interrupted(logPrefix, command, ex); }
        catch (IndexOutOfRangeException ex) { return Interrupted(logPrefix, command, ex); }
        catch (FormatException ex) { return Interrupted(logPrefix, command, ex); }
        catch (ArithmeticException ex) { return Interrupted(logPrefix, command, ex); }

        var result = outcome.Error ?? (outcome.Changed ? "fait" : "sans effet");
        Plugin.Log.Debug($"{logPrefix}commande {command.Op} : {result}, état vu {command.Seen}{detail}");
        return outcome;
    }

    // Ni message ni pile : ils peuvent citer ce que la page a envoyé.
    private static RemoteOutcome Interrupted(string logPrefix, RemoteCommand command, Exception ex)
    {
        var site = ex.TargetSite is { } method ? $"{method.DeclaringType?.Name}.{method.Name}" : "?";
        Plugin.Log.Error($"{logPrefix}commande {command.Op} interrompue ({ex.GetType().Name} dans {site})");
        return RemoteOutcome.Fail(RemoteErrors.Internal);
    }
}
