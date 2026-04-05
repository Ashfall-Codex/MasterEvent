using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;

namespace MasterEvent.UI.Components;
public static partial class DiceFormulaEditor
{
    [GeneratedRegex(@"^(\d+)d(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FormulaRegex();
    public static void Draw(EventTemplate template, string idSuffix)
    {
        ParseFormula(template.DiceFormula, out var count, out var faces);

        var inputWidth = 70f * ImGuiHelpers.GlobalScale;

        ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Dice.Count"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(inputWidth);
        if (ImGui.InputInt($"##dice_count_{idSuffix}", ref count))
        {
            if (count < 1) count = 1;
            if (count > 100) count = 100;
            template.DiceFormula = $"{count}d{faces}";
        }

        ImGui.SameLine();

        ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Dice.Faces"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(inputWidth);
        if (ImGui.InputInt($"##dice_faces_{idSuffix}", ref faces))
        {
            if (faces < 2) faces = 2;
            if (faces > 99999) faces = 99999;
            template.DiceFormula = $"{count}d{faces}";
        }
    }

    private static void ParseFormula(string formula, out int count, out int faces)
    {
        var match = FormulaRegex().Match(formula.Trim());
        if (match.Success)
        {
            count = int.Parse(match.Groups[1].Value);
            faces = int.Parse(match.Groups[2].Value);
        }
        else
        {
            count = 1;
            faces = 100;
        }
    }
}
