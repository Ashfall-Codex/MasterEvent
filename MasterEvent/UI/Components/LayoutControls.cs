using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace MasterEvent.UI.Components;

// Éléments de mise en page repris à l'identique par plusieurs fenêtres.
public static class LayoutControls
{

    public static (float Top, float Bottom) GetContainerBounds()
    {
        var winPos = ImGui.GetWindowPos();
        var top = winPos.Y + ImGui.GetWindowContentRegionMin().Y - ImGui.GetStyle().WindowPadding.Y;
        return (top, winPos.Y + ImGui.GetWindowSize().Y);
    }

    public static void DrawVerticalSeparator(float trailingOffset)
        => DrawVerticalSeparator(trailingOffset, GetContainerBounds());

    public static void DrawVerticalSeparator(float trailingOffset, (float Top, float Bottom) bounds)
    {
        var drawList = ImGui.GetWindowDrawList();
        var x = ImGui.GetCursorScreenPos().X;
        var thickness = 1f * ImGuiHelpers.GlobalScale;

        var sepColor = MasterEventTheme.AccentColor with { W = 0.6f };

        drawList.PushClipRect(
            new Vector2(x - thickness - 1f, bounds.Top),
            new Vector2(x + thickness + 1f, bounds.Bottom),
            false);
        drawList.AddLine(
            new Vector2(x, bounds.Top),
            new Vector2(x, bounds.Bottom),
            ImGui.GetColorU32(sepColor),
            thickness);
        drawList.PopClipRect();

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + trailingOffset * ImGuiHelpers.GlobalScale);
    }
}
