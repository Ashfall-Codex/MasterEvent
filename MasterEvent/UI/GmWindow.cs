using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

public sealed class GmWindow : MasterEventWindowBase
{
    private readonly SessionManager session;
    private readonly Configuration configuration;
    private readonly Action? onConsentRevoked;
    private readonly Action? onDebugDisabled;

    private bool revokeConfirmPending;

    private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private bool? relayOnline;
    private DateTime lastHealthCheck = DateTime.MinValue;
    private bool healthCheckInProgress;
    private const double HealthCheckIntervalSeconds = 30;

    private enum Tab { Markers, Group, Models, Settings }
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

    private readonly Dictionary<int, (Vector2 Min, Vector2 Max)> settingsSidebarRects = new();
    private Vector2 settingsSidebarIndicatorPos;
    private Vector2 settingsSidebarIndicatorSize;
    private bool settingsSidebarIndicatorInit;
    private Vector2 settingsSidebarWindowPos;

    public GmWindow(SessionManager session, Configuration configuration, Action? onConsentRevoked = null, Action? onDebugDisabled = null)
        : base("MasterEvent###MasterEventGM", ImGuiWindowFlags.NoScrollbar)
    {
        this.session = session;
        this.configuration = configuration;
        this.onConsentRevoked = onConsentRevoked;
        this.onDebugDisabled = onDebugDisabled;

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
                case Tab.Settings:
                    DrawSettingsContent();
                    break;
            }
        }
        ImGui.EndChild();
    }


    private void DrawSidebar()
    {
        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Spacing();

        DrawSidebarButton(FontAwesomeIcon.MapMarkerAlt, Tab.Markers, Loc.Get("Sidebar.Markers"));
        ImGui.Spacing();
        ImGui.Spacing();
        DrawSidebarButton(FontAwesomeIcon.Users, Tab.Group, Loc.Get("Sidebar.Group"));
        ImGui.Spacing();
        ImGui.Spacing();
        DrawSidebarButton(FontAwesomeIcon.FileAlt, Tab.Models, Loc.Get("Sidebar.Models"));
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


    private void DrawMarkersContent()
    {
        if (!session.CanEdit)
        {
            var avail = ImGui.GetContentRegionAvail();
            var text = Loc.Get("Gm.PlayerViewLocked");
            var textSz = ImGui.CalcTextSize(text);
            ImGui.SetCursorPos(new Vector2(
                ImGui.GetCursorPosX() + (avail.X - textSz.X) / 2f,
                ImGui.GetCursorPosY() + (avail.Y - textSz.Y) / 2f));
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), text);
            return;
        }

        DrawHeader();
        ImGui.Separator();

        var hasAnyMarker = false;
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            if (session.CurrentMarkers.Markers[i].HasData)
            {
                hasAnyMarker = true;
                break;
            }
        }

        if (hasAnyMarker)
        {
            if (ImGui.BeginChild("##markers_scroll", new Vector2(0, -30f * ImGuiHelpers.GlobalScale), false))
            {
                var first = true;
                for (var i = 0; i < Constants.WaymarkCount; i++)
                {
                    var waymarkId = (WaymarkId)i;
                    var marker = session.CurrentMarkers[waymarkId];

                    if (!marker.HasData)
                        continue;

                    if (!first)
                        ImGui.Spacing();

                    first = false;
                    MarkerCard.DrawEdit(waymarkId, marker,
                        onPlace: () =>
                        {
                            Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.PlaceWaymark"), waymarkId.ToLabel()));
                        },
                        onClear: () => session.ClearMarker(waymarkId),
                        onMove: () => session.MoveMarker(waymarkId),
                        onRoll: () => session.RollDice(waymarkId),
                        hpMode: session.HpMode,
                        showShield: session.ShowShield,
                        showMpBar: session.ShowMpBar,
                        mpMode: session.MpMode);
                }
            }
            ImGui.EndChild();

            DrawAddMarkerSmall();
        }
        else
        {
            DrawAddMarkerCentered();
        }
    }

    private unsafe void OpenFieldMarkerAgent()
    {
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentFieldMarker.Instance();
        if (agent != null)
            agent->Show();
    }

    private void DrawAddMarkerSmall()
    {
        var iconStr = FontAwesomeIcon.Plus.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(iconStr + "##add_small"))
                OpenFieldMarkerAgent();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Gm.AddMarker"));
            ImGui.EndTooltip();
        }
        ImGui.Separator();
    }

    private void DrawAddMarkerCentered()
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Gm.NoMarkers"));

        var avail = ImGui.GetContentRegionAvail();
        var btnLabel = "+ " + Loc.Get("Gm.AddMarker");
        var btnSize = ImGui.CalcTextSize(btnLabel) + ImGui.GetStyle().FramePadding * 2;

        ImGui.SetCursorPos(new Vector2(
            ImGui.GetCursorPosX() + (avail.X - btnSize.X) / 2f,
            ImGui.GetCursorPosY() + (avail.Y - btnSize.Y) / 2f));

        if (ImGui.Button(btnLabel + "##add_center"))
            OpenFieldMarkerAgent();
    }

    private void DrawHeader()
    {
        var totalWidth = ImGui.GetContentRegionAvail().X;

        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Gm.Title"));
        ImGui.SameLine();

        var statusColor = session.IsConnected
            ? new Vector4(0.2f, 1f, 0.2f, 1f)
            : new Vector4(0.5f, 0.5f, 0.5f, 1f);
        var statusText = session.IsConnected
            ? string.Format(Loc.Get("Gm.Connected"), session.ConnectedPlayerCount)
            : Loc.Get("Gm.Local");
        ImGui.TextColored(statusColor, statusText);

        if (session.ActiveTemplate != null)
        {
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.AccentColor, $"· {session.ActiveTemplate.Name}");
        }

        var buttonWidth = 50f * ImGuiHelpers.GlobalScale;
        var buttonPos = totalWidth - buttonWidth;
        if (buttonPos > 0)
            ImGui.SameLine(buttonPos);
        if (ImGui.Button(Loc.Get("Gm.Update")))
        {
            session.SyncWaymarks();
            session.BroadcastUpdate();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Gm.UpdateTooltip"));
            ImGui.EndTooltip();
        }
    }


    private void DrawGroupContent()
    {
        var availWidth = ImGui.GetContentRegionAvail().X;

        ImGuiHelpers.ScaledDummy(6f);

        var iconStr = FontAwesomeIcon.Users.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSz = ImGui.CalcTextSize(iconStr);
        const float scale = 1.6f;
        var scaledSz = iconSz * scale;
        var pos = ImGui.GetCursorScreenPos();
        var iconX = pos.X + (availWidth - scaledSz.X) / 2f;
        ImGui.Dummy(new Vector2(0, scaledSz.Y));
        var dl = ImGui.GetWindowDrawList();
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, new Vector2(iconX, pos.Y), ImGui.GetColorU32(MasterEventTheme.AccentColor), iconStr);
        ImGui.PopFont();

        ImGuiHelpers.ScaledDummy(4f);

        var titleSz = ImGui.CalcTextSize(Loc.Get("Group.Title"));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - titleSz.X) / 2f);
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Group.Title"));

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        if (ImGui.BeginChild("##group_scroll", Vector2.Zero, false))
        {
            // GM section
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Group.Gm"));
            ImGui.Spacing();

            var hasGm = false;
            foreach (var player in session.PartyMembers)
            {
                if (!player.IsGm) continue;
                hasGm = true;
                DrawGroupMember(player, true);
            }

            if (!hasGm)
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "—");

            ImGuiHelpers.ScaledDummy(4f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);

            // Players section
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Group.Players"));
            ImGui.Spacing();

            var hasPlayers = false;
            foreach (var player in session.PartyMembers)
            {
                if (player.IsGm) continue;
                hasPlayers = true;
                DrawGroupMember(player, false);
                ImGui.Spacing();
            }

            if (!hasPlayers)
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Group.NoPlayers"));
        }
        ImGui.EndChild();
    }

    private void DrawGroupMember(PlayerData player, bool isGmSection)
    {
        var availWidth = ImGui.GetContentRegionAvail().X;

        // Crown icon for GM
        if (isGmSection)
        {
            var crownStr = FontAwesomeIcon.Crown.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                ImGui.TextColored(MasterEventTheme.AccentColor, crownStr);
            }
            ImGui.SameLine();
        }

        // Player name
        ImGui.TextUnformatted(player.Name);
        ImGui.SameLine();

        // Co-GM badge
        if (!isGmSection && player.CanEdit)
        {
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Group.CoGm"));
            ImGui.SameLine();
        }

        // Connection indicator
        var connColor = player.IsConnected
            ? new Vector4(0.2f, 1f, 0.2f, 1f)
            : new Vector4(0.5f, 0.5f, 0.5f, 1f);
        var connTooltip = player.IsConnected
            ? Loc.Get("Group.Connected")
            : Loc.Get("Group.Disconnected");
        ImGui.TextColored(connColor, "●");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(connTooltip);
            ImGui.EndTooltip();
        }

        // Promote/demote button (only real GM can promote non-GM players)
        if (!isGmSection && session.IsGm)
        {
            ImGui.SameLine();
            var promoteIcon = player.CanEdit
                ? FontAwesomeIcon.UserMinus.ToIconString()
                : FontAwesomeIcon.UserPlus.ToIconString();
            var promoteTooltip = player.CanEdit
                ? Loc.Get("Group.Demote")
                : Loc.Get("Group.Promote");
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                if (ImGui.Button(promoteIcon + "##promote_" + player.Hash))
                    session.PromotePlayer(player.Hash, !player.CanEdit);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(promoteTooltip);
                ImGui.EndTooltip();
            }
        }

        // HP controls (only for non-GM players)
        if (!isGmSection)
        {
            if (session.CanEdit)
            {
                // Editable HP
                ImGui.TextUnformatted(Loc.Get("Group.Hp"));
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                var hp = player.Hp;
                if (ImGui.InputInt("##hp_" + player.Hash, ref hp))
                {
                    if (hp < 0) hp = 0;
                    if (hp > 100) hp = 100;
                    session.SetPlayerHp(player.Hash, hp);
                }

                HpBar.Draw(player.Hp, Attitude.Neutral, availWidth,
                    session.HpMode);
            }
            else
            {
                // Read-only HP bar
                HpBar.Draw(player.Hp, Attitude.Neutral, availWidth,
                    session.HpMode);
            }
        }
    }


    private void DrawModelsContent()
    {
        var availWidth = ImGui.GetContentRegionAvail().X;

        ImGuiHelpers.ScaledDummy(6f);

        // Header icon
        var iconStr = FontAwesomeIcon.FileAlt.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSz = ImGui.CalcTextSize(iconStr);
        const float scale = 1.6f;
        var scaledSz = iconSz * scale;
        var pos = ImGui.GetCursorScreenPos();
        var iconX = pos.X + (availWidth - scaledSz.X) / 2f;
        ImGui.Dummy(new Vector2(0, scaledSz.Y));
        var dl = ImGui.GetWindowDrawList();
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, new Vector2(iconX, pos.Y), ImGui.GetColorU32(MasterEventTheme.AccentColor), iconStr);
        ImGui.PopFont();

        ImGuiHelpers.ScaledDummy(4f);

        var titleSz = ImGui.CalcTextSize(Loc.Get("Sidebar.Models"));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - titleSz.X) / 2f);
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Sidebar.Models"));

        ImGuiHelpers.ScaledDummy(2f);

        var descColor = new Vector4(0.5f, 0.5f, 0.5f, 1f);
        var descSz = ImGui.CalcTextSize(Loc.Get("Models.Subtitle"));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - descSz.X) / 2f);
        ImGui.TextColored(descColor, Loc.Get("Models.Subtitle"));

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        if (ImGui.BeginChild("##models_scroll", Vector2.Zero, false))
        {
            // Active template
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Active"));
            ImGui.SameLine();
            if (session.ActiveTemplate != null)
            {
                ImGui.TextUnformatted(session.ActiveTemplate.Name);
                ImGui.SameLine();
                if (ImGui.Button(Loc.Get("Models.Clear") + "##deactivate"))
                {
                    session.ClearActiveTemplate();
                    configuration.ActiveTemplateName = string.Empty;
                    configuration.Save();
                }
                ImGui.SameLine();
                if (ImGui.Button(Loc.Get("Models.Share") + "##share"))
                {
                    session.BroadcastTemplate();
                    session.BroadcastUpdate();
                }
            }
            else
            {
                ImGui.TextColored(descColor, "—");
            }

            ImGuiHelpers.ScaledDummy(4f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);

            // New template creation
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.CreateTitle"));
            ImGui.SetNextItemWidth(availWidth - 80f * ImGuiHelpers.GlobalScale);
            ImGui.InputText("##new_template_name", ref newTemplateName, 64);
            ImGui.SameLine();
            if (ImGui.Button(Loc.Get("Models.Create") + "##create_template"))
            {
                if (!string.IsNullOrWhiteSpace(newTemplateName))
                {
                    var template = EventTemplate.CreateDefault();
                    template.Name = newTemplateName.Trim();
                    editingTemplate = template;
                    editingTemplateName = template.Name;
                    newTemplateName = string.Empty;
                }
            }

            // Template editor
            if (editingTemplate != null)
            {
                ImGuiHelpers.ScaledDummy(4f);
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(4f);

                // Editor card
                ImGui.PushStyleColor(ImGuiCol.Border, MasterEventTheme.AccentColor with { W = 0.6f });
                ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 2f);
                ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);

                if (ImGui.BeginChild("##tpl_editor", new Vector2(0, 0), true, ImGuiWindowFlags.AlwaysAutoResize))
                {
                    // Header
                    var penIcon = FontAwesomeIcon.Pen.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(MasterEventTheme.AccentColor, penIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(MasterEventTheme.AccentColor, string.Format(Loc.Get("Models.Editing"), editingTemplateName));

                    ImGuiHelpers.ScaledDummy(4f);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(4f);

                    var fieldWidth = ImGui.GetContentRegionAvail().X;

                    // ── Name ──
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Models.Name"));
                    ImGui.SetNextItemWidth(fieldWidth);
                    var tplName = editingTemplate.Name;
                    if (ImGui.InputText("##tpl_name", ref tplName, 64))
                        editingTemplate.Name = tplName;

                    ImGuiHelpers.ScaledDummy(6f);

                    // ── HP / MP modes side by side ──
                    var hpModeLabels = new[] { Loc.Get("Config.HpMode.Percentage"), Loc.Get("Config.HpMode.Points") };
                    var halfWidth = (fieldWidth - ImGui.GetStyle().ItemSpacing.X) / 2f;
                    var labelColor = new Vector4(0.7f, 0.7f, 0.7f, 1f);
                    var secondColX = ImGui.GetCursorPosX() + halfWidth + ImGui.GetStyle().ItemSpacing.X;

                    // Labels row
                    var heartIcon = FontAwesomeIcon.Heart.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(MasterEventTheme.AttitudeHostile, heartIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(labelColor, Loc.Get("Config.HpMode"));
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(secondColX);
                    var magicIcon = FontAwesomeIcon.Magic.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(MasterEventTheme.MpBarColor, magicIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(labelColor, Loc.Get("Config.MpMode"));

                    // Combos row
                    ImGui.SetNextItemWidth(halfWidth);
                    var hpModeIdx = (int)editingTemplate.HpMode;
                    if (ImGui.Combo("##tpl_hp_mode", ref hpModeIdx, hpModeLabels, hpModeLabels.Length))
                        editingTemplate.HpMode = (HpMode)hpModeIdx;
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                    var mpModeIdx = (int)editingTemplate.MpMode;
                    if (ImGui.Combo("##tpl_mp_mode", ref mpModeIdx, hpModeLabels, hpModeLabels.Length))
                        editingTemplate.MpMode = (HpMode)mpModeIdx;

                    ImGuiHelpers.ScaledDummy(4f);

                    // ── Toggles on same line ──
                    var tplShield = editingTemplate.ShowShield;
                    if (ImGui.Checkbox(Loc.Get("Config.ShowShield") + "##tpl_shield", ref tplShield))
                        editingTemplate.ShowShield = tplShield;

                    ImGui.SameLine();

                    var tplMpBar = editingTemplate.ShowMpBar;
                    if (ImGui.Checkbox(Loc.Get("Config.ShowMpBar") + "##tpl_mp_bar", ref tplMpBar))
                        editingTemplate.ShowMpBar = tplMpBar;

                    ImGuiHelpers.ScaledDummy(4f);

                    // ── Dice max ──
                    var diceIcon = FontAwesomeIcon.Dice.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), diceIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Config.DiceMax"));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                    var tplDiceMax = editingTemplate.DiceMax;
                    if (ImGui.InputInt("##tpl_dice_max", ref tplDiceMax))
                    {
                        if (tplDiceMax < 2) tplDiceMax = 2;
                        if (tplDiceMax > 1000) tplDiceMax = 1000;
                        editingTemplate.DiceMax = tplDiceMax;
                    }

                    ImGuiHelpers.ScaledDummy(4f);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(4f);

                    // ── Counter definitions ──
                    var listIcon = FontAwesomeIcon.ListUl.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(MasterEventTheme.AccentColor, listIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Counters"));
                    ImGuiHelpers.ScaledDummy(2f);

                    var counterDefs = editingTemplate.CounterDefinitions;
                    if (counterDefs != null)
                    {
                        for (var ci = 0; ci < counterDefs.Count; ci++)
                        {
                            var cd = counterDefs[ci];
                            ImGui.PushID(ci);
                            ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
                            var cdName = cd.Name;
                            if (ImGui.InputText("##cd_name", ref cdName, 32))
                                cd.Name = cdName;
                            ImGui.SameLine();
                            ImGui.SetNextItemWidth(60f * ImGuiHelpers.GlobalScale);
                            var cdMax = cd.DefaultMax;
                            if (ImGui.InputInt("##cd_max", ref cdMax))
                            {
                                if (cdMax < 1) cdMax = 1;
                                cd.DefaultMax = cdMax;
                            }
                            ImGui.SameLine();
                            var cdColor = new Vector3(cd.ColorR, cd.ColorG, cd.ColorB);
                            ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                            if (ImGui.ColorEdit3("##cd_color", ref cdColor, ImGuiColorEditFlags.NoInputs))
                            {
                                cd.ColorR = cdColor.X;
                                cd.ColorG = cdColor.Y;
                                cd.ColorB = cdColor.Z;
                            }
                            ImGui.SameLine();
                            var xIcon = FontAwesomeIcon.Times.ToIconString();
                            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                            {
                                if (ImGui.Button(xIcon + "##cd_del"))
                                {
                                    counterDefs.RemoveAt(ci);
                                    if (counterDefs.Count == 0) editingTemplate.CounterDefinitions = null;
                                }
                            }
                            ImGui.PopID();
                        }
                    }

                    var plusIcon = FontAwesomeIcon.Plus.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    {
                        if (ImGui.Button(plusIcon + "##add_cd_btn"))
                        {
                            editingTemplate.CounterDefinitions ??= new List<CounterDefinition>();
                            editingTemplate.CounterDefinitions.Add(new CounterDefinition());
                        }
                    }
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Models.AddCounter"));

                    ImGuiHelpers.ScaledDummy(6f);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(4f);

                    // ── Save / Cancel ──
                    var btnWidth = (fieldWidth - ImGui.GetStyle().ItemSpacing.X) / 2f;
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.2f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.6f, 0.25f, 1f));
                    if (ImGui.Button(Loc.Get("Gm.Save") + "##save_tpl", new Vector2(btnWidth, 0)))
                    {
                        if (editingTemplateName != null && editingTemplate.Name != editingTemplateName)
                            session.DeleteTemplate(editingTemplateName);
                        session.SaveTemplate(editingTemplate);
                        editingTemplate = null;
                        editingTemplateName = null;
                    }
                    ImGui.PopStyleColor(2);

                    ImGui.SameLine();

                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.5f, 0.2f, 0.2f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.6f, 0.25f, 0.25f, 1f));
                    if (ImGui.Button(Loc.Get("Gm.Cancel") + "##cancel_tpl", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
                    {
                        editingTemplate = null;
                        editingTemplateName = null;
                    }
                    ImGui.PopStyleColor(2);
                }
                ImGui.EndChild();

                ImGui.PopStyleVar(2);
                ImGui.PopStyleColor();
            }

            ImGuiHelpers.ScaledDummy(4f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);

            // Saved templates list
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Saved"));
            ImGui.Spacing();

            var templateNames = session.GetTemplateNames();
            if (templateNames.Count == 0)
            {
                ImGui.TextColored(descColor, Loc.Get("Models.None"));
            }
            else
            {
                foreach (var tplName in templateNames)
                {
                    var isDefault = string.Equals(tplName, configuration.DefaultTemplateName, StringComparison.OrdinalIgnoreCase);

                    // Star icon for default template
                    if (isDefault)
                    {
                        var starIcon = FontAwesomeIcon.Star.ToIconString();
                        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                            ImGui.TextColored(MasterEventTheme.AccentColor, starIcon);
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip();
                            ImGui.TextUnformatted(Loc.Get("Models.DefaultTooltip"));
                            ImGui.EndTooltip();
                        }
                        ImGui.SameLine();
                    }

                    ImGui.TextUnformatted(tplName);
                    ImGui.SameLine();

                    if (ImGui.Button(Loc.Get("Gm.Load") + "##load_" + tplName))
                    {
                        var loaded = session.LoadTemplate(tplName);
                        if (loaded != null)
                        {
                            session.ApplyTemplate(loaded);
                            session.BroadcastTemplate();
                            session.BroadcastUpdate();

                            configuration.ActiveTemplateName = loaded.Name;
                            configuration.Save();
                        }
                    }
                    ImGui.SameLine();

                    // Edit button
                    var editIcon = FontAwesomeIcon.Pen.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    {
                        if (ImGui.Button(editIcon + "##edit_" + tplName))
                        {
                            var loaded = session.LoadTemplate(tplName);
                            if (loaded != null)
                            {
                                editingTemplate = loaded.DeepCopy();
                                editingTemplateName = loaded.Name;
                            }
                        }
                    }
                    ImGui.SameLine();

                    // Set as default button (only if not already default)
                    if (!isDefault)
                    {
                        var defaultIcon = FontAwesomeIcon.Star.ToIconString();
                        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        {
                            if (ImGui.Button(defaultIcon + "##default_" + tplName))
                            {
                                configuration.DefaultTemplateName = tplName;
                                configuration.Save();
                            }
                        }
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip();
                            ImGui.TextUnformatted(Loc.Get("Models.SetDefault"));
                            ImGui.EndTooltip();
                        }
                        ImGui.SameLine();
                    }

                    // Delete button (disabled for "Standard")
                    if (tplName != "Standard")
                    {
                        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
                        if (ImGui.Button(Loc.Get("Models.Delete") + "##del_" + tplName))
                        {
                            session.DeleteTemplate(tplName);
                            if (session.ActiveTemplate?.Name == tplName)
                            {
                                session.ClearActiveTemplate();
                                configuration.ActiveTemplateName = string.Empty;
                                configuration.Save();
                            }
                            // If deleted template was the default, fall back to Standard
                            if (string.Equals(tplName, configuration.DefaultTemplateName, StringComparison.OrdinalIgnoreCase))
                            {
                                configuration.DefaultTemplateName = "Standard";
                                configuration.Save();
                            }
                        }
                        ImGui.PopStyleColor();
                    }

                    ImGui.Spacing();
                }
            }
        }
        ImGui.EndChild();
    }

    // ────────── Settings content (with sub-sidebar) ──────────

    private void DrawSettingsContent()
    {
        var sidebarW = SettingsSidebarWidth * ImGuiHelpers.GlobalScale;

        // Sub-sidebar
        if (ImGui.BeginChild("##settings_sidebar", new Vector2(sidebarW, 0), false, ImGuiWindowFlags.NoScrollbar))
        {
            DrawSettingsSidebar();
        }
        ImGui.EndChild();

        ImGui.SameLine();

        // Separator line
        var drawList = ImGui.GetWindowDrawList();
        var sepStart = ImGui.GetCursorScreenPos();
        var sepH = ImGui.GetContentRegionAvail().Y;
        var sepColor = MasterEventTheme.AccentColor with { W = 0.6f };
        drawList.AddLine(sepStart, new Vector2(sepStart.X, sepStart.Y + sepH), ImGui.GetColorU32(sepColor), 1f * ImGuiHelpers.GlobalScale);

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 6f * ImGuiHelpers.GlobalScale);

        // Content
        if (ImGui.BeginChild("##settings_content", Vector2.Zero, false))
        {
            switch (activeSettingsTab)
            {
                case 0: DrawGeneralContent(); break;
                case 1: DrawPrivacyContent(); break;
                case 2: DrawAdvancedContent(); break;
                case 3: DrawAboutContent(); break;
            }
        }
        ImGui.EndChild();
    }

    private void DrawSettingsSidebar()
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);
        settingsSidebarRects.Clear();

        ImGuiHelpers.ScaledDummy(4f);

        for (var i = 0; i < SettingsLabelKeys.Length; i++)
        {
            DrawSettingsSidebarButton(i);
            ImGuiHelpers.ScaledDummy(1f);
        }

        drawList.ChannelsSetCurrent(0);
        DrawSettingsSidebarIndicator(drawList);
        drawList.ChannelsMerge();
    }

    private void DrawSettingsSidebarButton(int tabIndex)
    {
        ImGui.PushID(tabIndex);

        const float btnH = 24f;
        const float iconTextGap = 6f;
        const float paddingX = 8f;
        var scaledBtnH = btnH * ImGuiHelpers.GlobalScale;
        var availWidth = ImGui.GetContentRegionAvail().X;

        var isActive = activeSettingsTab == tabIndex;

        var p = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton("##settingsSidebarBtn", new Vector2(availWidth, scaledBtnH));
        var hovered = ImGui.IsItemHovered();

        settingsSidebarRects[tabIndex] = (p, p + new Vector2(availWidth, scaledBtnH));

        // Hover background
        if (hovered && !isActive)
        {
            var hoverColor = new Vector4(
                MasterEventTheme.AccentColor.X * 0.4f,
                MasterEventTheme.AccentColor.Y * 0.4f,
                MasterEventTheme.AccentColor.Z * 0.4f, 1f);
            var dl = ImGui.GetWindowDrawList();
            var rounding = 6f * ImGuiHelpers.GlobalScale;
            var pad = 2f * ImGuiHelpers.GlobalScale;
            dl.AddRectFilled(p - new Vector2(pad), p + new Vector2(availWidth + pad, scaledBtnH + pad), ImGui.GetColorU32(hoverColor), rounding);
        }

        // Icon + text
        var dl2 = ImGui.GetWindowDrawList();
        var iconStr = SettingsIcons[tabIndex].ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSz = ImGui.CalcTextSize(iconStr);
        ImGui.PopFont();

        var textColor = isActive
            ? new Vector4(1f, 1f, 1f, 1f)
            : hovered
                ? new Vector4(0.9f, 0.85f, 1f, 1f)
                : new Vector4(0.7f, 0.65f, 0.8f, 1f);
        var textColorU32 = ImGui.GetColorU32(textColor);

        var startX = p.X + paddingX * ImGuiHelpers.GlobalScale;

        ImGui.PushFont(UiBuilder.IconFont);
        dl2.AddText(new Vector2(startX, p.Y + (scaledBtnH - iconSz.Y) / 2f), textColorU32, iconStr);
        ImGui.PopFont();

        var label = Loc.Get(SettingsLabelKeys[tabIndex]);
        dl2.AddText(new Vector2(startX + iconSz.X + iconTextGap * ImGuiHelpers.GlobalScale, p.Y + (scaledBtnH - iconSz.Y) / 2f), textColorU32, label);

        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (clicked) activeSettingsTab = tabIndex;

        ImGui.PopID();
    }

    private void DrawSettingsSidebarIndicator(ImDrawListPtr drawList)
    {
        if (!settingsSidebarRects.TryGetValue(activeSettingsTab, out var rect))
            return;

        var windowPos = ImGui.GetWindowPos();
        var targetPos = rect.Min;
        var targetSize = rect.Max - rect.Min;

        if (!settingsSidebarIndicatorInit || settingsSidebarWindowPos != windowPos)
        {
            settingsSidebarIndicatorPos = targetPos;
            settingsSidebarIndicatorSize = targetSize;
            settingsSidebarIndicatorInit = true;
            settingsSidebarWindowPos = windowPos;
        }
        else
        {
            var dt = ImGui.GetIO().DeltaTime;
            var lerpT = 1f - MathF.Exp(-SettingsSidebarAnimSpeed * dt);
            settingsSidebarIndicatorPos = Vector2.Lerp(settingsSidebarIndicatorPos, targetPos, lerpT);
            settingsSidebarIndicatorSize = Vector2.Lerp(settingsSidebarIndicatorSize, targetSize, lerpT);
        }

        var padding = 2f * ImGuiHelpers.GlobalScale;
        var min = settingsSidebarIndicatorPos - new Vector2(padding);
        var max = settingsSidebarIndicatorPos + settingsSidebarIndicatorSize + new Vector2(padding);
        var rounding = 6f * ImGuiHelpers.GlobalScale;
        var indicatorColor = activeSettingsTab == 1
            ? new Vector4(0f, 0.2f, 0.6f, 1f) // EU blue for Privacy
            : MasterEventTheme.AccentColor;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(indicatorColor), rounding);
    }

    private void DrawSectionHeader(int tabIndex)
    {
        var icon = SettingsIcons[tabIndex];
        var title = Loc.Get(SettingsLabelKeys[tabIndex]);
        var description = Loc.Get(SettingsDescriptionKeys[tabIndex]);
        var availWidth = ImGui.GetContentRegionAvail().X;

        ImGuiHelpers.ScaledDummy(6f);

        // Icon — large, centered
        var iconStr = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSz = ImGui.CalcTextSize(iconStr);
        const float scale = 1.6f;
        var scaledSz = iconSz * scale;
        var pos = ImGui.GetCursorScreenPos();
        var iconX = pos.X + (availWidth - scaledSz.X) / 2f;
        var iconY = pos.Y;
        ImGui.Dummy(new Vector2(0, scaledSz.Y));
        var dl = ImGui.GetWindowDrawList();
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, new Vector2(iconX, iconY), ImGui.GetColorU32(MasterEventTheme.AccentColor), iconStr);
        ImGui.PopFont();

        ImGuiHelpers.ScaledDummy(4f);

        var titleSz = ImGui.CalcTextSize(title);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - titleSz.X) / 2f);
        ImGui.TextColored(MasterEventTheme.AccentColor, title);

        ImGuiHelpers.ScaledDummy(2f);

        var descSz = ImGui.CalcTextSize(description);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - descSz.X) / 2f);
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), description);

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
    }


    private void DrawGeneralContent()
    {
        DrawSectionHeader(0);

        // Language selector
        ImGui.TextUnformatted(Loc.Get("Config.Language"));
        var currentLabel = Loc.GetLanguageDisplayName(Loc.CurrentLanguage);
        ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##ui_language", currentLabel))
        {
            foreach (var option in Loc.AvailableLanguages)
            {
                var isSelected = string.Equals(option.Key, Loc.CurrentLanguage, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(option.Value, isSelected))
                {
                    Loc.SetLanguage(option.Key);
                    configuration.UiLanguage = option.Key;
                    configuration.Save();
                }
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

    }

    private void CheckRelayHealth()
    {
        healthCheckInProgress = true;
        lastHealthCheck = DateTime.UtcNow;

        var healthUrl = configuration.RelayServerUrl
            .Replace("ws://", "http://")
            .Replace("wss://", "https://")
            .TrimEnd('/') + "/health";

        Task.Run(async () =>
        {
            try
            {
                var response = await httpClient.GetAsync(healthUrl);
                relayOnline = response.IsSuccessStatusCode;
            }
            catch
            {
                relayOnline = false;
            }
            finally
            {
                healthCheckInProgress = false;
            }
        });
    }

    // ────────── Privacy content ──────────

    private void DrawPrivacyContent()
    {
        DrawSectionHeader(1);

        // Consent status
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Privacy.ConsentTitle"));
        ImGui.Spacing();

        if (configuration.IsRgpdConsentValid && configuration.RgpdConsentDate.HasValue)
        {
            var dateStr = configuration.RgpdConsentDate.Value.ToString("dd/MM/yyyy HH:mm");
            ImGui.TextColored(new Vector4(0.5f, 0.8f, 0.5f, 1f),
                string.Format(Loc.Get("Privacy.ConsentActive"), dateStr));
        }
        else
        {
            ImGui.TextColored(new Vector4(0.8f, 0.4f, 0.4f, 1f), Loc.Get("Privacy.ConsentNone"));
        }

        ImGui.Spacing();

        // Revoke consent
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Privacy.RevokeTitle"));
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Privacy.RevokeDescription"));
        ImGui.Spacing();

        if (configuration.IsRgpdConsentValid)
        {
            if (!revokeConfirmPending)
            {
                if (ImGui.Button(Loc.Get("Privacy.Revoke")))
                    revokeConfirmPending = true;
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), Loc.Get("Privacy.RevokeWarning"));
                ImGui.Spacing();

                if (ImGui.Button(Loc.Get("Privacy.RevokeConfirm")))
                {
                    configuration.RgpdConsentGiven = false;
                    configuration.RgpdConsentDate = null;
                    configuration.AcceptedRgpdVersion = 0;
                    configuration.Save();
                    revokeConfirmPending = false;
                    onConsentRevoked?.Invoke();
                }

                ImGui.SameLine();
                if (ImGui.Button(Loc.Get("Gm.Cancel")))
                    revokeConfirmPending = false;
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Rights
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Privacy.RightsTitle"));
        ImGui.Spacing();

        var dimColor = new Vector4(0.7f, 0.7f, 0.7f, 1f);
        ImGui.TextColored(dimColor, Loc.Get("Privacy.RightAccess"));
        ImGui.TextColored(dimColor, Loc.Get("Privacy.RightErasure"));
        ImGui.TextColored(dimColor, Loc.Get("Privacy.RightObject"));

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Privacy.Controller"));
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Privacy.LegalBasis"));
    }

    // ────────── About content ──────────

    private void DrawAboutContent()
    {
        var availW = ImGui.GetContentRegionAvail().X;

        ImGuiHelpers.ScaledDummy(20f);

        // Large icon centered
        var moonIcon = FontAwesomeIcon.Dice.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSz = ImGui.CalcTextSize(moonIcon);
        const float iconScale = 1.6f;
        var scaledIconSz = iconSz * iconScale;
        var iconPos = ImGui.GetCursorScreenPos();
        var iconX = iconPos.X + (availW - scaledIconSz.X) / 2f;
        ImGui.Dummy(new Vector2(0, scaledIconSz.Y));
        var dl = ImGui.GetWindowDrawList();
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * iconScale, new Vector2(iconX, iconPos.Y), ImGui.GetColorU32(MasterEventTheme.AccentColor), moonIcon);
        ImGui.PopFont();

        ImGuiHelpers.ScaledDummy(8f);

        // Title centered
        var titleText = Loc.Get("About.Title");
        var titleSize = ImGui.CalcTextSize(titleText);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - titleSize.X) / 2f);
        ImGui.TextColored(MasterEventTheme.AccentColor, titleText);

        ImGuiHelpers.ScaledDummy(2f);

        // Version + author centered
        var versionLine = $"v{Constants.PluginVersion}  ·  {Loc.Get("About.Author")}";
        var vSz = ImGui.CalcTextSize(versionLine);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - vSz.X) / 2f);
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), versionLine);

        ImGuiHelpers.ScaledDummy(4f);

        // Description centered
        var descText = Loc.Get("About.Description");
        var descSz = ImGui.CalcTextSize(descText);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - descSz.X) / 2f);
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), descText);

        ImGuiHelpers.ScaledDummy(24f);

        // Links label centered
        var linksText = Loc.Get("About.Links");
        var linksSz = ImGui.CalcTextSize(linksText);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - linksSz.X) / 2f);
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), linksText);

        ImGuiHelpers.ScaledDummy(6f);

        // 3 link buttons
        var btnSpacing = 6f * ImGuiHelpers.GlobalScale;
        var margin = 8f * ImGuiHelpers.GlobalScale;
        var usableWidth = availW - margin * 2f;
        var btnWidth = (usableWidth - btnSpacing * 2f) / 3f;

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + margin);

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(MasterEventTheme.AccentColor.X * 0.3f, MasterEventTheme.AccentColor.Y * 0.3f, MasterEventTheme.AccentColor.Z * 0.3f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(MasterEventTheme.AccentColor.X * 0.45f, MasterEventTheme.AccentColor.Y * 0.45f, MasterEventTheme.AccentColor.Z * 0.45f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(MasterEventTheme.AccentColor.X * 0.6f, MasterEventTheme.AccentColor.Y * 0.6f, MasterEventTheme.AccentColor.Z * 0.6f, 1f));

        if (DrawAboutLinkButton(FontAwesomeIcon.Globe, Loc.Get("About.Discord"), btnWidth))
            Dalamud.Utility.Util.OpenLink(Constants.DiscordUrl);
        ImGui.SameLine(0, btnSpacing);
        if (DrawAboutLinkButton(FontAwesomeIcon.Code, Loc.Get("About.GitHub"), btnWidth))
            Dalamud.Utility.Util.OpenLink(Constants.GitHubUrl);
        ImGui.SameLine(0, btnSpacing);
        if (DrawAboutLinkButton(FontAwesomeIcon.FileAlt, Loc.Get("About.Changelog"), btnWidth))
            Dalamud.Utility.Util.OpenLink(Constants.ChangelogUrl);

        ImGui.PopStyleColor(3);

        ImGuiHelpers.ScaledDummy(20f);

        // Relay status centered
        if (!healthCheckInProgress && (DateTime.UtcNow - lastHealthCheck).TotalSeconds >= HealthCheckIntervalSeconds)
            CheckRelayHealth();

        var relayLabel = Loc.Get("General.RelayStatus") + " : ";
        string statusLabel;
        Vector4 statusColor;
        if (healthCheckInProgress && !relayOnline.HasValue)
        {
            statusLabel = Loc.Get("General.RelayChecking");
            statusColor = new Vector4(0.7f, 0.7f, 0.7f, 1f);
        }
        else if (relayOnline == true)
        {
            statusLabel = Loc.Get("General.RelayOnline");
            statusColor = new Vector4(0.2f, 1f, 0.2f, 1f);
        }
        else
        {
            statusLabel = Loc.Get("General.RelayOffline");
            statusColor = new Vector4(0.8f, 0.4f, 0.4f, 1f);
        }

        var fullRelayLine = relayLabel + statusLabel;
        var relaySz = ImGui.CalcTextSize(fullRelayLine);
        var relayStartX = (availW - relaySz.X) / 2f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + relayStartX);
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), relayLabel);
        ImGui.SameLine(0, 0);
        ImGui.TextColored(statusColor, statusLabel);

        ImGuiHelpers.ScaledDummy(8f);

        // Tagline centered
        var taglineText = Loc.Get("About.Tagline");
        var taglineSz = ImGui.CalcTextSize(taglineText);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - taglineSz.X) / 2f);
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), taglineText);

        ImGuiHelpers.ScaledDummy(6f);

        var heartIcon = FontAwesomeIcon.Heart.ToIconString();
        var gap = 8f * ImGuiHelpers.GlobalScale;
        const float heartScale = 1.3f;
        const uint baguetteIconId = 24030;
        const uint cheeseIconId = 24455;
        ImGui.PushFont(UiBuilder.IconFont);
        var heartBaseSz = ImGui.CalcTextSize(heartIcon);
        ImGui.PopFont();
        var heartSz = heartBaseSz * heartScale;
        var foodSize = heartSz.Y;
        var rowH = MathF.Max(foodSize, heartSz.Y);
        var totalIconW = foodSize + gap + heartSz.X + gap + foodSize;
        var baseScreenPos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(0, rowH));
        var dl2 = ImGui.GetWindowDrawList();
        var curX = baseScreenPos.X + (availW - totalIconW) / 2f;
        var baguetteWrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(baguetteIconId)).GetWrapOrEmpty();
        dl2.AddImage(baguetteWrap.Handle,
            new Vector2(curX, baseScreenPos.Y + (rowH - foodSize) / 2f),
            new Vector2(curX + foodSize, baseScreenPos.Y + (rowH + foodSize) / 2f));
        curX += foodSize + gap;

        dl2.AddText(UiBuilder.IconFont, ImGui.GetFontSize() * heartScale,
            new Vector2(curX, baseScreenPos.Y + (rowH - heartSz.Y) / 2f),
            ImGui.GetColorU32(MasterEventTheme.AccentColor), heartIcon);
        curX += heartSz.X + gap;
        var cheeseWrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(cheeseIconId)).GetWrapOrEmpty();
        dl2.AddImage(cheeseWrap.Handle,
            new Vector2(curX, baseScreenPos.Y + (rowH - foodSize) / 2f),
            new Vector2(curX + foodSize, baseScreenPos.Y + (rowH + foodSize) / 2f));
    }

    private static bool DrawAboutLinkButton(FontAwesomeIcon icon, string label, float width)
    {
        var fontSize = ImGui.GetFontSize() * 0.85f;
        var iconStr = icon.ToIconString();

        ImGui.PushFont(UiBuilder.IconFont);
        var iconSz = ImGui.CalcTextSize(iconStr) * 0.85f;
        ImGui.PopFont();

        var labelSz = ImGui.CalcTextSize(label) * 0.85f;
        var gap = 4f * ImGuiHelpers.GlobalScale;
        var totalW = iconSz.X + gap + labelSz.X;
        var btnH = 26f * ImGuiHelpers.GlobalScale;

        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.Button($"##{label}Link", new Vector2(width, btnH));

        var dl = ImGui.GetWindowDrawList();
        var startX = pos.X + (width - totalW) / 2f;
        var white = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));

        dl.AddText(UiBuilder.IconFont, fontSize, new Vector2(startX, pos.Y + (btnH - iconSz.Y) / 2f), white, iconStr);
        dl.AddText(ImGui.GetFont(), fontSize, new Vector2(startX + iconSz.X + gap, pos.Y + (btnH - labelSz.Y) / 2f), white, label);

        return clicked;
    }


    private void DrawAdvancedContent()
    {
        DrawSectionHeader(2);

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.6f, 0.2f, 1f));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextWrapped(Loc.Get("Advanced.Warning"));
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        ImGui.Spacing();
        ImGui.Spacing();

        // Debug mode toggle
        var debugMode = configuration.DebugMode;
        if (ImGui.Checkbox(Loc.Get("Advanced.DebugMode"), ref debugMode))
        {
            configuration.DebugMode = debugMode;
            configuration.Save();

            if (!debugMode)
                onDebugDisabled?.Invoke();
        }

        if (configuration.DebugMode)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Advanced.Commands"));
            ImGui.Spacing();

            var cmdColor = new Vector4(0.7f, 0.7f, 0.7f, 1f);
            ImGui.TextColored(cmdColor, "/masterevent connect");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "— " + Loc.Get("Advanced.Cmd.Connect"));

            ImGui.TextColored(cmdColor, "/masterevent disconnect");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "— " + Loc.Get("Advanced.Cmd.Disconnect"));

            ImGui.TextColored(cmdColor, "/masterevent joueur");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "— " + Loc.Get("Advanced.Cmd.Player"));

            ImGui.TextColored(cmdColor, "/masterevent mj");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "— " + Loc.Get("Advanced.Cmd.Gm"));
        }
    }
}
