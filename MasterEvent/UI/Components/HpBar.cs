using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;

namespace MasterEvent.UI.Components;

public static class HpBar
{
    public static void Draw(int hp, Attitude attitude, float width, HpMode mode = HpMode.Points, int hpMax = 100, float height = 0, int shield = 0)
    {
        if (height <= 0)
            height = 14f * ImGuiHelpers.GlobalScale;

        var cursor = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        var barBg = new Vector4(0.15f, 0.15f, 0.15f, 1f);
        var fillRatio = mode == HpMode.Percentage
            ? hp / 100f
            : hpMax > 0 ? hp / (float)hpMax : 0f;
        var barColor = GetBarColor(fillRatio, attitude);

        var fullSize = new Vector2(width, height);
        drawList.AddRectFilled(cursor, cursor + fullSize, ImGui.ColorConvertFloat4ToU32(barBg), 3f);

        var fillWidth = width * Math.Clamp(fillRatio, 0f, 1f);
        if (fillWidth > 0)
        {
            drawList.AddRectFilled(cursor, cursor + new Vector2(fillWidth, height),
                ImGui.ColorConvertFloat4ToU32(barColor), 3f);
        }

        // Shield overlay: cyan segment after HP fill
        if (shield > 0)
        {
            var shieldRatio = mode == HpMode.Percentage
                ? shield / 100f
                : hpMax > 0 ? shield / (float)hpMax : 0f;
            var shieldWidth = width * Math.Clamp(shieldRatio, 0f, 1f - Math.Clamp(fillRatio, 0f, 1f));
            if (shieldWidth > 0)
            {
                var shieldStart = cursor + new Vector2(fillWidth, 0);
                drawList.AddRectFilled(shieldStart, shieldStart + new Vector2(shieldWidth, height),
                    ImGui.ColorConvertFloat4ToU32(MasterEventTheme.ShieldOverlayColor), 3f);
            }
        }

        var hpLabel = Loc.Get("Marker.Hp");
        var hpText = mode == HpMode.Percentage
            ? shield > 0 ? $"{hpLabel}: {hp}% (+{shield}%)" : $"{hpLabel}: {hp}%"
            : shield > 0 ? $"{hpLabel}: {hp} (+{shield}) / {hpMax}" : $"{hpLabel}: {hp} / {hpMax}";
        var textSize = ImGui.CalcTextSize(hpText);
        var textPos = cursor + new Vector2((width - textSize.X) * 0.5f, (height - textSize.Y) * 0.5f);
        drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)), hpText);

        ImGui.Dummy(fullSize);
    }

    public static void DrawMpBar(int mp, float width, HpMode mode = HpMode.Points, int mpMax = 100, float height = 0)
    {
        if (height <= 0)
            height = 12f * ImGuiHelpers.GlobalScale;

        var cursor = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        var barBg = new Vector4(0.15f, 0.15f, 0.15f, 1f);
        var fillRatio = mode == HpMode.Percentage
            ? mp / 100f
            : mpMax > 0 ? mp / (float)mpMax : 0f;

        var fullSize = new Vector2(width, height);
        drawList.AddRectFilled(cursor, cursor + fullSize, ImGui.ColorConvertFloat4ToU32(barBg), 3f);

        var fillWidth = width * Math.Clamp(fillRatio, 0f, 1f);
        if (fillWidth > 0)
        {
            drawList.AddRectFilled(cursor, cursor + new Vector2(fillWidth, height),
                ImGui.ColorConvertFloat4ToU32(MasterEventTheme.MpBarColor), 3f);
        }

        var mpLabel = Loc.Get("Marker.Mp");
        var mpText = mode == HpMode.Percentage ? $"{mpLabel}: {mp}%" : $"{mpLabel}: {mp} / {mpMax}";
        var textSize = ImGui.CalcTextSize(mpText);
        var textPos = cursor + new Vector2((width - textSize.X) * 0.5f, (height - textSize.Y) * 0.5f);
        drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)), mpText);

        ImGui.Dummy(fullSize);
    }

    private static Vector4 GetBarColor(float fillRatio, Attitude attitude)
    {
        var baseColor = attitude switch
        {
            Attitude.Hostile => MasterEventTheme.AttitudeHostile,
            Attitude.Neutral => MasterEventTheme.AttitudeNeutral,
            Attitude.Friendly => MasterEventTheme.AttitudeFriendly,
            _ => MasterEventTheme.AttitudeNeutral,
        };

        if (fillRatio <= 0.25f)
        {
            // Darken when low HP
            return new Vector4(baseColor.X * 0.6f, baseColor.Y * 0.6f, baseColor.Z * 0.6f, baseColor.W);
        }

        return baseColor;
    }
}
