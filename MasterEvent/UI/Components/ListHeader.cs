using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace MasterEvent.UI.Components;

// En-tête d'une liste : le titre, le compte en retrait, et une action facultative alignée à droite.
public static class ListHeader
{
    public static void Draw(string title, string? count, Action? trailing = null, float trailingWidth = 0f,
        Vector4? titleColor = null)
    {
        var rowStart = ImGui.GetCursorPosX();
        var avail = ImGui.GetContentRegionAvail().X;

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(titleColor ?? MasterEventTheme.TextStrong, title);
        if (count != null)
        {
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.MutedTextColor, count);
        }

        if (trailing != null)
        {
            ImGui.SameLine(rowStart + avail - trailingWidth - MasterEventTheme.ListRightMargin);
            trailing();
        }
        else
        {
            // Même hauteur de ligne qu'avec un bouton, pour que les colonnes restent alignées.
            ImGui.SameLine();
            ImGui.Dummy(new Vector2(0, ImGui.GetFrameHeight()));
        }

        ImGuiHelpers.ScaledDummy(2f);
    }
}
