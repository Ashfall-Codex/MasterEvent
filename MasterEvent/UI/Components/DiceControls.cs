using System;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;

namespace MasterEvent.UI.Components;

public static partial class DiceControls
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

    // Tuile cliquable d'un jet : le nom, l'abréviation entre parenthèses en dessous si le nom
    // en porte une (« Intimidation (Cha) »), puis le modificateur. Séparer l'abréviation
    // évite de tronquer ou de réduire les noms longs.
    public static void DrawDiceTile(string line1, string? line2, string id, float w, float h, Action onClick)
    {
        var rounding = 6f * ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, rounding);

        if (ImGui.Button("##" + id, new Vector2(w, h)))
            onClick();

        var btnMin = ImGui.GetItemRectMin();
        var dlst = ImGui.GetWindowDrawList();

        string? abbreviation = null;
        var name = line1;
        var split = AbbreviationRegex().Match(line1);
        if (split.Success)
        {
            name = split.Groups[1].Value;
            abbreviation = split.Groups[2].Value;
        }

        const float abbreviationFactor = 0.85f;
        const float modifierFactor = 1.1f;
        var fontSize = ImGui.GetFontSize();
        var gap = 2f * ImGuiHelpers.GlobalScale;

        var totalTextH = fontSize;
        if (abbreviation != null) totalTextH += gap + fontSize * abbreviationFactor;
        if (line2 != null) totalTextH += gap + fontSize * modifierFactor;

        var textY = btnMin.Y + (h - totalTextH) / 2f;
        var centerX = btnMin.X + w / 2f;
        var maxTextW = w - TileTextPadding * 2f * ImGuiHelpers.GlobalScale;

        DrawFittedText(dlst, name, centerX, textY, maxTextW, MasterEventTheme.TextStrong);
        textY += fontSize + gap;

        if (abbreviation != null)
        {
            DrawFittedText(dlst, abbreviation, centerX, textY, maxTextW, MasterEventTheme.TextDim, abbreviationFactor);
            textY += fontSize * abbreviationFactor + gap;
        }

        if (line2 != null)
            DrawFittedText(dlst, line2, centerX, textY, maxTextW, MasterEventTheme.TextSecondary, modifierFactor);

        ImGui.PopStyleVar();
    }

    [GeneratedRegex(@"^(.*?)\s*\(([^()]+)\)$")]
    private static partial Regex AbbreviationRegex();

    private static void DrawFittedText(ImDrawListPtr dl, string text, float centerX, float y,
        float maxWidth, Vector4 color, float sizeFactor = 1f)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0f) return;

        var baseSize = ImGui.GetFontSize() * sizeFactor;
        var width = ImGui.CalcTextSize(text).X * sizeFactor;

        // La largeur d'un texte ImGui est proportionnelle à la taille de police : le facteur
        // de réduction se déduit donc directement, sans remesurer à chaque essai.
        var scale = width <= maxWidth ? 1f : Math.Max(MinTextScale, maxWidth / width);

        var shown = text;
        if (width * scale > maxWidth)
        {
            const string ellipsis = "...";
            while (shown.Length > 1 && ImGui.CalcTextSize(shown + ellipsis).X * sizeFactor * scale > maxWidth)
                shown = shown[..^1];
            shown += ellipsis;
            width = ImGui.CalcTextSize(shown).X * sizeFactor;
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
