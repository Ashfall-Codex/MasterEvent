using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;

namespace MasterEvent.UI.Components;

// Stats en lecture seule, en tuiles : nom à gauche, valeur alignée à droite. Les compétences d'un
// modèle (« Acrobaties (Dex) ») forment un second bloc sous les caractéristiques, leur base en retrait.
public static class StatGrid
{
    private const int MaxColumns = 3;
    private const string Dots = "...";

    private readonly record struct Tile(string Name, string Basis, string Value, int Modifier, string FullName);

    public static void Draw(IReadOnlyList<StatValue> stats, float width)
    {
        if (stats.Count == 0) return;

        var tiles = stats.Select(ToTile).ToList();
        var abilities = tiles.Where(t => t.Basis.Length == 0).ToList();
        var skills = tiles.Where(t => t.Basis.Length > 0).ToList();

        if (abilities.Count == 0 || skills.Count == 0)
        {
            DrawBlock(tiles, width);
            return;
        }

        DrawBlock(abilities, width);
        ImGuiHelpers.ScaledDummy(3f);
        DrawBlock(skills, width);
    }

    private static Tile ToTile(StatValue stat)
    {
        var name = stat.Name.Trim();
        var basis = string.Empty;
        var open = name.LastIndexOf('(');
        if (name.EndsWith(')') && open > 0 && open < name.Length - 2)
        {
            basis = name[(open + 1)..^1].Trim();
            name = name[..open].TrimEnd();
        }
        return new Tile(name, basis, StatDisplay.Stat(stat.Modifier), stat.Modifier, stat.Name);
    }

    private static void DrawBlock(List<Tile> tiles, float width)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var spacing = 4f * scale;
        var pad = 7f * scale;
        var gap = 8f * scale;
        var height = ImGui.GetTextLineHeight() + 7f * scale;
        var space = ImGui.CalcTextSize(" ").X;

        float Measure(Tile t, bool withBasis) =>
            pad * 2f + ImGui.CalcTextSize(t.Name).X + gap + ImGui.CalcTextSize(t.Value).X
            + (withBasis && t.Basis.Length > 0 ? space + ImGui.CalcTextSize(t.Basis).X : 0f);

        int ColumnsFor(float tileWidth) =>
            Math.Clamp((int)((width + spacing) / (tileWidth + spacing)), 1, MaxColumns);

        // La base s'affiche sur toutes les tuiles ou sur aucune : deux colonnes valent mieux qu'une
        // longue liste, et l'infobulle rend le nom complet.
        var withBasis = true;
        var columns = ColumnsFor(tiles.Max(t => Measure(t, true)));
        if (columns == 1)
        {
            var bare = ColumnsFor(tiles.Max(t => Measure(t, false)));
            if (bare > 1)
            {
                columns = bare;
                withBasis = false;
            }
        }
        columns = Math.Min(columns, tiles.Count);

        var tileWidth = (width - spacing * (columns - 1)) / columns;
        var rows = (tiles.Count + columns - 1) / columns;
        var origin = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var rounding = MasterEventTheme.RadiusControl * scale;
        var fill = ImGui.GetColorU32(MasterEventTheme.ThemeFrameBg);
        var nameColor = ImGui.GetColorU32(MasterEventTheme.TextSecondary);
        var basisColor = ImGui.GetColorU32(MasterEventTheme.TextDim);
        var signed = VitalLabels.ActiveTemplate is not { StatResolution: StatResolution.Target };
        string? tooltip = null;

        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var min = origin + new Vector2(i % columns * (tileWidth + spacing), i / columns * (height + spacing));
            var max = min + new Vector2(tileWidth, height);
            dl.AddRectFilled(min, max, fill, rounding);

            var valueSize = ImGui.CalcTextSize(tile.Value);
            var textY = min.Y + (height - valueSize.Y) / 2f;
            var valueX = max.X - pad - valueSize.X;
            dl.AddText(new Vector2(valueX, textY), ImGui.GetColorU32(ValueColor(tile.Modifier, signed)), tile.Value);

            var nameX = min.X + pad;
            var room = valueX - gap - nameX;
            var name = Clip(tile.Name, room, out var clipped);
            dl.AddText(new Vector2(nameX, textY), nameColor, name);

            var hidden = clipped || (!withBasis && tile.Basis.Length > 0);
            if (withBasis && tile.Basis.Length > 0 && !clipped)
            {
                var basisX = nameX + ImGui.CalcTextSize(name).X + space;
                if (basisX + ImGui.CalcTextSize(tile.Basis).X <= valueX - gap)
                    dl.AddText(new Vector2(basisX, textY), basisColor, tile.Basis);
                else
                    hidden = true;
            }

            if (hidden && ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min, max))
                tooltip = $"{tile.FullName}  {tile.Value}";
        }

        ImGui.Dummy(new Vector2(width, rows * height + (rows - 1) * spacing));
        if (tooltip != null) ImGui.SetTooltip(tooltip);
    }

    // Une stat nulle s'efface, une stat négative se lit d'un coup d'œil ; un seuil n'a pas de signe.
    private static Vector4 ValueColor(int modifier, bool signed) =>
        !signed || modifier > 0 ? MasterEventTheme.TextStrong
        : modifier < 0 ? MasterEventTheme.DangerColor
        : MasterEventTheme.TextDim;

    private static string Clip(string text, float room, out bool clipped)
    {
        clipped = ImGui.CalcTextSize(text).X > room;
        if (!clipped) return text;

        var end = text.Length;
        while (end > 1 && ImGui.CalcTextSize(text[..end].TrimEnd() + Dots).X > room) end--;
        return text[..end].TrimEnd() + Dots;
    }
}
