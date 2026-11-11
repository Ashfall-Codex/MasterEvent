using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MasterEvent.Communication;
using MasterEvent.Localization;
using MasterEvent.Services;
using MasterEvent.Services.Remote;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

// Barre de séance du joueur : où il en est à gauche, les dés et le menu à droite, et une action
// pleine seulement quand on l'attend, jet demandé ou fin de son tour.
public sealed partial class PlayerWindow
{
    private const string SessionPopupId = "##player_session";
    private const string DicePopupId = "##player_dice";
    private const string MorePopupId = "##player_more";
    private const string LeaveConfirmId = "player_leave_lobby";
    private const float DicePopupWidth = 320f;
    private const long PrimaryInertMs = 600;

    private (PlayerPrimaryKind Kind, string? Stat, bool EndTurn, int Round, int Active, int Encounter) primaryKey;
    private long primaryChangedAt;
    private bool placeDicePopup;

    private void DrawBar()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGuiHelpers.ScaledDummy(4f);

        var icons = BarIcons();
        var primary = viewState == PlayerViewState.GmPreview
            ? default
            : session.ResolvePlayerPrimary(inInstance());
        var key = (primary.Kind, primary.StatName, primary.CanEndTurn, session.CurrentTurnState?.Round ?? 0,
            session.ActiveTurnIndex, session.EncounterSerial);
        if (key != primaryKey)
        {
            primaryKey = key;
            primaryChangedAt = Environment.TickCount64;
        }

        var endTurn = primary.CanEndTurn ? EndTurnAction(primary) : null;
        SessionBar.BarAction? primaryAction = null;
        SessionBar.BarAction? tinted = null;
        Action? onPrimary = null;
        switch (primary.Kind)
        {
            case PlayerPrimaryKind.Roll:
                // Le nom de la stat se coupe pour que l'action tienne sur la ligne, fin de tour comprise.
                var spacing = ImGui.GetStyle().ItemSpacing.X;
                var tintedWidth = endTurn != null ? Buttons.TextWidth(endTurn.Label) + spacing : 0f;
                var budget = Math.Clamp(SessionBar.LineWidth - SessionBar.IconsWidth(icons) - tintedWidth
                                        - Buttons.PrimaryWidth(string.Format(Loc.Get("PlayerView.RollStatFmt"), string.Empty)),
                    40f * scale, 160f * scale);
                var label = primary.StatName is { } stat
                    ? string.Format(Loc.Get("PlayerView.RollStatFmt"), TextFit.Fit(stat, budget))
                    : Loc.Get("PlayerView.RollNoStat");
                primaryAction = new SessionBar.BarAction(label, "player_primary", null);
                onPrimary = session.AnswerRollRequest;
                tinted = endTurn;
                break;
            case PlayerPrimaryKind.EndTurn:
                primaryAction = endTurn;
                onPrimary = session.RequestEndOwnTurn;
                break;
        }

        var content = new SessionBar.Content
        {
            Chips = BuildChips(),
            Icons = icons,
            Tinted = tinted,
            Primary = primaryAction,
        };
        if (combatShown && session.CurrentTurnState is { } turns)
        {
            var (total, acted) = turns.CountBlocks();
            content.Lead = string.Format(Loc.Get("Turns.Round"), turns.Round);
            content.LeadDetail = string.Format(Loc.Get("Turns.Progress"), acted, total);
        }

        var clicked = SessionBar.Draw(content);
        if (clicked != SessionBar.Clicked.None && PrimaryReady())
        {
            if (clicked == SessionBar.Clicked.Primary) onPrimary?.Invoke();
            else session.RequestEndOwnTurn();
            primaryChangedAt = lastRollAt = Environment.TickCount64;
        }

        if (requestOpenDice)
        {
            requestOpenDice = false;
            placeDicePopup = true;
            ImGui.OpenPopup(DicePopupId);
        }

        DrawBarPopups();
        SessionBar.End();
    }

    // Le bouton change d'action dès le premier clic : le second d'un double clic tomberait sur la suivante.
    private bool PrimaryReady() =>
        ImGui.GetMouseClickedCount(ImGuiMouseButton.Left) < 2
        && Environment.TickCount64 - primaryChangedAt >= PrimaryInertMs;

    private long lastRollAt;

    // Un jet part au relais et le premier prend la demande du MJ : le second clic d'un double clic,
    // sur la même tuile ou sur ce que la fenêtre Dés refermée découvre, ne relance rien.
    private void RollOnce(Action roll)
    {
        if (Environment.TickCount64 - lastRollAt < PrimaryInertMs) return;
        lastRollAt = Environment.TickCount64;
        roll();
    }

    // Grisée, la fin de tour dit pourquoi : déjà envoyée, ou personne pour la recevoir.
    private static SessionBar.BarAction EndTurnAction(PlayerPrimary primary)
    {
        const string id = "player_end_turn";
        var label = Loc.Get(primary.GroupTurn ? "Tactical.EndGroupTurn" : "Tactical.EndMyTurn");
        return primary.Hold switch
        {
            EndTurnHold.Sent => new SessionBar.BarAction(Loc.Get("PlayerView.EndTurnSent"), id, null, Disabled: true),
            EndTurnHold.Handover => new SessionBar.BarAction(label, id, Loc.Get("PlayerView.EndTurnHandover"), Disabled: true),
            EndTurnHold.GmAway => new SessionBar.BarAction(label, id, Loc.Get("PlayerView.EndTurnGmAway"), Disabled: true),
            _ => new SessionBar.BarAction(label, id, Loc.Get("PlayerView.EndMyTurnTooltip")),
        };
    }

    private List<SessionBar.Icon> BarIcons() =>
    [
        new(FontAwesomeIcon.Dice, "player_dice", Loc.Get("Dice.Title"), () => ImGui.OpenPopup(DicePopupId)),
        new(FontAwesomeIcon.EllipsisH, "player_more", Loc.Get("Sheets.More"), () => ImGui.OpenPopup(MorePopupId)),
    ];

    private List<SessionBar.Chip> BuildChips()
    {
        var chips = new List<SessionBar.Chip> { SessionChip() };

        if (!combatShown && IsSessionLike(viewState) && AmbianceText() is { } ambiance)
        {
            chips.Add(new SessionBar.Chip("ambiance", FontAwesomeIcon.CloudSunRain, MasterEventTheme.TextSecondary,
                ambiance, MasterEventTheme.TextStrong, null, Loc.Get("PlayerView.AmbianceTooltip"), null));
        }

        if (combatShown) chips.Add(TurnChip());
        return chips;
    }

    private SessionBar.Chip SessionChip()
    {
        // Les états hors séance portent leur action dans le corps de la vue ; l'aperçu du MJ ne quitte rien.
        Action? open = viewState is PlayerViewState.InSession or PlayerViewState.Handover or PlayerViewState.GmAway
            or PlayerViewState.Reconnecting or PlayerViewState.Joining
            ? () => ImGui.OpenPopup(SessionPopupId)
            : null;
        var code = session.LobbyCode ?? string.Empty;

        return viewState switch
        {
            PlayerViewState.Instance => QuietChip(FontAwesomeIcon.PauseCircle, Loc.Get("PlayerView.InstanceChip"),
                Loc.Get("PlayerView.InstanceHint"), null),
            PlayerViewState.VersionRejected => TintedChip(FontAwesomeIcon.ExclamationTriangle, MasterEventTheme.DangerColor,
                Loc.Get("PlayerView.VersionChip"), VersionHint(), null),
            PlayerViewState.JoinRejected => TintedChip(FontAwesomeIcon.Ban, MasterEventTheme.DangerColor,
                session.IsLobbyMode
                    ? string.Format(Loc.Get("PlayerView.LobbyRejectedFmt"), code)
                    : Loc.Get("PlayerView.RejectedTitle"),
                RejectionHint(), null),
            PlayerViewState.AwaitingApproval => TintedChip(FontAwesomeIcon.HourglassHalf, MasterEventTheme.PendingColor,
                string.Format(Loc.Get("PlayerView.LobbyPendingFmt"), code),
                string.Format(Loc.Get("PlayerView.PendingHintFmt"), code), null),
            PlayerViewState.Offline => TintedChip(FontAwesomeIcon.Unlink, MasterEventTheme.DangerColor, Loc.Get("Gm.Local"),
                Loc.Get(configuration.IsRgpdConsentValid ? "PlayerView.OfflineHint" : "PlayerView.OfflineConsentHint"), null),
            PlayerViewState.Reconnecting => TintedChip(FontAwesomeIcon.SyncAlt, MasterEventTheme.WarningColor,
                Loc.Get("Gm.LinkReconnecting"), Loc.Get("PlayerView.ReconnectingNotice"), open),
            PlayerViewState.OutOfSession => QuietChip(FontAwesomeIcon.Cloud, Loc.Get("PlayerView.LinkServer"),
                string.Empty, null),
            PlayerViewState.Joining => QuietChip(FontAwesomeIcon.SyncAlt, Loc.Get("PlayerView.LinkJoining"),
                string.Empty, open),
            PlayerViewState.Handover => TintedChip(FontAwesomeIcon.ExchangeAlt, MasterEventTheme.PendingColor,
                Loc.Get("PlayerView.Handover"), Loc.Get("PlayerView.HandoverTooltip"), open),
            PlayerViewState.GmAway => TintedChip(FontAwesomeIcon.UserSlash, MasterEventTheme.WarningColor,
                session.IsLobbyMode
                    ? string.Format(Loc.Get("PlayerView.LobbyGmAwayFmt"), code)
                    : Loc.Get("PlayerView.GmAway"),
                Loc.Get("PlayerView.GmAwayTooltip"), open),
            _ => RoomChip(open),
        };
    }

    private static SessionBar.Chip QuietChip(FontAwesomeIcon icon, string text, string tooltip, Action? onClick)
        => new("session", icon, MasterEventTheme.MutedTextColor, text, MasterEventTheme.TextSecondary, null, tooltip,
            onClick);

    private static SessionBar.Chip TintedChip(FontAwesomeIcon icon, Vector4 color, string text, string tooltip,
        Action? onClick)
        => new("session", icon, color, text, color, color, tooltip, onClick);

    private SessionBar.Chip RoomChip(Action? onClick)
    {
        var tooltip = RoomTooltip();
        if (session.IsLobbyMode)
        {
            var players = SessionPlayers();
            var online = players.Count(p => p.IsConnected);
            var full = string.Format(Loc.Get("Scene.LobbyFmt"), session.LobbyCode) + " · "
                       + string.Format(Loc.Get("Scene.OnlineFmt"), online, players.Count);
            // Étroite, la puce se raccourcit pour laisser « Manche 3 » et sa progression sur la même ligne.
            if (!narrowLayout)
                return new SessionBar.Chip("session", FontAwesomeIcon.DoorOpen, MasterEventTheme.AccentText, full,
                    MasterEventTheme.TextStrong, null, tooltip, onClick);

            var shortText = string.Format(Loc.Get("PlayerView.LobbyShortFmt"), session.LobbyCode, online, players.Count);
            return new SessionBar.Chip("session", FontAwesomeIcon.DoorOpen, MasterEventTheme.AccentText, shortText,
                MasterEventTheme.TextStrong, null, tooltip.Length > 0 ? full + "\n" + tooltip : full, onClick);
        }

        var (icon, color, linkText) = LinkStatus.Describe(session.LinkState, session.ConnectedPlayerCount);
        var text = session.LinkState switch
        {
            RelayLinkState.InSession => linkText,
            RelayLinkState.ServerOnly => Loc.Get("Gm.LinkServer"),
            RelayLinkState.Reconnecting => Loc.Get("Gm.LinkReconnecting"),
            _ => Loc.Get("Gm.Local"),
        };
        return new SessionBar.Chip("session", icon, color, text, MasterEventTheme.TextStrong, null, tooltip, onClick);
    }

    private string RoomTooltip()
    {
        var lines = new List<string>();
        if (session.GmPresence is { Name: { } gmName })
            lines.Add(string.Format(Loc.Get("PlayerView.GmFmt"), gmName));
        if (session.ActiveTemplate is { } template)
            lines.Add($"{Loc.Get("Player.CurrentTemplate")} {template.Name}");
        return string.Join("\n", lines);
    }

    // Seulement ce que le MJ a imposé : le joueur garde sinon la météo du jeu.
    private string? AmbianceText()
    {
        if (!session.IsAmbianceReceived && viewState != PlayerViewState.GmPreview) return null;

        var parts = new List<string>();
        if (session.CurrentWeatherId != 0)
            parts.Add(session.CurrentWeatherName ?? session.CurrentWeatherId.ToString());
        if (session.CurrentEorzeaTime is { } seconds)
            parts.Add($"{WeatherService.SecondsToHour(seconds):00}:00");
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private SessionBar.Chip TurnChip()
    {
        var state = session.CurrentTurnState!;
        var index = session.ActiveTurnIndex;
        if (index < 0)
            return new SessionBar.Chip("turn", FontAwesomeIcon.HourglassHalf, MasterEventTheme.MutedTextColor,
                Loc.Get("PlayerView.AllActed"), MasterEventTheme.MutedTextColor, null, string.Empty, null);

        if (session.IsLocalPlayerTurn)
            return new SessionBar.Chip("turn", FontAwesomeIcon.HourglassHalf, MasterEventTheme.PendingColor,
                Loc.Get("PlayerView.YourTurn"), MasterEventTheme.PendingColor, MasterEventTheme.PendingColor,
                string.Empty, null);

        // Un groupe sans nom laisserait la puce sans personne : on nomme celui qui joue.
        var entry = state.Entries[index];
        var label = state.FindGroupFor(entry)?.Label;
        var name = string.IsNullOrWhiteSpace(label) ? entry.Name : label;
        return new SessionBar.Chip("turn", FontAwesomeIcon.HourglassHalf, MasterEventTheme.TextSecondary,
            string.Format(Loc.Get("PlayerView.TurnOfFmt"), name), MasterEventTheme.TextStrong, null, string.Empty, null);
    }

    private void DrawBarPopups()
    {
        var scale = ImGuiHelpers.GlobalScale;

        ImGui.SetNextWindowSize(new Vector2(360f * scale, 0));
        if (ImGui.BeginPopup(SessionPopupId))
        {
            DrawSessionPopup();
            ImGui.EndPopup();
        }
        else if (ConfirmInline.IsOpen(LeaveConfirmId))
        {
            // Rouverte plus tard, la fenêtre ne doit pas reprendre sur la question.
            ConfirmInline.Close();
        }

        // Ouverte depuis la barre flottante, la souris est ailleurs : la fenêtre se pose sous l'icône.
        if (placeDicePopup)
        {
            placeDicePopup = false;
            var right = ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X;
            ImGui.SetNextWindowPos(new Vector2(right - DicePopupWidth * scale, ImGui.GetCursorScreenPos().Y),
                ImGuiCond.Appearing);
        }
        ImGui.SetNextWindowSize(new Vector2(DicePopupWidth * scale, 0));
        if (ImGui.BeginPopup(DicePopupId))
        {
            DrawDicePopup();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup(MorePopupId))
        {
            DrawMoreMenu();
            ImGui.EndPopup();
        }
    }

    private void DrawSessionPopup()
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(MasterEventTheme.AccentText, Loc.Get("PlayerView.SessionTitle"));
        ImGuiHelpers.ScaledDummy(2f);

        if (session.IsLobbyMode)
            ImGui.TextUnformatted(string.Format(Loc.Get("Scene.LobbyFmt"), session.LobbyCode));

        if (session.GmPresence is { } gm)
        {
            ImGui.TextUnformatted(gm.Name != null ? string.Format(Loc.Get("PlayerView.GmFmt"), gm.Name) : Loc.Get("Group.Gm"));
            ImGui.SameLine();
            LinkStatus.DrawPresence(gm.Connected);
        }

        if (session.ActiveTemplate is { } template)
            ImGui.TextUnformatted($"{Loc.Get("Player.CurrentTemplate")} {template.Name}");
        ImGui.PopTextWrapPos();

        // Pour le MJ qui participe, rejoindre un autre lobby ou quitter le sien laisserait ses joueurs
        // sans MJ, sans passation : c'est dans sa fenêtre, lobby fermé.
        if (session.IsLobbyMode && session.IsGm) return;

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        // Teinté : un jet demandé peut déjà occuper l'action pleine de la barre.
        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Lobby.JoinOther"));
        LobbyJoinControls.Draw("player_join_other", ref joinCodeInput, code =>
        {
            onJoinAlliance?.Invoke(code);
            ImGui.CloseCurrentPopup();
        }, tinted: true);

        if (!session.IsLobbyMode) return;

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
        if (ConfirmInline.Draw(LeaveConfirmId, Loc.Get("Lobby.Leave"),
                string.Format(Loc.Get("PlayerView.LeaveConfirmFmt"), session.LobbyCode), Loc.Get("PlayerView.LeaveConfirm")))
        {
            ImGui.CloseCurrentPopup();
            onLeaveAlliance?.Invoke();
        }
    }

    private void DrawDicePopup()
    {
        ImGui.TextColored(MasterEventTheme.AccentText, Loc.Get("Player.RollDice"));
        ImGuiHelpers.ScaledDummy(2f);

        // Sans personnage local connu, RollDiceForPlayer ne lancerait rien.
        var local = LocalPlayer;
        using (ImRaii.Disabled(local == null))
        {
            DiceControls.DrawDiceTile(Loc.Get("Dice.NoStat"), null, "player_roll_simple",
                ImGui.GetContentRegionAvail().X, 40f * ImGuiHelpers.GlobalScale, () =>
                {
                    RollOnce(() => session.RollDiceForPlayer(session.LocalPlayerHash));
                    ImGui.CloseCurrentPopup();
                });
        }

        if (local?.Stats is { Count: > 0 } stats)
        {
            ImGuiHelpers.ScaledDummy(4f);
            DiceControls.DrawRollStatMenu(stats, "player_dice",
                statId => RollOnce(() => session.RollDiceForPlayer(session.LocalPlayerHash, statId)), withNoStat: false);
        }

        DiceControls.DrawRollHistory(session, maxEntries: 20, showClearButton: true);
    }

    private void DrawMoreMenu()
    {
        // Poser les marqueurs est local : tout joueur peut le faire, pas seulement qui les édite.
        var anyMarker = session.CurrentMarkers.Markers.Any(m => m.IsVisible);
        if (ImGui.Selectable(Loc.Get("Player.ApplyMarkers"), false,
                anyMarker ? ImGuiSelectableFlags.None : ImGuiSelectableFlags.Disabled))
            ApplyMarkers();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.Get("Player.ApplyMarkersTooltip"));

        if (openSheets != null && ImGui.Selectable(Loc.Get("PlayerView.ManageSheets")))
            openSheets();
        if (toggleMainWindow != null && ImGui.Selectable(Loc.Get("Player.ToggleMainWindow")))
            toggleMainWindow();

        if (RemoteBridge is { } bridge) DrawRemoteMenu(bridge);
    }

    private void DrawRemoteMenu(PlayerRemoteBridge bridge)
    {
        // L'aperçu du MJ n'a pas de joueur à montrer ; un écran ouvert avant qu'il prenne la scène reste à couper.
        if (viewState != PlayerViewState.GmPreview)
        {
            ImGui.Separator();
            if (ImGui.Selectable(Loc.Get("PlayerRemote.Open")) && OpenRemoteLink(bridge) is { } url)
                Dalamud.Utility.Util.OpenLink(url);

            if (ImGui.Selectable(Loc.Get("PlayerRemote.CopyLink")) && OpenRemoteLink(bridge) is { } link)
            {
                ImGui.SetClipboardText(link);
                Plugin.ChatGui.Print(Loc.Get("PlayerRemote.LinkCopied"));
            }
        }

        if (!bridge.IsRunning) return;

        ImGui.Separator();
        var connected = bridge.SessionCount;
        ImGui.TextColored(MasterEventTheme.MutedTextColor, connected switch
        {
            0 => Loc.Get("PlayerRemote.StatusNone"),
            1 => Loc.Get("PlayerRemote.StatusOne"),
            _ => string.Format(Loc.Get("PlayerRemote.StatusFmt"), connected),
        });
        if (ImGui.Selectable(Loc.Get("PlayerRemote.Stop")))
            bridge.Stop();
    }

    // Ouvrir et copier émettent chacun un nouveau lien, qui invalide le précédent.
    private static string? OpenRemoteLink(PlayerRemoteBridge bridge)
    {
        var url = bridge.Open();
        if (url is null) Plugin.ChatGui.PrintError(Loc.Get("PlayerRemote.PortError"));
        return url;
    }
}
