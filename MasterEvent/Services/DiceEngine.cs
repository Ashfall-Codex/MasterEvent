using System;
using System.Text.RegularExpressions;
using MasterEvent.Models;

namespace MasterEvent.Services;


// Moteur de dés : parse une formule XdY et lance les dés.

public readonly struct DiceRollDetail(int[] rolls, int sum, int faces)
{
    public int[] Rolls { get; } = rolls;
    public int Sum { get; } = sum;
    public int Faces { get; } = faces;
}

/// Issue d'un jet une fois la stat appliquée. `Target` et `Success` restent nuls en mode
/// modificateur : un jet additif ne tranche pas entre réussite et échec.
public readonly record struct RollOutcome(int Total, int? Target, bool? Success);

public static partial class DiceEngine
{
    [GeneratedRegex(@"^(\d+)d(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DiceFormulaRegex();

    /// Applique la stat au jet selon le mode de résolution du modèle. Centralisé ici pour que
    /// les trois chemins de jet — marqueur, joueur, réception réseau — ne puissent pas diverger.
    /// Tolère un modèle absent en retombant sur le mode additif d'origine.
    /// `statValue` est nul quand le jet ne porte sur aucune stat : il n'y a alors pas de seuil à
    /// viser, et le jet reste additif même en mode cible. Sans ce cas, un jet libre comparerait
    /// le dé à zéro et échouerait toujours.
    public static RollOutcome Resolve(EventTemplate? template, int rawRoll, int? statValue, int tempModifier)
    {
        if (template is not { StatResolution: StatResolution.Target } || statValue is not { } stat)
            return new RollOutcome(rawRoll + (statValue ?? 0) + tempModifier, null, null);

        // Le bonus ponctuel déplace la cible, pas le dé : le jet brut reste lisible tel qu'il
        // est tombé, convention des systèmes en jet-sous.
        var target = stat + tempModifier;

        return new RollOutcome(rawRoll, target, template.IsSuccess(rawRoll, target));
    }

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

    public static DiceRollDetail RollDetailed(string formula)
    {
        var match = DiceFormulaRegex().Match(formula.Trim());
        if (!match.Success)
            return new DiceRollDetail([Random.Shared.Next(1, 101)], Random.Shared.Next(1, 101), 100);

        var count = int.Parse(match.Groups[1].Value);
        var faces = int.Parse(match.Groups[2].Value);

        if (count < 1) count = 1;
        if (count > 100) count = 100;
        if (faces < 2) faces = 2;
        if (faces > 99999) faces = 99999;

        var rolls = new int[count];
        var total = 0;
        for (var i = 0; i < count; i++)
        {
            rolls[i] = Random.Shared.Next(1, faces + 1);
            total += rolls[i];
        }

        return new DiceRollDetail(rolls, total, faces);
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
