using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace MasterEvent.UI.Components;

// Curseur à crans : une piste fine remplie jusqu'au bouton, les crans en pointillé dessous et une
// icône à chaque bout. Partagé par la transparence (Réglages) et l'heure de l'Ambiance (Météo).
public static class NotchedSlider
{
    public delegate void IconDrawer(ImDrawListPtr drawList, Vector2 pos, float size, uint color);

    private static float dragStartValue;

    // `value` va de 0 à 1 et se cale sur les crans. `changed` signale chaque déplacement ; le
    // retour vaut vrai au relâchement si la valeur a bougé, pour n'enregistrer qu'une fois.
    // Sans icône à un bout, la piste va jusqu'au bord ; `majorLabel` écrit une graduation sous
    // chaque cran principal (son rang parmi les crans en paramètre), et sous le dernier cran pour
    // que l'échelle se termine par sa valeur.
    public static bool Draw(string id, ref float value, float width, int notches, int majorEvery,
        IconDrawer? startIcon, IconDrawer? endIcon, out bool changed, string? tooltip = null,
        Func<int, string>? majorLabel = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var dl = ImGui.GetWindowDrawList();

        var iconSize = 14f * scale;
        var gap = 10f * scale;
        var knobRadius = 7f * scale;
        var trackThickness = 4f * scale;
        var tickGap = 6f * scale;
        var labelHeight = majorLabel != null ? ImGui.GetTextLineHeight() + 2f * scale : 0f;
        var height = knobRadius * 2f + tickGap + 3f * scale + labelHeight;

        var origin = ImGui.GetCursorScreenPos();
        var centerY = origin.Y + knobRadius;

        var muted = ImGui.GetColorU32(MasterEventTheme.TextSecondary);
        startIcon?.Invoke(dl, new Vector2(origin.X, centerY - iconSize * 0.5f), iconSize, muted);

        var startInset = startIcon != null ? iconSize + gap : 0f;
        var endInset = endIcon != null ? iconSize + gap : 0f;
        var trackStart = origin.X + startInset + knobRadius;
        var trackEnd = origin.X + width - endInset - knobRadius;
        var trackLength = Math.Max(1f, trackEnd - trackStart);

        ImGui.SetCursorScreenPos(new Vector2(trackStart - knobRadius, origin.Y));
        ImGui.InvisibleButton(id, new Vector2(trackLength + knobRadius * 2f, height));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();

        if (ImGui.IsItemActivated())
            dragStartValue = value;

        var before = value;
        if (active)
        {
            var raw = Math.Clamp((ImGui.GetIO().MousePos.X - trackStart) / trackLength, 0f, 1f);
            value = Snap(raw, notches);
        }
        changed = Math.Abs(value - before) > 0.0001f;
        var released = ImGui.IsItemDeactivated() && Math.Abs(value - dragStartValue) > 0.0001f;

        value = Math.Clamp(value, 0f, 1f);
        var knobX = trackStart + value * trackLength;
        var half = trackThickness * 0.5f;
        var rest = ImGui.GetColorU32(MasterEventTheme.ThemeButtonBg with { W = 0.85f });
        var fill = ImGui.GetColorU32(MasterEventTheme.AccentColor);
        dl.AddRectFilled(new Vector2(trackStart, centerY - half), new Vector2(trackEnd, centerY + half), rest, half);
        dl.AddRectFilled(new Vector2(trackStart, centerY - half), new Vector2(knobX, centerY + half), fill, half);

        // Un cran sur `majorEvery` est marqué plus nettement, pour garder la piste lisible.
        var tickY = centerY + knobRadius + tickGap * 0.5f;
        for (var i = 0; i < notches; i++)
        {
            var t = i / (float)(notches - 1);
            var x = trackStart + t * trackLength;
            var current = Math.Abs(t - value) < 0.001f;
            var major = i % majorEvery == 0 || (majorLabel != null && i == notches - 1);
            var color = current ? MasterEventTheme.TextStrong
                : major ? MasterEventTheme.TextDim
                : MasterEventTheme.TextDim with { W = 0.55f };
            var dot = current ? 1.6f : major ? 1.1f : 0.7f;
            dl.AddCircleFilled(new Vector2(x, tickY), dot * scale, ImGui.GetColorU32(color), 8);

            if (!major || majorLabel == null) continue;
            var label = majorLabel(i);
            var labelSize = ImGui.CalcTextSize(label);
            var labelX = Math.Clamp(x - labelSize.X * 0.5f, origin.X, origin.X + width - labelSize.X);
            dl.AddText(new Vector2(labelX, tickY + 3f * scale), ImGui.GetColorU32(MasterEventTheme.TextDim), label);
        }

        var radius = knobRadius * (active ? 1.08f : hovered ? 1.04f : 1f);
        dl.AddCircleFilled(new Vector2(knobX, centerY + 1f * scale), radius, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.35f)), 24);
        dl.AddCircleFilled(new Vector2(knobX, centerY), radius, ImGui.GetColorU32(MasterEventTheme.TextStrong), 24);

        endIcon?.Invoke(dl, new Vector2(origin.X + width - iconSize, centerY - iconSize * 0.5f), iconSize, muted);

        if (tooltip != null && hovered && !active)
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
            ImGui.TextUnformatted(tooltip);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + height));
        ImGui.Dummy(new Vector2(width, 0f));
        return released;
    }

    private static float Snap(float raw, int notches)
    {
        var steps = notches - 1;
        return MathF.Round(raw * steps) / steps;
    }
}
