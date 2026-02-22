using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;

namespace MasterEvent.UI.Components;

public static class AttitudePicker
{
    private static readonly (Attitude attitude, Vector4 color, string label)[] Attitudes =
    [
        (Attitude.Hostile, MasterEventTheme.AttitudeHostile, "H"),
        (Attitude.Neutral, MasterEventTheme.AttitudeNeutral, "N"),
        (Attitude.Friendly, MasterEventTheme.AttitudeFriendly, "A"),
    ];

    public static bool Draw(string id, ref Attitude current)
    {
        var changed = false;
        var buttonSize = new Vector2(24f * ImGuiHelpers.GlobalScale, 20f * ImGuiHelpers.GlobalScale);
        var textHeight = ImGui.GetFontSize();
        var padY = (buttonSize.Y - textHeight) * 0.5f;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X, padY));

        for (var i = 0; i < Attitudes.Length; i++)
        {
            if (i > 0) ImGui.SameLine();

            var (attitude, color, label) = Attitudes[i];
            var isSelected = current == attitude;

            var bgColor = isSelected ? color : new Vector4(color.X * 0.3f, color.Y * 0.3f, color.Z * 0.3f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Button, bgColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(color.X * 0.8f, color.Y * 0.8f, color.Z * 0.8f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);

            if (ImGui.Button($"{label}##{id}_{i}", buttonSize))
            {
                current = attitude;
                changed = true;
            }

            ImGui.PopStyleColor(3);

            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(attitude switch
                {
                    Attitude.Hostile => Loc.Get("Attitude.Hostile"),
                    Attitude.Neutral => Loc.Get("Attitude.Neutral"),
                    Attitude.Friendly => Loc.Get("Attitude.Friendly"),
                    _ => "?",
                });
                ImGui.EndTooltip();
            }
        }

        ImGui.PopStyleVar();
        return changed;
    }
}
