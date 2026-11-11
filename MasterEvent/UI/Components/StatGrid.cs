using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;

namespace MasterEvent.UI.Components;

// Stats en tuiles : nom à gauche, valeur alignée à droite. Les compétences d'un modèle
// (« Acrobaties (Dex) ») forment un second bloc sous les caractéristiques, leur base en retrait.
// Avec onRoll, chaque tuile devient un bouton qui lance sa stat ; sans, la grille reste en lecture seule.
public static class StatGrid
{
    private const int MaxColumns = 3;

    private readonly record struct Tile(string Name, string Basis, string Value, int Modifier, string FullName, StatValue Stat);

    // Ce qu'il faut pour des tuiles cliquables : la tuile surlignée est celle que le MJ attend, et
    // tempModifier, le bonus temporaire qui s'ajoutera au jet, s'annonce dans l'infobulle.
    private readonly record struct Rolling(Action<StatValue> OnRoll, string? HighlightedStatId,
        string? HighlightTooltip, int TempModifier);

    public static void Draw(IReadOnlyList<StatValue> stats, float width, Action<StatValue>? onRoll = null,
        string? highlightedStatId = null, string? highlightTooltip = null, int tempModifier = 0)
    {
        if (stats.Count == 0) return;

        Rolling? rolling = onRoll != null
            ? new Rolling(onRoll, highlightedStatId, highlightTooltip, tempModifier)
            : null;

        var tiles = stats.Select(ToTile).ToList();
        var abilities = tiles.Where(t => t.Basis.Length == 0).ToList();
        var skills = tiles.Where(t => t.Basis.Length > 0).ToList();

        if (abilities.Count == 0 || skills.Count == 0)
        {
            DrawBlock(tiles, width, rolling);
            return;
        }

        DrawBlock(abilities, width, rolling);
        ImGuiHelpers.ScaledDummy(3f);
        DrawBlock(skills, width, rolling);
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
        return new Tile(name, basis, StatDisplay.Stat(stat.Modifier), stat.Modifier, stat.Name, stat);
    }

    // Mesures et couleurs d'un bloc, communes à toutes ses tuiles.
    private struct Grid
    {
        public float Scale;
        public float Spacing;
        public float Pad;
        public float Gap;
        public float Height;
        public float Space;
        public int Columns;
        public bool WithBasis;
        public float TileWidth;
        public Vector2 Origin;
        public ImDrawListPtr Dl;
        public float Rounding;
        public uint Fill;
        public uint NameColor;
        public uint BasisColor;
        public bool Signed;
    }

    private static void DrawBlock(List<Tile> tiles, float width, Rolling? rolling)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var grid = new Grid
        {
            Scale = scale,
            Spacing = 4f * scale,
            Pad = 7f * scale,
            Gap = 8f * scale,
            Height = ImGui.GetTextLineHeight() + 7f * scale,
            Space = ImGui.CalcTextSize(" ").X,
        };
        (grid.Columns, grid.WithBasis) = ChooseColumns(tiles, width, grid);
        grid.TileWidth = (width - grid.Spacing * (grid.Columns - 1)) / grid.Columns;
        grid.Origin = ImGui.GetCursorScreenPos();
        grid.Dl = ImGui.GetWindowDrawList();
        grid.Rounding = MasterEventTheme.RadiusControl * scale;
        grid.Fill = ImGui.GetColorU32(MasterEventTheme.ThemeFrameBg);
        grid.NameColor = ImGui.GetColorU32(MasterEventTheme.TextSecondary);
        grid.BasisColor = ImGui.GetColorU32(MasterEventTheme.TextDim);
        grid.Signed = VitalLabels.ActiveTemplate is not { StatResolution: StatResolution.Target };

        var rows = (tiles.Count + grid.Columns - 1) / grid.Columns;
        string? tooltip = null;
        for (var i = 0; i < tiles.Count; i++)
        {
            var min = grid.Origin + new Vector2(i % grid.Columns * (grid.TileWidth + grid.Spacing),
                i / grid.Columns * (grid.Height + grid.Spacing));
            tooltip = DrawTile(grid, tiles[i], min, rolling) ?? tooltip;
        }

        if (rolling != null) ImGui.SetCursorScreenPos(grid.Origin);
        ImGui.Dummy(new Vector2(width, rows * grid.Height + (rows - 1) * grid.Spacing));
        if (tooltip != null) ImGui.SetTooltip(tooltip);
    }

    // La base s'affiche sur toutes les tuiles ou sur aucune : deux colonnes valent mieux qu'une
    // longue liste, et l'infobulle rend le nom complet.
    private static (int Columns, bool WithBasis) ChooseColumns(List<Tile> tiles, float width, Grid grid)
    {
        var withBasis = true;
        var columns = ColumnsFor(width, tiles.Max(t => Measure(t, true, grid)), grid.Spacing);
        if (columns == 1)
        {
            var bare = ColumnsFor(width, tiles.Max(t => Measure(t, false, grid)), grid.Spacing);
            if (bare > 1)
            {
                columns = bare;
                withBasis = false;
            }
        }
        return (Math.Min(columns, tiles.Count), withBasis);
    }

    private static float Measure(Tile t, bool withBasis, in Grid grid) =>
        grid.Pad * 2f + ImGui.CalcTextSize(t.Name).X + grid.Gap + ImGui.CalcTextSize(t.Value).X
        + (withBasis && t.Basis.Length > 0 ? grid.Space + ImGui.CalcTextSize(t.Basis).X : 0f);

    private static int ColumnsFor(float width, float tileWidth, float spacing) =>
        Math.Clamp((int)((width + spacing) / (tileWidth + spacing)), 1, MaxColumns);

    // Renvoie l'infobulle du nom complet quand la tuile, en lecture seule et survolée, le coupe.
    private static string? DrawTile(in Grid grid, Tile tile, Vector2 min, Rolling? rolling)
    {
        var max = min + new Vector2(grid.TileWidth, grid.Height);
        var (hovered, highlighted) = rolling is { } roll ? DrawTileButton(tile, min, max, roll) : (false, false);

        var dl = grid.Dl;
        dl.AddRectFilled(min, max, hovered ? ImGui.GetColorU32(MasterEventTheme.ThemeFrameBgHovered) : grid.Fill, grid.Rounding);
        if (highlighted)
        {
            dl.AddRectFilled(min, max, ImGui.GetColorU32(MasterEventTheme.PendingColor with { W = 0.12f }), grid.Rounding);
            dl.AddRect(min, max, ImGui.GetColorU32(MasterEventTheme.PendingColor), grid.Rounding, ImDrawFlags.None, grid.Scale);
        }

        var valueSize = ImGui.CalcTextSize(tile.Value);
        var textY = min.Y + (grid.Height - valueSize.Y) / 2f;
        var valueX = max.X - grid.Pad - valueSize.X;
        dl.AddText(new Vector2(valueX, textY), ImGui.GetColorU32(ValueColor(tile.Modifier, grid.Signed)), tile.Value);

        // Le dé ne paraît qu'au survol : le nom ne cède sa place qu'à ce moment.
        var nameEnd = valueX;
        if (hovered)
        {
            var dice = FontAwesomeIcon.Dice.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                var diceSize = ImGui.CalcTextSize(dice);
                nameEnd = valueX - grid.Gap / 2f - diceSize.X;
                dl.AddText(new Vector2(nameEnd, min.Y + (grid.Height - diceSize.Y) / 2f),
                    ImGui.GetColorU32(MasterEventTheme.TextSecondary), dice);
            }
        }

        var nameX = min.X + grid.Pad;
        var room = nameEnd - grid.Gap - nameX;
        var name = TextFit.Clip(tile.Name, room, out var clipped);
        dl.AddText(new Vector2(nameX, textY), grid.NameColor, name);

        var hidden = clipped || (!grid.WithBasis && tile.Basis.Length > 0);
        if (grid.WithBasis && tile.Basis.Length > 0 && !clipped)
        {
            var basisX = nameX + ImGui.CalcTextSize(name).X + grid.Space;
            if (basisX + ImGui.CalcTextSize(tile.Basis).X <= nameEnd - grid.Gap)
                dl.AddText(new Vector2(basisX, textY), grid.BasisColor, tile.Basis);
            else
                hidden = true;
        }

        if (rolling is { } r)
        {
            if (hovered) DrawRollTooltip(tile, hidden, highlighted, r);
            return null;
        }
        return hidden && ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min, max)
            ? $"{tile.FullName}  {tile.Value}"
            : null;
    }

    private static (bool Hovered, bool Highlighted) DrawTileButton(Tile tile, Vector2 min, Vector2 max, Rolling roll)
    {
        ImGui.SetCursorScreenPos(min);
        // ImGui refuse un bouton de taille nulle, possible dans une fenêtre réduite à rien.
        if (ImGui.InvisibleButton("##stat_" + tile.Stat.Id, Vector2.Max(max - min, Vector2.One)))
            roll.OnRoll(tile.Stat);
        var hovered = ImGui.IsItemHovered();
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return (hovered, roll.HighlightedStatId != null && roll.HighlightedStatId == tile.Stat.Id);
    }

    // Une seule infobulle par tuile : le nom entier quand la tuile le coupe, l'attente du MJ, puis le jet.
    private static void DrawRollTooltip(Tile tile, bool nameHidden, bool highlighted, Rolling rolling)
    {
        ImGui.BeginTooltip();
        if (nameHidden)
            ImGui.TextUnformatted(tile.FullName);
        if (highlighted && !string.IsNullOrEmpty(rolling.HighlightTooltip))
            ImGui.TextColored(MasterEventTheme.PendingColor, rolling.HighlightTooltip);

        var line = rolling.TempModifier != 0
            ? string.Format(Loc.Get("StatGrid.RollBonusFmt"), tile.Name, tile.Value, StatDisplay.Signed(rolling.TempModifier))
            : string.Format(Loc.Get("StatGrid.RollFmt"), tile.Name, tile.Value);
        ImGui.TextColored(MasterEventTheme.TextSecondary, line);
        ImGui.EndTooltip();
    }

    // Une stat nulle s'efface, une stat négative se lit d'un coup d'œil ; un seuil n'a pas de signe.
    private static Vector4 ValueColor(int modifier, bool signed)
    {
        if (!signed || modifier > 0) return MasterEventTheme.TextStrong;
        return modifier < 0 ? MasterEventTheme.DangerColor : MasterEventTheme.TextDim;
    }
}
