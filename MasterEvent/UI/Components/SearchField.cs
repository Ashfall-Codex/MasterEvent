using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using MasterEvent.Localization;

namespace MasterEvent.UI.Components;

// Champ de recherche : une loupe devant, un bouton pour effacer quand il y a du texte, Échap vide
// le champ. Vrai quand le texte a changé.
public static class SearchField
{
    public static bool Draw(string id, ref string text, string hint, float width, int maxLength = 64)
    {
        var style = ImGui.GetStyle();
        var iconWidth = Buttons.IconWidth(FontAwesomeIcon.Search) - style.FramePadding.X * 2f;
        var clearWidth = text.Length > 0 ? Buttons.IconWidth(FontAwesomeIcon.Times) + style.ItemSpacing.X : 0f;

        ImGui.AlignTextToFramePadding();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.TextDim, FontAwesomeIcon.Search.ToIconString());
        ImGui.SameLine();

        ImGui.SetNextItemWidth(Math.Max(width - iconWidth - style.ItemSpacing.X - clearWidth, 40f));
        var changed = ImGui.InputTextWithHint("##" + id, hint, ref text, maxLength);

        if (ImGui.IsItemActive() && ImGui.IsKeyPressed(ImGuiKey.Escape) && text.Length > 0)
        {
            text = string.Empty;
            changed = true;
        }

        if (text.Length > 0)
        {
            ImGui.SameLine();
            if (Buttons.QuietIcon(FontAwesomeIcon.Times, id + "_clear", Loc.Get("Search.Clear")))
            {
                text = string.Empty;
                changed = true;
            }
        }

        return changed;
    }
}
