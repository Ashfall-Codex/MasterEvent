using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace MasterEvent.UI.Components;

// Liste déroulante dont l'aperçu et chaque entrée portent une icône : une icône du jeu (météo),
// ou à défaut une icône FontAwesome. La place de l'icône est réservée même quand une entrée n'en
// a pas, pour que les noms restent alignés.
public static class IconCombo
{
    public readonly record struct Entry(int Key, string Label, uint GameIconId = 0, FontAwesomeIcon? Icon = null);

    // Comme une liste ImGui ordinaire : huit entrées visibles, le reste défile.
    private const int VisibleEntries = 8;

    // Vrai quand l'utilisateur choisit une entrée ; `selectedKey` prend alors sa clé.
    public static bool Draw(string id, IReadOnlyList<Entry> entries, ref int selectedKey, float width)
    {
        var style = ImGui.GetStyle();
        var iconSize = ImGui.GetTextLineHeight();
        var gap = style.ItemInnerSpacing.X;

        var current = -1;
        var widest = 0f;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Key == selectedKey) current = i;
            widest = Math.Max(widest, ImGui.CalcTextSize(entries[i].Label).X);
        }

        // La liste s'élargit au besoin plutôt que de couper un nom long.
        var popupWidth = Math.Max(width, iconSize + gap + widest + style.WindowPadding.X * 2f + style.ScrollbarSize);
        var popupMaxHeight = ImGui.GetTextLineHeightWithSpacing() * VisibleEntries - style.ItemSpacing.Y
                             + style.WindowPadding.Y * 2f;
        ImGui.SetNextWindowSizeConstraints(new Vector2(popupWidth, 0f), new Vector2(float.MaxValue, popupMaxHeight));

        var frameMin = ImGui.GetCursorScreenPos();
        var parentDrawList = ImGui.GetWindowDrawList();
        var changed = false;

        ImGui.SetNextItemWidth(width);
        if (ImGui.BeginCombo("##" + id, string.Empty))
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var rowMin = ImGui.GetCursorScreenPos();

                // Libellé masqué : l'entrée occupe toute la ligne, l'icône et le nom sont dessinés par-dessus.
                if (ImGui.Selectable($"##{id}_{i}", i == current))
                {
                    selectedKey = entry.Key;
                    changed = true;
                }
                if (i == current) ImGui.SetItemDefaultFocus();

                DrawEntry(ImGui.GetWindowDrawList(), rowMin, entry, iconSize, gap);
            }
            ImGui.EndCombo();
        }

        // Aperçu dans le cadre fermé, coupé avant la flèche.
        if (current >= 0)
        {
            var frameHeight = ImGui.GetFrameHeight();
            var clipMax = new Vector2(frameMin.X + width - frameHeight, frameMin.Y + frameHeight);
            parentDrawList.PushClipRect(frameMin, clipMax, true);
            DrawEntry(parentDrawList, frameMin + style.FramePadding, entries[current], iconSize, gap);
            parentDrawList.PopClipRect();
        }

        return changed;
    }

    private static void DrawEntry(ImDrawListPtr drawList, Vector2 pos, Entry entry, float iconSize, float gap)
    {
        if (entry.GameIconId != 0
            && Plugin.TextureProvider.GetFromGameIcon(entry.GameIconId).GetWrapOrDefault() is { } texture)
        {
            drawList.AddImage(texture.Handle, pos, pos + new Vector2(iconSize, iconSize),
                Vector2.Zero, Vector2.One, ImGui.GetColorU32(Vector4.One));
        }
        else if (entry.Icon is { } icon)
        {
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                var glyph = icon.ToIconString();
                var glyphSize = ImGui.CalcTextSize(glyph);
                drawList.AddText(pos + new Vector2((iconSize - glyphSize.X) / 2f, (iconSize - glyphSize.Y) / 2f),
                    ImGui.GetColorU32(MasterEventTheme.TextSecondary), glyph);
            }
        }

        drawList.AddText(new Vector2(pos.X + iconSize + gap, pos.Y), ImGui.GetColorU32(ImGuiCol.Text), entry.Label);
    }
}
