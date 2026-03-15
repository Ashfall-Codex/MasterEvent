using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly Action? onEnableAlliance;
    private readonly Action? onDisableAlliance;
    public MasterEventWindowBase? PlayerWindowRef { get; set; }

    private bool revokeConfirmPending;

    private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private bool? relayOnline;
    private DateTime lastHealthCheck = DateTime.MinValue;
    private bool healthCheckInProgress;
    private const double HealthCheckIntervalSeconds = 30;

    private enum Tab { Markers, Group, Models, Profiles, Turns, Settings }
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
        if (!gmAccess && activeTab is Tab.Group or Tab.Models or Tab.Turns)
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


    private void DrawMarkersContent()
    {
        if (!session.CanEdit && !HasGmAccess())
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
            if (ImGui.BeginChild("##markers_scroll", new Vector2(0, -30f * ImGuiHelpers.GlobalScale)))
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
                        onRoll: statId => session.RollDiceWithStat(waymarkId, statId),
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

        var framePad = ImGui.GetStyle().FramePadding.X * 2;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var updateBtnWidth = ImGui.CalcTextSize(Loc.Get("Gm.Update")).X + framePad;
        var historyIcon = FontAwesomeIcon.History.ToIconString();
        float historyBtnWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            historyBtnWidth = ImGui.CalcTextSize(historyIcon).X + framePad;
        var totalBtnWidth = historyBtnWidth + spacing + updateBtnWidth;
        var buttonPos = totalWidth - totalBtnWidth;
        if (buttonPos > 0)
            ImGui.SameLine(buttonPos);

        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(historyIcon + "##roll_history"))
                ImGui.OpenPopup("##roll_history_popup");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Dice.History"));
            ImGui.EndTooltip();
        }

        if (ImGui.BeginPopup("##roll_history_popup"))
        {
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Dice.History"));
            ImGui.Separator();

            if (session.RollHistory.Count == 0)
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Dice.NoHistory"));
            }
            else
            {
                for (var hi = 0; hi < session.RollHistory.Count && hi < 20; hi++)
                {
                    var roll = session.RollHistory[hi];
                    var modStr = roll.Modifier >= 0 ? $"+{roll.Modifier}" : roll.Modifier.ToString();
                    var statInfo = roll.StatName != null ? $" [{roll.StatName} {modStr}]" : "";
                    var line = $"{roll.RollerName}: {roll.RawRoll}/{roll.DiceMax}{statInfo} = {roll.Total}";
                    ImGui.TextUnformatted(line);
                }

                ImGui.Separator();
                if (ImGui.Selectable(Loc.Get("Dice.ClearHistory")))
                    session.ClearRollHistory();
            }

            ImGui.EndPopup();
        }

        ImGui.SameLine();
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


    private void DrawTurnsContent()
    {
        var availWidth = ImGui.GetContentRegionAvail().X;
        var state = session.CurrentTurnState;

        if (state is not { IsActive: true })
        {
            // Idle state
            ImGuiHelpers.ScaledDummy(6f);

            var iconStr = FontAwesomeIcon.ListOl.ToIconString();
            ImGui.PushFont(UiBuilder.IconFont);
            var iconSz = ImGui.CalcTextSize(iconStr);
            const float iconScale = 1.6f;
            var scaledSz = iconSz * iconScale;
            var pos = ImGui.GetCursorScreenPos();
            var iconX = pos.X + (availWidth - scaledSz.X) / 2f;
            ImGui.Dummy(new Vector2(0, scaledSz.Y));
            var dl = ImGui.GetWindowDrawList();
            dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * iconScale, new Vector2(iconX, pos.Y), ImGui.GetColorU32(MasterEventTheme.AccentColor), iconStr);
            ImGui.PopFont();

            ImGuiHelpers.ScaledDummy(4f);

            var titleSz = ImGui.CalcTextSize(Loc.Get("Turns.Title"));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - titleSz.X) / 2f);
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Turns.Title"));

            ImGuiHelpers.ScaledDummy(8f);

            var noEncText = Loc.Get("Turns.NoEncounter");
            var noEncSz = ImGui.CalcTextSize(noEncText);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - noEncSz.X) / 2f);
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), noEncText);

            ImGuiHelpers.ScaledDummy(8f);

            var startLabel = Loc.Get("Turns.Start");
            var startSz = ImGui.CalcTextSize(startLabel) + ImGui.GetStyle().FramePadding * 2;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - startSz.X) / 2f);
            if (ImGui.Button(startLabel + "##start_encounter"))
                session.StartEncounter();
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Turns.StartTooltip"));
                ImGui.EndTooltip();
            }
            return;
        }

        // Active encounter
        ImGuiHelpers.ScaledDummy(4f);

        // Round header + dice indicator
        var roundText = string.Format(Loc.Get("Turns.Round"), state.Round);
        ImGui.TextColored(MasterEventTheme.AccentColor, roundText);
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), $"(d{state.DiceMax})");

        ImGuiHelpers.ScaledDummy(2f);

        // Navigation buttons
        var btnWidth = (availWidth - ImGui.GetStyle().ItemSpacing.X) / 2f;
        if (ImGui.Button(Loc.Get("Turns.NextRound") + "##next_round", new Vector2(btnWidth, 0)))
            session.NextRound();
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Turns.NextRoundTooltip"));
            ImGui.EndTooltip();
        }
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.7f, 0.2f, 0.2f, 1f));
        if (ImGui.Button(Loc.Get("Turns.End") + "##end", new Vector2(btnWidth, 0)))
            session.EndEncounter();
        ImGui.PopStyleColor(2);

        ImGuiHelpers.ScaledDummy(2f);

        // Re-roll all button
        var rerollAllIcon = FontAwesomeIcon.DiceD20.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(rerollAllIcon + "##reroll_all"))
                session.RerollAllInitiative();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Turns.RerollAll"));
            ImGui.EndTooltip();
        }

        // Sync button
        ImGui.SameLine();
        var syncIcon = FontAwesomeIcon.Sync.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(syncIcon + "##sync_turns"))
                session.BroadcastTurnState();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Turns.Sync"));
            ImGui.EndTooltip();
        }

        // Bouton ajout de participant
        ImGui.SameLine();
        var addIcon = FontAwesomeIcon.Plus.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(addIcon + "##add_participant"))
                ImGui.OpenPopup("##add_participant_popup");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Turns.AddParticipant"));
            ImGui.EndTooltip();
        }
        DrawAddParticipantPopup(state);

        ImGuiHelpers.ScaledDummy(2f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        // Progress counter
        var actedCount = state.Entries.Count(e => e.HasActed);
        var progressText = string.Format(Loc.Get("Turns.Progress"), actedCount, state.Entries.Count);
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), progressText);

        ImGuiHelpers.ScaledDummy(2f);

        // Participant list
        if (ImGui.BeginChild("##turns_scroll", Vector2.Zero))
        {
            for (var i = 0; i < state.Entries.Count; i++)
            {
                var entry = state.Entries[i];

                ImGui.PushID(i);

                // Checkbox for HasActed
                var hasActed = entry.HasActed;
                if (ImGui.Checkbox("##acted_" + i, ref hasActed))
                    session.ToggleHasActed(i);
                ImGui.SameLine();

                // Icon: waymark or user
                var iconSize = ImGui.GetFrameHeight();
                if (entry.IsMarker && entry.WaymarkIndex.HasValue)
                {
                    var waymarkId = (WaymarkId)entry.WaymarkIndex.Value;
                    var iconId = waymarkId.ToIconId();
                    var wrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
                    ImGui.Image(wrap.Handle, new Vector2(iconSize, iconSize));
                    ImGui.SameLine();
                }
                else
                {
                    var userIcon = FontAwesomeIcon.User.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    {
                        ImGui.TextColored(new Vector4(0.227f, 0.604f, 1f, 0.8f), userIcon);
                    }
                    ImGui.SameLine();
                }

                // Name — greyed out if already acted
                var nameColor = entry.HasActed ? new Vector4(0.5f, 0.5f, 0.5f, 1f) : new Vector4(1f, 1f, 1f, 1f);
                ImGui.TextColored(nameColor, entry.Name);
                ImGui.SameLine();

                // Initiative
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), $"[{entry.Initiative}]");
                if (ImGui.IsItemHovered() && entry.InitiativeRoll > 0)
                {
                    ImGui.BeginTooltip();
                    if (entry.InitiativeStatName != null)
                    {
                        var modStr = entry.InitiativeModifier >= 0 ? $"+{entry.InitiativeModifier}" : entry.InitiativeModifier.ToString();
                        ImGui.TextUnformatted($"{Loc.Get("Turns.InitRoll")}: {entry.InitiativeRoll} ({entry.InitiativeStatName} {modStr}) = {entry.Initiative}");
                    }
                    else
                    {
                        ImGui.TextUnformatted($"{Loc.Get("Turns.InitRoll")}: {entry.InitiativeRoll}");
                    }
                    ImGui.EndTooltip();
                }

                // Action buttons on the right
                var upIcon = FontAwesomeIcon.ChevronUp.ToIconString();
                var downIcon = FontAwesomeIcon.ChevronDown.ToIconString();
                var diceIcon = FontAwesomeIcon.Dice.ToIconString();
                var trashIcon = FontAwesomeIcon.Trash.ToIconString();
                float upBtnW, downBtnW, diceBtnW, trashBtnW;
                var framePad = ImGui.GetStyle().FramePadding.X * 2;
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    upBtnW = ImGui.CalcTextSize(upIcon).X + framePad;
                    downBtnW = ImGui.CalcTextSize(downIcon).X + framePad;
                    diceBtnW = ImGui.CalcTextSize(diceIcon).X + framePad;
                    trashBtnW = ImGui.CalcTextSize(trashIcon).X + framePad;
                }
                var spacing = ImGui.GetStyle().ItemSpacing.X;
                var buttonsWidth = upBtnW + downBtnW + diceBtnW + trashBtnW + spacing * 3;
                var childWidth = ImGui.GetContentRegionMax().X;
                var rightPos = childWidth - buttonsWidth;
                if (rightPos > ImGui.GetCursorPosX())
                    ImGui.SameLine(rightPos);

                // Move up
                var isFirst = i == 0;
                if (isFirst) ImGui.BeginDisabled();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.Button(upIcon + "##up_" + i))
                        session.MoveParticipantUp(i);
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(Loc.Get("Turns.MoveUp"));
                    ImGui.EndTooltip();
                }
                if (isFirst) ImGui.EndDisabled();

                // Move down
                ImGui.SameLine();
                var isLast = i == state.Entries.Count - 1;
                if (isLast) ImGui.BeginDisabled();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.Button(downIcon + "##down_" + i))
                        session.MoveParticipantDown(i);
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(Loc.Get("Turns.MoveDown"));
                    ImGui.EndTooltip();
                }
                if (isLast) ImGui.EndDisabled();

                // Re-roll
                ImGui.SameLine();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.Button(diceIcon + "##reroll_" + i))
                        session.RerollInitiative(i);
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(Loc.Get("Turns.Reroll"));
                    ImGui.EndTooltip();
                }

                // Remove
                ImGui.SameLine();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.Button(trashIcon + "##remove_" + i))
                        session.RemoveTurnParticipant(i);
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(Loc.Get("Turns.Remove"));
                    ImGui.EndTooltip();
                }

                ImGui.PopID();
                ImGui.Spacing();
            }
        }
        ImGui.EndChild();
    }

    private void DrawAddParticipantPopup(TurnState state)
    {
        if (!ImGui.BeginPopup("##add_participant_popup")) return;

        // Collecter les participants déjà présents
        var existingWaymarks = new HashSet<int>();
        var existingPlayers = new HashSet<string>();
        foreach (var e in state.Entries)
        {
            if (e.WaymarkIndex.HasValue)
                existingWaymarks.Add(e.WaymarkIndex.Value);
            if (e.PlayerHash != null)
                existingPlayers.Add(e.PlayerHash);
        }

        var hasItems = false;

        // Marqueurs disponibles (non encore dans le combat)
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            if (existingWaymarks.Contains(i)) continue;
            var waymarkId = (WaymarkId)i;
            var marker = session.CurrentMarkers[waymarkId];
            if (!marker.HasData || string.IsNullOrEmpty(marker.Name)) continue;

            hasItems = true;
            var iconSize = ImGui.GetFrameHeight();
            var iconId = waymarkId.ToIconId();
            var wrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(iconSize, iconSize));
            ImGui.SameLine();
            if (ImGui.Selectable(marker.Name + "##add_m_" + i))
            {
                session.AddTurnParticipant(new TurnEntry
                {
                    WaymarkIndex = i,
                    Name = marker.Name,
                });
                ImGui.CloseCurrentPopup();
            }
        }

        // Joueurs disponibles (non encore dans le combat)
        foreach (var player in session.PartyMembers)
        {
            if (player.IsGm && !session.GmIsPlayer) continue;
            if (existingPlayers.Contains(player.Hash)) continue;

            hasItems = true;
            var userIcon = FontAwesomeIcon.User.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(new Vector4(0.227f, 0.604f, 1f, 0.8f), userIcon);
            ImGui.SameLine();
            if (ImGui.Selectable(player.Name + "##add_p_" + player.Hash))
            {
                session.AddTurnParticipant(new TurnEntry
                {
                    PlayerHash = player.Hash,
                    Name = player.Name,
                });
                ImGui.CloseCurrentPopup();
            }
        }

        if (!hasItems)
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Turns.NoAvailableParticipants"));

        ImGui.EndPopup();
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

        // Section Mode Alliance
        if (!session.IsAllianceMode)
        {
            if (ImGui.Button(Loc.Get("Alliance.Enable") + "##enable_alliance"))
                onEnableAlliance?.Invoke();
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Alliance.EnableTooltip"));
                ImGui.EndTooltip();
            }
        }
        else
        {
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Alliance.RoomCode"));
            ImGui.SameLine();
            var code = session.AllianceRoomCode ?? "";
            var spaced = string.Join("  ", code.ToCharArray());
            ImGui.TextUnformatted(spaced);

            if (ImGui.Button(Loc.Get("Alliance.Copy") + "##copy_alliance"))
                ImGui.SetClipboardText(session.AllianceRoomCode ?? "");

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.7f, 0.2f, 0.2f, 1f));
            if (ImGui.Button(Loc.Get("Alliance.Disable") + "##disable_alliance"))
                onDisableAlliance?.Invoke();
            ImGui.PopStyleColor(2);
        }

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        if (ImGui.BeginChild("##group_scroll", Vector2.Zero))
        {
            // GM section
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Group.Gm"));
            ImGui.Spacing();

            var hasGm = false;
            foreach (var player in session.PartyMembers.Where(p => p.IsGm))
            {
                hasGm = true;
                DrawGroupMember(player, true);
            }

            if (!hasGm)
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "—");

            // GM as player checkbox
            var gmIsPlayer = session.GmIsPlayer;
            if (ImGui.Checkbox(Loc.Get("Group.GmIsPlayer"), ref gmIsPlayer))
            {
                session.GmIsPlayer = gmIsPlayer;
                configuration.GmIsPlayer = gmIsPlayer;
                configuration.Save();
                session.BroadcastPlayerUpdate();

                // Update active encounter: add/remove GM from turn entries
                if (session.CurrentTurnState is { IsActive: true } turnState)
                {
                    var gmPlayer = session.PartyMembers.FirstOrDefault(p => p.IsGm);
                    if (gmPlayer != null)
                    {
                        if (!gmIsPlayer)
                        {
                            turnState.Entries.RemoveAll(e => e.PlayerHash == gmPlayer.Hash);
                        }
                        else if (turnState.Entries.All(e => e.PlayerHash != gmPlayer.Hash))
                        {
                            session.AddTurnParticipant(new TurnEntry
                            {
                                PlayerHash = gmPlayer.Hash,
                                Name = gmPlayer.Name,
                            });
                        }
                    }
                    session.BroadcastTurnState();
                }
            }

            ImGuiHelpers.ScaledDummy(4f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);

            // Players section
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Group.Players"));
            ImGui.Spacing();

            var hasPlayers = false;
            foreach (var player in session.PartyMembers.Where(p => !p.IsGm || session.GmIsPlayer))
            {
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
        if (!isGmSection && session.IsGm && !player.IsGm)
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

        // HP/EP/Shield/Counter controls (only for non-GM players)
        if (!isGmSection)
        {
            var pmBtnW = ImGui.GetFrameHeight();
            var barSpacing = ImGui.GetStyle().ItemSpacing.X;
            var barFramePad = ImGui.GetStyle().FramePadding.X * 2;
            float editBtnW;
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                editBtnW = ImGui.CalcTextSize(FontAwesomeIcon.Pen.ToIconString()).X + barFramePad;
            var barRightPad = editBtnW + barSpacing;

            if (session.CanEdit)
            {
                // --- Shield ---
                if (session.ShowShield)
                {
                    var shield = player.Shield;
                    var shieldMax = session.HpMode == HpMode.Percentage ? 100 : player.HpMax;
                    if (ImGui.Button($"-##psh_dec_{player.Hash}", new Vector2(pmBtnW, 0)))
                    {
                        shield = Math.Max(0, shield - 1);
                        session.SetPlayerShield(player.Hash, shield);
                    }
                    ImGui.SameLine();
                    ImGui.TextColored(MasterEventTheme.ShieldOverlayColor, $"{Loc.Get("Marker.Shield")}: {player.Shield}");
                    ImGui.SameLine();
                    if (ImGui.Button($"+##psh_inc_{player.Hash}", new Vector2(pmBtnW, 0)))
                    {
                        shield = Math.Min(shieldMax, shield + 1);
                        session.SetPlayerShield(player.Hash, shield);
                    }
                }

                // --- HP ---
                if (session.HpMode == HpMode.Points)
                {
                    var editIcon = FontAwesomeIcon.Pen.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    {
                        if (ImGui.Button($"{editIcon}##phmax_edit_{player.Hash}"))
                            ImGui.OpenPopup($"phmax_popup_{player.Hash}");
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(Loc.Get("Marker.HpMax"));
                        ImGui.EndTooltip();
                    }
                    if (ImGui.BeginPopup($"phmax_popup_{player.Hash}"))
                    {
                        ImGui.TextUnformatted(Loc.Get("Marker.HpMax"));
                        ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
                        var editHpMax = player.HpMax;
                        if (ImGui.InputInt($"##phmax_{player.Hash}", ref editHpMax))
                        {
                            if (editHpMax < 1) editHpMax = 1;
                            if (editHpMax > 99999) editHpMax = 99999;
                            session.SetPlayerHpMax(player.Hash, editHpMax);
                        }
                        ImGui.EndPopup();
                    }
                    ImGui.SameLine();
                }

                var hp = player.Hp;
                var hpClampMax = session.HpMode == HpMode.Percentage ? 100 : player.HpMax;
                if (ImGui.Button($"-##php_dec_{player.Hash}", new Vector2(pmBtnW, 0)))
                {
                    hp = Math.Max(0, hp - 1);
                    session.SetPlayerHp(player.Hash, hp);
                }
                ImGui.SameLine();
                var hpBarWidth = ImGui.GetContentRegionAvail().X - pmBtnW - barSpacing - barRightPad;
                HpBar.Draw(player.Hp, Attitude.Neutral, hpBarWidth, session.HpMode, hpMax: player.HpMax,
                    shield: session.ShowShield ? player.Shield : 0);
                ImGui.SameLine();
                if (ImGui.Button($"+##php_inc_{player.Hash}", new Vector2(pmBtnW, 0)))
                {
                    hp = Math.Min(hpClampMax, hp + 1);
                    session.SetPlayerHp(player.Hash, hp);
                }

                // --- EP ---
                if (session.ShowMpBar)
                {
                    var mp = player.Mp;
                    var mpClampMax = session.MpMode == HpMode.Percentage ? 100 : player.MpMax;

                    if (session.MpMode == HpMode.Points)
                    {
                        var editIcon = FontAwesomeIcon.Pen.ToIconString();
                        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        {
                            if (ImGui.Button($"{editIcon}##pmpmax_edit_{player.Hash}"))
                                ImGui.OpenPopup($"pmpmax_popup_{player.Hash}");
                        }
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip();
                            ImGui.TextUnformatted(Loc.Get("Marker.MpMax"));
                            ImGui.EndTooltip();
                        }
                        if (ImGui.BeginPopup($"pmpmax_popup_{player.Hash}"))
                        {
                            ImGui.TextUnformatted(Loc.Get("Marker.MpMax"));
                            ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
                            var editMpMax = player.MpMax;
                            if (ImGui.InputInt($"##pmpmax_{player.Hash}", ref editMpMax))
                            {
                                if (editMpMax < 1) editMpMax = 1;
                                if (editMpMax > 99999) editMpMax = 99999;
                                session.SetPlayerMpMax(player.Hash, editMpMax);
                            }
                            ImGui.EndPopup();
                        }
                        ImGui.SameLine();
                    }

                    if (ImGui.Button($"-##pmp_dec_{player.Hash}", new Vector2(pmBtnW, 0)))
                    {
                        mp = Math.Max(0, mp - 1);
                        session.SetPlayerMp(player.Hash, mp);
                    }
                    ImGui.SameLine();
                    var mpBarWidth = ImGui.GetContentRegionAvail().X - pmBtnW - barSpacing - barRightPad;
                    HpBar.DrawMpBar(player.Mp, mpBarWidth, session.MpMode, player.MpMax);
                    ImGui.SameLine();
                    if (ImGui.Button($"+##pmp_inc_{player.Hash}", new Vector2(pmBtnW, 0)))
                    {
                        mp = Math.Min(mpClampMax, mp + 1);
                        session.SetPlayerMp(player.Hash, mp);
                    }
                }

                // --- Counters ---
                if (player.Counters != null)
                {
                    for (var ci = 0; ci < player.Counters.Count; ci++)
                    {
                        var counter = player.Counters[ci];

                        var editIcon = FontAwesomeIcon.Pen.ToIconString();
                        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        {
                            if (ImGui.Button($"{editIcon}##pcnt_edit_{player.Hash}_{ci}"))
                                ImGui.OpenPopup($"pcnt_popup_{player.Hash}_{ci}");
                        }
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip();
                            ImGui.TextUnformatted(Loc.Get("Counter.Edit"));
                            ImGui.EndTooltip();
                        }
                        if (ImGui.BeginPopup($"pcnt_popup_{player.Hash}_{ci}"))
                        {
                            ImGui.TextUnformatted(Loc.Get("Counter.Edit"));
                            ImGui.Separator();
                            var cMax = counter.Max;
                            ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                            if (ImGui.InputInt($"##pcnt_max_{player.Hash}_{ci}", ref cMax))
                            {
                                if (cMax < 1) cMax = 1;
                                counter.Max = cMax;
                                counter.Value = cMax;
                                session.BroadcastPlayerUpdate();
                            }
                            ImGui.EndPopup();
                        }
                        ImGui.SameLine();

                        if (ImGui.Button($"-##pcnt_dec_{player.Hash}_{ci}", new Vector2(pmBtnW, 0)))
                        {
                            counter.Value = Math.Max(0, counter.Value - 1);
                            session.BroadcastPlayerUpdate();
                        }
                        ImGui.SameLine();
                        var cntBarWidth = ImGui.GetContentRegionAvail().X - pmBtnW - barSpacing - barRightPad;
                        CounterBar.Draw(counter, cntBarWidth);
                        ImGui.SameLine();
                        if (ImGui.Button($"+##pcnt_inc_{player.Hash}_{ci}", new Vector2(pmBtnW, 0)))
                        {
                            counter.Value = Math.Min(counter.Max, counter.Value + 1);
                            session.BroadcastPlayerUpdate();
                        }
                    }
                }

                // --- Bonus/malus temporaire ---
                var tempIcon = FontAwesomeIcon.Magic.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.Button($"{tempIcon}##ptemp_edit_{player.Hash}"))
                        ImGui.OpenPopup($"ptemp_popup_{player.Hash}");
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(Loc.Get("Marker.TempMod"));
                    ImGui.EndTooltip();
                }
                if (player.TempModifier != 0)
                {
                    ImGui.SameLine();
                    var tempStr = player.TempModifier >= 0 ? $"+{player.TempModifier}" : player.TempModifier.ToString();
                    var tempColor = player.TempModifier > 0
                        ? new Vector4(0.2f, 0.8f, 0.2f, 1f)
                        : new Vector4(1f, 0.4f, 0.4f, 1f);
                    ImGui.TextColored(tempColor, tempStr);
                    if (player.TempModTurns > 0)
                    {
                        ImGui.SameLine(0, 2f * ImGuiHelpers.GlobalScale);
                        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"({player.TempModTurns}t)");
                    }
                }
                if (ImGui.BeginPopup($"ptemp_popup_{player.Hash}"))
                {
                    ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Marker.TempMod"));
                    ImGui.Separator();
                    ImGui.TextUnformatted(Loc.Get("Marker.TempModValue"));
                    ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
                    var tempMod = player.TempModifier;
                    if (ImGui.InputInt($"##ptemp_val_{player.Hash}", ref tempMod))
                    {
                        player.TempModifier = tempMod;
                        session.BroadcastPlayerUpdate();
                    }
                    ImGui.TextUnformatted(Loc.Get("Marker.TempModTurns"));
                    ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
                    var tempTurns = player.TempModTurns;
                    if (ImGui.InputInt($"##ptemp_turns_{player.Hash}", ref tempTurns))
                    {
                        if (tempTurns < 0) tempTurns = 0;
                        player.TempModTurns = tempTurns;
                        session.BroadcastPlayerUpdate();
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(Loc.Get("Marker.TempModTurnsHint"));
                        ImGui.EndTooltip();
                    }
                    ImGui.Spacing();
                    if (ImGui.SmallButton(Loc.Get("Marker.TempModReset") + $"##ptemp_reset_{player.Hash}"))
                    {
                        player.TempModifier = 0;
                        player.TempModTurns = 0;
                        session.BroadcastPlayerUpdate();
                    }
                    ImGui.EndPopup();
                }
            }
            else
            {
                // Read-only bars
                HpBar.Draw(player.Hp, Attitude.Neutral, availWidth,
                    session.HpMode, hpMax: player.HpMax,
                    shield: session.ShowShield ? player.Shield : 0);

                if (session.ShowMpBar)
                    HpBar.DrawMpBar(player.Mp, availWidth, session.MpMode, player.MpMax);

                if (player.Counters != null)
                {
                    foreach (var counter in player.Counters)
                        CounterBar.Draw(counter, availWidth);
                }
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

        if (ImGui.BeginChild("##models_scroll", Vector2.Zero))
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

            // Afficher le dernier code exporté
            if (lastExportCode != null)
            {
                ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1f),
                    string.Format(Loc.Get("Models.ExportCode"), lastExportCode));
                ImGui.SameLine();
                var copyIcon = FontAwesomeIcon.Copy.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.SmallButton(copyIcon + "##copy_code"))
                        ImGui.SetClipboardText(lastExportCode);
                }
            }
            if (exportInProgress)
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), Loc.Get("Models.Exporting"));

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
                    var mpDisabled = !editingTemplate.ShowMpBar;
                    if (mpDisabled) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.4f);
                    var magicIcon = FontAwesomeIcon.Magic.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(MasterEventTheme.MpBarColor, magicIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(labelColor, Loc.Get("Config.MpMode"));
                    if (mpDisabled) ImGui.PopStyleVar();

                    // Combos row
                    ImGui.SetNextItemWidth(halfWidth);
                    var hpModeIdx = (int)editingTemplate.HpMode;
                    if (ImGui.Combo("##tpl_hp_mode", ref hpModeIdx, hpModeLabels, hpModeLabels.Length))
                        editingTemplate.HpMode = (HpMode)hpModeIdx;
                    ImGui.SameLine();
                    if (mpDisabled) ImGui.BeginDisabled();
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                    var mpModeIdx = (int)editingTemplate.MpMode;
                    if (ImGui.Combo("##tpl_mp_mode", ref mpModeIdx, hpModeLabels, hpModeLabels.Length))
                        editingTemplate.MpMode = (HpMode)mpModeIdx;
                    if (mpDisabled) ImGui.EndDisabled();

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

                    // ── Default HP / MP max ──
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Config.HpMax"));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                    var tplHpMax = editingTemplate.DefaultHpMax;
                    if (ImGui.InputInt("##tpl_hp_max", ref tplHpMax))
                    {
                        if (tplHpMax < 1) tplHpMax = 1;
                        if (tplHpMax > 99999) tplHpMax = 99999;
                        editingTemplate.DefaultHpMax = tplHpMax;
                    }

                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Config.PlayerHpMax"));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                    var tplPlayerHpMax = editingTemplate.DefaultPlayerHpMax;
                    if (ImGui.InputInt("##tpl_player_hp_max", ref tplPlayerHpMax))
                    {
                        if (tplPlayerHpMax < 1) tplPlayerHpMax = 1;
                        if (tplPlayerHpMax > 99999) tplPlayerHpMax = 99999;
                        editingTemplate.DefaultPlayerHpMax = tplPlayerHpMax;
                    }

                    if (mpDisabled) ImGui.BeginDisabled();
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Config.MpMax"));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                    var tplMpMax = editingTemplate.DefaultMpMax;
                    if (ImGui.InputInt("##tpl_mp_max", ref tplMpMax))
                    {
                        if (tplMpMax < 1) tplMpMax = 1;
                        if (tplMpMax > 99999) tplMpMax = 99999;
                        editingTemplate.DefaultMpMax = tplMpMax;
                    }
                    if (mpDisabled) ImGui.EndDisabled();

                    ImGuiHelpers.ScaledDummy(4f);

                    // ── Formule de dé ──
                    var diceIcon = FontAwesomeIcon.Dice.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), diceIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Dice.Formula"));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
                    var tplDiceFormula = editingTemplate.DiceFormula;
                    if (ImGui.InputText("##tpl_dice_formula", ref tplDiceFormula, 16))
                        editingTemplate.DiceFormula = tplDiceFormula;
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(Loc.Get("Dice.FormulaTooltip"));
                        ImGui.EndTooltip();
                    }

                    // ── Stat d'initiative ──
                    ImGuiHelpers.ScaledDummy(2f);
                    var initIcon = FontAwesomeIcon.SortNumericDown.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), initIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Models.InitiativeStat"));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
                    var currentInitStatName = Loc.Get("Models.InitiativeNone");
                    if (editingTemplate.InitiativeStatId != null && editingTemplate.StatDefinitions != null)
                    {
                        var initStat = editingTemplate.StatDefinitions.FirstOrDefault(s => s.Id == editingTemplate.InitiativeStatId);
                        if (initStat != null) currentInitStatName = initStat.Name;
                    }
                    if (ImGui.BeginCombo("##tpl_init_stat", currentInitStatName))
                    {
                        if (ImGui.Selectable(Loc.Get("Models.InitiativeNone"), editingTemplate.InitiativeStatId == null))
                            editingTemplate.InitiativeStatId = null;
                        foreach (var sd in (editingTemplate.StatDefinitions ?? []).Where(sd => ImGui.Selectable(sd.Name, sd.Id == editingTemplate.InitiativeStatId)))
                        {
                            editingTemplate.InitiativeStatId = sd.Id;
                        }
                        ImGui.EndCombo();
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(Loc.Get("Models.InitiativeStatHint"));
                        ImGui.EndTooltip();
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

                    var chartIcon = FontAwesomeIcon.ChartBar.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(MasterEventTheme.AccentColor, chartIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Stats"));
                    ImGuiHelpers.ScaledDummy(2f);

                    var statDefs = editingTemplate.StatDefinitions;
                    if (statDefs != null)
                    {
                        for (var si = 0; si < statDefs.Count; si++)
                        {
                            var sd = statDefs[si];
                            ImGui.PushID(1000 + si);
                            ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
                            var sdName = sd.Name;
                            if (ImGui.InputTextWithHint("##sd_name", Loc.Get("Stat.Name"), ref sdName, 32))
                                sd.Name = sdName;
                            ImGui.SameLine();
                            var xIcon = FontAwesomeIcon.Times.ToIconString();
                            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                            {
                                if (ImGui.Button(xIcon + "##sd_del"))
                                {
                                    statDefs.RemoveAt(si);
                                    if (statDefs.Count == 0) editingTemplate.StatDefinitions = null;
                                }
                            }
                            ImGui.PopID();
                        }
                    }

                    var plusStatIcon = FontAwesomeIcon.Plus.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    {
                        if (ImGui.Button(plusStatIcon + "##add_sd_btn"))
                        {
                            editingTemplate.StatDefinitions ??= new List<StatDefinition>();
                            editingTemplate.StatDefinitions.Add(new StatDefinition());
                        }
                    }
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Models.AddStat"));

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

                    // Export button
                    DrawExportButtonByName(tplName);
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

            ImGuiHelpers.ScaledDummy(4f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);

            var importIcon = FontAwesomeIcon.Download.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(MasterEventTheme.AccentColor, importIcon);
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Import"));
            ImGuiHelpers.ScaledDummy(2f);

            if (importInProgress) ImGui.BeginDisabled();
            var importWidth = ImGui.GetContentRegionAvail().X;
            ImGui.SetNextItemWidth(importWidth - 40f * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint("##import_code", Loc.Get("Models.ImportCode"), ref importCode, 16);
            ImGui.SameLine();
            var dlIconStr = FontAwesomeIcon.Download.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                var canImport = !string.IsNullOrWhiteSpace(importCode);
                if (!canImport) ImGui.BeginDisabled();
                if (ImGui.Button(dlIconStr + "##do_import"))
                {
                    importInProgress = true;
                    var code = importCode.Trim();
                    _ = Task.Run(async () =>
                    {
                        var template = await SessionManager.ImportTemplateAsync(code, configuration.RelayServerUrl);
                        importInProgress = false;
                        if (template != null)
                        {
                            session.SaveTemplate(template);
                            Plugin.ChatGui.Print(string.Format(Loc.Get("Models.Imported"), template.Name));
                            importCode = string.Empty;
                        }
                        else
                        {
                            Plugin.ChatGui.Print(Loc.Get("Models.ImportError"));
                        }
                    });
                }
                if (!canImport) ImGui.EndDisabled();
            }
            if (importInProgress) ImGui.EndDisabled();
            if (importInProgress)
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), Loc.Get("Models.Importing"));
        }
        ImGui.EndChild();
    }

    private void DrawExportButtonByName(string templateName)
    {
        if (exportInProgress) ImGui.BeginDisabled();

        var exportIcon = FontAwesomeIcon.Upload.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(exportIcon + "##export_" + templateName))
                ImGui.OpenPopup("##export_popup_" + templateName);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Models.ExportTooltip"));
            ImGui.EndTooltip();
        }

        if (exportInProgress) ImGui.EndDisabled();

        if (ImGui.BeginPopup("##export_popup_" + templateName))
        {
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Export"));
            ImGui.Separator();

            ImGui.Checkbox(Loc.Get("Models.ExportPermanent") + "##perm_" + templateName, ref exportPermanent);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Models.ExportPermanentTooltip"));
                ImGui.EndTooltip();
            }

            if (ImGui.Button(Loc.Get("Models.Export") + "##do_export_" + templateName))
            {
                var tpl = session.LoadTemplate(templateName);
                if (tpl != null)
                {
                    exportInProgress = true;
                    lastExportCode = null;
                    var perm = exportPermanent;
                    _ = Task.Run(async () =>
                    {
                        var code = await SessionManager.ExportTemplateAsync(tpl, configuration.RelayServerUrl, perm);
                        exportInProgress = false;
                        if (code != null)
                        {
                            lastExportCode = code;
                            ImGui.SetClipboardText(code);
                            Plugin.ChatGui.Print(Loc.Get("Models.Exported"));
                        }
                        else
                        {
                            Plugin.ChatGui.Print(Loc.Get("Models.ImportError"));
                        }
                    });
                }
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    // ────────── Profiles content ──────────

    private void DrawProfilesContent()
    {
        if (ImGui.BeginChild("##profiles_scroll", Vector2.Zero))
        {
            if (editingProfile != null)
                DrawProfileEditor();
            else
                DrawProfilesMain();
        }
        ImGui.EndChild();
    }

    private void DrawProfilesMain()
    {
        var availWidth = ImGui.GetContentRegionAvail().X;

        // Header
        ImGuiHelpers.ScaledDummy(6f);
        var iconStr = FontAwesomeIcon.Scroll.ToIconString();
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
        var titleSz = ImGui.CalcTextSize(Loc.Get("Player.Sheet"));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - titleSz.X) / 2f);
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Player.Sheet"));
        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        //Créer un profil
        var templateNames = session.GetTemplateNames();
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Player.CreateProfile"));
        ImGuiHelpers.ScaledDummy(2f);

        ImGui.SetNextItemWidth(availWidth);
        ImGui.InputTextWithHint("##profile_name", Loc.Get("Player.ProfileName"), ref newProfileName, 64);

        ImGui.SetNextItemWidth(availWidth);
        if (ImGui.BeginCombo("##tpl_select", string.IsNullOrEmpty(selectedTemplateName) ? Loc.Get("Player.SelectTemplate") : selectedTemplateName))
        {
            foreach (var tplName in templateNames.Where(t => ImGui.Selectable(t, t == selectedTemplateName)))
            {
                selectedTemplateName = tplName;
            }
            ImGui.EndCombo();
        }

        var canCreate = !string.IsNullOrWhiteSpace(newProfileName) && !string.IsNullOrEmpty(selectedTemplateName);
        if (!canCreate) ImGui.BeginDisabled();
        if (ImGui.Button(Loc.Get("Player.CreateProfile") + "##do_create", new Vector2(availWidth, 0)))
        {
            var tpl = session.LoadTemplate(selectedTemplateName);
            if (tpl != null)
            {
                var profile = PlayerSheet.FromTemplate(tpl, newProfileName.Trim());
                editingProfile = profile;
                editingDirty = true;
                newProfileName = string.Empty;
            }
        }
        if (!canCreate) ImGui.EndDisabled();

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        // Mes profils
        DrawGmProfilesList();
    }

    private void DrawGmProfilesList()
    {
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Player.MyProfiles"));
        ImGuiHelpers.ScaledDummy(2f);

        var sheetNames = session.GetPlayerSheetNames();
        if (sheetNames.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Player.NoProfiles"));
            return;
        }

        string? toDelete = null;
        foreach (var name in sheetNames)
        {
            var sheet = session.LoadPlayerSheet(name);
            if (sheet == null) continue;

            var isDefault = configuration.DefaultSheetName == name;
            var userIcon = FontAwesomeIcon.User.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(new Vector4(0.227f, 0.604f, 1f, 0.8f), userIcon);
            ImGui.SameLine();
            ImGui.TextUnformatted($"{name} - {sheet.TemplateName}");

            var btnSize = new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight());

            // Définir par défaut
            var starIcon = FontAwesomeIcon.Star.ToIconString();
            var starColor = isDefault
                ? new Vector4(1f, 0.85f, 0.2f, 1f)
                : new Vector4(1f, 1f, 1f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Text, starColor);
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                if (ImGui.Button(starIcon + $"##default_{name}", btnSize))
                {
                    configuration.DefaultSheetName = isDefault ? null : name;
                    configuration.Save();
                }
            }
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(isDefault ? Loc.Get("Player.UnsetDefault") : Loc.Get("Player.SetDefault"));
                ImGui.EndTooltip();
            }
            ImGui.SameLine();

            // Charger
            var loadIcon = FontAwesomeIcon.Play.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                if (ImGui.Button(loadIcon + $"##load_{name}", btnSize))
                {
                    session.ApplyPlayerSheet(sheet);
                    Plugin.ChatGui.Print(string.Format(Loc.Get("Player.ProfileLoaded"), name));
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Player.LoadSheet"));
                ImGui.EndTooltip();
            }
            ImGui.SameLine();

            // Éditer
            var editBtnIcon = FontAwesomeIcon.Pen.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                if (ImGui.Button(editBtnIcon + $"##edit_{name}", btnSize))
                {
                    editingProfile = sheet.DeepCopy();
                    editingDirty = false;
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Player.EditProfile"));
                ImGui.EndTooltip();
            }
            ImGui.SameLine();

            // Supprimer
            var trashIcon = FontAwesomeIcon.Trash.ToIconString();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                if (ImGui.Button(trashIcon + $"##del_{name}", btnSize))
                    toDelete = name;
            }
            ImGui.PopStyleColor();

            ImGui.Spacing();
        }

        if (toDelete != null)
        {
            session.DeletePlayerSheet(toDelete);
            if (configuration.DefaultSheetName == toDelete)
            {
                configuration.DefaultSheetName = null;
                configuration.Save();
            }
            Plugin.ChatGui.Print(Loc.Get("Player.ProfileDeleted"));
        }
    }

    private void DrawProfileEditor()
    {
        var profile = editingProfile!;
        var fieldWidth = ImGui.GetContentRegionAvail().X;

        // Titre
        var penIcon = FontAwesomeIcon.Pen.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.AccentColor, penIcon);
        ImGui.SameLine();
        var dirtyMarker = editingDirty ? " *" : "";
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Player.EditProfile"));
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.227f, 0.604f, 1f, 0.8f), profile.Name + dirtyMarker);
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), $"[{profile.TemplateName}]");

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        //  PV
        var heartIcon = FontAwesomeIcon.Heart.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.AttitudeHostile, heartIcon);
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Config.HpMax"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
        var hpMax = profile.HpMax;
        if (ImGui.InputInt("##prof_hpmax", ref hpMax))
        {
            if (hpMax < 1) hpMax = 1;
            profile.HpMax = hpMax;
            if (profile.Hp > hpMax) profile.Hp = hpMax;
            editingDirty = true;
        }

        //  PE
        var linkedTpl = session.LoadTemplate(profile.TemplateName);
        var profMpDisabled = linkedTpl != null && !linkedTpl.ShowMpBar;
        if (profMpDisabled) ImGui.BeginDisabled();
        var magicIcon = FontAwesomeIcon.Magic.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.MpBarColor, magicIcon);
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Loc.Get("Config.MpMax"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
        var mpMax = profile.MpMax;
        if (ImGui.InputInt("##prof_mpmax", ref mpMax))
        {
            if (mpMax < 1) mpMax = 1;
            profile.MpMax = mpMax;
            if (profile.Mp > mpMax) profile.Mp = mpMax;
            editingDirty = true;
        }
        if (profMpDisabled) ImGui.EndDisabled();

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        // Stats
        if (profile.Stats != null && profile.Stats.Count > 0)
        {
            var chartIcon = FontAwesomeIcon.ChartBar.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(MasterEventTheme.AccentColor, chartIcon);
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Stats"));
            ImGuiHelpers.ScaledDummy(2f);

            var labelWidth = 0f;
            foreach (var stat in profile.Stats)
            {
                var w = ImGui.CalcTextSize(stat.Name).X;
                if (w > labelWidth) labelWidth = w;
            }
            labelWidth += 8f * ImGuiHelpers.GlobalScale;

            foreach (var stat in profile.Stats)
            {
                ImGui.TextUnformatted(stat.Name);
                ImGui.SameLine(labelWidth);
                ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
                var mod = stat.Modifier;
                if (ImGui.InputInt($"##pstat_{stat.Id}", ref mod))
                {
                    stat.Modifier = mod;
                    editingDirty = true;
                }
                ImGui.SameLine();
                var modStr = mod >= 0 ? $"+{mod}" : mod.ToString();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), $"({modStr})");
            }
        }

        // Compteurs
        if (profile.Counters != null && profile.Counters.Count > 0)
        {
            ImGuiHelpers.ScaledDummy(4f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);

            var listIcon = FontAwesomeIcon.ListUl.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(MasterEventTheme.AccentColor, listIcon);
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Counters"));
            ImGuiHelpers.ScaledDummy(2f);

            foreach (var counter in profile.Counters)
            {
                ImGui.TextUnformatted(counter.Name);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(60f * ImGuiHelpers.GlobalScale);
                var val = counter.Value;
                if (ImGui.InputInt($"##pcnt_{counter.Name}", ref val))
                {
                    if (val < 0) val = 0;
                    if (val > counter.Max) val = counter.Max;
                    counter.Value = val;
                    editingDirty = true;
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), $"/ {counter.Max}");
            }
        }

        // Boutons
        ImGuiHelpers.ScaledDummy(8f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        var btnWidth = (fieldWidth - ImGui.GetStyle().ItemSpacing.X * 2) / 3f;

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.2f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.6f, 0.25f, 1f));
        if (ImGui.Button(Loc.Get("Gm.Save") + "##save_prof", new Vector2(btnWidth, 0)))
        {
            session.SavePlayerSheet(profile);
            Plugin.ChatGui.Print(string.Format(Loc.Get("Player.ProfileSaved"), profile.Name));
            editingProfile = null;
        }
        ImGui.PopStyleColor(2);

        ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.35f, 0.6f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.4f, 0.7f, 1f));
        if (ImGui.Button(Loc.Get("Player.LoadSheet") + "##apply_prof", new Vector2(btnWidth, 0)))
        {
            session.SavePlayerSheet(profile);
            session.ApplyPlayerSheet(profile);
            Plugin.ChatGui.Print(string.Format(Loc.Get("Player.ProfileLoaded"), profile.Name));
            editingProfile = null;
        }
        ImGui.PopStyleColor(2);

        ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.5f, 0.2f, 0.2f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.6f, 0.25f, 0.25f, 1f));
        if (ImGui.Button(Loc.Get("Gm.Cancel") + "##back_prof", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
        {
            editingProfile = null;
        }
        ImGui.PopStyleColor(2);
    }

    //Settings content

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
        if (ImGui.BeginChild("##settings_content", Vector2.Zero))
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

    private static void DrawSectionHeader(int tabIndex)
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

        ImGuiHelpers.ScaledDummy(4f);

        var autoOpen = configuration.AutoOpenPlayerWindow;
        if (ImGui.Checkbox(Loc.Get("General.AutoOpenPlayerWindow"), ref autoOpen))
        {
            configuration.AutoOpenPlayerWindow = autoOpen;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("General.AutoOpenPlayerWindow.Tooltip"));
            ImGui.EndTooltip();
        }

        if (ImGui.Button(Loc.Get("General.ShowPlayerWindow")))
        {
            if (PlayerWindowRef is { } playerWindow)
                playerWindow.IsOpen = !playerWindow.IsOpen;
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

        var buildLine = $"Build : {Constants.PluginBuild}";
        var buildSz = ImGui.CalcTextSize(buildLine);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - buildSz.X) / 2f);
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), buildLine);

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

        // Ashfall Codex branding
        ImGuiHelpers.ScaledDummy(16f);

        var sepStart = ImGui.GetCursorScreenPos();
        var sepW = 120f * ImGuiHelpers.GlobalScale;
        var sepX2 = sepStart.X + (availW - sepW) / 2f;
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(sepX2, sepStart.Y),
            new Vector2(sepX2 + sepW, sepStart.Y),
            ImGui.GetColorU32(new Vector4(0.831f, 0.686f, 0.416f, 0.12f)), 1f);
        ImGuiHelpers.ScaledDummy(16f);

        var logoSize = 72f * ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - logoSize) / 2f);
        var logoPos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(logoSize, logoSize));
        DrawAshfallCodexLogo(new Vector2(logoPos.X + logoSize / 2f, logoPos.Y + logoSize / 2f), logoSize);

        ImGuiHelpers.ScaledDummy(4f);

        var brandName = "Ashfall Codex";
        var brandSz = ImGui.CalcTextSize(brandName);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.831f, 0.686f, 0.416f, 0.4f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.831f, 0.384f, 0.165f, 0.2f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.831f, 0.384f, 0.165f, 0.3f));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - brandSz.X) / 2f);
        if (ImGui.Selectable(brandName, false, ImGuiSelectableFlags.None, brandSz))
            Dalamud.Utility.Util.OpenLink("https://ashfall-codex.dev/");
        ImGui.PopStyleColor(3);
    }

    private static void DrawAshfallCodexLogo(Vector2 center, float size)
    {
        var dl = ImGui.GetWindowDrawList();
        var s = size / 512f;
        var ox = center.X - 256f * s;
        var oy = center.Y - 256f * s;
        var t = ImGui.GetTime();
        var cxVb = 261f;
        var cyVb = 256f;
        var logoCenter = new Vector2(ox + cxVb * s, oy + cyVb * s);
        var glowPulse = 0.5f + 0.3f * MathF.Sin((float)(t * Math.PI * 2.0 / 2.5));
        var haloR = 80f * s;
        for (var ring = 5; ring >= 0; ring--)
        {
            var r = haloR * (1f + ring * 0.5f);
            var a = 0.04f * glowPulse * (1f - ring / 6f);
            dl.AddCircleFilled(logoCenter, r,
                ImGui.GetColorU32(new Vector4(0.831f, 0.384f, 0.165f, a)), 48);
        }

        dl.AddCircle(new Vector2(ox + 256f * s, oy + 256f * s), 241f * s,
            ImGui.GetColorU32(new Vector4(0.831f, 0.686f, 0.416f, 0.25f)), 64, MathF.Max(1.5f, 4.4f * s));
        var bookR = MathF.Max(2f, 11f * s);
        var bookMin = new Vector2(ox + 150f * s, oy + 117f * s);
        var bookMax = new Vector2(ox + 362f * s, oy + 395f * s);
        dl.AddRectFilled(bookMin, bookMax,
            ImGui.GetColorU32(new Vector4(0.075f, 0.075f, 0.082f, 1f)), bookR);
        dl.AddRect(bookMin, bookMax,
            ImGui.GetColorU32(new Vector4(0.831f, 0.686f, 0.416f, 0.85f)), bookR, ImDrawFlags.None, MathF.Max(1.5f, 6.6f * s));

        dl.AddRectFilled(
            new Vector2(ox + 150f * s, oy + 117f * s),
            new Vector2(ox + 176f * s, oy + 395f * s),
            ImGui.GetColorU32(new Vector4(0.102f, 0.102f, 0.118f, 0.7f)), bookR);
        dl.AddLine(
            new Vector2(ox + 176f * s, oy + 125f * s),
            new Vector2(ox + 176f * s, oy + 387f * s),
            ImGui.GetColorU32(new Vector4(0.831f, 0.686f, 0.416f, 0.3f)), MathF.Max(1f, 1.5f * s));

        var d1 = new Vector2(ox + cxVb * s, oy + 212f * s);
        var d2 = new Vector2(ox + 305f * s, oy + cyVb * s);
        var d3 = new Vector2(ox + cxVb * s, oy + 300f * s);
        var d4 = new Vector2(ox + 217f * s, oy + cyVb * s);
        var outerThk = MathF.Max(1.5f, 5.5f * s);
        var outerCol = ImGui.GetColorU32(new Vector4(0.831f, 0.384f, 0.165f, 0.85f));
        dl.AddLine(d1, d2, outerCol, outerThk);
        dl.AddLine(d2, d3, outerCol, outerThk);
        dl.AddLine(d3, d4, outerCol, outerThk);
        dl.AddLine(d4, d1, outerCol, outerThk);

        var i1 = new Vector2(ox + cxVb * s, oy + 229f * s);
        var i2 = new Vector2(ox + 288f * s, oy + cyVb * s);
        var i3 = new Vector2(ox + cxVb * s, oy + 283f * s);
        var i4 = new Vector2(ox + 234f * s, oy + cyVb * s);
        var innerThk = MathF.Max(1.2f, 3.7f * s);
        var innerCol = ImGui.GetColorU32(new Vector4(0.941f, 0.565f, 0.259f, 0.65f));
        dl.AddLine(i1, i2, innerCol, innerThk);
        dl.AddLine(i2, i3, innerCol, innerThk);
        dl.AddLine(i3, i4, innerCol, innerThk);
        dl.AddLine(i4, i1, innerCol, innerThk);

        var emberR = MathF.Max(2f, 11f * s);
        for (var g = 3; g >= 0; g--)
        {
            var r = emberR * (1f + g * 0.8f);
            var a = 0.12f * glowPulse * (1f - g / 4f);
            dl.AddCircleFilled(logoCenter, r,
                ImGui.GetColorU32(new Vector4(0.941f, 0.565f, 0.259f, a)), 32);
        }
        dl.AddCircleFilled(logoCenter, emberR,
            ImGui.GetColorU32(new Vector4(0.941f, 0.565f, 0.259f, 0.5f + 0.35f * glowPulse)), 32);

        var cThk = MathF.Max(1.2f, 2.9f * s);
        var cCol = ImGui.GetColorU32(new Vector4(0.831f, 0.686f, 0.416f, 0.4f));
        dl.AddLine(new Vector2(ox + 187f * s, oy + 128f * s), new Vector2(ox + 187f * s, oy + 146f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 187f * s, oy + 146f * s), new Vector2(ox + 206f * s, oy + 146f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 342f * s, oy + 128f * s), new Vector2(ox + 342f * s, oy + 146f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 342f * s, oy + 146f * s), new Vector2(ox + 324f * s, oy + 146f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 187f * s, oy + 383f * s), new Vector2(ox + 187f * s, oy + 365f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 187f * s, oy + 365f * s), new Vector2(ox + 206f * s, oy + 365f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 342f * s, oy + 383f * s), new Vector2(ox + 342f * s, oy + 365f * s), cCol, cThk);
        dl.AddLine(new Vector2(ox + 342f * s, oy + 365f * s), new Vector2(ox + 324f * s, oy + 365f * s), cCol, cThk);
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
