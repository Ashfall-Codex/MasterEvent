using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;

namespace MasterEvent.UI.Components;

public static class DiceControls
{
    // Marge intérieure d'une tuile : le texte ne doit jamais toucher le bord arrondi.
    private const float TileTextPadding = 6f;

    // En dessous de ce facteur, réduire davantage rendrait le libellé illisible : on tronque.
    private const float MinTextScale = 0.72f;

    private static string rollStatFilter = string.Empty;
    public static string FormatStatChoice(StatValue stat)
        => stat.Modifier >= 0 ? $"{stat.Name}  +{stat.Modifier}" : $"{stat.Name}  {stat.Modifier}";
    public static void DrawRollStatMenu(IVitalEntity entity, string id, Action<string?> onRoll, bool withHeader = false)
    {
        if (withHeader)
        {
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Dice.SelectStat"));
            ImGui.Separator();
        }

        var stats = entity.Stats ?? [];
        if (stats.Count > VitalsControls.StatFilterThreshold)
        {
            ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint($"##roll_filter_{id}", Loc.Get("Models.StatsFilter"), ref rollStatFilter, 64);
            ImGuiHelpers.ScaledDummy(2f);
        }

        if (ImGui.Selectable(Loc.Get("Dice.NoStat")))
            onRoll(null);

        foreach (var stat in VitalsControls.Filtered(stats, rollStatFilter))
            if (ImGui.Selectable(FormatStatChoice(stat)))
                onRoll(stat.Id);
    }

    public static void DrawLastRollInline(IVitalEntity entity)
    {
        if (entity.LastRollResult <= 0) return;

        ImGui.SameLine();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.TextStrong, FontAwesomeIcon.Dice.ToIconString());
        ImGui.SameLine(0, 4f * ImGuiHelpers.GlobalScale);
        ImGui.TextColored(MasterEventTheme.TextStrong, $"{entity.LastRollResult} / {entity.LastRollMax}");
    }

    // Tuile cliquable d'un jet : un libellé principal, un modificateur optionnel en dessous.
    public static void DrawDiceTile(string line1, string? line2, string id, float w, float h, Action onClick)
    {
        var rounding = 6f * ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, rounding);

        if (ImGui.Button("##" + id, new Vector2(w, h)))
            onClick();

        var btnMin = ImGui.GetItemRectMin();
        var dlst = ImGui.GetWindowDrawList();

        var lineHeight = ImGui.GetFontSize();
        var totalTextH = line2 != null ? lineHeight * 2f + 2f : lineHeight;
        var textY = btnMin.Y + (h - totalTextH) / 2f;

        var centerX = btnMin.X + w / 2f;
        var maxTextW = w - TileTextPadding * 2f * ImGuiHelpers.GlobalScale;

        DrawFittedText(dlst, line1, centerX, textY, maxTextW, MasterEventTheme.TextStrong);

        if (line2 != null)
            DrawFittedText(dlst, line2, centerX, textY + lineHeight + 2f, maxTextW,
                MasterEventTheme.TextSecondary);

        ImGui.PopStyleVar();
    }

    /// <summary>
    /// Texte centré tenant dans une largeur imposée : la police rétrécit d'abord, puis le
    /// libellé est tronqué si le plancher de lisibilité ne suffit toujours pas. Sans ça, un
    /// nom de stat long (« Représentation (Cha) ») débordait de sa tuile sur les voisines.
    /// </summary>
    private static void DrawFittedText(ImDrawListPtr dl, string text, float centerX, float y,
        float maxWidth, Vector4 color)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0f) return;

        var baseSize = ImGui.GetFontSize();
        var width = ImGui.CalcTextSize(text).X;

        // La largeur d'un texte ImGui est proportionnelle à la taille de police : le facteur
        // de réduction se déduit donc directement, sans remesurer à chaque essai.
        var scale = width <= maxWidth ? 1f : Math.Max(MinTextScale, maxWidth / width);

        var shown = text;
        if (width * scale > maxWidth)
        {
            const string ellipsis = "...";
            while (shown.Length > 1 && ImGui.CalcTextSize(shown + ellipsis).X * scale > maxWidth)
                shown = shown[..^1];
            shown += ellipsis;
            width = ImGui.CalcTextSize(shown).X;
        }

        dl.AddText(ImGui.GetFont(), baseSize * scale,
            new Vector2(centerX - width * scale / 2f, y), ImGui.GetColorU32(color), shown);
    }

    // Séparateur, titre et derniers jets. Le nombre d'entrées et le bouton d'effacement
    // varient selon la fenêtre hôte, d'où les deux paramètres.
    public static void DrawRollHistory(SessionManager session, int maxEntries, bool showClearButton)
    {
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Dice.History"));
        ImGuiHelpers.ScaledDummy(2f);

        if (session.RollHistory.Count == 0)
        {
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Dice.NoHistory"));
            return;
        }

        for (var i = 0; i < session.RollHistory.Count && i < maxEntries; i++)
        {
            var roll = session.RollHistory[i];
            var rollModStr = roll.Modifier >= 0 ? $"+{roll.Modifier}" : roll.Modifier.ToString();
            var statInfo = roll.StatName != null ? $" [{roll.StatName} {rollModStr}]" : "";
            var breakdown = roll.IndividualRolls is { Length: > 1 }
                ? string.Join(" + ", roll.IndividualRolls) + " = "
                : "";
            var line = $"{roll.RollerName}: {breakdown}{roll.RawRoll}/{roll.DiceMax}{statInfo} = {roll.Total}";

            // Mettre en valeur le dernier jet
            if (i == 0)
                ImGui.TextColored(MasterEventTheme.TextStrong, line);
            else
                ImGui.TextColored(MasterEventTheme.TextDim, line);

            if (roll is { Target: { } target, Success: { } success })
            {
                ImGui.SameLine();
                var verdict = string.Format(Loc.Get(success ? "Dice.Success" : "Dice.Failure"), target);
                var color = success ? new Vector4(0.4f, 0.9f, 0.4f, 1f) : new Vector4(0.9f, 0.4f, 0.4f, 1f);
                ImGui.TextColored(i == 0 ? color : color with { W = 0.6f }, verdict);
            }
        }

        if (!showClearButton)
            return;

        ImGuiHelpers.ScaledDummy(4f);
        if (ImGui.SmallButton(Loc.Get("Dice.ClearHistory")))
            session.ClearRollHistory();
    }
}
