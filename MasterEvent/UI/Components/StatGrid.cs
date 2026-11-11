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

    private static void DrawBlock(List<Tile> tiles, float width, Rolling? rolling)
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

            var hovered = false;
            var highlighted = false;
            if (rolling is { } roll)
            {
                ImGui.SetCursorScreenPos(min);
                // ImGui refuse un bouton de taille nulle, possible dans une fenêtre réduite à rien.
                if (ImGui.InvisibleButton("##stat_" + tile.Stat.Id, Vector2.Max(max - min, Vector2.One)))
                    roll.OnRoll(tile.Stat);
                hovered = ImGui.IsItemHovered();
                if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                highlighted = roll.HighlightedStatId != null && roll.HighlightedStatId == tile.Stat.Id;
            }

            dl.AddRectFilled(min, max, hovered ? ImGui.GetColorU32(MasterEventTheme.ThemeFrameBgHovered) : fill, rounding);
            if (highlighted)
            {
                dl.AddRectFilled(min, max, ImGui.GetColorU32(MasterEventTheme.PendingColor with { W = 0.12f }), rounding);
                dl.AddRect(min, max, ImGui.GetColorU32(MasterEventTheme.PendingColor), rounding, ImDrawFlags.None, scale);
            }

            var valueSize = ImGui.CalcTextSize(tile.Value);
            var textY = min.Y + (height - valueSize.Y) / 2f;
            var valueX = max.X - pad - valueSize.X;
            dl.AddText(new Vector2(valueX, textY), ImGui.GetColorU32(ValueColor(tile.Modifier, signed)), tile.Value);

            // Le dé ne paraît qu'au survol : le nom ne cède sa place qu'à ce moment.
            var nameEnd = valueX;
            if (hovered)
            {
                var dice = FontAwesomeIcon.Dice.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    var diceSize = ImGui.CalcTextSize(dice);
                    nameEnd = valueX - gap / 2f - diceSize.X;
                    dl.AddText(new Vector2(nameEnd, min.Y + (height - diceSize.Y) / 2f),
                        ImGui.GetColorU32(MasterEventTheme.TextSecondary), dice);
                }
            }

            var nameX = min.X + pad;
            var room = nameEnd - gap - nameX;
            var name = TextFit.Clip(tile.Name, room, out var clipped);
            dl.AddText(new Vector2(nameX, textY), nameColor, name);

            var hidden = clipped || (!withBasis && tile.Basis.Length > 0);
            if (withBasis && tile.Basis.Length > 0 && !clipped)
            {
                var basisX = nameX + ImGui.CalcTextSize(name).X + space;
                if (basisX + ImGui.CalcTextSize(tile.Basis).X <= nameEnd - gap)
                    dl.AddText(new Vector2(basisX, textY), basisColor, tile.Basis);
                else
                    hidden = true;
            }

            if (rolling is { } r)
            {
                if (hovered) DrawRollTooltip(tile, hidden, highlighted, r);
            }
            else if (hidden && ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min, max))
            {
                tooltip = $"{tile.FullName}  {tile.Value}";
            }
        }

        if (rolling != null) ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, rows * height + (rows - 1) * spacing));
        if (tooltip != null) ImGui.SetTooltip(tooltip);
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
    private static Vector4 ValueColor(int modifier, bool signed) =>
        !signed || modifier > 0 ? MasterEventTheme.TextStrong
        : modifier < 0 ? MasterEventTheme.DangerColor
        : MasterEventTheme.TextDim;
}
