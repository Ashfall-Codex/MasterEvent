using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;

namespace MasterEvent.UI;

public sealed partial class GmWindow : MasterEventWindowBase
{
    private readonly SessionManager session;
    private readonly Configuration configuration;
    private readonly Action? onConsentRevoked;
    private readonly Action? onDebugDisabled;
    private readonly Action? onEnableAlliance;
    private readonly Action? onDisableAlliance;
    public MasterEventWindowBase? PlayerWindowRef { get; set; }

    private bool revokeConfirmPending;

    // Météo et temps
    private byte selectedWeatherId;
    private Dictionary<byte, string>? cachedWeatherList;
    private ushort cachedTerritoryId;
    private int selectedHour = -1; // -1 = pas encore initialisé

    private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private bool? relayOnline;
    private DateTime lastHealthCheck = DateTime.MinValue;
    private bool healthCheckInProgress;
    private const double HealthCheckIntervalSeconds = 30;

    private enum Tab { Markers, Group, Models, Profiles, Turns, Weather, Settings }
    private Tab activeTab = Tab.Markers;

    private const float SidebarWidth = 48f;
    private const float SidebarButtonSize = 34f;
    private const float SidebarButtonRounding = 6f;

    private int activeSettingsTab;
    private const float SettingsSidebarWidth = 130f;
    private const float SettingsSidebarAnimSpeed = 18f;

    private static readonly string[] SettingsLabelKeys = ["Sidebar.General", "Sidebar.Privacy", "Sidebar.Advanced", "Sidebar.About"];
    private static readonly FontAwesomeIcon[] SettingsIcons = [FontAwesomeIcon.Cog, FontAwesomeIcon.ShieldAlt, FontAwesomeIcon.Wrench, FontAwesomeIcon.InfoCircle];
    private static readonly string[] SettingsDescriptionKeys = ["General.Subtitle", "Privacy.Subtitle", "Advanced.Subtitle", "About.Description"];

    private string newTemplateName = string.Empty;
    private EventTemplate? editingTemplate;
    private string? editingTemplateName;

    private bool exportPermanent;
    private string? lastExportCode;
    private bool exportInProgress;

    private string newProfileName = string.Empty;
    private string selectedTemplateName = string.Empty;
    private string importCode = string.Empty;
    private bool importInProgress;
    private PlayerSheet? editingProfile;
    private bool editingDirty;

    private readonly Dictionary<int, (Vector2 Min, Vector2 Max)> settingsSidebarRects = new();
    private Vector2 settingsSidebarIndicatorPos;
    private Vector2 settingsSidebarIndicatorSize;
    private bool settingsSidebarIndicatorInit;
    private Vector2 settingsSidebarWindowPos;

    public GmWindow(SessionManager session, Configuration configuration, Action? onConsentRevoked = null, Action? onDebugDisabled = null,
        Action? onEnableAlliance = null, Action? onDisableAlliance = null)
        : base("MasterEvent###MasterEventGM", ImGuiWindowFlags.NoScrollbar)
    {
        this.session = session;
        this.configuration = configuration;
        this.onConsentRevoked = onConsentRevoked;
        this.onDebugDisabled = onDebugDisabled;
        this.onEnableAlliance = onEnableAlliance;
        this.onDisableAlliance = onDisableAlliance;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 420),
            MaximumSize = new Vector2(560, 1200),
        };
    }

    protected override void DrawContents()
    {
        var sidebarW = SidebarWidth * ImGuiHelpers.GlobalScale;

        if (ImGui.BeginChild("##sidebar", new Vector2(sidebarW, 0), false, ImGuiWindowFlags.NoScrollbar))
        {
            DrawSidebar();
        }
        ImGui.EndChild();

        ImGui.SameLine();

        var drawList = ImGui.GetWindowDrawList();
        var sepPos = ImGui.GetCursorScreenPos();
        var sepHeight = ImGui.GetContentRegionAvail().Y;
        var sepColor = new Vector4(
            MasterEventTheme.AccentColor.X,
            MasterEventTheme.AccentColor.Y,
            MasterEventTheme.AccentColor.Z, 0.6f);
        drawList.AddLine(
            sepPos,
            new Vector2(sepPos.X, sepPos.Y + sepHeight),
            ImGui.GetColorU32(sepColor),
            1f * ImGuiHelpers.GlobalScale);

        // --- Right content area ---
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f * ImGuiHelpers.GlobalScale);

        if (ImGui.BeginChild("##content", Vector2.Zero, false, ImGuiWindowFlags.NoScrollbar))
        {
            switch (activeTab)
            {
                case Tab.Markers:
                    DrawMarkersContent();
                    break;
                case Tab.Group:
                    DrawGroupContent();
                    break;
                case Tab.Models:
                    DrawModelsContent();
                    break;
                case Tab.Profiles:
                    DrawProfilesContent();
                    break;
                case Tab.Turns:
                    DrawTurnsContent();
                    break;
                case Tab.Weather:
                    DrawWeatherContent();
                    break;
                case Tab.Settings:
                    DrawSettingsContent();
                    break;
            }
        }
        ImGui.EndChild();
    }


    private bool HasGmAccess() => session.IsGm || session.IsGmAsPlayer;

    private void DrawSidebar()
    {
        var gmAccess = HasGmAccess();
        if (!gmAccess && activeTab is Tab.Group or Tab.Models or Tab.Turns or Tab.Weather)
            activeTab = Tab.Markers;

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Spacing();

        DrawSidebarButton(FontAwesomeIcon.MapMarkerAlt, Tab.Markers, Loc.Get("Sidebar.Markers"));
        ImGui.Spacing();
        ImGui.Spacing();

        if (gmAccess)
        {
            DrawSidebarButton(FontAwesomeIcon.Users, Tab.Group, Loc.Get("Sidebar.Group"));
            ImGui.Spacing();
            ImGui.Spacing();
            DrawSidebarButton(FontAwesomeIcon.FileAlt, Tab.Models, Loc.Get("Sidebar.Models"));
            ImGui.Spacing();
            ImGui.Spacing();
            DrawSidebarButton(FontAwesomeIcon.ListOl, Tab.Turns, Loc.Get("Sidebar.Turns"));
            ImGui.Spacing();
            ImGui.Spacing();
            DrawSidebarButton(FontAwesomeIcon.CloudSunRain, Tab.Weather, Loc.Get("Sidebar.Weather"));
            ImGui.Spacing();
            ImGui.Spacing();
        }

        DrawSidebarButton(FontAwesomeIcon.Scroll, Tab.Profiles, Loc.Get("Player.Sheet"));
        ImGui.Spacing();
        ImGui.Spacing();

        DrawSidebarButton(FontAwesomeIcon.Cog, Tab.Settings, Loc.Get("Sidebar.Settings"));
    }

    private void DrawSidebarButton(FontAwesomeIcon icon, Tab tab, string tooltip)
    {
        var isActive = activeTab == tab;
        var size = SidebarButtonSize * ImGuiHelpers.GlobalScale;
        var availW = ImGui.GetContentRegionAvail().X;
        var offset = Math.Max(0f, (availW - size) / 2f);

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);

        var bgColor = isActive ? MasterEventTheme.AccentColor : MasterEventTheme.ThemeButtonBg;
        var hoverColor = isActive ? MasterEventTheme.AccentColor : MasterEventTheme.ThemeButtonHovered;

        ImGui.PushStyleColor(ImGuiCol.Button, bgColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hoverColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, MasterEventTheme.ThemeButtonActive);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, SidebarButtonRounding * ImGuiHelpers.GlobalScale);

        var iconStr = icon.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(iconStr + "##tab_" + (int)tab, new Vector2(size, size)))
                activeTab = tab;
        }

        ImGui.PopStyleVar();
        ImGui.PopStyleColor(3);

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(tooltip);
            ImGui.EndTooltip();
        }
    }

    private unsafe void OpenFieldMarkerAgent()
    {
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentFieldMarker.Instance();
        if (agent != null)
            agent->Show();
    }
}
