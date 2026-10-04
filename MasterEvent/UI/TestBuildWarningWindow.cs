using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using MasterEvent.Localization;

namespace MasterEvent.UI;

public sealed class TestBuildWarningWindow : MasterEventWindowBase
{
    private readonly Configuration configuration;
    private readonly string version;

    public TestBuildWarningWindow(Configuration configuration, IDalamudPluginInterface pluginInterface)
        : base(Loc.Get("TestBuild.WindowTitle") + "###MasterEventTestBuild",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoDocking
            | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.configuration = configuration;
        version = Constants.PluginVersionObj.ToString();

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 0),
            MaximumSize = new Vector2(460, 9999),
        };

        ShowCloseButton = false;
        RespectCloseHotkey = false;

        var isTestBuild = pluginInterface.IsDev || pluginInterface.IsTesting;
        if (isTestBuild && !string.Equals(configuration.LastTestBuildWarningVersion, version, StringComparison.Ordinal))
            IsOpen = true;
    }

    public override void OnOpen()
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    }

    protected override void DrawContents()
    {
        var warningIcon = FontAwesomeIcon.ExclamationTriangle.ToIconString();
        var header = Loc.Get("TestBuild.Header");

        float iconWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            iconWidth = ImGui.CalcTextSize(warningIcon).X;

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        CenterCursor(iconWidth + spacing + ImGui.CalcTextSize(header).X);

        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.DangerColor, warningIcon);
        ImGui.SameLine();
        ImGui.TextColored(MasterEventTheme.DangerColor, header);

        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        ImGui.TextWrapped(Loc.Get("TestBuild.Body"));

        ImGuiHelpers.ScaledDummy(6f);
        var signature = Loc.Get("TestBuild.Signature");
        var heart = FontAwesomeIcon.Heart.ToIconString();

        float heartWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            heartWidth = ImGui.CalcTextSize(heart).X;

        CenterCursor(ImGui.CalcTextSize(signature).X + spacing + heartWidth);
        ImGui.TextColored(MasterEventTheme.TextSecondary, signature);
        ImGui.SameLine();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.DangerColor, heart);

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        var buttonWidth = 200f * ImGuiHelpers.GlobalScale;
        CenterCursor(buttonWidth);
        if (ImGui.Button(Loc.Get("TestBuild.Acknowledge"), new Vector2(buttonWidth, 0)))
        {
            configuration.LastTestBuildWarningVersion = version;
            configuration.Save();
            IsOpen = false;
        }
    }

    // Place le curseur pour qu'un contenu de cette largeur tombe au centre de la fenêtre.
    private static void CenterCursor(float width)
        => ImGui.SetCursorPosX(ImGui.GetCursorPosX()
                               + Math.Max(0f, (ImGui.GetContentRegionAvail().X - width) / 2f));
}
