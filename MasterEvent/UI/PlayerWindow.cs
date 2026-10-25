using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using MasterEvent.Communication;
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
    public void ResetSheetSelection() => selectedSheetName = string.Empty;
    private string allianceCodeInput = string.Empty;
    private string diceStatFilter = string.Empty;
    private string statsPopupFilter = string.Empty;

    // Résultat de « Appliquer les marqueurs », affiché sous le bouton plutôt que dans le chat.
    private static readonly TimeSpan MarkersNoticeLifetime = TimeSpan.FromSeconds(5);
    private string? markersNoticeText;
    private bool markersNoticeFailed;
    private long markersNoticeAt;
    public UmbraPortraitCache? UmbraPortraits { get; set; }
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
            MaximumSize = new Vector2(1000, 1600),
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

        LayoutControls.DrawVerticalSeparator(8f);

        if (ImGui.BeginChild("##player_content", Vector2.Zero))
        {
            DrawRollRequestBanner();

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
                ImGui.GetColorU32(MasterEventTheme.AccentText), word);
            ImGui.Dummy(new Vector2(0, fontSize + 1f * ImGuiHelpers.GlobalScale));
        }

        ImGui.Spacing();

        DrawSidebarButton(FontAwesomeIcon.List, PlayerTab.Overview, Loc.Get("Player.OverviewTab"));
        DrawSidebarButton(FontAwesomeIcon.Dice, PlayerTab.Dice, Loc.Get("Player.RollDice"));
    }

    /// <summary>
    /// Bascule la vue joueur sur son onglet de jet. Referme la fenêtre si elle affiche déjà
    /// cet onglet, pour que le bouton flottant se comporte comme les deux autres.
    /// </summary>
    public void ToggleDiceView()
    {
        if (IsOpen && activeTab == PlayerTab.Dice)
        {
            IsOpen = false;
            return;
        }

        activeTab = PlayerTab.Dice;
        IsOpen = true;
    }

    private bool DrawOwnPortrait()
    {
        if (UmbraPortraits is not { } cache) return false;

        var objectId = UmbraPortraitCache.ResolveObjectId(
            Plugin.ObjectTable.LocalPlayer?.Name.TextValue ?? string.Empty);
        if (objectId == 0) return false;

        var texture = cache.Get(objectId);
        if (texture == null) return false;

        var side = ImGui.GetFrameHeight();
        var (uv0, uv1) = UmbraPortraitCache.CoverUv(texture.Width, texture.Height, side, side);
        var rounding = Math.Min(MasterEventTheme.RadiusCard * ImGuiHelpers.GlobalScale, side * 0.5f);

        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddImageRounded(
            texture.Handle, pos, pos + new Vector2(side, side), uv0, uv1,
            ImGui.GetColorU32(Vector4.One), rounding);
        ImGui.Dummy(new Vector2(side, side));
        ImGui.SameLine();
        return true;
    }

    private void DrawSidebarButton(FontAwesomeIcon icon, PlayerTab tab, string tooltip)
    {
        if (SidebarControls.DrawButton(icon, "##ptab_" + tab, activeTab == tab, tooltip, SidebarButtonSize))
            activeTab = tab;
    }

    private void DrawRollRequestBanner()
    {
        if (session.PendingRollRequest is not { } request) return;

        LayoutControls.DrawNotice(string.Format(Loc.Get("RollRequest.Banner"), request.StatName, request.Threshold),
            MasterEventTheme.AccentText, FontAwesomeIcon.Dice);

        // Répondre est ce que le MJ attend : « Lancer » plein, « Ignorer » discret à côté.
        var rollLabel = Loc.Get("RollRequest.Roll");
        if (Buttons.Primary(rollLabel + "##roll_request_answer", Buttons.PrimaryWidth(rollLabel)))
            session.AnswerRollRequest();
        ImGui.SameLine();
        if (Buttons.Quiet(Loc.Get("RollRequest.Dismiss") + "##roll_request_dismiss"))
            session.DismissRollRequest();

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
    }

    private void DrawOverviewContent()
    {
        // L'état de la connexion d'abord : il dit s'il y a une séance à suivre avant tout le reste.
        LinkStatus.Draw(session.LinkState, session.ConnectedPlayerCount);
        ImGuiHelpers.ScaledDummy(4f);

        if (session.IsAwaitingApproval)
        {
            LayoutControls.DrawNotice(Loc.Get("Lobby.AwaitingApproval"), MasterEventTheme.PendingColor,
                FontAwesomeIcon.HourglassHalf);
            ImGuiHelpers.ScaledDummy(4f);
        }

        if (session.IsLobbyMode)
        {
            DrawLobbyLine();
        }
        else if (!session.IsConnected)
        {
            LayoutControls.BeginCard(Loc.Get("Lobby.JoinTitle"), FontAwesomeIcon.DoorOpen);
            ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Lobby.JoinHint"));
            LobbyJoinControls.Draw("player_lobby", ref allianceCodeInput, code => onJoinAlliance?.Invoke(code));
            LayoutControls.EndCard();
        }
        else
        {
            // Rare en séance : replié derrière un triangle, la saisie n'encombre pas la vue.
            ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.MutedTextColor);
            var open = ImGui.TreeNode(Loc.Get("Lobby.JoinOther") + "##other_lobby_join");
            ImGui.PopStyleColor();
            if (open)
            {
                LobbyJoinControls.Draw("player_lobby_other", ref allianceCodeInput, code => onJoinAlliance?.Invoke(code));
                ImGui.TreePop();
            }

            ImGuiHelpers.ScaledDummy(4f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);
        }

        var state = session.CurrentTurnState;
        var turnsActive = state is { IsActive: true } && state.Entries.Count > 0;

        // En combat, le bouton reste proposé ; hors combat, seulement s'il y a des marqueurs à poser.
        bool offerApply;
        if (turnsActive)
        {
            DrawCombinedTurnView(state!);
            offerApply = true;
        }
        else
        {
            DrawLocalPlayerCard();
            offerApply = DrawPlainMarkerList();
        }

        DrawApplyMarkersButton(offerApply);
    }

    // Lobby en une phrase, son sous-groupe en couleur ; quitter attend dans le menu « ... ».
    private void DrawLobbyLine()
    {
        var localPlayer = session.PartyMembers.FirstOrDefault(p => p.Hash == session.LocalPlayerHash);
        var groupLabel = localPlayer?.GroupLabel;
        var iconColor = groupLabel != null ? MasterEventTheme.GroupColor(groupLabel) : MasterEventTheme.AccentText;
        var sentence = groupLabel != null
            ? string.Format(Loc.Get("Lobby.InLobbyGroupFmt"), session.LobbyCode, groupLabel)
            : string.Format(Loc.Get("Lobby.InLobbyFmt"), session.LobbyCode);

        var style = ImGui.GetStyle();
        var rowStart = ImGui.GetCursorPosX();
        var avail = ImGui.GetContentRegionAvail().X;
        var menuWidth = Buttons.IconWidth(FontAwesomeIcon.EllipsisH);

        ImGui.AlignTextToFramePadding();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(iconColor, FontAwesomeIcon.Users.ToIconString());
        ImGui.SameLine();
        ImGui.PushTextWrapPos(rowStart + avail - menuWidth - style.ItemSpacing.X);
        ImGui.TextUnformatted(sentence);
        ImGui.PopTextWrapPos();

        ImGui.SameLine(rowStart + avail - menuWidth);
        if (Buttons.QuietIcon(FontAwesomeIcon.EllipsisH, "lobby_menu", Loc.Get("Sheets.More")))
            ImGui.OpenPopup("##lobby_menu");

        if (ImGui.BeginPopup("##lobby_menu"))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.DangerColor);
            if (ImGui.Selectable(Loc.Get("Lobby.Leave") + "##leave_alliance"))
                onLeaveAlliance?.Invoke();
            ImGui.PopStyleColor();
            ImGui.EndPopup();
        }

        // Répartition des sous-groupes : le total en clair, le détail au survol.
        var groupCounts = session.GetGroupCounts();
        if (groupCounts.Count > 1)
        {
            ImGui.TextColored(MasterEventTheme.TextDim, string.Format(Loc.Get("Lobby.GroupsSummaryFmt"),
                groupCounts.Values.Sum(), groupCounts.Count));
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                foreach (var (label, count) in groupCounts.OrderBy(g => g.Key, StringComparer.Ordinal))
                    ImGui.TextColored(MasterEventTheme.GroupColor(label),
                        string.Format(Loc.Get("Lobby.GroupCountFmt"), label, count));
                ImGui.EndTooltip();
            }
        }

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
    }

    // Un seul bouton pour les deux vues (combat ou non), réservé à qui peut poser les marqueurs.
    private void DrawApplyMarkersButton(bool offer)
    {
        if (!session.CanEdit || !offer) return;

        ImGuiHelpers.ScaledDummy(4f);
        if (Buttons.Tinted(Loc.Get("Player.ApplyMarkers") + "##apply_markers", ImGui.GetContentRegionAvail().X))
        {
            var placed = session.ApplyWaymarks();
            markersNoticeFailed = placed <= 0;
            markersNoticeText = placed switch
            {
                <= 0 => Loc.Get("Player.MarkersApplyFailed"),
                1 => Loc.Get("Player.MarkersAppliedOne"),
                _ => string.Format(Loc.Get("Player.MarkersAppliedFmt"), placed),
            };
            markersNoticeAt = Environment.TickCount64;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.Get("Player.ApplyMarkersTooltip"));

        DrawMarkersNotice();
    }

    // Le retour s'estompe sur sa durée, comme ceux du Terrain côté MJ.
    private void DrawMarkersNotice()
    {
        if (markersNoticeText == null) return;

        var age = (Environment.TickCount64 - markersNoticeAt) / MarkersNoticeLifetime.TotalMilliseconds;
        if (age >= 1d)
        {
            markersNoticeText = null;
            return;
        }

        ImGuiHelpers.ScaledDummy(2f);
        var alpha = (float)(1d - age * age);
        if (markersNoticeFailed)
            LayoutControls.DrawNotice(markersNoticeText, MasterEventTheme.WarningColor,
                FontAwesomeIcon.ExclamationTriangle, alpha);
        else
            LayoutControls.DrawNotice(markersNoticeText, MasterEventTheme.SuccessColor,
                FontAwesomeIcon.CheckCircle, alpha);
    }

    // Onglet Dés

    private void DrawDiceContent()
    {
        var availWidth = ImGui.GetContentRegionAvail().X;

        ImGuiHelpers.ScaledDummy(6f);

        // Header
        LayoutControls.DrawCenteredIcon(FontAwesomeIcon.Dice, availWidth, 1.6f);
        ImGuiHelpers.ScaledDummy(4f);

        var titleSz = ImGui.CalcTextSize(Loc.Get("Player.RollDice"));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - titleSz.X) / 2f);
        ImGui.TextColored(MasterEventTheme.AccentText, Loc.Get("Player.RollDice"));
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
            ImGui.TextColored(MasterEventTheme.MutedTextColor, activeTemplateName);
            ImGuiHelpers.ScaledDummy(2f);
        }

        // Réinitialiser la sélection si la fiche active n'est plus dans la liste filtrée
        if (!string.IsNullOrEmpty(selectedSheetName) && filteredSheets.All(s => s.Name != selectedSheetName))
            selectedSheetName = string.Empty;

        // Pré-sélectionner la fiche par défaut dans le combo si pas encore sélectionnée
        if (string.IsNullOrEmpty(selectedSheetName))
        {
            var defaultName = configuration.GetDefaultSheetName();
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
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Player.NoProfiles"));
            ImGuiHelpers.ScaledDummy(4f);
        }

        var localHash = Plugin.GeneratePlayerHash(playerState.ContentId);
        var localPlayer = session.PartyMembers.FirstOrDefault(p => p.Hash == localHash);

        if (ImGui.BeginChild("##dice_scroll", Vector2.Zero))
        {
            // Largeur mesurée dans le cadre défilant : celle de la fenêtre ignore la barre de
            // défilement, et la dernière colonne passait dessous.
            var gridWidth = ImGui.GetContentRegionAvail().X;

            // Filtre de stats
            if (localPlayer?.Stats != null && localPlayer.Stats.Count > 5)
            {
                SearchField.Draw("dice_stat_filter", ref diceStatFilter, Loc.Get("Models.StatsFilter"), gridWidth);
                ImGuiHelpers.ScaledDummy(4f);
            }

            // Grille de boutons de jet
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            var columns = 3;
            var tileSize = (gridWidth - spacing * (columns - 1)) / columns;
            var tileH = tileSize * 0.75f;
            var idx = 0;
            var requested = session.PendingRollRequest;
            if (string.IsNullOrEmpty(diceStatFilter) && (requested == null || requested.StatId == null))
            {
                DiceControls.DrawDiceTile(Loc.Get("Dice.NoStat"), null, "roll_simple", tileSize, tileH, () =>
                    session.RollDiceForPlayer(localHash));
                idx++;
            }

            // Boutons par stat
            if (localPlayer?.Stats != null && localPlayer.Stats.Count > 0)
            {
                var diceStats = localPlayer.Stats.Where(s =>
                    requested?.StatId is not { } wanted ? (
                        string.IsNullOrEmpty(diceStatFilter) ||
                        s.Name.Contains(diceStatFilter, StringComparison.OrdinalIgnoreCase))
                    : s.Id == wanted);
                foreach (var stat in diceStats)
                {
                    if (idx % columns != 0)
                        ImGui.SameLine();

                    var modStr = StatDisplay.Stat(stat.Modifier);
                    var statId = stat.Id;

                    DiceControls.DrawDiceTile(stat.Name, modStr, "roll_" + stat.Id, tileSize, tileH, () =>
                        session.RollDiceForPlayer(localHash, statId));
                    idx++;
                }
            }

            DiceControls.DrawRollHistory(session, maxEntries: 20, showClearButton: true);
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

        var playerBlue = MasterEventTheme.PlayerColor;
        ImGui.PushStyleColor(ImGuiCol.Border, playerBlue);
        ImGui.PushStyleColor(ImGuiCol.ChildBg,
            MasterEventTheme.ThemeButtonBg with { W = MasterEventTheme.CardAlpha() });
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, MasterEventTheme.RadiusCard * ImGuiHelpers.GlobalScale);

        if (ImGui.BeginChild("##player_hp_card", new Vector2(cardWidth, cardHeight), true))
        {
            if (!DrawOwnPortrait())
            {
                var userIcon = FontAwesomeIcon.User.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    ImGui.TextColored(playerBlue, userIcon);
                }
                ImGui.SameLine();
            }

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
                ImGui.TextColored(MasterEventTheme.TextDim,
                    $"{Loc.Get("Models.Stats")} ({localPlayer.Stats.Count})");

                if (ImGui.BeginPopup("##pstats_popup"))
                {
                    ImGui.TextColored(MasterEventTheme.AccentText, Loc.Get("Models.Stats"));
                    ImGui.Separator();
                    if (localPlayer.Stats.Count > 5)
                    {
                        SearchField.Draw("pstats_filter", ref statsPopupFilter, Loc.Get("Models.StatsFilter"),
                            200f * ImGuiHelpers.GlobalScale);
                        ImGuiHelpers.ScaledDummy(2f);
                    }
                    var popupStats = localPlayer.Stats.Where(s =>
                        string.IsNullOrEmpty(statsPopupFilter) ||
                        s.Name.Contains(statsPopupFilter, StringComparison.OrdinalIgnoreCase));
                    foreach (var stat in popupStats)
                    {
                        ImGui.TextUnformatted(stat.Name);
                        ImGui.SameLine();
                        var modStr = StatDisplay.Stat(stat.Modifier);
                        ImGui.TextColored(MasterEventTheme.TextSecondary, modStr);
                    }
                    ImGui.EndPopup();
                }
            }

            // Affichage du bonus/malus temporaire
            if (localPlayer.TempModifier != 0)
            {
                var tempIcon = FontAwesomeIcon.Magic.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(MasterEventTheme.TempModifierColor, tempIcon);
                ImGui.SameLine();
                var tempStr = localPlayer.TempModifier >= 0 ? $"+{localPlayer.TempModifier}" : localPlayer.TempModifier.ToString();
                var tempColor = localPlayer.TempModifier > 0
                    ? MasterEventTheme.SuccessColor
                    : MasterEventTheme.DangerColor;
                ImGui.TextColored(tempColor, $"{Loc.Get("Marker.TempMod")}: {tempStr}");
                if (localPlayer.TempModTurns > 0)
                {
                    ImGui.SameLine(0, 4f * ImGuiHelpers.GlobalScale);
                    ImGui.TextColored(MasterEventTheme.TextSecondary, $"({localPlayer.TempModTurns}t)");
                }
            }
        }
        ImGui.EndChild();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);

        ImGuiHelpers.ScaledDummy(4f);
    }

    //  Vue des tours

    // Compte les blocs (groupe = 1 bloc, solo = 1 bloc) et ceux qui ont joué, pour le compteur de progression.
    private static (int Total, int Acted) CountTurnBlocks(TurnState state)
    {
        var total = 0;
        var acted = 0;
        var seen = new System.Collections.Generic.HashSet<string>();
        foreach (var entry in state.Entries)
        {
            if (entry.GroupId != null)
            {
                if (!seen.Add(entry.GroupId)) continue;
                total++;
                var group = state.Groups.FirstOrDefault(g => g.Id == entry.GroupId);
                if (group?.HasActed == true) acted++;
            }
            else
            {
                total++;
                if (entry.HasActed) acted++;
            }
        }
        return (total, acted);
    }

    // Retourne le GroupId du prochain bloc à jouer (null si solo), et l'index du premier membre.
    private static string? FindNextBlockGroupId(TurnState state, out int firstMemberIdx)
    {
        var seen = new System.Collections.Generic.HashSet<string>();
        for (var i = 0; i < state.Entries.Count; i++)
        {
            var entry = state.Entries[i];
            if (entry.GroupId != null)
            {
                if (!seen.Add(entry.GroupId)) continue;
                var group = state.Groups.FirstOrDefault(g => g.Id == entry.GroupId);
                if (group != null && !group.HasActed)
                {
                    firstMemberIdx = i;
                    return entry.GroupId;
                }
            }
            else if (!entry.HasActed)
            {
                firstMemberIdx = i;
                return null;
            }
        }
        firstMemberIdx = -1;
        return null;
    }

    private void DrawCombinedTurnView(TurnState state)
    {
        var roundText = string.Format(Loc.Get("Turns.Round"), state.Round);
        ImGui.TextColored(MasterEventTheme.AccentText, roundText);
        ImGui.SameLine();
        ImGui.TextColored(MasterEventTheme.MutedTextColor, $"(d{state.DiceMax})");

        // Compte les blocs (groupe = 1, solo = 1) et combien ont joué
        var (blocksTotal, blocksActed) = CountTurnBlocks(state);
        var progressText = string.Format(Loc.Get("Turns.Progress"), blocksActed, blocksTotal);
        ImGui.TextColored(MasterEventTheme.MutedTextColor, progressText);

        ImGuiHelpers.ScaledDummy(4f);

        // Identifie le premier bloc non-joué pour marquer "next"
        var nextBlockGroupId = FindNextBlockGroupId(state, out var nextBlockFirstIdx);

        var seenGroupIds = new System.Collections.Generic.HashSet<string>();
        for (var i = 0; i < state.Entries.Count; i++)
        {
            var entry = state.Entries[i];
            var blockActed = state.HasEntryActed(entry);

            // Label du groupe au 1er membre rencontré
            if (entry.GroupId != null && seenGroupIds.Add(entry.GroupId))
            {
                var group = state.Groups.FirstOrDefault(g => g.Id == entry.GroupId);
                if (group != null && !string.IsNullOrEmpty(group.Label))
                {
                    var linkIcon = Dalamud.Interface.FontAwesomeIcon.Link.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        ImGui.TextColored(MasterEventTheme.AccentText, linkIcon);
                    ImGui.SameLine();
                    ImGui.TextColored(MasterEventTheme.TextSecondary, group.Label);
                }
            }

            // "isNext" : pour un solo, si c'est l'index du prochain ; pour un membre de groupe, si c'est le groupe du prochain bloc
            var isNext = entry.GroupId != null
                ? entry.GroupId == nextBlockGroupId
                : i == nextBlockFirstIdx;
            var indicator = GetTurnIndicator(entry, isNext);

            if (blockActed)
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

            if (blockActed)
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

        var playerBlue = MasterEventTheme.PlayerColor;
        ImGui.PushStyleColor(ImGuiCol.Border, playerBlue);
        ImGui.PushStyleColor(ImGuiCol.ChildBg,
            MasterEventTheme.ThemeButtonBg with { W = MasterEventTheme.CardAlpha() });
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, MasterEventTheme.RadiusCard * ImGuiHelpers.GlobalScale);

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
                    ? MasterEventTheme.AccentText
                    : MasterEventTheme.TextDim;
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
            ImGui.TextColored(MasterEventTheme.MutedTextColor, initText);
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
                    ImGui.TextColored(MasterEventTheme.TempModifierColor, tmIcon);
                ImGui.SameLine();
                var tmStr = player.TempModifier >= 0 ? $"+{player.TempModifier}" : player.TempModifier.ToString();
                var tmColor = player.TempModifier > 0
                    ? MasterEventTheme.SuccessColor
                    : MasterEventTheme.DangerColor;
                ImGui.TextColored(tmColor, tmStr);
                if (player.TempModTurns > 0)
                {
                    ImGui.SameLine(0, 2f * ImGuiHelpers.GlobalScale);
                    ImGui.TextColored(MasterEventTheme.TextSecondary, $"({player.TempModTurns}t)");
                }
            }
        }
        ImGui.EndChild();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    private static void DrawTurnLine(TurnEntry entry, bool isNext)
    {
        var indicator = GetTurnIndicator(entry, isNext);
        if (indicator != null)
        {
            var indicatorColor = entry.HasActed
                ? MasterEventTheme.TextDim
                : MasterEventTheme.AccentText;
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
                ImGui.TextColored(MasterEventTheme.PlayerColor, userIcon);
        }
        ImGui.SameLine();

        Vector4 nameColor;
        if (entry.HasActed)
            nameColor = MasterEventTheme.TextDim;
        else if (isNext)
            nameColor = MasterEventTheme.AccentText;
        else
            nameColor = MasterEventTheme.TextStrong;
        ImGui.TextColored(nameColor, entry.Name);

        ImGui.SameLine();
        var initText = $"[{entry.Initiative}]";
        var initWidth = ImGui.CalcTextSize(initText).X;
        var initPos = ImGui.GetContentRegionMax().X - initWidth;
        if (initPos > ImGui.GetCursorPosX())
            ImGui.SameLine(initPos);
        ImGui.TextColored(MasterEventTheme.MutedTextColor, initText);
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

    // Vrai s'il y a au moins un marqueur visible.
    private bool DrawPlainMarkerList()
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

        // Hors combat et sans marqueur : rien n'est en attente, la séance n'a simplement pas commencé.
        if (session.LinkState == RelayLinkState.InSession && !hasVisibleMarkers)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Player.Waiting"));
            ImGui.PopTextWrapPos();
        }

        return hasVisibleMarkers;
    }
}
