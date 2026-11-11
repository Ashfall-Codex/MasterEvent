using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace MasterEvent.UI.Components;

// Barre de séance : l'état de la salle en puces à gauche, qui passent à la ligne, puis à droite les
// icônes discrètes, une action teintée facultative et l'action pleine. Le bloc de droite descend
// entier quand il ne tient pas sur la ligne des puces.
public static class SessionBar
{
    // Sans clic, la puce n'a pas de survol. TooltipContent, s'il est donné, dessine l'infobulle à la
    // place du texte Tooltip (lignes de couleurs différentes).
    public sealed record Chip(string Id, FontAwesomeIcon Icon, Vector4 IconColor, string Text, Vector4 TextColor,
        Vector4? Tint, string Tooltip, Action? OnClick, Action? TooltipContent = null);

    public readonly record struct Icon(FontAwesomeIcon Glyph, string Id, string Tooltip, Action OnClick);

    // Grisée, l'action garde son infobulle, qui dit pourquoi.
    public sealed record BarAction(string Label, string Id, string? Tooltip, bool Disabled = false);

    public enum Clicked { None, Primary, Tinted }

    public sealed class Content
    {
        // En tête, en combat : « Manche 3 » en accent, puis « 2 / 7 ont joué » en retrait.
        public string? Lead;
        public string? LeadDetail;
        public IReadOnlyList<Chip> Chips = [];
        public IReadOnlyList<Icon> Icons = [];
        public BarAction? Tinted;
        public BarAction? Primary;
    }

    public static float Height => ImGui.GetFrameHeight() * 1.2f;

    public static float LineWidth => ImGui.GetContentRegionAvail().X - MasterEventTheme.ListRightMargin;

    public static float IconsWidth(IReadOnlyList<Icon> icons)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        return icons.Sum(i => Buttons.IconWidth(i.Glyph) + spacing);
    }

    // Les puces et les icônes agissent d'elles-mêmes ; les deux actions sont rendues à l'appelant,
    // qui peut encore filtrer le clic (double clic, action qui vient de changer).
    public static Clicked Draw(Content content)
    {
        var style = ImGui.GetStyle();
        var spacing = style.ItemSpacing.X;
        var height = Height;
        var iconsWidth = IconsWidth(content.Icons);

        var origin = ImGui.GetCursorScreenPos();
        var lineWidth = LineWidth;
        var limit = origin.X + lineWidth;
        var x = origin.X;
        var y = origin.Y;

        if (content.Lead != null)
        {
            var textY = y + (height - ImGui.GetTextLineHeight()) / 2f;
            ImGui.SetCursorScreenPos(new Vector2(x, textY));
            ImGui.TextColored(MasterEventTheme.AccentText, content.Lead);
            x += ImGui.CalcTextSize(content.Lead).X + spacing;
            if (content.LeadDetail != null)
            {
                ImGui.SetCursorScreenPos(new Vector2(x, textY));
                ImGui.TextColored(MasterEventTheme.MutedTextColor, content.LeadDetail);
                x += ImGui.CalcTextSize(content.LeadDetail).X + spacing;
            }
            x += spacing;
        }

        // Les puces passent à la ligne plutôt que de déborder d'une fenêtre étroite ; seule une puce
        // plus large qu'une ligne entière se coupe.
        foreach (var chip in content.Chips)
        {
            var width = Math.Min(ChipWidth(chip), lineWidth);
            if (x > origin.X && x + width > limit)
            {
                x = origin.X;
                y += height + style.ItemSpacing.Y;
            }

            ImGui.SetCursorScreenPos(new Vector2(x, y));
            if (DrawChip(chip, height, lineWidth, out var drawn)) chip.OnClick?.Invoke();
            x += drawn + spacing;
        }

        var tintedWidth = content.Tinted != null ? Buttons.TextWidth(content.Tinted.Label) : 0f;
        var primaryWidth = content.Primary != null ? Buttons.PrimaryWidth(content.Primary.Label) : 0f;
        var rightWidth = iconsWidth + (content.Tinted != null ? tintedWidth + spacing : 0f) + primaryWidth;
        if (x > origin.X && x + rightWidth > limit)
            y += height + style.ItemSpacing.Y;

        var rightX = limit - rightWidth;
        var iconY = y + (height - ImGui.GetFrameHeight()) / 2f;
        foreach (var icon in content.Icons)
        {
            ImGui.SetCursorScreenPos(new Vector2(rightX, iconY));
            if (Buttons.QuietIcon(icon.Glyph, icon.Id, icon.Tooltip)) icon.OnClick();
            rightX += Buttons.IconWidth(icon.Glyph) + spacing;
        }

        var clicked = Clicked.None;
        if (content.Tinted is { } tinted)
        {
            ImGui.SetCursorScreenPos(new Vector2(rightX, y));
            if (DrawAction(tinted, tintedWidth, height, primary: false)) clicked = Clicked.Tinted;
            rightX += tintedWidth + spacing;
        }

        if (content.Primary is { } primaryAction)
        {
            ImGui.SetCursorScreenPos(new Vector2(rightX, y));
            if (DrawAction(primaryAction, primaryWidth, height, primary: true)) clicked = Clicked.Primary;
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, y + height));
        ImGui.Dummy(Vector2.Zero);
        return clicked;
    }

    public static void End()
    {
        ImGuiHelpers.ScaledDummy(2f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
    }

    private static bool DrawAction(BarAction action, float width, float height, bool primary)
    {
        bool clicked;
        var label = action.Label + "##" + action.Id;
        using (ImRaii.Disabled(action.Disabled))
            clicked = primary ? Buttons.Primary(label, width, height) : Buttons.Tinted(label, width, height);
        if (action.Tooltip != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(action.Tooltip);
        return clicked && !action.Disabled;
    }

    private static float ChipChrome(Chip chip)
    {
        var scale = ImGuiHelpers.GlobalScale;
        float iconWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            iconWidth = ImGui.CalcTextSize(chip.Icon.ToIconString()).X;
        return 20f * scale + iconWidth + 6f * scale;
    }

    private static float ChipWidth(Chip chip) => ChipChrome(chip) + ImGui.CalcTextSize(chip.Text).X;

    private static bool DrawChip(Chip chip, float height, float maxWidth, out float width)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var text = chip.Text;
        width = ChipWidth(chip);
        var clipped = width > maxWidth;
        if (clipped)
        {
            var chrome = ChipChrome(chip);
            text = TextFit.Fit(chip.Text, maxWidth - chrome);
            width = chrome + ImGui.CalcTextSize(text).X;
        }

        var pos = ImGui.GetCursorScreenPos();
        var clickedButton = ImGui.InvisibleButton("##chip_" + chip.Id, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var interactive = chip.OnClick != null;

        var dl = ImGui.GetWindowDrawList();
        var rounding = MasterEventTheme.RadiusCard * scale;
        var fill = chip.Tint is { } tint
            ? tint with { W = hovered && interactive ? 0.22f : 0.12f }
            : hovered && interactive ? MasterEventTheme.ThemeFrameBgHovered : MasterEventTheme.ThemeFrameBg;
        var border = chip.Tint is { } edge ? edge with { W = 0.5f } : MasterEventTheme.ThemeBorder;
        dl.AddRectFilled(pos, pos + new Vector2(width, height), ImGui.GetColorU32(fill), rounding);
        dl.AddRect(pos, pos + new Vector2(width, height), ImGui.GetColorU32(border), rounding, ImDrawFlags.None, 1f * scale);

        var x = pos.X + 10f * scale;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            var icon = chip.Icon.ToIconString();
            var iconSize = ImGui.CalcTextSize(icon);
            dl.AddText(new Vector2(x, pos.Y + (height - iconSize.Y) / 2f), ImGui.GetColorU32(chip.IconColor), icon);
            x += iconSize.X + 6f * scale;
        }

        var textSize = ImGui.CalcTextSize(text);
        dl.AddText(new Vector2(x, pos.Y + (height - textSize.Y) / 2f), ImGui.GetColorU32(chip.TextColor), text);

        if (hovered && (clipped || chip.TooltipContent != null || chip.Tooltip.Length > 0))
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
            if (clipped) ImGui.TextUnformatted(chip.Text);
            if (chip.TooltipContent != null) chip.TooltipContent();
            else if (chip.Tooltip.Length > 0) ImGui.TextUnformatted(chip.Tooltip);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        return clickedButton && interactive;
    }
}
