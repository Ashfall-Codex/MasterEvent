using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace MasterEvent.UI.Components;

// Sélecteur segmenté : une piste arrondie, le segment choisi en accent.
public static class SegmentedControl
{
    public static int Draw(string id, string[] labels, int selected, float? fixedWidth = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = fixedWidth ?? ImGui.GetContentRegionAvail().X - MasterEventTheme.ListRightMargin;
        var height = ImGui.GetFrameHeight() * 1.2f;
        var inset = 3f * scale;
        var origin = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        ImGui.Dummy(new Vector2(width, height));
        var end = ImGui.GetCursorScreenPos();

        dl.AddRectFilled(origin, origin + new Vector2(width, height),
            ImGui.GetColorU32(MasterEventTheme.TextStrong with { W = 0.07f }), height * 0.5f);

        var segmentWidth = (width - inset * 2f) / labels.Length;
        for (var i = 0; i < labels.Length; i++)
        {
            var min = origin + new Vector2(inset + segmentWidth * i, inset);
            var size = new Vector2(segmentWidth, height - inset * 2f);

            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton($"##{id}_{i}", size)) selected = i;
            var hovered = ImGui.IsItemHovered();

            var active = i == selected;
            if (active)
            {
                dl.AddRectFilled(min, min + size, ImGui.GetColorU32(MasterEventTheme.AccentColor with { W = 0.75f }),
                    size.Y * 0.5f);
            }
            else if (hovered)
            {
                dl.AddRectFilled(min, min + size, ImGui.GetColorU32(MasterEventTheme.TextStrong with { W = 0.08f }), size.Y * 0.5f);
            }

            var textSize = ImGui.CalcTextSize(labels[i]);
            var color = active ? MasterEventTheme.TextStrong : MasterEventTheme.TextSecondary;
            dl.AddText(min + (size - textSize) * 0.5f, ImGui.GetColorU32(color), labels[i]);
        }

        ImGui.SetCursorScreenPos(end);
        return selected;
    }
}
