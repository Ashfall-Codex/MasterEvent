using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace MasterEvent.UI.Components;

// Éléments de mise en page repris à l'identique par plusieurs fenêtres.
public static class LayoutControls
{
    // Trait vertical entre la barre latérale et la zone de contenu, suivi du
    // décalage du curseur vers le contenu. Les fenêtres n'en diffèrent que par ce décalage.
    public static void DrawVerticalSeparator(float trailingOffset)
    {
        var drawList = ImGui.GetWindowDrawList();
        var sepPos = ImGui.GetCursorScreenPos();
        var sepHeight = ImGui.GetContentRegionAvail().Y;
        var sepColor = MasterEventTheme.AccentColor with { W = 0.6f };

        drawList.AddLine(
            sepPos,
            new System.Numerics.Vector2(sepPos.X, sepPos.Y + sepHeight),
            ImGui.GetColorU32(sepColor),
            1f * ImGuiHelpers.GlobalScale);

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + trailingOffset * ImGuiHelpers.GlobalScale);
    }
}
