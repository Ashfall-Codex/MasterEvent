using MasterEvent.Models;

namespace MasterEvent.Localization;

public static class TurnText
{
    // « Jet d'initiative: 14 (Dextérité +5) = 19 », ou le dé seul sans stat ; rien sans jet.
    public static string? InitiativeTooltip(TurnEntry entry)
        => InitiativeTooltip(entry.InitiativeRoll, entry.InitiativeModifier, entry.InitiativeStatName, entry.Initiative);

    public static string? InitiativeTooltip(int roll, int modifier, string? statName, int total)
    {
        if (roll <= 0) return null;
        if (statName == null)
            return $"{Loc.Get("Turns.InitRoll")}: {roll}";

        var signed = modifier >= 0 ? $"+{modifier}" : modifier.ToString();
        return $"{Loc.Get("Turns.InitRoll")}: {roll} ({statName} {signed}) = {total}";
    }

    // Durée restante d'un bonus temporaire, décomptée à chaque nouvelle manche : « (3 manches) ».
    public static string TempRounds(int rounds)
        => rounds == 1 ? Loc.Get("Turns.TempRoundsOne") : string.Format(Loc.Get("Turns.TempRoundsFmt"), rounds);
}
