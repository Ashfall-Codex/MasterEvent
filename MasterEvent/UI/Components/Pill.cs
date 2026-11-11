using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace MasterEvent.UI.Components;

// Gélule : un mot sur un fond de sa couleur à 16 %, entièrement arrondie.
public static class Pill
{
    // Dans une ligne d'acteur, dessinée à côté du nom sans passer par la mise en page.
    public static float Width(string text)
        => ImGui.CalcTextSize(text).X + 10f * ImGuiHelpers.GlobalScale;

    public static float Draw(ImDrawListPtr dl, Vector2 pos, string text, Vector4 color)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = ImGui.CalcTextSize(text);
        var width = size.X + 10f * scale;
        dl.AddRectFilled(pos - new Vector2(0f, 1f * scale), pos + new Vector2(width, size.Y + 1f * scale),
            ImGui.GetColorU32(color with { W = 0.16f }), (size.Y + 2f * scale) / 2f);
        dl.AddText(pos + new Vector2(5f * scale, 0f), ImGui.GetColorU32(color), text);
        return width;
    }

    // Posée comme un élément ImGui, centrée sur la hauteur d'un cadre (cartes, lignes de texte).
    public static void Item(string text, Vector4 color)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var padX = 6f * scale;
        var textSize = ImGui.CalcTextSize(text);
        var frameHeight = ImGui.GetFrameHeight();
        var pillHeight = textSize.Y + 4f * scale;
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(textSize.X + padX * 2f, frameHeight));

        var min = new Vector2(pos.X, pos.Y + (frameHeight - pillHeight) / 2f);
        var max = new Vector2(min.X + textSize.X + padX * 2f, min.Y + pillHeight);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(min, max, ImGui.GetColorU32(color with { W = 0.16f }), pillHeight / 2f);
        dl.AddText(new Vector2(min.X + padX, min.Y + 2f * scale), ImGui.GetColorU32(color), text);
    }
}
