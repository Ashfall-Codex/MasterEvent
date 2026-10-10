using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace MasterEvent.UI.Components;

// Éléments de mise en page repris à l'identique par plusieurs fenêtres.
public static class LayoutControls
{

    public static void DrawCenteredWrapped(string text, Vector4 color, float width)
    {
        if (string.IsNullOrEmpty(text)) return;

        foreach (var line in WrapToWidth(text, width))
        {
            var size = ImGui.CalcTextSize(line);
            var offset = Math.Max(0f, (width - size.X) / 2f);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
            ImGui.TextColored(color, line);
        }
    }

    public static bool DrawEmptyState(FontAwesomeIcon icon, string title, string hint,
        string? buttonLabel = null, FontAwesomeIcon? buttonIcon = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var avail = ImGui.GetContentRegionAvail();
        var wrapWidth = MathF.Min(avail.X * 0.85f, 420f * scale);
        var originX = ImGui.GetCursorPosX();

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + MathF.Max(8f * scale, avail.Y * 0.22f));

        var iconText = icon.ToIconString();
        var iconColor = MasterEventTheme.AccentColor with { W = 0.55f };
        if (Plugin.LargeIconFont is { Available: true } largeIcon)
        {
            using (largeIcon.Push())
            {
                var iconSize = ImGui.CalcTextSize(iconText);
                ImGui.SetCursorPosX(originX + MathF.Max(0f, (avail.X - iconSize.X) / 2f));
                ImGui.TextColored(iconColor, iconText);
            }
        }
        else
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.SetWindowFontScale(2.2f);
                var iconSize = ImGui.CalcTextSize(iconText);
                ImGui.SetCursorPosX(originX + MathF.Max(0f, (avail.X - iconSize.X) / 2f));
                ImGui.TextColored(iconColor, iconText);
                ImGui.SetWindowFontScale(1f);
            }
        }

        ImGuiHelpers.ScaledDummy(8f);

        ImGui.SetWindowFontScale(1.2f);
        var titleSize = ImGui.CalcTextSize(title);
        ImGui.SetCursorPosX(originX + MathF.Max(0f, (avail.X - titleSize.X) / 2f));
        ImGui.TextUnformatted(title);
        ImGui.SetWindowFontScale(1f);

        ImGuiHelpers.ScaledDummy(2f);

        foreach (var line in WrapToWidth(hint, wrapWidth))
        {
            var lineSize = ImGui.CalcTextSize(line);
            ImGui.SetCursorPosX(originX + MathF.Max(0f, (avail.X - lineSize.X) / 2f));
            ImGui.TextColored(MasterEventTheme.TextDim, line);
        }

        if (string.IsNullOrEmpty(buttonLabel)) return false;

        ImGuiHelpers.ScaledDummy(12f);
        return DrawCallToActionButton(buttonLabel, buttonIcon, avail.X, originX);
    }

    private static bool DrawCallToActionButton(string label, FontAwesomeIcon? icon, float availWidth, float originX)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var padX = 18f * scale;
        var gap = 8f * scale;
        var height = ImGui.GetFrameHeight() + 10f * scale;

        var iconText = icon?.ToIconString();
        var iconSize = Vector2.Zero;
        if (iconText != null)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                iconSize = ImGui.CalcTextSize(iconText);
        }

        var labelSize = ImGui.CalcTextSize(label);
        var contentWidth = labelSize.X + (iconText != null ? iconSize.X + gap : 0f);
        var size = new Vector2(contentWidth + padX * 2f, height);

        ImGui.SetCursorPosX(originX + MathF.Max(0f, (availWidth - size.X) / 2f));
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var clicked = ImGui.InvisibleButton("##emptyStateAction", size);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var drawList = ImGui.GetWindowDrawList();
        var rounding = height / 2f;
        if (hovered)
        {
            var glow = 3f * scale;
            drawList.AddRectFilled(min - new Vector2(glow), max + new Vector2(glow),
                ImGui.GetColorU32(MasterEventTheme.AccentColor with { W = 0.22f }), rounding + glow);
        }

        var fill = active
            ? MasterEventTheme.ThemeButtonActive
            : MasterEventTheme.AccentColor with { W = hovered ? 1f : 0.85f };
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), rounding);
        drawList.AddRect(min, max, ImGui.GetColorU32(MasterEventTheme.ThemeButtonActive with { W = hovered ? 0.9f : 0.5f }),
            rounding, ImDrawFlags.None, scale);

        var textColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));
        var x = min.X + (size.X - contentWidth) / 2f;
        if (iconText != null)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                drawList.AddText(new Vector2(x, min.Y + (height - iconSize.Y) / 2f), textColor, iconText);
            x += iconSize.X + gap;
        }

        drawList.AddText(new Vector2(x, min.Y + (height - labelSize.Y) / 2f), textColor, label);
        return clicked;
    }

    private static List<string> WrapToWidth(string text, float width)
    {
        var lines = new List<string>();
        var current = string.Empty;

        foreach (var word in text.Split(' '))
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (current.Length > 0 && ImGui.CalcTextSize(candidate).X > width)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    public static void DrawNotice(string text, Vector4 color, FontAwesomeIcon icon = FontAwesomeIcon.ExclamationTriangle)
    {
        var availWidth = ImGui.GetContentRegionAvail().X;
        var padding = 6f * ImGuiHelpers.GlobalScale;
        var startX = ImGui.GetCursorPosX();
        var startScreen = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var iconStr = icon.ToIconString();
        float iconWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            iconWidth = ImGui.CalcTextSize(iconStr).X;

        var gap = ImGui.GetStyle().ItemSpacing.X;
        var contentWidth = iconWidth + gap + ImGui.CalcTextSize(text).X;
        var innerWidth = availWidth - padding * 2f;
        var centered = contentWidth <= innerWidth;

        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        ImGuiHelpers.ScaledDummy(2f);
        ImGui.Indent(padding);

        if (centered)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (innerWidth - contentWidth) / 2f);

        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(color, iconStr);
        ImGui.SameLine();

        if (centered)
        {
            ImGui.TextColored(color, text);
        }
        else
        {
            ImGui.PushTextWrapPos(startX + availWidth - padding);
            ImGui.TextColored(color, text);
            ImGui.PopTextWrapPos();
        }

        ImGui.Unindent(padding);
        ImGuiHelpers.ScaledDummy(2f);

        var endY = ImGui.GetCursorScreenPos().Y;
        dl.ChannelsSetCurrent(0);
        var min = startScreen;
        var max = new Vector2(startScreen.X + availWidth, endY);
        var rounding = MasterEventTheme.RadiusCard * ImGuiHelpers.GlobalScale;
        dl.AddRectFilled(min, max, ImGui.GetColorU32(color with { W = 0.12f }), rounding);
        dl.AddRect(min, max, ImGui.GetColorU32(color), rounding);
        dl.ChannelsMerge();
    }

    private static readonly Stack<(Vector2 Start, float Width, float Padding, ImDrawListPtr DrawList)> OpenCards = new();
    public static void BeginCard(string title, FontAwesomeIcon icon, Vector4? accentOverride = null)
    {
        var accent = accentOverride ?? MasterEventTheme.AccentColor;
        var availWidth = ImGui.GetContentRegionAvail().X;
        var padding = 8f * ImGuiHelpers.GlobalScale;
        var startScreen = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        OpenCards.Push((startScreen, availWidth, padding, dl));

        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Indent(padding);

        if (title.Length > 0)
        {
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(accent, icon.ToIconString());
            ImGui.SameLine();
            ImGui.TextColored(accent, title);
            ImGuiHelpers.ScaledDummy(2f);
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + availWidth - padding * 2f);
        ImGui.PushItemWidth(availWidth - padding * 2f);
        var spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, spacing with { Y = spacing.Y * 1.7f });
    }

    public static float CardContentWidth =>
        OpenCards.Count > 0
            ? OpenCards.Peek().Width - OpenCards.Peek().Padding * 2f
            : ImGui.GetContentRegionAvail().X;

    public static void EndCard(Vector4? accentOverride = null)
    {
        if (OpenCards.Count == 0) return;

        var (startScreen, availWidth, padding, dl) = OpenCards.Pop();
        var accent = accentOverride ?? MasterEventTheme.AccentColor;

        ImGui.PopStyleVar();
        ImGui.PopItemWidth();
        ImGui.PopTextWrapPos();

        ImGui.Unindent(padding);
        ImGuiHelpers.ScaledDummy(4f);

        var endY = ImGui.GetCursorScreenPos().Y;
        dl.ChannelsSetCurrent(0);
        var rounding = MasterEventTheme.RadiusCard * ImGuiHelpers.GlobalScale;
        var min = startScreen;
        var max = new Vector2(startScreen.X + availWidth, endY);
        var background = MasterEventTheme.ThemeButtonBg with { W = MasterEventTheme.CardAlpha() };
        dl.AddRectFilled(min, max, ImGui.GetColorU32(background), rounding);
        dl.AddRect(min, max, ImGui.GetColorU32(accent with { W = 0.45f }), rounding);
        dl.ChannelsMerge();

        ImGuiHelpers.ScaledDummy(6f);
    }

    public static void DrawCard(string title, FontAwesomeIcon icon, Vector4 accent, Action content)
    {
        BeginCard(title, icon, accent);
        content();
        EndCard(accent);
    }

    public static void DrawCenteredIcon(FontAwesomeIcon icon, float availWidth, float sizeScale = 1.6f)
    {
        var iconStr = icon.ToIconString();
        float targetSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            targetSize = ImGui.GetFontSize() * sizeScale;

        var pos = ImGui.GetCursorScreenPos();
        var color = ImGui.GetColorU32(MasterEventTheme.AccentColor);

        if (Plugin.LargeIconFont is { Available: true } largeIcon)
        {
            using (largeIcon.Push())
            {
                var size = ImGui.CalcTextSize(iconStr) * (targetSize / ImGui.GetFontSize());
                ImGui.Dummy(new Vector2(0, size.Y));
                ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), targetSize,
                    new Vector2(pos.X + (availWidth - size.X) / 2f, pos.Y), color, iconStr);
            }
            return;
        }

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var size = ImGui.CalcTextSize(iconStr) * sizeScale;
            ImGui.Dummy(new Vector2(0, size.Y));
            ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), targetSize,
                new Vector2(pos.X + (availWidth - size.X) / 2f, pos.Y), color, iconStr);
        }
    }

    public static void DrawTabHeader(FontAwesomeIcon icon, string title, string? subtitle = null, string? badge = null)
    {
        var availWidth = ImGui.GetContentRegionAvail().X;

        ImGuiHelpers.ScaledDummy(6f);

        DrawCenteredIcon(icon, availWidth);

        ImGuiHelpers.ScaledDummy(4f);

        var titleSize = ImGui.CalcTextSize(title);
        var spaceWidth = ImGui.CalcTextSize(" ").X;
        var badgeSize = badge is null ? Vector2.Zero : ImGui.CalcTextSize(badge) + new Vector2(spaceWidth, 0);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - titleSize.X - badgeSize.X) / 2f);
        ImGui.TextColored(MasterEventTheme.AccentColor, title);
        if (badge is not null)
        {
            ImGui.SameLine(0, spaceWidth);
            ImGui.TextColored(MasterEventTheme.TextDim, badge);
        }

        if (!string.IsNullOrEmpty(subtitle))
        {
            ImGuiHelpers.ScaledDummy(2f);
            var subtitleSize = ImGui.CalcTextSize(subtitle);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - subtitleSize.X) / 2f);
            ImGui.TextColored(MasterEventTheme.TextDim, subtitle);
        }

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
    }

    public static (float Top, float Bottom) GetContainerBounds()
    {
        var winPos = ImGui.GetWindowPos();
        var top = winPos.Y + ImGui.GetWindowContentRegionMin().Y - ImGui.GetStyle().WindowPadding.Y;
        return (top, winPos.Y + ImGui.GetWindowSize().Y);
    }

    public static void DrawVerticalSeparator(float trailingOffset)
        => DrawVerticalSeparator(trailingOffset, GetContainerBounds());

    public static void DrawVerticalSeparator(float trailingOffset, (float Top, float Bottom) bounds)
    {
        var drawList = ImGui.GetWindowDrawList();
        var x = ImGui.GetCursorScreenPos().X;
        var thickness = 1f * ImGuiHelpers.GlobalScale;

        var sepColor = MasterEventTheme.AccentColor with { W = 0.6f };

        drawList.PushClipRect(
            new Vector2(x - thickness - 1f, bounds.Top),
            new Vector2(x + thickness + 1f, bounds.Bottom),
            false);
        drawList.AddLine(
            new Vector2(x, bounds.Top),
            new Vector2(x, bounds.Bottom),
            ImGui.GetColorU32(sepColor),
            thickness);
        drawList.PopClipRect();

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + trailingOffset * ImGuiHelpers.GlobalScale);
    }
}
