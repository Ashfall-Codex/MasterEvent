using System;
using System.Text.RegularExpressions;

namespace MasterEvent.Services;


// Moteur de dés : parse une formule XdY et lance les dés.

public static partial class DiceEngine
{
    [GeneratedRegex(@"^(\d+)d(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DiceFormulaRegex();

    public static int Roll(string formula)
    {
        var match = DiceFormulaRegex().Match(formula.Trim());
        if (!match.Success)
            return Random.Shared.Next(1, 101); // Fallback 1d100

        var count = int.Parse(match.Groups[1].Value);
        var faces = int.Parse(match.Groups[2].Value);

        if (count < 1) count = 1;
        if (count > 100) count = 100;
        if (faces < 2) faces = 2;
        if (faces > 99999) faces = 99999;

        var total = 0;
        for (var i = 0; i < count; i++)
            total += Random.Shared.Next(1, faces + 1);

        return total;
    }

    // Retourne le maximum possible pour une formule donnée.
    public static int GetMax(string formula)
    {
        var match = DiceFormulaRegex().Match(formula.Trim());
        if (!match.Success)
            return 100;

        var count = int.Parse(match.Groups[1].Value);
        var faces = int.Parse(match.Groups[2].Value);

        if (count < 1) count = 1;
        if (count > 100) count = 100;
        if (faces < 2) faces = 2;
        if (faces > 99999) faces = 99999;

        return count * faces;
    }

    // Vérifie si une formule de dé est valide.
    public static bool IsValidFormula(string formula)
    {
        return DiceFormulaRegex().IsMatch(formula.Trim());
    }
}
