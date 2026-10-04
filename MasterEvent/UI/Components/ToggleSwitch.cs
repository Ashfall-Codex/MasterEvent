using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace MasterEvent.UI.Components;

public static class ToggleSwitch
{
    private static readonly Dictionary<string, float> Positions = new();
    private const float AnimationSpeed = 16f;
    private const int CircleSegments = 32;

    public static bool Draw(string id, string label, ref bool value, string? tooltip = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var height = ImGui.GetFrameHeight() * 0.82f;
        var width = height * 1.8f;
        var radius = height * 0.5f;

        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        ImGui.InvisibleButton(id, new Vector2(width, height));
        var toggled = ImGui.IsItemClicked();
        if (toggled) value = !value;

        var hovered = ImGui.IsItemHovered();

        var target = value ? 1f : 0f;
        var t = Positions.TryGetValue(id, out var stored) ? stored : target;
        t += (target - t) * Math.Min(1f, ImGui.GetIO().DeltaTime * AnimationSpeed);
        if (Math.Abs(target - t) < 0.001f) t = target;
        Positions[id] = t;

        var off = MasterEventTheme.ThemeButtonBg with { W = 0.85f };
        var track = Vector4.Lerp(off, MasterEventTheme.AccentColor, t);
        if (hovered) track = Vector4.Lerp(track, MasterEventTheme.TextStrong, 0.12f);
        var trackColor = ImGui.GetColorU32(track);
        var centerY = pos.Y + radius;
        var leftCenter = new Vector2(pos.X + radius, centerY);
        var rightCenter = new Vector2(pos.X + width - radius, centerY);

        dl.AddCircleFilled(leftCenter, radius, trackColor, CircleSegments);
        dl.AddCircleFilled(rightCenter, radius, trackColor, CircleSegments);
        dl.AddRectFilled(new Vector2(leftCenter.X, pos.Y), new Vector2(rightCenter.X, pos.Y + height), trackColor);

        var knobX = pos.X + radius + t * (width - radius * 2f);
        var knobColor = value ? MasterEventTheme.TextStrong : MasterEventTheme.TextSecondary;
        dl.AddCircleFilled(new Vector2(knobX, centerY), radius - 2f * scale,
            ImGui.GetColorU32(knobColor), CircleSegments);

        ImGui.SameLine();
        var textPos = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(textPos with { Y = pos.Y + (height - ImGui.GetTextLineHeight()) * 0.5f });
        ImGui.TextUnformatted(label);

        if (tooltip != null && (hovered || ImGui.IsItemHovered()))
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
            ImGui.TextUnformatted(tooltip);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        return toggled;
    }
}
