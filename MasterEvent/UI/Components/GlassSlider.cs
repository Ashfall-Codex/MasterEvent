using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace MasterEvent.UI.Components;
public static class GlassSlider
{
    public const int Notches = 41;
    private const int MajorEvery = 4;
    private static readonly NotchedSlider.Options Slider = new()
    {
        Notches = Notches,
        MajorEvery = MajorEvery,
        StartIcon = (dl, pos, size, color) => DrawGlassIcon(dl, pos, size, color, filled: false),
        EndIcon = (dl, pos, size, color) => DrawGlassIcon(dl, pos, size, color, filled: true),
    };

    public static bool Draw(string id, ref float value, float width, string? tooltip = null)
        => NotchedSlider.Draw(id, ref value, width, Slider, out _, tooltip);

    private static void DrawGlassIcon(ImDrawListPtr dl, Vector2 pos, float size, uint color, bool filled)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var square = size * 0.72f;
        var rounding = square * 0.28f;
        var back = pos;
        var front = pos + new Vector2(size - square, size - square);

        dl.AddRect(back, back + new Vector2(square), color, rounding, ImDrawFlags.None, 1.2f * scale);
        if (filled)
            dl.AddRectFilled(front, front + new Vector2(square), color, rounding);
        else
            dl.AddRect(front, front + new Vector2(square), color, rounding, ImDrawFlags.None, 1.2f * scale);
    }
}
