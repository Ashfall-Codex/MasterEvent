using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

public sealed partial class GmWindow
{
    private const string GroupCloseLobbyConfirmId = "group_close_lobby";

    private void DrawGroupContent()
    {
        // Copie pour l'image : retirer un joueur du lobby modifie PartyMembers en pleine itération.
        var members = session.PartyMembers.ToList();
        var players = members.Where(p => !p.IsGm || session.GmIsPlayer).ToList();

        DrawGroupHeader(players);

        if (ImGui.BeginChild("##group_scroll", Vector2.Zero))
        {
            DrawPendingRequests();
            DrawGroupLobbyCard(members);
            DrawGroupGmCard(members);
            DrawGroupPlayersCard(players);
        }
        ImGui.EndChild();
    }

    private void DrawGroupHeader(List<PlayerData> players)
    {
        string? subtitle = null;
        if (players.Count > 0)
        {
            var connected = players.Count(p => p.IsConnected);
            var playersPart = players.Count == 1
                ? Loc.Get("Group.PlayersOne")
                : string.Format(Loc.Get("Group.PlayersFmt"), players.Count);
            var connectedPart = connected switch
            {
                0 => Loc.Get("Group.ConnectedNone"),
                1 => Loc.Get("Group.ConnectedOne"),
                _ => string.Format(Loc.Get("Group.ConnectedFmt"), connected),
            };
            subtitle = string.Format(Loc.Get("Group.SummaryFmt"), playersPart, connectedPart);
        }

        // Les statistiques s'affichent sous les jauges modifiables : la bascule ne sert qu'au MJ et à ses co-MJ.
        if (!session.CanEdit)
        {
            PageHeader.Draw(FontAwesomeIcon.Users, Loc.Get("Group.Title"), subtitle);
            return;
        }

        var statsLabel = Loc.Get("Group.StatsToggle");
        var toggleWidth = ImGui.GetFrameHeight() * 0.82f * 1.8f + ImGui.GetStyle().ItemSpacing.X
                          + ImGui.CalcTextSize(statsLabel).X;
        PageHeader.Draw(FontAwesomeIcon.Users, Loc.Get("Group.Title"), subtitle,
            () => DrawGroupStatsToggle(statsLabel), toggleWidth);
    }

    // Réglage local (Configuration.ShowPlayerStatsInline), placé dans l'écran qu'il concerne.
    private void DrawGroupStatsToggle(string label)
    {
        var inlineStats = configuration.ShowPlayerStatsInline;
        if (!ToggleSwitch.Draw("##group_inline_stats", label, ref inlineStats, Loc.Get("Group.StatsToggleTooltip")))
            return;

        configuration.ShowPlayerStatsInline = inlineStats;
        configuration.Save();
    }

    private void DrawGroupLobbyCard(List<PlayerData> members)
    {
        LayoutControls.BeginCard(Loc.Get("Lobby.Title"), FontAwesomeIcon.DoorOpen);

        if (!session.IsLobbyMode)
            DrawGroupLobbyClosed();
        else
            DrawGroupLobbyOpen(members);

        if (session.IsAwaitingApproval)
        {
            ImGuiHelpers.ScaledDummy(4f);
            ImGui.TextColored(MasterEventTheme.PendingColor, Loc.Get("Lobby.AwaitingApproval"));
        }

        LayoutControls.EndCard();
    }

    private void DrawGroupLobbyClosed()
    {
        // Une confirmation restée ouverte resurgirait au prochain lobby.
        if (ConfirmInline.IsOpen(GroupCloseLobbyConfirmId)) ConfirmInline.Close();

        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Lobby.Inactive"));
        ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Lobby.InactiveTooltip"));
        ImGuiHelpers.ScaledDummy(2f);

        var openLabel = Loc.Get("Lobby.OpenManually");
        if (Buttons.Primary(openLabel + "##open_lobby", Buttons.PrimaryWidth(openLabel)))
            onEnableAlliance?.Invoke();

        ImGuiHelpers.ScaledDummy(2f);

        // Rejoindre le lobby d'un autre MJ reste rare : derrière un triangle de repli.
        ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.MutedTextColor);
        var joinOpen = ImGui.TreeNodeEx(Loc.Get("Lobby.JoinOther") + "##gm_join_other",
            ImGuiTreeNodeFlags.NoTreePushOnOpen);
        ImGui.PopStyleColor();
        if (joinOpen)
            LobbyJoinControls.Draw("gm_lobby_code", ref lobbyCodeInput, code => onJoinLobby?.Invoke(code));
    }

    private void DrawGroupLobbyOpen(List<PlayerData> members)
    {
        var code = session.LobbyCode ?? "";

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Lobby.Code"));
        ImGui.SameLine();
        // Police à chasse fixe, à la taille du texte : chaque caractère se lit sans agrandir le code.
        using (ImRaii.PushFont(UiBuilder.MonoFont))
            ImGui.TextColored(MasterEventTheme.TextStrong, code);

        ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Lobby.ShareHint"));
        ImGuiHelpers.ScaledDummy(2f);

        var rowStartX = ImGui.GetCursorPosX();
        var copyLabel = Loc.Get("Lobby.CopyInvite");
        if (Buttons.Primary(copyLabel + "##copy_lobby", Buttons.PrimaryWidth(copyLabel)))
        {
            ImGui.SetClipboardText(SessionManager.FormatLobbyInvitation(code));
            lobbyInviteCopiedAt = DateTime.UtcNow;
        }

        // Le MJ créateur qui s'en va confie le lobby à son co-MJ connecté au lieu de le fermer
        // (même règle que Plugin.DisableLobby) : le bouton et la question le disent.
        var successor = configuration.LobbyIsCreator && session.IsGm && session.IsConnected
            ? members.FirstOrDefault(p => p.CanEdit && p.IsConnected && p.Hash != session.LocalPlayerHash)
            : null;
        var closeLabel = successor != null ? Loc.Get("Lobby.HandOver") : Loc.Get("Lobby.Close");
        var question = successor != null
            ? string.Format(Loc.Get("Lobby.HandOverConfirmFmt"), successor.Name)
            : Loc.Get("Lobby.CloseConfirm");

        // Fermé, le bouton se range à droite ; ouverte, la question passe sous la ligne.
        if (!ConfirmInline.IsOpen(GroupCloseLobbyConfirmId))
            AlignGroupActions(rowStartX, Buttons.TextWidth(closeLabel));
        if (ConfirmInline.Draw(GroupCloseLobbyConfirmId, closeLabel, question, closeLabel))
            onDisableAlliance?.Invoke();

        if (DateTime.UtcNow - lobbyInviteCopiedAt < TimeSpan.FromSeconds(3))
            ImGui.TextColored(MasterEventTheme.MutedTextColor,
                string.Format(Loc.Get("Lobby.InviteCopiedFmt"), SessionManager.FormatLobbyInvitation(code)));
    }

    private void DrawGroupGmCard(List<PlayerData> members)
    {
        LayoutControls.BeginCard(Loc.Get("Group.Gm"), FontAwesomeIcon.Crown);

        var hasGm = false;
        foreach (var player in members.Where(p => p.IsGm))
        {
            hasGm = true;
            DrawGroupMember(player, true);
        }

        if (!hasGm)
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Group.NoGm"));

        // Le MJ participe aussi en tant que joueur
        var gmIsPlayer = session.GmIsPlayer;
        if (ToggleSwitch.Draw("##gmIsPlayer", Loc.Get("Group.GmIsPlayer"), ref gmIsPlayer))
        {
            session.GmIsPlayer = gmIsPlayer;
            configuration.GmIsPlayer = gmIsPlayer;
            configuration.Save();
            session.BroadcastPlayerUpdate();

            // Ouvrir/fermer la fenêtre joueur
            if (PlayerWindowRef is { } playerWin)
                playerWin.IsOpen = gmIsPlayer;

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

        if (session.GmIsPlayer && PlayerWindowRef != null)
        {
            ImGui.SameLine();
            if (Buttons.Quiet(Loc.Get("General.ShowPlayerWindow") + "##group_player_view"))
                PlayerWindowRef.IsOpen = !PlayerWindowRef.IsOpen;
        }

        LayoutControls.EndCard();
    }

    private void DrawGroupPlayersCard(List<PlayerData> players)
    {
        LayoutControls.BeginCard(Loc.Get("Group.Players"), FontAwesomeIcon.Users);

        // Compteur par groupe en mode alliance
        if (session.IsLobbyMode)
        {
            ImGui.SameLine();
            var groupCounts = session.GetGroupCounts();
            var countParts = new List<string>();
            foreach (var (label, count) in groupCounts)
                countParts.Add($"{label}:{count}");
            if (countParts.Count > 0)
            {
                ImGui.TextColored(MasterEventTheme.TextDim,
                    $"  ({string.Join(" | ", countParts)})");
            }
        }
        ImGui.Spacing();

        foreach (var player in players)
        {
            DrawGroupMember(player, false);
            ImGui.Spacing();
        }

        if (players.Count == 0)
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Group.NoPlayers"));
        LayoutControls.EndCard();
    }

    private bool DrawPortraitThumbnail(string playerName, float alpha = 1f)
    {
        if (UmbraPortraits is not { } cache) return false;

        var objectId = UmbraPortraitCache.ResolveObjectId(playerName);
        if (objectId == 0) return false;

        var texture = cache.Get(objectId);
        if (texture == null) return false;

        var side = ImGui.GetFrameHeight();
        var (uv0, uv1) = UmbraPortraitCache.CoverUv(texture.Width, texture.Height, side, side);
        var rounding = Math.Min(MasterEventTheme.RadiusCard * ImGuiHelpers.GlobalScale, side * 0.5f);
        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddImageRounded(
            texture.Handle, pos, pos + new Vector2(side, side), uv0, uv1,
            ImGui.GetColorU32(Vector4.One with { W = alpha }), rounding);
        ImGui.Dummy(new Vector2(side, side));

        ImGui.SameLine(0, 4f * ImGuiHelpers.GlobalScale);
        return true;
    }

    // Demandes d'accès en tête de vue, une ligne par sous-groupe : accepter un joueur fait entrer
    // tout son groupe.
    private void DrawPendingRequests()
    {
        if (session.PendingMembers.Count == 0) return;
        if (!session.IsGm && !session.IsPromoted) return;

        LayoutControls.BeginCard(Loc.Get("Lobby.PendingTitle"), FontAwesomeIcon.UserClock, MasterEventTheme.PendingColor);

        ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Lobby.PendingHint"));
        ImGuiHelpers.ScaledDummy(2f);

        var decisions = new List<(PendingMember Representative, int Teammates)>();
        foreach (var pending in session.PendingMembers)
        {
            var existing = pending.GroupId == null
                ? -1
                : decisions.FindIndex(d => d.Representative.GroupId == pending.GroupId);

            if (existing >= 0)
                decisions[existing] = (decisions[existing].Representative, decisions[existing].Teammates + 1);
            else
                decisions.Add((pending, 0));
        }

        var admitLabel = Loc.Get("Lobby.Admit");
        var denyLabel = Loc.Get("Lobby.Deny");
        var actionsWidth = Buttons.TextWidth(admitLabel) + ImGui.GetStyle().ItemSpacing.X
                           + Buttons.TextWidth(denyLabel);

        foreach (var (pending, teammates) in decisions)
        {
            var rowStartX = ImGui.GetCursorPosX();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(pending.Name);

            if (teammates > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(MasterEventTheme.MutedTextColor, teammates == 1
                    ? Loc.Get("Lobby.PendingTeammatesOne")
                    : string.Format(Loc.Get("Lobby.PendingTeammatesFmt"), teammates));
            }

            // Accepter en vert, Refuser en rouge : les deux réponses ne doivent pas se confondre.
            AlignGroupActions(rowStartX, actionsWidth);
            if (Buttons.Positive($"{admitLabel}##admit_{pending.Hash}"))
                session.AdmitPending(pending.Hash);

            ImGui.SameLine();
            if (Buttons.Destructive($"{denyLabel}##deny_{pending.Hash}"))
            {
                // Refuser le seul représentant laisserait ses coéquipiers dans la file, et la
                // demande paraîtrait revenir aussitôt sous un autre nom.
                foreach (var member in session.PendingMembers
                             .Where(m => m.Hash == pending.Hash
                                         || (pending.GroupId != null && m.GroupId == pending.GroupId))
                             .ToList())
                    session.DenyPending(member.Hash);
            }
        }

        LayoutControls.EndCard(MasterEventTheme.PendingColor);
    }

    private void DrawGroupMember(PlayerData player, bool isGmSection)
    {
        var availWidth = ImGui.GetContentRegionAvail().X;
        var rowStartX = ImGui.GetCursorPosX();

        ImGui.AlignTextToFramePadding();

        // Couronne du MJ
        if (isGmSection)
        {
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(MasterEventTheme.AccentColor, FontAwesomeIcon.Crown.ToIconString());
            ImGui.SameLine();
        }

        if (session.IsLobbyMode && !isGmSection && player.GroupLabel != null)
        {
            ImGui.TextColored(MasterEventTheme.GroupColor(player.GroupLabel), $"[{player.GroupLabel}]");
            ImGui.SameLine(0, 4f * ImGuiHelpers.GlobalScale);
        }

        ImGui.TextUnformatted(player.Name);

        if (!isGmSection && player.CanEdit)
        {
            ImGui.SameLine();
            DrawGroupPill(Loc.Get("Group.CoGm"), MasterEventTheme.AccentText);
        }

        if (!isGmSection && session.IsAwaitingRoll(player.Hash))
        {
            ImGui.SameLine();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(MasterEventTheme.PendingColor, FontAwesomeIcon.Dice.ToIconString());
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Group.RollPending"));
                ImGui.EndTooltip();
            }
        }

        ImGui.SameLine();
        DrawGroupConnection(player.IsConnected);

        // Demande de jet pour qui peut modifier ; menu réservé au vrai MJ, pour les joueurs non MJ.
        var canAskRoll = !isGmSection && !player.IsGm && session.CanEdit && session.IsConnected;
        var hasMenu = !isGmSection && session.IsGm && !player.IsGm;
        var kickId = "group_kick_" + player.Hash;

        if (canAskRoll || hasMenu)
        {
            var askLabel = Loc.Get("Group.AskRoll");
            var actionsWidth = (canAskRoll ? Buttons.TextWidth(askLabel) : 0f)
                               + (canAskRoll && hasMenu ? ImGui.GetStyle().ItemSpacing.X : 0f)
                               + (hasMenu ? Buttons.IconWidth(FontAwesomeIcon.EllipsisH) : 0f);
            AlignGroupActions(rowStartX, actionsWidth);

            if (canAskRoll)
            {
                if (Buttons.Tinted($"{askLabel}##ask_roll_{player.Hash}"))
                    RollRequestWindowRef?.Open(player);
                if (hasMenu) ImGui.SameLine();
            }

            if (hasMenu)
            {
                if (Buttons.QuietIcon(FontAwesomeIcon.EllipsisH, "member_menu_" + player.Hash, Loc.Get("Sheets.More")))
                    ImGui.OpenPopup("##member_menu_" + player.Hash);
                DrawGroupMemberMenu(player, kickId);
            }
        }

        if (ConfirmInline.DrawQuestion(kickId, string.Format(Loc.Get("Lobby.RemoveConfirmFmt"), player.Name),
                Loc.Get("Lobby.RemoveConfirm")))
            session.RemoveLobbyPlayer(player.Hash);

        if (isGmSection) return;

        if (session.CanEdit)
        {
            var vitals = new PlayerVitals(player, session);
            var vitalsId = "p" + player.Hash;
            VitalsEditor.DrawBars(vitals, vitalsId, session.HpMode, session.ShowShield, session.ShowMpBar, session.MpMode);

            if (configuration.ShowPlayerStatsInline && player.Stats is { Count: > 0 } inlineStats)
            {
                var statLine = string.Join("   ", inlineStats.Select(st =>
                {
                    var mod = StatDisplay.Stat(st.Modifier);
                    return $"{st.Name} {mod}";
                }));

                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + LayoutControls.CardContentWidth);
                ImGui.TextColored(MasterEventTheme.TextDim, statLine);
                ImGui.PopTextWrapPos();
            }

            VitalsEditor.DrawCounters(vitals, vitalsId);
            VitalsEditor.DrawTempModifier(vitals, vitalsId);
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

    private void DrawGroupMemberMenu(PlayerData player, string kickId)
    {
        if (!ImGui.BeginPopup("##member_menu_" + player.Hash)) return;

        if (ImGui.Selectable(Loc.Get(player.CanEdit ? "Group.Demote" : "Group.Promote")))
            session.PromotePlayer(player.Hash, !player.CanEdit);

        if (player.IsLobbyPlayer)
        {
            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.DangerColor);
            if (ImGui.Selectable(Loc.Get("Lobby.Remove")))
                ConfirmInline.Open(kickId);
            ImGui.PopStyleColor();
        }

        ImGui.EndPopup();
    }

    private static void DrawGroupConnection(bool connected)
    {
        var icon = connected ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.Unlink;
        var iconColor = connected ? MasterEventTheme.SuccessColor : MasterEventTheme.TextDim;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(iconColor, icon.ToIconString());
        ImGui.SameLine(0, 4f * ImGuiHelpers.GlobalScale);
        ImGui.TextColored(connected ? MasterEventTheme.TextSecondary : MasterEventTheme.TextDim,
            Loc.Get(connected ? "Group.Connected" : "Group.Disconnected"));
    }

    private static void DrawGroupPill(string text, Vector4 color)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var padX = 6f * scale;
        var textSize = ImGui.CalcTextSize(text);
        var frameHeight = ImGui.GetFrameHeight();
        var pillHeight = textSize.Y + 4f * scale;
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(textSize.X + padX * 2f, frameHeight));

        var min = new Vector2(pos.X, pos.Y + (frameHeight - pillHeight) / 2f);
        var max = new Vector2(min.X + textSize.X + padX * 2f, min.Y + pillHeight);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(min, max, ImGui.GetColorU32(color with { W = 0.16f }), pillHeight / 2f);
        dl.AddText(new Vector2(min.X + padX, min.Y + 2f * scale), ImGui.GetColorU32(color), text);
    }

    private static void AlignGroupActions(float rowStartX, float actionsWidth)
    {
        ImGui.SameLine();
        var target = rowStartX + LayoutControls.CardContentWidth - actionsWidth;
        if (ImGui.GetCursorPosX() > target)
            ImGui.NewLine();
        ImGui.SetCursorPosX(Math.Max(rowStartX, target));
    }
}
