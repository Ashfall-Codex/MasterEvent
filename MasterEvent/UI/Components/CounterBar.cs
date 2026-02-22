using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using MasterEvent.Models;

namespace MasterEvent.UI.Components;

public static class CounterBar
{
    public static void Draw(CustomCounter counter, float width, float height = 0)
    {
        if (height <= 0)
            height = 12f * ImGuiHelpers.GlobalScale;

        var cursor = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        var barBg = new Vector4(0.15f, 0.15f, 0.15f, 1f);
        var fillRatio = counter.Max > 0 ? counter.Value / (float)counter.Max : 0f;
        var barColor = new Vector4(counter.ColorR, counter.ColorG, counter.ColorB, 1f);

        var fullSize = new Vector2(width, height);
        drawList.AddRectFilled(cursor, cursor + fullSize, ImGui.ColorConvertFloat4ToU32(barBg), 3f);

        var fillWidth = width * Math.Clamp(fillRatio, 0f, 1f);
        if (fillWidth > 0)
        {
            drawList.AddRectFilled(cursor, cursor + new Vector2(fillWidth, height),
                ImGui.ColorConvertFloat4ToU32(barColor), 3f);
        }

        var text = $"{counter.Name}: {counter.Value} / {counter.Max}";
        var textSize = ImGui.CalcTextSize(text);
        var textPos = cursor + new Vector2((width - textSize.X) * 0.5f, (height - textSize.Y) * 0.5f);
        drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)), text);

        ImGui.Dummy(fullSize);
    }
}
