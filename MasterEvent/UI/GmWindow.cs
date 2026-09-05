using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using MasterEvent.Localization;
using MasterEvent.UI.Components;
using MasterEvent.Models;
using MasterEvent.Services;

namespace MasterEvent.UI;

public sealed partial class GmWindow : MasterEventWindowBase, IDisposable
{
    private readonly SessionManager session;
    private readonly Configuration configuration;
    private readonly Action? onConsentRevoked;
    private readonly Action? onDebugDisabled;
    private readonly Action? onEnableAlliance;
    private readonly Action? onDisableAlliance;
    public MasterEventWindowBase? PlayerWindowRef { get; set; }
    public MasterEventWindowBase? NotesWindowRef { get; set; }
    public MasterEventWindowBase? SetupAssistantRef { get; set; }

    private bool revokeConfirmPending;

    // Météo et temps
    private byte selectedWeatherId;
    private Dictionary<byte, string>? cachedWeatherList;
    private uint cachedTerritoryId;
    private int selectedHour = -1; // -1 = pas encore initialisé

    private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private bool? relayOnline;
    private DateTime lastHealthCheck = DateTime.MinValue;
    private bool healthCheckInProgress;
    private const double HealthCheckIntervalSeconds = 30;

    private enum Tab { Markers, Group, Models, Profiles, Turns, Weather, Npc, Settings }
    private Tab activeTab = Tab.Markers;

    private const float SidebarWidth = 48f;
    private const float SidebarButtonSize = 34f;

    private int activeSettingsTab;
    private const float SettingsSidebarWidth = 130f;
    private const float SettingsSidebarAnimSpeed = 18f;

    private static readonly string[] SettingsLabelKeys = ["Sidebar.General", "Sidebar.Cloud", "Sidebar.Guide", "Sidebar.Privacy", "Sidebar.Advanced", "Sidebar.About"];
    private static readonly FontAwesomeIcon[] SettingsIcons = [FontAwesomeIcon.Cog, FontAwesomeIcon.Cloud, FontAwesomeIcon.HatWizard, FontAwesomeIcon.ShieldAlt, FontAwesomeIcon.Wrench, FontAwesomeIcon.InfoCircle];
    private static readonly string[] SettingsDescriptionKeys = ["General.Subtitle", "Cloud.Subtitle", "Guide.Subtitle", "Privacy.Subtitle", "Advanced.Subtitle", "About.Description"];
    private static readonly int PrivacySettingsTab = Array.IndexOf(SettingsLabelKeys, "Sidebar.Privacy");
    private static readonly int CloudSettingsTab = Array.IndexOf(SettingsLabelKeys, "Sidebar.Cloud");

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
    private string? modelsImportedName;
    private string profileImportCode = string.Empty;
    private bool profileImportInProgress;
    private string? profileImportedName;
    private PlayerSheet? editingProfile;
    private bool editingDirty;

    private readonly Dictionary<int, (Vector2 Min, Vector2 Max)> settingsSidebarRects = new();
    private Vector2 settingsSidebarIndicatorPos;
    private Vector2 settingsSidebarIndicatorSize;
    private bool settingsSidebarIndicatorInit;
    private Vector2 settingsSidebarWindowPos;
    private string settingsSearch = string.Empty;
    private (float Top, float Bottom) rootBounds;

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
        rootBounds = LayoutControls.GetContainerBounds();

        var sidebarW = SidebarWidth * ImGuiHelpers.GlobalScale;

        if (ImGui.BeginChild("##sidebar", new Vector2(sidebarW, 0), false, ImGuiWindowFlags.NoScrollbar))
        {
            DrawSidebar();
        }
        ImGui.EndChild();

        ImGui.SameLine();

        // --- Right content area ---
        LayoutControls.DrawVerticalSeparator(8f);

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
                case Tab.Npc:
                    DrawNpcContent();
                    break;
                case Tab.Settings:
                    DrawSettingsContent();
                    break;
            }
        }
        ImGui.EndChild();

        // Popup modal déclaré au niveau root de la fenêtre (hors des childs).
        // Le flag permet de déclencher OpenPopup depuis la sidebar au bon moment du frame.
        if (requestOpenAnnouncePopup)
        {
            ImGui.OpenPopup("##gm_announce_popup");
            requestOpenAnnouncePopup = false;
        }
        DrawGmAnnouncePopup();
    }

    private bool requestOpenAnnouncePopup;
    private string announceDraft = string.Empty;
    private const int AnnounceMaxChars = 180;

    private void DrawGmAnnouncePopup()
    {
        var open = true;
        if (!ImGui.BeginPopupModal("##gm_announce_popup", ref open, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var rubyColor = new Vector4(0.78f, 0.15f, 0.22f, 1f);
        ImGui.TextColored(rubyColor, Loc.Get("Gm.AnnouncePopupTitle"));
        ImGui.Separator();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 400f * ImGuiHelpers.GlobalScale);
        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Gm.AnnouncePopupHint"));
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        ImGui.InputTextMultiline(
            "##announce_text",
            ref announceDraft,
            AnnounceMaxChars,
            new Vector2(400f * ImGuiHelpers.GlobalScale, 80f * ImGuiHelpers.GlobalScale));

        // Compteur de caractères : vert sous 80%, orange entre 80% et 100%, rouge à saturation.
        var used = announceDraft.Length;
        var ratio = (float)used / AnnounceMaxChars;
        var counterColor = ratio switch
        {
            >= 1f => MasterEventTheme.DangerColor,
            >= 0.8f => new Vector4(0.95f, 0.7f, 0.2f, 1f),
            _ => MasterEventTheme.MutedTextColor,
        };
        ImGui.TextColored(counterColor, $"{used} / {AnnounceMaxChars}");

        ImGui.Spacing();
        var canSend = !string.IsNullOrWhiteSpace(announceDraft);
        if (!canSend) ImGui.BeginDisabled();
        ImGui.PushStyleColor(ImGuiCol.Button, rubyColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, rubyColor with { W = 0.85f });
        if (ImGui.Button(Loc.Get("Gm.AnnounceSend") + "##send_announce"))
        {
            session.ShowGmAnnouncement(announceDraft);
            announceDraft = string.Empty;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleColor(2);
        if (!canSend) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button(Loc.Get("Gm.Cancel") + "##cancel_announce"))
        {
            announceDraft = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }


    private bool HasGmAccess() => session.IsGm || session.IsGmAsPlayer;

    private void DrawSidebar()
    {
        var gmAccess = HasGmAccess();
        if (!gmAccess && activeTab is Tab.Group or Tab.Models or Tab.Turns or Tab.Weather or Tab.Npc)
            activeTab = Tab.Markers;

        ImGuiHelpers.ScaledDummy(6f);

        // ── Navigation : change le panneau de droite ──
        DrawTabButton(FontAwesomeIcon.MapMarkerAlt, Tab.Markers, Loc.Get("Sidebar.Markers"));
        DrawTabButton(FontAwesomeIcon.Users, Tab.Group, Loc.Get("Sidebar.Group"),
            gmAccess && (session.IsGm || session.IsPromoted) ? session.PendingMembers.Count : 0,
            enabled: gmAccess);
        DrawTabButton(FontAwesomeIcon.FileAlt, Tab.Models, Loc.Get("Sidebar.Models"), enabled: gmAccess);
        DrawTabButton(FontAwesomeIcon.ListOl, Tab.Turns, Loc.Get("Sidebar.Turns"), enabled: gmAccess);
        DrawTabButton(FontAwesomeIcon.CloudSunRain, Tab.Weather, Loc.Get("Sidebar.Weather"), enabled: gmAccess);
        DrawTabButton(FontAwesomeIcon.UserFriends, Tab.Npc, Loc.Get("Sidebar.Npc"), enabled: gmAccess);
        DrawTabButton(FontAwesomeIcon.Scroll, Tab.Profiles, Loc.Get("Player.Sheet"));

        SidebarControls.DrawSeparator(SidebarButtonSize);

        // ── Fenêtre : s'ouvre ailleurs à l'écran, d'où le contour ──
        var notesOpen = NotesWindowRef is { IsOpen: true };
        if (SidebarControls.DrawButton(FontAwesomeIcon.StickyNote, "##sidebar_notes", notesOpen,
                Loc.Get("Notes.Title"), SidebarButtonSize, outlined: true)
            && NotesWindowRef is { } notes)
        {
            notes.IsOpen = !notes.IsOpen;
        }

        SidebarControls.DrawSeparator(SidebarButtonSize);

        // ── Action immédiate, puis réglages ──
        // Le popup doit être ouvert hors du child de la barre : on note la demande ici.
        if (SidebarControls.DrawButton(FontAwesomeIcon.Bullhorn, "##sidebar_announce", false,
                Loc.Get("Gm.AnnounceTooltip"), SidebarButtonSize,
                enabled: gmAccess, outlined: true, hoverOverride: MasterEventTheme.AccentColor))
        {
            requestOpenAnnouncePopup = true;
        }

        DrawTabButton(FontAwesomeIcon.Cog, Tab.Settings, Loc.Get("Sidebar.Settings"));
    }

    private void DrawTabButton(FontAwesomeIcon icon, Tab tab, string tooltip, int badge = 0, bool enabled = true)
    {
        if (SidebarControls.DrawButton(icon, "##tab_" + (int)tab, activeTab == tab, tooltip,
                SidebarButtonSize, badge, enabled))
        {
            activeTab = tab;
        }
    }

    private unsafe void OpenFieldMarkerAgent()
    {
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentFieldMarker.Instance();
        if (agent != null)
            agent->Show();
    }

    // Libère le jeton d'annulation du flux de liaison cloud : sans cela, décharger
    // le plugin pendant une liaison en cours laissait le CancellationTokenSource fuir.
    public void Dispose()
    {
        ResetCloudLinkFlow();
    }
}
