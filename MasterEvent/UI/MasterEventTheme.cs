using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MasterEvent.UI;


public static class MasterEventTheme
{

    public static readonly Vector4 AccentColor = new(0xE6 / 255f, 0x45 / 255f, 0x45 / 255f, 1f);
    public static readonly Vector4 AccentHoverColor = new(0x52 / 255f, 0x29 / 255f, 0x29 / 255f, 1f);
    public static readonly Vector4 ThemeWindowBg = new(0.11f, 0.11f, 0.11f, 1f);
    public static readonly Vector4 ThemeChildBg = new(0f, 0f, 0f, 0f);
    public static readonly Vector4 ThemeBorder = new(0x52 / 255f, 0x29 / 255f, 0x29 / 255f, 1f);
    public static readonly Vector4 ThemeSeparator = new(0x52 / 255f, 0x29 / 255f, 0x29 / 255f, 0.60f);
    public static readonly Vector4 ThemeTitleBar = new(0x3D / 255f, 0x1F / 255f, 0x1F / 255f, 1f);
    public static readonly Vector4 ThemeFrameBg = new(0.18f, 0.15f, 0.15f, 1f);
    public static readonly Vector4 ThemeFrameBgHovered = new(0x52 / 255f, 0x29 / 255f, 0x29 / 255f, 0.7f);
    public static readonly Vector4 ThemeFrameBgActive = new(0x68 / 255f, 0x36 / 255f, 0x36 / 255f, 1f);
    public static readonly Vector4 ThemeButtonBg = new(0x3D / 255f, 0x1F / 255f, 0x1F / 255f, 1f);
    public static readonly Vector4 ThemeButtonHovered = new(0x52 / 255f, 0x29 / 255f, 0x29 / 255f, 1f);
    public static readonly Vector4 ThemeButtonActive = new(0x68 / 255f, 0x36 / 255f, 0x36 / 255f, 1f);
    public static readonly Vector4 ThemeHeaderBg = new(0x1C / 255f, 0x1C / 255f, 0x1C / 255f, 1f);
    public static readonly Vector4 ThemeHeaderHovered = new(0x52 / 255f, 0x29 / 255f, 0x29 / 255f, 0.7f);
    public static readonly Vector4 ThemeHeaderActive = new(0x68 / 255f, 0x36 / 255f, 0x36 / 255f, 1f);
    public static readonly Vector4 ThemeScrollbarBg = new(0.08f, 0.08f, 0.08f, 0.50f);
    public static readonly Vector4 ThemeScrollbarGrab = new(0x52 / 255f, 0x29 / 255f, 0x29 / 255f, 1f);
    public static readonly Vector4 ThemeScrollbarHover = new(0x68 / 255f, 0x36 / 255f, 0x36 / 255f, 1f);
    public static readonly Vector4 ThemeScrollbarActive = new(0x80 / 255f, 0x45 / 255f, 0x45 / 255f, 1f);
    public static readonly Vector4 ThemeTabNormal = new(0x1C / 255f, 0x1C / 255f, 0x1C / 255f, 0.90f);
    public static readonly Vector4 ThemeTabHovered = new(0x52 / 255f, 0x29 / 255f, 0x29 / 255f, 1f);
    public static readonly Vector4 ThemeTabActive = new(0x68 / 255f, 0x36 / 255f, 0x36 / 255f, 1f);
    public static readonly Vector4 AttitudeHostile = new(1.0f, 0.2f, 0.2f, 1f);
    public static readonly Vector4 AttitudeNeutral = new(1.0f, 0.75f, 0.2f, 1f);
    public static readonly Vector4 AttitudeFriendly = new(0.3f, 0.8f, 0.3f, 1f);
    public static readonly Vector4 MpBarColor = new(0.2f, 0.4f, 0.9f, 1f);
    public static readonly Vector4 ShieldOverlayColor = new(0.6f, 0.85f, 1f, 0.7f);
    public const int ThemeColorCount = 23;
    public const int ThemeStyleVarCount = 2;
    public static void PushTheme()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 4f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, ThemeWindowBg);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ThemeChildBg);
        ImGui.PushStyleColor(ImGuiCol.Border, ThemeBorder);
        ImGui.PushStyleColor(ImGuiCol.Separator, ThemeSeparator);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, ThemeTitleBar);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, ThemeTitleBar);
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, ThemeTitleBar);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, ThemeFrameBg);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ThemeFrameBgHovered);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ThemeFrameBgActive);
        ImGui.PushStyleColor(ImGuiCol.Button, ThemeButtonBg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ThemeButtonHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ThemeButtonActive);
        ImGui.PushStyleColor(ImGuiCol.Header, ThemeHeaderBg);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ThemeHeaderHovered);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, ThemeHeaderActive);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, ThemeScrollbarBg);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, ThemeScrollbarGrab);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, ThemeScrollbarHover);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, ThemeScrollbarActive);
        ImGui.PushStyleColor(ImGuiCol.Tab, ThemeTabNormal);
        ImGui.PushStyleColor(ImGuiCol.TabHovered, ThemeTabHovered);
        ImGui.PushStyleColor(ImGuiCol.TabActive, ThemeTabActive);
    }

    public static void PopTheme()
    {
        ImGui.PopStyleColor(ThemeColorCount);
        ImGui.PopStyleVar(ThemeStyleVarCount);
    }
}
