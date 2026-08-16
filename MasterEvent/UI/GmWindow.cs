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
    private const float SidebarButtonRounding = 6f;

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
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Gm.AnnouncePopupHint"));
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
            >= 1f => new Vector4(0.9f, 0.3f, 0.3f, 1f),
            >= 0.8f => new Vector4(0.95f, 0.7f, 0.2f, 1f),
            _ => new Vector4(0.6f, 0.6f, 0.6f, 1f),
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

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Spacing();

        DrawSidebarButton(FontAwesomeIcon.MapMarkerAlt, Tab.Markers, Loc.Get("Sidebar.Markers"));
        ImGui.Spacing();
        ImGui.Spacing();

        if (gmAccess)
        {
            DrawSidebarButton(FontAwesomeIcon.Users, Tab.Group, Loc.Get("Sidebar.Group"),
                session.IsGm || session.IsPromoted ? session.PendingMembers.Count : 0);
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
            DrawSidebarButton(FontAwesomeIcon.UserFriends, Tab.Npc, Loc.Get("Sidebar.Npc"));
            ImGui.Spacing();
            ImGui.Spacing();
        }
        DrawSidebarToggleButton(FontAwesomeIcon.StickyNote, Loc.Get("Notes.Title"),
            NotesWindowRef is { IsOpen: true },
            () => { if (NotesWindowRef is { } w) w.IsOpen = !w.IsOpen; });
        ImGui.Spacing();
        ImGui.Spacing();

        DrawSidebarButton(FontAwesomeIcon.Scroll, Tab.Profiles, Loc.Get("Player.Sheet"));
        ImGui.Spacing();
        ImGui.Spacing();

        // Bouton "Annonce MJ" : placé juste avant les réglages pour rester proche des actions MJ.
        // On stocke la demande d'ouverture pour appeler OpenPopup hors du child sidebar
        // (les popups ImGui doivent être déclenchés au même niveau que leur BeginPopupModal).
        if (gmAccess)
        {
            // Reprend exactement les couleurs du bouton actif d'onglet pour rester cohérent visuellement.
            DrawSidebarAction(
                FontAwesomeIcon.Bullhorn,
                Loc.Get("Gm.AnnounceTooltip"),
                () => requestOpenAnnouncePopup = true,
                MasterEventTheme.AccentColor);
            ImGui.Spacing();
            ImGui.Spacing();
        }

        DrawSidebarButton(FontAwesomeIcon.Cog, Tab.Settings, Loc.Get("Sidebar.Settings"));
    }

    private void DrawSidebarAction(FontAwesomeIcon icon, string tooltip, Action onClick, Vector4 accentColor)
    {
        var size = SidebarButtonSize * ImGuiHelpers.GlobalScale;
        var availW = ImGui.GetContentRegionAvail().X;
        var offset = Math.Max(0f, (availW - size) / 2f);

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);

        // Comportement : idle = fond discret comme un tab non-sélectionné, hover/active = rouge vif.
        // Permet au bouton d'action de s'intégrer sans visuellement dominer la sidebar.
        ImGui.PushStyleColor(ImGuiCol.Button, MasterEventTheme.ThemeButtonBg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, accentColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, MasterEventTheme.ThemeButtonActive);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, SidebarButtonRounding * ImGuiHelpers.GlobalScale);

        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            var iconStr = icon.ToIconString();
            if (ImGui.Button(iconStr + "##sidebar_action_" + (int)icon, new Vector2(size, size)))
                onClick();
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

    /// Pastille de notification peinte dans le coin d'un bouton de la barre latérale. Les
    /// onglets sont des icônes sans libellé : un compteur ne peut pas être accolé à un texte,
    /// il doit être dessiné par-dessus.
    private static void DrawSidebarBadge(int count)
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();

        var radius = 7f * ImGuiHelpers.GlobalScale;
        var center = new Vector2(max.X - radius * 0.7f, min.Y + radius * 0.7f);

        var dl = ImGui.GetWindowDrawList();
        dl.AddCircleFilled(center, radius, ImGui.GetColorU32(new Vector4(0.85f, 0.25f, 0.25f, 1f)));

        var label = count > 9 ? "9+" : count.ToString();
        var textSz = ImGui.CalcTextSize(label);
        dl.AddText(center - textSz / 2f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), label);
    }

    private void DrawSidebarToggleButton(FontAwesomeIcon icon, string tooltip, bool active, Action onClick)
    {
        var size = SidebarButtonSize * ImGuiHelpers.GlobalScale;
        var availW = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (availW - size) / 2f));

        var bgColor = active ? MasterEventTheme.AccentColor : MasterEventTheme.ThemeButtonBg;
        var hoverColor = active ? MasterEventTheme.AccentColor : MasterEventTheme.ThemeButtonHovered;

        ImGui.PushStyleColor(ImGuiCol.Button, bgColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hoverColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, MasterEventTheme.ThemeButtonActive);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, SidebarButtonRounding * ImGuiHelpers.GlobalScale);

        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(icon.ToIconString() + "##sidebar_toggle_notes", new Vector2(size, size)))
                onClick();
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

    private void DrawSidebarButton(FontAwesomeIcon icon, Tab tab, string tooltip, int badge = 0)
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

        // Après les Pop : le rect de l'item reste celui du bouton qu'on vient de dessiner.
        if (badge > 0)
            DrawSidebarBadge(badge);

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

    // Libère le jeton d'annulation du flux de liaison cloud : sans cela, décharger
    // le plugin pendant une liaison en cours laissait le CancellationTokenSource fuir.
    public void Dispose()
    {
        ResetCloudLinkFlow();
    }
}
