using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Services;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

// Ce que la vue montre sans séance à suivre : une phrase qui dit pourquoi, et la seule action utile.
public sealed partial class PlayerWindow
{
    private void DrawStateContent(bool compact)
    {
        switch (viewState)
        {
            case PlayerViewState.OutOfSession:
                DrawJoinCard();
                break;
            case PlayerViewState.Joining:
                LayoutControls.DrawEmptyState(FontAwesomeIcon.SyncAlt, Loc.Get("PlayerView.LinkJoining"),
                    Loc.Get("PlayerView.OnFieldEmptyHint"), compact: compact);
                break;
            case PlayerViewState.Offline:
                DrawOfflineState(compact);
                break;
            case PlayerViewState.AwaitingApproval:
                LayoutControls.DrawEmptyState(FontAwesomeIcon.HourglassHalf, Loc.Get("PlayerView.PendingTitle"),
                    string.Format(Loc.Get("PlayerView.PendingHintFmt"), session.LobbyCode ?? string.Empty),
                    compact: compact);
                // Le relais n'a pas encore de salle pour ce joueur : il n'y a rien à annuler, seulement à quitter.
                ImGuiHelpers.ScaledDummy(8f);
                if (CenteredQuiet(Loc.Get("Lobby.Leave"), "player_pending_leave"))
                    onLeaveAlliance?.Invoke();
                break;
            case PlayerViewState.JoinRejected:
                DrawRejectedState(compact);
                break;
            case PlayerViewState.VersionRejected:
                LayoutControls.DrawEmptyState(FontAwesomeIcon.ExclamationTriangle, Loc.Get("PlayerView.VersionTitle"),
                    VersionHint(), compact: compact);
                break;
            case PlayerViewState.Instance:
                LayoutControls.DrawEmptyState(FontAwesomeIcon.PauseCircle, Loc.Get("PlayerView.InstanceTitle"),
                    Loc.Get("PlayerView.InstanceHint"), compact: compact);
                break;
        }
    }

    private void DrawJoinCard()
    {
        LayoutControls.BeginCard(Loc.Get("Lobby.JoinTitle"), FontAwesomeIcon.DoorOpen);
        ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Lobby.JoinHint"));
        LobbyJoinControls.Draw("player_join", ref joinCodeInput, code => onJoinAlliance?.Invoke(code));
        LayoutControls.EndCard();
    }

    // La reconnexion passe par le chemin habituel du plugin, qui demande aussi l'accord s'il manque.
    private void DrawOfflineState(bool compact)
    {
        var hint = configuration.IsRgpdConsentValid ? "PlayerView.OfflineHint" : "PlayerView.OfflineConsentHint";
        if (LayoutControls.DrawEmptyState(FontAwesomeIcon.Unlink, Loc.Get("Gm.Local"), Loc.Get(hint),
                Loc.Get("PlayerView.Reconnect"), compact: compact))
            onReconnect?.Invoke();

        ImGuiHelpers.ScaledDummy(4f);
        if (openSettings != null && CenteredQuiet(Loc.Get("PlayerView.OpenSettings"), "player_open_settings"))
            openSettings();

        // Rejoindre reconnecte aussi : teinté, pour laisser « Se reconnecter » seul en plein.
        ImGuiHelpers.ScaledDummy(12f);
        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Lobby.JoinTitle"));
        LobbyJoinControls.Draw("player_join_offline", ref joinCodeInput, code => onJoinAlliance?.Invoke(code),
            tinted: true);
    }

    private void DrawRejectedState(bool compact)
    {
        LayoutControls.DrawEmptyState(FontAwesomeIcon.Ban, Loc.Get("PlayerView.RejectedTitle"), RejectionHint(),
            compact: compact);

        ImGuiHelpers.ScaledDummy(12f);
        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Lobby.JoinOther"));
        LobbyJoinControls.Draw("player_join_rejected", ref joinCodeInput, code => onJoinAlliance?.Invoke(code));

        ImGuiHelpers.ScaledDummy(8f);
        // Un MJ qui a refusé ne changera pas d'avis sur un nouvel essai ; un serveur plein, si.
        if (session.JoinRejection != JoinRejection.Denied && CenteredQuiet(Loc.Get("PlayerView.Retry"), "player_retry"))
            onReconnect?.Invoke();
        if (session.IsLobbyMode && CenteredQuiet(Loc.Get("Lobby.Leave"), "player_rejected_leave"))
            onLeaveAlliance?.Invoke();
    }

    private string RejectionHint() => session.JoinRejection switch
    {
        JoinRejection.Denied when session.IsLobbyMode =>
            string.Format(Loc.Get("PlayerView.RejectedDeniedFmt"), session.LobbyCode),
        JoinRejection.RoomLimit => Loc.Get("PlayerView.RejectedRoomLimit"),
        JoinRejection.RateLimited => Loc.Get("PlayerView.RejectedRateLimited"),
        _ => Loc.Get("PlayerView.RejectedInvalid"),
    };

    private string VersionHint() =>
        string.Format(Loc.Get("PlayerView.VersionHintFmt"), session.RejectedMinVersion ?? string.Empty);

    private static bool CenteredQuiet(string label, string id)
    {
        var width = Buttons.TextWidth(label);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (ImGui.GetContentRegionAvail().X - width) / 2f));
        return Buttons.Quiet(label + "##" + id);
    }
}
