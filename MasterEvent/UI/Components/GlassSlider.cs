using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace MasterEvent.UI.Components;
public static class GlassSlider
{
    // Nombre de crans, extrémités comprises : 41 donne un pas de 2,5 %.
    public const int Notches = 41;
    // Un cran sur quatre est marqué plus nettement (tous les 10 %) pour garder la piste lisible.
    private const int MajorEvery = 4;

    public static bool Draw(string id, ref float value, float width, string? tooltip = null)
        => NotchedSlider.Draw(id, ref value, width, Notches, MajorEvery,
            (dl, pos, size, color) => DrawGlassIcon(dl, pos, size, color, filled: false),
            (dl, pos, size, color) => DrawGlassIcon(dl, pos, size, color, filled: true),
            out _, tooltip);

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
