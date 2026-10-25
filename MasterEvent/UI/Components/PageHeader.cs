using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

namespace MasterEvent.UI.Components;

// En-tête unique de toutes les vues : icône et titre à gauche, un sous-titre d'état en dessous,
// et à droite une commande propre à la vue (bascule, bouton discret). Compact, pour laisser la
// place au contenu dans une fenêtre étroite.
public static class PageHeader
{
    public static void Draw(FontAwesomeIcon icon, string title, string? subtitle = null,
        Action? trailing = null, float trailingWidth = 0f)
    {
        ImGuiHelpers.ScaledDummy(4f);

        var rowStart = ImGui.GetCursorPosX();
        var availWidth = ImGui.GetContentRegionAvail().X;

        ImGui.AlignTextToFramePadding();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.AccentText, icon.ToIconString());
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(MasterEventTheme.TextStrong, title);

        if (trailing != null)
        {
            ImGui.SameLine(rowStart + availWidth - trailingWidth);
            trailing();
        }

        if (!string.IsNullOrEmpty(subtitle))
            ImGui.TextColored(MasterEventTheme.MutedTextColor, subtitle);

        ImGuiHelpers.ScaledDummy(2f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
    }
}
