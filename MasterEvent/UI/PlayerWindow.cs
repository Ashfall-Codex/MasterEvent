using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

public sealed class PlayerWindow : MasterEventWindowBase
{
    private enum PlayerTab { Overview, Dice }

    private const float SidebarWidth = 44f;
    private const float SidebarButtonSize = 32f;

    private readonly SessionManager session;
    private readonly IPlayerState playerState;
    private readonly Configuration configuration;
    private readonly Action<string>? onJoinAlliance;
    private readonly Action? onLeaveAlliance;
    private PlayerTab activeTab = PlayerTab.Overview;
    private string selectedSheetName = string.Empty;
    private string allianceCodeInput = string.Empty;

    public PlayerWindow(SessionManager session, IPlayerState playerState, Configuration configuration,
        Action<string>? onJoinAlliance = null, Action? onLeaveAlliance = null)
        : base("MasterEvent###MasterEventPlayer")
    {
        this.session = session;
        this.playerState = playerState;
        this.configuration = configuration;
        this.onJoinAlliance = onJoinAlliance;
        this.onLeaveAlliance = onLeaveAlliance;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 250),
        };
    }

    protected override void DrawContents()
    {
        var sidebarW = SidebarWidth * ImGuiHelpers.GlobalScale;
        if (ImGui.BeginChild("##player_sidebar", new Vector2(sidebarW, 0), false, ImGuiWindowFlags.NoScrollbar))
        {
            DrawSidebar();
        }
        ImGui.EndChild();

        ImGui.SameLine();

        // Ligne de séparation
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

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f * ImGuiHelpers.GlobalScale);

        if (ImGui.BeginChild("##player_content", Vector2.Zero))
        {
            switch (activeTab)
            {
                case PlayerTab.Overview:
                    DrawOverviewContent();
                    break;
                case PlayerTab.Dice:
                    DrawDiceContent();
                    break;
            }
        }
        ImGui.EndChild();
    }

    private void DrawSidebar()
    {
        var title = Loc.Get("Player.Title");
        var words = title.Split(' ');
        var sidebarW = SidebarWidth * ImGuiHelpers.GlobalScale;
        var fontSize = ImGui.GetFontSize() * 1.0f;

        foreach (var word in words)
        {
            var wordW = ImGui.CalcTextSize(word).X * (fontSize / ImGui.GetFontSize());
            var wordX = (sidebarW - wordW) / 2f;
            var dl = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            dl.AddText(ImGui.GetFont(), fontSize,
                new Vector2(pos.X + wordX, pos.Y),
                ImGui.GetColorU32(MasterEventTheme.AccentColor), word);
            ImGui.Dummy(new Vector2(0, fontSize + 1f * ImGuiHelpers.GlobalScale));
        }

        ImGui.Spacing();

        DrawSidebarButton(FontAwesomeIcon.List, PlayerTab.Overview, Loc.Get("Player.OverviewTab"));
        ImGui.Spacing();
        ImGui.Spacing();

        DrawSidebarButton(FontAwesomeIcon.Dice, PlayerTab.Dice, Loc.Get("Player.RollDice"));
    }

    private void DrawSidebarButton(FontAwesomeIcon icon, PlayerTab tab, string tooltip)
    {
        var isActive = activeTab == tab;
        var size = SidebarButtonSize * ImGuiHelpers.GlobalScale;
        var availW = ImGui.GetContentRegionAvail().X;
        var offset = Math.Max(0f, (availW - size) / 2f);

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);

        if (isActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, MasterEventTheme.AccentColor with { W = 0.5f });
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, MasterEventTheme.AccentColor with { W = 0.7f });
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, MasterEventTheme.AccentColor with { W = 0.9f });
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.15f, 0.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.25f, 0.25f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.3f, 0.3f, 0.3f, 1f));
        }
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f * ImGuiHelpers.GlobalScale);

        var iconStr = icon.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(iconStr + "##ptab_" + tab, new Vector2(size, size)))
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

    // Onglet Vue d'ensemble

    private void DrawOverviewContent()
    {
        // Section Mode Alliance
        if (session.IsAllianceMode)
        {
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Alliance.Connected"));
            ImGui.SameLine();
            ImGui.TextUnformatted(session.AllianceRoomCode);
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.7f, 0.2f, 0.2f, 1f));
            if (ImGui.SmallButton(Loc.Get("Alliance.Leave") + "##leave_alliance"))
                onLeaveAlliance?.Invoke();
            ImGui.PopStyleColor(2);
            ImGuiHelpers.ScaledDummy(4f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);
        }
        else
        {
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), Loc.Get("Alliance.JoinLabel"));
            var availWidth = ImGui.GetContentRegionAvail().X;
            ImGui.SetNextItemWidth(availWidth * 0.5f);
            ImGui.InputTextWithHint("##alliance_code", "ABC123", ref allianceCodeInput, 6);
            ImGui.SameLine();
            var canJoin = allianceCodeInput.Length >= 6;
            if (!canJoin) ImGui.BeginDisabled();
            if (ImGui.Button(Loc.Get("Alliance.Join") + "##join_alliance"))
            {
                onJoinAlliance?.Invoke(allianceCodeInput);
                allianceCodeInput = string.Empty;
            }
            if (!canJoin) ImGui.EndDisabled();
            ImGuiHelpers.ScaledDummy(4f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);
        }

        var state = session.CurrentTurnState;
        var turnsActive = state is { IsActive: true } && state.Entries.Count > 0;

        if (!turnsActive)
        {
            DrawLocalPlayerCard();
        }

        if (turnsActive)
        {
            DrawCombinedTurnView(state!);
        }
        else
        {
            DrawPlainMarkerList();
        }
    }

    // Onglet Dés

    private void DrawDiceContent()
    {
        var availWidth = ImGui.GetContentRegionAvail().X;

        ImGuiHelpers.ScaledDummy(6f);

        // Header
        var iconStr = FontAwesomeIcon.Dice.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSz = ImGui.CalcTextSize(iconStr);
        const float scale = 1.6f;
        var scaledSz = iconSz * scale;
        var pos = ImGui.GetCursorScreenPos();
        var iconX = pos.X + (availWidth - scaledSz.X) / 2f;
        ImGui.Dummy(new Vector2(0, scaledSz.Y));
        var dl = ImGui.GetWindowDrawList();
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, new Vector2(iconX, pos.Y),
            ImGui.GetColorU32(MasterEventTheme.AccentColor), iconStr);
        ImGui.PopFont();
        ImGuiHelpers.ScaledDummy(4f);

        var titleSz = ImGui.CalcTextSize(Loc.Get("Player.RollDice"));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - titleSz.X) / 2f);
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Player.RollDice"));
        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        // Sélection de la fiche (filtrée par le modèle actif du MJ)
        var activeTemplateName = session.ActiveTemplate?.Name;
        var allSheetNames = session.GetPlayerSheetNames();
        var filteredSheets = new System.Collections.Generic.List<(string Name, PlayerSheet Sheet)>();
        foreach (var name in allSheetNames)
        {
            var sheet = session.LoadPlayerSheet(name);
            if (sheet != null && (activeTemplateName == null || sheet.TemplateName == activeTemplateName))
                filteredSheets.Add((name, sheet));
        }

        if (activeTemplateName != null)
        {
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), activeTemplateName);
            ImGuiHelpers.ScaledDummy(2f);
        }

        // Réinitialiser la sélection si la fiche active n'est plus dans la liste filtrée
        if (!string.IsNullOrEmpty(selectedSheetName) && filteredSheets.All(s => s.Name != selectedSheetName))
            selectedSheetName = string.Empty;

        // Pré-sélectionner la fiche par défaut dans le combo si pas encore sélectionnée
        if (string.IsNullOrEmpty(selectedSheetName))
        {
            var defaultName = configuration.DefaultSheetName;
            if (!string.IsNullOrEmpty(defaultName) && filteredSheets.Any(s => s.Name == defaultName))
                selectedSheetName = defaultName;
        }

        if (filteredSheets.Count > 0)
        {
            var comboLabel = string.IsNullOrEmpty(selectedSheetName)
                ? Loc.Get("Player.SelectSheet")
                : selectedSheetName;
            ImGui.SetNextItemWidth(availWidth);
            if (ImGui.BeginCombo("##sheet_select", comboLabel))
            {
                foreach (var (name, sheet) in filteredSheets)
                {
                    if (ImGui.Selectable(name, name == selectedSheetName))
                    {
                        selectedSheetName = name;
                        session.ApplyPlayerSheet(sheet);
                    }
                }
                ImGui.EndCombo();
            }
            ImGuiHelpers.ScaledDummy(4f);
        }
        else if (activeTemplateName != null)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Player.NoProfiles"));
            ImGuiHelpers.ScaledDummy(4f);
        }

        var localHash = Plugin.GeneratePlayerHash(playerState.ContentId);
        var localPlayer = session.PartyMembers.FirstOrDefault(p => p.Hash == localHash);

        if (ImGui.BeginChild("##dice_scroll", Vector2.Zero))
        {
            // Grille de boutons de jet
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            var columns = 3;
            var tileSize = (availWidth - spacing * (columns - 1)) / columns;
            var tileH = tileSize * 0.75f;
            var idx = 0;

            // Bouton jet simple
            DrawDiceTile(Loc.Get("Dice.NoStat"), null, "roll_simple", tileSize, tileH, () =>
                session.RollDiceForPlayer(localHash));
            idx++;

            // Boutons par stat
            if (localPlayer?.Stats != null && localPlayer.Stats.Count > 0)
            {
                foreach (var stat in localPlayer.Stats)
                {
                    if (idx % columns != 0)
                        ImGui.SameLine();

                    var modStr = stat.Modifier >= 0 ? $"+{stat.Modifier}" : stat.Modifier.ToString();
                    var statId = stat.Id;

                    DrawDiceTile(stat.Name, modStr, "roll_" + stat.Id, tileSize, tileH, () =>
                        session.RollDiceForPlayer(localHash, statId));
                    idx++;
                }
            }

            ImGuiHelpers.ScaledDummy(4f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);

            // Historique des jets
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Dice.History"));
            ImGuiHelpers.ScaledDummy(2f);

            if (session.RollHistory.Count == 0)
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Dice.NoHistory"));
            }
            else
            {
                for (var i = 0; i < session.RollHistory.Count && i < 20; i++)
                {
                    var roll = session.RollHistory[i];
                    var rollModStr = roll.Modifier >= 0 ? $"+{roll.Modifier}" : roll.Modifier.ToString();
                    var statInfo = roll.StatName != null ? $" [{roll.StatName} {rollModStr}]" : "";
                    var line = $"{roll.RollerName}: {roll.RawRoll}/{roll.DiceMax}{statInfo} = {roll.Total}";

                    // Mettre en valeur le dernier jet
                    if (i == 0)
                        ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), line);
                    else
                        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), line);
                }

                ImGuiHelpers.ScaledDummy(4f);
                if (ImGui.SmallButton(Loc.Get("Dice.ClearHistory")))
                    session.ClearRollHistory();
            }
        }
        ImGui.EndChild();
    }

    // Carte joueur local

    private void DrawLocalPlayerCard()
    {
        var localHash = Plugin.GeneratePlayerHash(playerState.ContentId);
        var localPlayer = session.PartyMembers.FirstOrDefault(p => p.Hash == localHash);
        if (localPlayer == null) return;

        var cardWidth = ImGui.GetContentRegionAvail().X;
        var extraRows = 0;
        if (session.ShowMpBar) extraRows++;
        if (localPlayer.Counters != null) extraRows += localPlayer.Counters.Count;
        if (localPlayer.Stats != null && localPlayer.Stats.Count > 0) extraRows++;
        if (localPlayer.TempModifier != 0) extraRows++;
        var cardHeight = ImGui.GetFrameHeightWithSpacing() * (2 + extraRows) + ImGui.GetStyle().WindowPadding.Y * 2;

        var playerBlue = new Vector4(0.227f, 0.604f, 1f, 0.8f);
        ImGui.PushStyleColor(ImGuiCol.Border, playerBlue);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);

        if (ImGui.BeginChild("##player_hp_card", new Vector2(cardWidth, cardHeight), true))
        {
            var userIcon = FontAwesomeIcon.User.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                ImGui.TextColored(playerBlue, userIcon);
            }
            ImGui.SameLine();

            var nameWidth = ImGui.CalcTextSize(localPlayer.Name).X;
            var nameX = (cardWidth - nameWidth) / 2f;
            var minX = ImGui.GetCursorPosX();
            if (nameX > minX)
                ImGui.SetCursorPosX(nameX);
            ImGui.TextUnformatted(localPlayer.Name);

            HpBar.Draw(localPlayer.Hp, Attitude.Neutral, ImGui.GetContentRegionAvail().X,
                session.HpMode, hpMax: localPlayer.HpMax,
                shield: session.ShowShield ? localPlayer.Shield : 0);

            if (session.ShowMpBar)
                HpBar.DrawMpBar(localPlayer.Mp, ImGui.GetContentRegionAvail().X, session.MpMode, localPlayer.MpMax);

            if (localPlayer.Counters != null)
            {
                foreach (var counter in localPlayer.Counters)
                    CounterBar.Draw(counter, ImGui.GetContentRegionAvail().X);
            }

            // Bouton stats avec popup en lecture seule
            if (localPlayer.Stats != null && localPlayer.Stats.Count > 0)
            {
                var statsIcon = FontAwesomeIcon.ChartBar.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.Button($"{statsIcon}##pstats_view"))
                        ImGui.OpenPopup("##pstats_popup");
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(Loc.Get("Player.ShowStats"));
                    ImGui.EndTooltip();
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
                    $"{Loc.Get("Models.Stats")} ({localPlayer.Stats.Count})");

                if (ImGui.BeginPopup("##pstats_popup"))
                {
                    ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Models.Stats"));
                    ImGui.Separator();
                    foreach (var stat in localPlayer.Stats)
                    {
                        ImGui.TextUnformatted(stat.Name);
                        ImGui.SameLine();
                        var modStr = stat.Modifier >= 0 ? $"+{stat.Modifier}" : stat.Modifier.ToString();
                        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), modStr);
                    }
                    ImGui.EndPopup();
                }
            }

            // Affichage du bonus/malus temporaire
            if (localPlayer.TempModifier != 0)
            {
                var tempIcon = FontAwesomeIcon.Magic.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(new Vector4(0.8f, 0.6f, 1f, 1f), tempIcon);
                ImGui.SameLine();
                var tempStr = localPlayer.TempModifier >= 0 ? $"+{localPlayer.TempModifier}" : localPlayer.TempModifier.ToString();
                var tempColor = localPlayer.TempModifier > 0
                    ? new Vector4(0.2f, 0.8f, 0.2f, 1f)
                    : new Vector4(1f, 0.4f, 0.4f, 1f);
                ImGui.TextColored(tempColor, $"{Loc.Get("Marker.TempMod")}: {tempStr}");
                if (localPlayer.TempModTurns > 0)
                {
                    ImGui.SameLine(0, 4f * ImGuiHelpers.GlobalScale);
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"({localPlayer.TempModTurns}t)");
                }
            }
        }
        ImGui.EndChild();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();

        ImGuiHelpers.ScaledDummy(4f);
    }

    //  Vue des tours

    private void DrawCombinedTurnView(TurnState state)
    {
        var roundText = string.Format(Loc.Get("Turns.Round"), state.Round);
        ImGui.TextColored(MasterEventTheme.AccentColor, roundText);
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), $"(d{state.DiceMax})");

        var actedCount = state.Entries.Count(e => e.HasActed);
        var progressText = string.Format(Loc.Get("Turns.Progress"), actedCount, state.Entries.Count);
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), progressText);

        ImGuiHelpers.ScaledDummy(4f);

        var nextIndex = -1;
        for (var j = 0; j < state.Entries.Count; j++)
        {
            if (!state.Entries[j].HasActed) { nextIndex = j; break; }
        }

        for (var i = 0; i < state.Entries.Count; i++)
        {
            var entry = state.Entries[i];
            var isNext = i == nextIndex;
            var indicator = GetTurnIndicator(entry, isNext);

            if (entry.HasActed)
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.45f);

            if (entry.IsMarker && entry.WaymarkIndex.HasValue)
            {
                var waymarkId = (WaymarkId)entry.WaymarkIndex.Value;
                var marker = session.CurrentMarkers[waymarkId];

                if (!marker.IsVisible || string.IsNullOrEmpty(marker.Name))
                    DrawTurnLine(entry, isNext);
                else
                    MarkerCard.DrawReadOnly(waymarkId, marker, session.HpMode,
                        session.ShowShield, session.ShowMpBar, session.MpMode,
                        turnIndicator: indicator, initiative: entry.Initiative,
                        initRoll: entry.InitiativeRoll, initMod: entry.InitiativeModifier,
                        initStatName: entry.InitiativeStatName);
            }
            else
            {
                DrawPlayerTurnCard(i, entry, isNext);
            }

            if (entry.HasActed)
                ImGui.PopStyleVar();

            ImGui.Spacing();
        }

        // Marqueurs visibles non inclus dans les tours
        var turnWaymarks = new System.Collections.Generic.HashSet<int>();
        foreach (var e in state.Entries)
        {
            if (e.WaymarkIndex.HasValue)
                turnWaymarks.Add(e.WaymarkIndex.Value);
        }
        var hasExtra = false;
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            if (turnWaymarks.Contains(i)) continue;
            var waymarkId = (WaymarkId)i;
            var marker = session.CurrentMarkers[waymarkId];
            if (!marker.IsVisible || string.IsNullOrEmpty(marker.Name)) continue;

            if (!hasExtra)
            {
                ImGuiHelpers.ScaledDummy(4f);
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(4f);
                hasExtra = true;
            }
            MarkerCard.DrawReadOnly(waymarkId, marker, session.HpMode,
                session.ShowShield, session.ShowMpBar, session.MpMode);
            ImGui.Spacing();
        }

        if (session.CanEdit)
        {
            ImGuiHelpers.ScaledDummy(4f);
            var btnWidth = ImGui.GetContentRegionAvail().X;
            if (ImGui.Button(Loc.Get("Player.ApplyMarkers"), new Vector2(btnWidth, 0)))
            {
                var placed = session.ApplyWaymarks();
                if (placed > 0)
                    Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.WaymarksApplied"), placed));
                else
                    Plugin.ChatGui.Print(Loc.Get("Chat.WaymarksApplyFailed"));
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Player.ApplyMarkersTooltip"));
                ImGui.EndTooltip();
            }
        }
    }

    private static string? GetTurnIndicator(TurnEntry entry, bool isNext)
    {
        if (entry.HasActed) return "\u2713";
        if (isNext) return ">";
        return null;
    }

    private void DrawPlayerTurnCard(int index, TurnEntry entry, bool isNext)
    {
        var indicator = GetTurnIndicator(entry, isNext);
        var player = session.PartyMembers.FirstOrDefault(p => p.Hash == entry.PlayerHash);
        var hp = player?.Hp ?? 100;

        var playerBlue = new Vector4(0.227f, 0.604f, 1f, 0.8f);
        ImGui.PushStyleColor(ImGuiCol.Border, playerBlue);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);

        var cardWidth = ImGui.GetContentRegionAvail().X;
        var extraRows = 0;
        if (session.ShowMpBar) extraRows++;
        if (player?.Counters != null) extraRows += player.Counters.Count;
        if (player is { TempModifier: not 0 }) extraRows++;
        var cardHeight = ImGui.GetFrameHeightWithSpacing() * (2 + extraRows) + ImGui.GetStyle().WindowPadding.Y * 2;
        if (ImGui.BeginChild($"##pturn_{index}", new Vector2(cardWidth, cardHeight), true))
        {
            if (indicator != null)
            {
                var indicatorColor = indicator == ">"
                    ? MasterEventTheme.AccentColor
                    : new Vector4(0.4f, 0.4f, 0.4f, 1f);
                ImGui.TextColored(indicatorColor, indicator);
                ImGui.SameLine();
            }

            var userIcon = FontAwesomeIcon.User.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(playerBlue, userIcon);
            ImGui.SameLine();

            var nameWidth = ImGui.CalcTextSize(entry.Name).X;
            var nameX = (cardWidth - nameWidth) / 2f;
            var minX = ImGui.GetCursorPosX();
            if (nameX > minX)
                ImGui.SetCursorPosX(nameX);
            ImGui.TextUnformatted(entry.Name);

            var initText = $"[{entry.Initiative}]";
            var initW = ImGui.CalcTextSize(initText).X;
            ImGui.SameLine(cardWidth - initW - ImGui.GetStyle().WindowPadding.X);
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), initText);
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

            HpBar.Draw(hp, Attitude.Neutral, ImGui.GetContentRegionAvail().X, session.HpMode,
                hpMax: player?.HpMax ?? 100,
                shield: session.ShowShield ? player?.Shield ?? 0 : 0);

            if (session.ShowMpBar)
                HpBar.DrawMpBar(player?.Mp ?? 100, ImGui.GetContentRegionAvail().X, session.MpMode, player?.MpMax ?? 100);

            if (player?.Counters != null)
            {
                foreach (var counter in player.Counters)
                    CounterBar.Draw(counter, ImGui.GetContentRegionAvail().X);
            }

            // Bonus/malus temporaire
            if (player is { TempModifier: not 0 })
            {
                var tmIcon = FontAwesomeIcon.Magic.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(new Vector4(0.8f, 0.6f, 1f, 1f), tmIcon);
                ImGui.SameLine();
                var tmStr = player.TempModifier >= 0 ? $"+{player.TempModifier}" : player.TempModifier.ToString();
                var tmColor = player.TempModifier > 0
                    ? new Vector4(0.2f, 0.8f, 0.2f, 1f)
                    : new Vector4(1f, 0.4f, 0.4f, 1f);
                ImGui.TextColored(tmColor, tmStr);
                if (player.TempModTurns > 0)
                {
                    ImGui.SameLine(0, 2f * ImGuiHelpers.GlobalScale);
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"({player.TempModTurns}t)");
                }
            }
        }
        ImGui.EndChild();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    private static void DrawTurnLine(TurnEntry entry, bool isNext)
    {
        var indicator = GetTurnIndicator(entry, isNext);
        if (indicator != null)
        {
            var indicatorColor = entry.HasActed
                ? new Vector4(0.5f, 0.5f, 0.5f, 1f)
                : MasterEventTheme.AccentColor;
            ImGui.TextColored(indicatorColor, indicator);
        }
        else
        {
            ImGui.TextUnformatted("  ");
        }
        ImGui.SameLine();

        var iconSize = ImGui.GetFrameHeight();
        if (entry.IsMarker && entry.WaymarkIndex.HasValue)
        {
            var waymarkId = (WaymarkId)entry.WaymarkIndex.Value;
            var iconId = waymarkId.ToIconId();
            var wrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(iconSize, iconSize));
        }
        else
        {
            var userIcon = FontAwesomeIcon.User.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(new Vector4(0.227f, 0.604f, 1f, 0.8f), userIcon);
        }
        ImGui.SameLine();

        Vector4 nameColor;
        if (entry.HasActed)
            nameColor = new Vector4(0.5f, 0.5f, 0.5f, 1f);
        else if (isNext)
            nameColor = MasterEventTheme.AccentColor;
        else
            nameColor = new Vector4(1f, 1f, 1f, 1f);
        ImGui.TextColored(nameColor, entry.Name);

        ImGui.SameLine();
        var initText = $"[{entry.Initiative}]";
        var initWidth = ImGui.CalcTextSize(initText).X;
        var initPos = ImGui.GetContentRegionMax().X - initWidth;
        if (initPos > ImGui.GetCursorPosX())
            ImGui.SameLine(initPos);
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), initText);
        if (ImGui.IsItemHovered() && entry.InitiativeRoll > 0)
        {
            ImGui.BeginTooltip();
            if (entry.InitiativeModifier != 0 && entry.InitiativeStatName != null)
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
    }

    private static void DrawDiceTile(string line1, string? line2, string id, float w, float h, Action onClick)
    {
        var rounding = 6f * ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, rounding);


        if (ImGui.Button("##" + id, new Vector2(w, h)))
            onClick();

        var btnMin = ImGui.GetItemRectMin();
        var dlst = ImGui.GetWindowDrawList();

        var lineHeight = ImGui.GetFontSize();
        var totalTextH = line2 != null ? lineHeight * 2f + 2f : lineHeight;
        var textY = btnMin.Y + (h - totalTextH) / 2f;

        var sz1 = ImGui.CalcTextSize(line1);
        var x1 = btnMin.X + (w - sz1.X) / 2f;
        dlst.AddText(new Vector2(x1, textY), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), line1);


        if (line2 != null)
        {
            var sz2 = ImGui.CalcTextSize(line2);
            var x2 = btnMin.X + (w - sz2.X) / 2f;
            dlst.AddText(new Vector2(x2, textY + lineHeight + 2f),
                ImGui.GetColorU32(new Vector4(0.7f, 0.7f, 0.7f, 1f)), line2);
        }

        ImGui.PopStyleVar();
    }

    private void DrawPlainMarkerList()
    {
        var hasVisibleMarkers = false;
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            var waymarkId = (WaymarkId)i;
            var marker = session.CurrentMarkers[waymarkId];

            if (!marker.IsVisible || string.IsNullOrEmpty(marker.Name))
                continue;

            hasVisibleMarkers = true;
            MarkerCard.DrawReadOnly(waymarkId, marker, session.HpMode,
                session.ShowShield, session.ShowMpBar, session.MpMode);
            ImGui.Spacing();
        }

        if (!hasVisibleMarkers)
        {
            ImGui.Separator();
            if (!session.IsConnected)
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), Loc.Get("Player.Disconnected"));
            else
                ImGui.TextColored(MasterEventTheme.AttitudeNeutral, Loc.Get("Player.Waiting"));
        }
        else if (session.CanEdit)
        {
            ImGuiHelpers.ScaledDummy(4f);
            var btnWidth = ImGui.GetContentRegionAvail().X;
            if (ImGui.Button(Loc.Get("Player.ApplyMarkers"), new Vector2(btnWidth, 0)))
            {
                var placed = session.ApplyWaymarks();
                if (placed > 0)
                    Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.WaymarksApplied"), placed));
                else
                    Plugin.ChatGui.Print(Loc.Get("Chat.WaymarksApplyFailed"));
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Player.ApplyMarkersTooltip"));
                ImGui.EndTooltip();
            }
        }
    }
}
