using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Services;

namespace MasterEvent.UI.Components;

public static class DiceControls
{
    // Tuile cliquable d'un jet : un libellé principal, un modificateur optionnel en dessous.
    public static void DrawDiceTile(string line1, string? line2, string id, float w, float h, Action onClick)
    {
        var rounding = 6f * ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, rounding);

        if (ImGui.Button("##" + id, new Vector2(w, h)))
            onClick();

        var btnMin = ImGui.GetItemRectMin();
        var dlst = ImGui.GetWindowDrawList();

        var lineHeight = ImGui.GetFontSize();
        var totalTextH = line2 != null ? lineHeight * 2f + 2f : lineHeight;
        var textY = btnMin.Y + (h - totalTextH) / 2f;

        var sz1 = ImGui.CalcTextSize(line1);
        var x1 = btnMin.X + (w - sz1.X) / 2f;
        dlst.AddText(new Vector2(x1, textY), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), line1);

        if (line2 != null)
        {
            var sz2 = ImGui.CalcTextSize(line2);
            var x2 = btnMin.X + (w - sz2.X) / 2f;
            dlst.AddText(new Vector2(x2, textY + lineHeight + 2f),
                ImGui.GetColorU32(new Vector4(0.7f, 0.7f, 0.7f, 1f)), line2);
        }

        ImGui.PopStyleVar();
    }

    // Séparateur, titre et derniers jets. Le nombre d'entrées et le bouton d'effacement
    // varient selon la fenêtre hôte, d'où les deux paramètres.
    public static void DrawRollHistory(SessionManager session, int maxEntries, bool showClearButton)
    {
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Dice.History"));
        ImGuiHelpers.ScaledDummy(2f);

        if (session.RollHistory.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Dice.NoHistory"));
            return;
        }

        for (var i = 0; i < session.RollHistory.Count && i < maxEntries; i++)
        {
            var roll = session.RollHistory[i];
            var rollModStr = roll.Modifier >= 0 ? $"+{roll.Modifier}" : roll.Modifier.ToString();
            var statInfo = roll.StatName != null ? $" [{roll.StatName} {rollModStr}]" : "";
            var breakdown = roll.IndividualRolls is { Length: > 1 }
                ? string.Join(" + ", roll.IndividualRolls) + " = "
                : "";
            var line = $"{roll.RollerName}: {breakdown}{roll.RawRoll}/{roll.DiceMax}{statInfo} = {roll.Total}";

            // Mettre en valeur le dernier jet
            if (i == 0)
                ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), line);
            else
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), line);
        }

        if (!showClearButton)
            return;

        ImGuiHelpers.ScaledDummy(4f);
        if (ImGui.SmallButton(Loc.Get("Dice.ClearHistory")))
            session.ClearRollHistory();
    }
}
