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

    // Ce qui distingue un curseur d'un autre. Une instance statique par usage : rien n'est
    // alloué à chaque image.
    public sealed class Options
    {
        // Nombre de crans, extrémités comprises.
        public required int Notches { get; init; }
        // Un cran sur MajorEvery est marqué plus nettement, pour garder la piste lisible.
        public required int MajorEvery { get; init; }
        // Sans icône à un bout, la piste va jusqu'au bord.
        public IconDrawer? StartIcon { get; init; }
        public IconDrawer? EndIcon { get; init; }
        // Graduation sous chaque cran principal (son rang en paramètre), et sous le dernier cran
        // pour que l'échelle se termine par sa valeur.
        public Func<int, string>? MajorLabel { get; init; }
    }

    // Mesures du curseur pour l'image en cours, partagées par les étapes de dessin.
    private readonly struct Geometry
    {
        public Vector2 Origin { get; init; }
        public float Width { get; init; }
        public float Height { get; init; }
        public float CenterY { get; init; }
        public float TrackStart { get; init; }
        public float TrackEnd { get; init; }
        public float KnobRadius { get; init; }
        public float Scale { get; init; }

        public float TrackLength => Math.Max(1f, TrackEnd - TrackStart);
        public float XAt(float t) => TrackStart + t * TrackLength;
    }

    private const float IconSize = 14f;
    private const float IconGap = 10f;
    private const float KnobRadius = 7f;
    private const float TrackThickness = 4f;
    private const float TickGap = 6f;

    private static float dragStartValue;

    // `value` va de 0 à 1 et se cale sur les crans. `changed` signale chaque déplacement ; le
    // retour vaut vrai au relâchement si la valeur a bougé, pour n'enregistrer qu'une fois.
    public static bool Draw(string id, ref float value, float width, Options options, out bool changed,
        string? tooltip = null)
    {
        var g = Measure(options, width);
        var dl = ImGui.GetWindowDrawList();
        var iconSize = IconSize * g.Scale;
        var iconY = g.CenterY - iconSize * 0.5f;
        var muted = ImGui.GetColorU32(MasterEventTheme.TextSecondary);
        options.StartIcon?.Invoke(dl, new Vector2(g.Origin.X, iconY), iconSize, muted);

        ImGui.SetCursorScreenPos(new Vector2(g.TrackStart - g.KnobRadius, g.Origin.Y));
        ImGui.InvisibleButton(id, new Vector2(g.TrackLength + g.KnobRadius * 2f, g.Height));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var released = Drag(ref value, g, options.Notches, active, out changed);

        DrawTrack(dl, g, value);
        DrawNotches(dl, g, value, options);
        DrawKnob(dl, g, value, KnobScale(active, hovered));
        options.EndIcon?.Invoke(dl, new Vector2(g.Origin.X + width - iconSize, iconY), iconSize, muted);

        if (tooltip != null && hovered && !active)
            DrawTooltip(tooltip);

        ImGui.SetCursorScreenPos(new Vector2(g.Origin.X, g.Origin.Y + g.Height));
        ImGui.Dummy(new Vector2(width, 0f));
        return released;
    }

    private static Geometry Measure(Options options, float width)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var knobRadius = KnobRadius * scale;
        var labelHeight = options.MajorLabel != null ? ImGui.GetTextLineHeight() + 2f * scale : 0f;
        var origin = ImGui.GetCursorScreenPos();
        var startInset = options.StartIcon != null ? (IconSize + IconGap) * scale : 0f;
        var endInset = options.EndIcon != null ? (IconSize + IconGap) * scale : 0f;

        return new Geometry
        {
            Origin = origin,
            Width = width,
            Height = knobRadius * 2f + TickGap * scale + 3f * scale + labelHeight,
            CenterY = origin.Y + knobRadius,
            TrackStart = origin.X + startInset + knobRadius,
            TrackEnd = origin.X + width - endInset - knobRadius,
            KnobRadius = knobRadius,
            Scale = scale,
        };
    }

    // Suit la souris pendant le glisser, juste après le bouton invisible qui porte l'interaction.
    private static bool Drag(ref float value, Geometry g, int notches, bool active, out bool changed)
    {
        if (ImGui.IsItemActivated())
            dragStartValue = value;

        var before = value;
        if (active)
        {
            var raw = Math.Clamp((ImGui.GetIO().MousePos.X - g.TrackStart) / g.TrackLength, 0f, 1f);
            value = Snap(raw, notches);
        }
        changed = Math.Abs(value - before) > 0.0001f;
        var released = ImGui.IsItemDeactivated() && Math.Abs(value - dragStartValue) > 0.0001f;

        value = Math.Clamp(value, 0f, 1f);
        return released;
    }

    private static void DrawTrack(ImDrawListPtr dl, Geometry g, float value)
    {
        var half = TrackThickness * g.Scale * 0.5f;
        var rest = ImGui.GetColorU32(MasterEventTheme.ThemeButtonBg with { W = 0.85f });
        var fill = ImGui.GetColorU32(MasterEventTheme.AccentColor);
        dl.AddRectFilled(new Vector2(g.TrackStart, g.CenterY - half), new Vector2(g.TrackEnd, g.CenterY + half), rest, half);
        dl.AddRectFilled(new Vector2(g.TrackStart, g.CenterY - half), new Vector2(g.XAt(value), g.CenterY + half), fill, half);
    }

    private static void DrawNotches(ImDrawListPtr dl, Geometry g, float value, Options options)
    {
        var tickY = g.CenterY + g.KnobRadius + TickGap * g.Scale * 0.5f;
        for (var i = 0; i < options.Notches; i++)
        {
            var t = i / (float)(options.Notches - 1);
            var x = g.XAt(t);
            var current = Math.Abs(t - value) < 0.001f;
            var major = i % options.MajorEvery == 0 || (options.MajorLabel != null && i == options.Notches - 1);
            dl.AddCircleFilled(new Vector2(x, tickY), NotchRadius(current, major) * g.Scale,
                ImGui.GetColorU32(NotchColor(current, major)), 8);

            if (major && options.MajorLabel != null)
                DrawMajorLabel(dl, g, new Vector2(x, tickY), options.MajorLabel(i));
        }
    }

    // Graduation centrée sous son cran, sans sortir du curseur aux deux bouts.
    private static void DrawMajorLabel(ImDrawListPtr dl, Geometry g, Vector2 tick, string label)
    {
        var labelSize = ImGui.CalcTextSize(label);
        var labelX = Math.Clamp(tick.X - labelSize.X * 0.5f, g.Origin.X, g.Origin.X + g.Width - labelSize.X);
        dl.AddText(new Vector2(labelX, tick.Y + 3f * g.Scale), ImGui.GetColorU32(MasterEventTheme.TextDim), label);
    }

    private static Vector4 NotchColor(bool current, bool major)
    {
        if (current) return MasterEventTheme.TextStrong;
        return major ? MasterEventTheme.TextDim : MasterEventTheme.TextDim with { W = 0.55f };
    }

    private static float NotchRadius(bool current, bool major)
    {
        if (current) return 1.6f;
        return major ? 1.1f : 0.7f;
    }

    // Le bouton grossit à peine au survol, un peu plus pendant le glisser.
    private static float KnobScale(bool active, bool hovered)
    {
        if (active) return 1.08f;
        return hovered ? 1.04f : 1f;
    }

    private static void DrawKnob(ImDrawListPtr dl, Geometry g, float value, float knobScale)
    {
        var center = new Vector2(g.XAt(value), g.CenterY);
        var radius = g.KnobRadius * knobScale;
        dl.AddCircleFilled(center + new Vector2(0f, 1f * g.Scale), radius, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.35f)), 24);
        dl.AddCircleFilled(center, radius, ImGui.GetColorU32(MasterEventTheme.TextStrong), 24);
    }

    private static void DrawTooltip(string tooltip)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
        ImGui.TextUnformatted(tooltip);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static float Snap(float raw, int notches)
    {
        var steps = notches - 1;
        return MathF.Round(raw * steps) / steps;
    }
}
