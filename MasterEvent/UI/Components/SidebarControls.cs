using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;

namespace MasterEvent.UI.Components;

public static class SidebarControls
{
    public const float ButtonRounding = 6f;

    public static bool DrawButton(
        FontAwesomeIcon icon,
        string id,
        bool active,
        string tooltip,
        float size,
        int badge = 0,
        bool enabled = true,
        bool outlined = false,
        Vector4? hoverOverride = null)
    {
        var scaled = size * ImGuiHelpers.GlobalScale;
        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (avail - scaled) / 2f));

        var bg = active ? MasterEventTheme.AccentColor : MasterEventTheme.ThemeButtonBg;
        var hover = hoverOverride ?? (active ? MasterEventTheme.AccentColor : MasterEventTheme.ThemeButtonHovered);

        ImGui.PushStyleColor(ImGuiCol.Button, bg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, MasterEventTheme.ThemeButtonActive);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, ButtonRounding * ImGuiHelpers.GlobalScale);

        if (!enabled) ImGui.BeginDisabled();
        bool clicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            clicked = ImGui.Button(icon.ToIconString() + id, new Vector2(scaled, scaled));
        if (!enabled) ImGui.EndDisabled();

        if (outlined)
        {
            ImGui.GetWindowDrawList().AddRect(
                ImGui.GetItemRectMin(),
                ImGui.GetItemRectMax(),
                ImGui.GetColorU32(MasterEventTheme.AccentColor with { W = 0.45f }),
                ButtonRounding * ImGuiHelpers.GlobalScale,
                ImDrawFlags.None,
                1f * ImGuiHelpers.GlobalScale);
        }

        ImGui.PopStyleVar();
        ImGui.PopStyleColor(3);

        // Après les Pop : le rect de l'item reste celui du bouton qu'on vient de dessiner.
        if (badge > 0)
            DrawBadge(badge);

        DrawTooltip(tooltip, enabled);
        ImGuiHelpers.ScaledDummy(4f);

        return clicked && enabled;
    }

    public static void DrawSeparator(float buttonSize)
    {
        ImGuiHelpers.ScaledDummy(2f);

        var width = buttonSize * 0.62f * ImGuiHelpers.GlobalScale;
        var cursor = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        var start = cursor with { X = cursor.X + (avail - width) / 2f };

        ImGui.GetWindowDrawList().AddLine(
            start,
            start with { X = start.X + width },
            ImGui.GetColorU32(MasterEventTheme.ThemeSeparator),
            1f * ImGuiHelpers.GlobalScale);

        ImGuiHelpers.ScaledDummy(6f);
    }

    private static void DrawBadge(int count)
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();

        var radius = 7f * ImGuiHelpers.GlobalScale;
        var center = new Vector2(max.X - radius * 0.7f, min.Y + radius * 0.7f);

        var dl = ImGui.GetWindowDrawList();
        dl.AddCircleFilled(center, radius, ImGui.GetColorU32(MasterEventTheme.DangerColor));

        var label = count > 9 ? "9+" : count.ToString();
        var textSz = ImGui.CalcTextSize(label);
        dl.AddText(center - textSz / 2f, ImGui.GetColorU32(MasterEventTheme.TextStrong), label);
    }

    // Un bouton grisé doit dire pourquoi il l'est, sinon il ressemble à une panne.
    private static void DrawTooltip(string tooltip, bool enabled)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(tooltip);
        if (!enabled)
            ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Sidebar.GmRequired"));
        ImGui.EndTooltip();
    }
}
