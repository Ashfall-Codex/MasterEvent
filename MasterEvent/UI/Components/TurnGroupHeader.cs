using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

namespace MasterEvent.UI.Components;

// En-tête d'un groupe d'initiative en lecture seule : l'icône de lien et le nom, sur la hauteur d'un
// cadre. Il commence après la colonne de coche des lignes d'acteur, que les membres gardent.
public static class TurnGroupHeader
{
    public static void Draw(string label, bool reserveCheck = true)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var startX = ImGui.GetCursorPosX();
        var indent = reserveCheck ? ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X : 0f;
        var avail = ImGui.GetContentRegionAvail().X - MasterEventTheme.ListRightMargin;

        ImGui.SetCursorPosX(startX + indent);
        ImGui.AlignTextToFramePadding();
        float iconWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            var icon = FontAwesomeIcon.Link.ToIconString();
            iconWidth = ImGui.CalcTextSize(icon).X;
            ImGui.TextColored(MasterEventTheme.TextSecondary, icon);
        }

        var gap = 6f * scale;
        ImGui.SameLine(0, gap);
        ImGui.TextColored(MasterEventTheme.TextSecondary, TextFit.Fit(label, avail - indent - iconWidth - gap));
    }
}
