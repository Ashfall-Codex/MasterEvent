using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;

namespace MasterEvent.UI;

public sealed partial class GmWindow
{
    private void DrawGuideContent()
    {
        if (ImGui.BeginChild("##guide_scroll", Vector2.Zero))
        {
            var availWidth = ImGui.GetContentRegionAvail().X;
            var availHeight = ImGui.GetContentRegionAvail().Y;
            ImGuiHelpers.ScaledDummy(availHeight * 0.12f);
            var iconStr = FontAwesomeIcon.HatWizard.ToIconString();
            ImGui.PushFont(UiBuilder.IconFont);
            var iconSz = ImGui.CalcTextSize(iconStr);
            const float iconScale = 1.4f;
            var scaledSz = iconSz * iconScale;
            var pos = ImGui.GetCursorScreenPos();
            var iconX = pos.X + (availWidth - scaledSz.X) / 2f;
            ImGui.Dummy(new Vector2(0, scaledSz.Y));
            var dl = ImGui.GetWindowDrawList();
            dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * iconScale, new Vector2(iconX, pos.Y), ImGui.GetColorU32(MasterEventTheme.AccentColor), iconStr);
            ImGui.PopFont();

            ImGuiHelpers.ScaledDummy(12f);

            var title = Loc.Get("Guide.Landing.Title");
            var titleSz = ImGui.CalcTextSize(title);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - titleSz.X) / 2f);
            ImGui.TextColored(MasterEventTheme.TextStrong, title);

            ImGuiHelpers.ScaledDummy(6f);

            var desc = Loc.Get("Guide.Landing.Description");
            var descSz = ImGui.CalcTextSize(desc);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - descSz.X) / 2f);
            ImGui.TextColored(MasterEventTheme.MutedTextColor, desc);

            ImGuiHelpers.ScaledDummy(24f);

            var btnWidth = 180f * ImGuiHelpers.GlobalScale;
            var btnHeight = 32f * ImGuiHelpers.GlobalScale;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - btnWidth) / 2f);

            ImGui.PushStyleColor(ImGuiCol.Button, MasterEventTheme.AccentColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, MasterEventTheme.AccentColor with { W = 0.85f });
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, MasterEventTheme.AccentColor with { W = 0.7f });
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 16f * ImGuiHelpers.GlobalScale);

            if (ImGui.Button(Loc.Get("Guide.Landing.Start") + "##guide_start", new Vector2(btnWidth, btnHeight)))
            {
                if (SetupAssistantRef != null)
                {
                    SetupAssistantRef.IsOpen = true;
                    IsOpen = false;
                }
            }

            ImGui.PopStyleVar();
            ImGui.PopStyleColor(3);

            ImGuiHelpers.ScaledDummy(16f);

            var previewColor = new Vector4(0.4f, 0.4f, 0.4f, 1f);
            var gap = 6f * ImGuiHelpers.GlobalScale;
            var steps = new[]
            {
                (FontAwesomeIcon.FileAlt, Loc.Get("Guide.Landing.Preview1")),
                (FontAwesomeIcon.Scroll, Loc.Get("Guide.Landing.Preview2")),
                (FontAwesomeIcon.CheckCircle, Loc.Get("Guide.Landing.Preview3")),
            };

            float iconFixedW;
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                iconFixedW = ImGui.CalcTextSize(FontAwesomeIcon.FileAlt.ToIconString()).X;

            foreach (var (icon, text) in steps)
            {
                var textSz = ImGui.CalcTextSize(text);
                var totalW = iconFixedW + gap + textSz.X;
                var startX = (availWidth - totalW) / 2f;
                if (startX < 0) startX = 0;

                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + startX);
                var lineIconStr = icon.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(previewColor, lineIconStr);
                ImGui.SameLine(0, gap);
                ImGui.TextColored(previewColor, text);
                ImGuiHelpers.ScaledDummy(1f);
            }
        }
        ImGui.EndChild();
    }
}
