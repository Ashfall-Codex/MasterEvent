using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

namespace MasterEvent.UI.Components;

// Éléments de mise en page repris à l'identique par plusieurs fenêtres.
public static class LayoutControls
{

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

    public static void DrawTabHeader(FontAwesomeIcon icon, string title, string? subtitle = null, string? badge = null)
    {
        var availWidth = ImGui.GetContentRegionAvail().X;
        const float iconScale = 1.6f;

        ImGuiHelpers.ScaledDummy(6f);

        var iconStr = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(iconStr) * iconScale;
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(0, iconSize.Y));
        ImGui.GetWindowDrawList().AddText(
            ImGui.GetFont(),
            ImGui.GetFontSize() * iconScale,
            new Vector2(pos.X + (availWidth - iconSize.X) / 2f, pos.Y),
            ImGui.GetColorU32(MasterEventTheme.AccentColor),
            iconStr);
        ImGui.PopFont();

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
