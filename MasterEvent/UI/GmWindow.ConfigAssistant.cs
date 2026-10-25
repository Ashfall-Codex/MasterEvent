using System;
using Dalamud.Bindings.ImGui;
using MasterEvent.UI.Components;
using MasterEvent.Localization;

namespace MasterEvent.UI;

public sealed partial class GmWindow
{
    private void DrawRelaunchAssistant(float availWidth)
    {
        var label = Loc.Get("About.RelaunchAssistant");
        var width = Buttons.TextWidth(label);

        var start = BeginSettingAnchor();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (availWidth - width) / 2f));

        var available = SetupAssistantRef != null;
        if (!available) ImGui.BeginDisabled();
        if (Buttons.Tinted(label + "##relaunch_assistant", width) && SetupAssistantRef != null)
        {
            SetupAssistantRef.IsOpen = true;
            IsOpen = false;
        }
        if (!available) ImGui.EndDisabled();

        EndSettingAnchor("About.RelaunchAssistant", start);
    }
}
