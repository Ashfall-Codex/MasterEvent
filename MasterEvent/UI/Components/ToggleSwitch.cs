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
        // La cible a la hauteur d'un champ, l'interrupteur dessiné reste plus petit, centré dedans.
        var rowHeight = ImGui.GetFrameHeight();
        var height = rowHeight * 0.82f;
        var width = height * 1.8f;
        var radius = height * 0.5f;

        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        // La valeur de retour couvre le clic comme Espace ou Entrée quand l'interrupteur a le focus clavier.
        var toggled = ImGui.InvisibleButton(id, new Vector2(width, rowHeight));
        var hovered = ImGui.IsItemHovered();
        var focused = ImGui.IsItemFocused() && ImGui.GetIO().NavVisible;

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);

        // Le libellé bascule aussi, comme une case à cocher.
        var labelHovered = ImGui.IsItemHovered();
        if (labelHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) toggled = true;
        if (hovered || labelHovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        if (toggled) value = !value;

        var target = value ? 1f : 0f;
        var t = Positions.TryGetValue(id, out var stored) ? stored : target;
        t += (target - t) * Math.Min(1f, ImGui.GetIO().DeltaTime * AnimationSpeed);
        if (Math.Abs(target - t) < 0.001f) t = target;
        Positions[id] = t;

        var off = MasterEventTheme.ThemeButtonBg with { W = 0.85f };
        var track = Vector4.Lerp(off, MasterEventTheme.AccentColor, t);
        if (hovered || labelHovered) track = Vector4.Lerp(track, MasterEventTheme.TextStrong, 0.12f);
        var trackColor = ImGui.GetColorU32(track);
        var top = pos.Y + (rowHeight - height) * 0.5f;
        var centerY = top + radius;
        var leftCenter = new Vector2(pos.X + radius, centerY);
        var rightCenter = new Vector2(pos.X + width - radius, centerY);

        dl.AddCircleFilled(leftCenter, radius, trackColor, CircleSegments);
        dl.AddCircleFilled(rightCenter, radius, trackColor, CircleSegments);
        dl.AddRectFilled(new Vector2(leftCenter.X, top), new Vector2(rightCenter.X, top + height), trackColor);

        var knobX = pos.X + radius + t * (width - radius * 2f);
        var knobColor = value ? MasterEventTheme.TextStrong : MasterEventTheme.TextSecondary;
        dl.AddCircleFilled(new Vector2(knobX, centerY), radius - 2f * scale,
            ImGui.GetColorU32(knobColor), CircleSegments);

        // Anneau de focus : ImGui n'en dessine pas autour d'un bouton invisible.
        if (focused)
        {
            var ring = 2f * scale;
            dl.AddRect(new Vector2(pos.X - ring, top - ring), new Vector2(pos.X + width + ring, top + height + ring),
                ImGui.GetColorU32(MasterEventTheme.AccentText), radius + ring, ImDrawFlags.None, 1.5f * scale);
        }

        if (tooltip != null && (hovered || labelHovered))
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
