using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Communication;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

// Barre de séance : l'état de la salle en pastilles à gauche, les actions discrètes puis l'unique
// action pleine à droite. Lobby, demandes d'accès, ambiance et annonce y remplacent l'écran Groupe.
public sealed partial class GmWindow
{
    private void DrawSceneBar(bool gmAccess, bool combat)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var state = session.CurrentTurnState;

        ImGuiHelpers.ScaledDummy(4f);

        // Fin de combat demandée : la question prend toute la barre, rien d'autre n'attire le clic.
        if (!combat && ConfirmInline.IsOpen(TurnsEndConfirmId)) ConfirmInline.Close();
        if (combat && ConfirmInline.IsOpen(TurnsEndConfirmId))
        {
            if (ConfirmInline.DrawQuestion(TurnsEndConfirmId, Loc.Get("Turns.EndConfirm"), Loc.Get("Turns.EndConfirmYes")))
                session.EndEncounter();
            SessionBar.End();
            return;
        }

        var members = session.PartyMembers.ToList();
        var chips = BuildSceneChips(gmAccess, combat, members);
        var icons = SceneBarIcons(gmAccess, combat);

        // Le nom de qui joue se coupe pour que l'action principale tienne toujours sur sa ligne.
        var nameBudget = Math.Clamp(SessionBar.LineWidth - SessionBar.IconsWidth(icons)
                                    - Buttons.PrimaryWidth(Loc.Get("Scene.EndTurnFmt")), 40f * scale, 160f * scale);
        var (primaryLabel, primaryTooltip, primaryAction) = ScenePrimaryAction(gmAccess, nameBudget);

        var content = new SessionBar.Content
        {
            Chips = chips,
            Icons = icons,
            Primary = primaryLabel != null ? new SessionBar.BarAction(primaryLabel, "scene_primary", primaryTooltip) : null,
        };
        if (combat)
        {
            var (total, acted) = state!.CountBlocks();
            content.Lead = string.Format(Loc.Get("Turns.Round"), state.Round);
            content.LeadDetail = string.Format(Loc.Get("Turns.Progress"), acted, total);
        }

        if (SessionBar.Draw(content) == SessionBar.Clicked.Primary && ScenePrimaryReady())
        {
            primaryAction?.Invoke();
            scenePrimaryChangedAt = Environment.TickCount64;
        }

        DrawSceneBarPopups(gmAccess, combat, members);
        SessionBar.End();
    }

    private List<SessionBar.Chip> BuildSceneChips(bool gmAccess, bool combat, List<PlayerData> members)
    {
        var chips = new List<SessionBar.Chip>();
        var (linkIcon, linkColor, linkText) = LinkStatus.Describe(session.LinkState, session.ConnectedPlayerCount);
        var tooltip = session.ActiveTemplate is { } template
            ? $"{linkText}\n{Loc.Get("Player.CurrentTemplate")} {template.Name}"
            : linkText;

        // Ouvrir ou fermer un lobby reste réservé au MJ, comme l'ancien écran Groupe.
        Action? openSession = gmAccess ? () => ImGui.OpenPopup("##scene_session_popup") : null;
        if (session.IsLobbyMode)
        {
            var players = members.Where(p => !p.IsGm || session.GmIsPlayer).ToList();
            var text = string.Format(Loc.Get("Scene.LobbyFmt"), session.LobbyCode) + " · "
                       + string.Format(Loc.Get("Scene.OnlineFmt"), players.Count(p => p.IsConnected), players.Count);
            chips.Add(new SessionBar.Chip("session", FontAwesomeIcon.DoorOpen, MasterEventTheme.AccentText, text,
                MasterEventTheme.TextStrong, null, tooltip, openSession));
        }
        else
        {
            chips.Add(new SessionBar.Chip("session", linkIcon, linkColor, SceneLinkText(), MasterEventTheme.TextStrong, null,
                tooltip, openSession));
        }

        if (gmAccess && !combat)
        {
            chips.Add(new SessionBar.Chip("ambiance", FontAwesomeIcon.CloudSunRain, MasterEventTheme.TextSecondary,
                SceneAmbianceText(), MasterEventTheme.TextStrong, null, Loc.Get("Scene.AmbianceTooltip"),
                () => activeTab = Tab.Weather));
        }

        // Mêmes droits que le badge de la barre latérale et l'ancien écran Groupe.
        var pending = session.PendingGroupCount;
        if (pending > 0 && gmAccess && (session.IsGm || session.IsPromoted))
        {
            var text = pending == 1 ? Loc.Get("Scene.RequestsOne") : string.Format(Loc.Get("Scene.RequestsFmt"), pending);
            chips.Add(new SessionBar.Chip("pending", FontAwesomeIcon.UserClock, MasterEventTheme.PendingColor, text,
                MasterEventTheme.PendingColor, MasterEventTheme.PendingColor, Loc.Get("Lobby.PendingTitle"),
                () => ImGui.OpenPopup("##scene_pending_popup")));
        }

        return chips;
    }

    private string SceneLinkText() => session.LinkState switch
    {
        RelayLinkState.InSession => session.ConnectedPlayerCount == 1
            ? Loc.Get("Link.InSessionOne")
            : string.Format(Loc.Get("Link.InSession"), session.ConnectedPlayerCount),
        RelayLinkState.ServerOnly => Loc.Get("Gm.LinkServer"),
        RelayLinkState.Reconnecting => Loc.Get("Gm.LinkReconnecting"),
        _ => Loc.Get("Gm.Local"),
    };

    private string SceneAmbianceText()
    {
        var parts = new List<string>();
        if (session.CurrentWeatherId != 0)
            parts.Add(session.CurrentWeatherName ?? session.CurrentWeatherId.ToString());
        if (session.CurrentEorzeaTime is { } seconds)
            parts.Add($"{WeatherService.SecondsToHour(seconds):00}:00");
        return parts.Count > 0 ? string.Join(" · ", parts) : Loc.Get("Scene.AmbianceGame");
    }

    private const long ScenePrimaryInertMs = 600;
    private (ScenePrimaryKind Kind, int Index, int Round, int Encounter) scenePrimaryKey;
    private long scenePrimaryChangedAt;

    // Le bouton change d'action dès le premier clic : le second d'un double clic tomberait sur la suivante.
    private bool ScenePrimaryReady() =>
        ImGui.GetMouseClickedCount(ImGuiMouseButton.Left) < 2
        && Environment.TickCount64 - scenePrimaryChangedAt >= ScenePrimaryInertMs;

    // L'unique action pleine : lancer le combat, puis finir le tour de celui qui joue, puis la manche suivante.
    private (string? Label, string? Tooltip, Action? Action) ScenePrimaryAction(bool gmAccess, float nameBudget)
    {
        var (kind, index, name) = session.ResolveScenePrimary(gmAccess);
        var key = (kind, index, session.CurrentTurnState?.Round ?? 0, session.EncounterSerial);
        if (key != scenePrimaryKey)
        {
            scenePrimaryKey = key;
            scenePrimaryChangedAt = Environment.TickCount64;
        }

        switch (kind)
        {
            case ScenePrimaryKind.Start:
                return (Loc.Get("Turns.Start"), Loc.Get("Turns.StartTooltip"), () => sceneActions.StartEncounter());
            case ScenePrimaryKind.NextRound:
                return (Loc.Get("Turns.NextRound"), Loc.Get("Turns.NextRoundTooltip"),
                    () => sceneActions.NextRoundNow(force: false));
            case ScenePrimaryKind.EndTurn:
                return (string.Format(Loc.Get("Scene.EndTurnFmt"), TextFit.Fit(name ?? string.Empty, nameBudget)),
                    Loc.Get("Scene.EndTurnTooltip"), () => sceneActions.EndTurnAt(index));
            default:
                return (null, null, null);
        }
    }

    private List<SessionBar.Icon> SceneBarIcons(bool gmAccess, bool combat)
    {
        var icons = new List<SessionBar.Icon>
        {
            new(FontAwesomeIcon.Dice, "scene_dice", Loc.Get("Dice.Title"), () => ImGui.OpenPopup("##roll_history_popup")),
        };

        if (gmAccess)
            icons.Add(new(FontAwesomeIcon.Bullhorn, "scene_announce", Loc.Get("Gm.AnnounceTooltip"),
                () => requestOpenAnnouncePopup = true));
        if (combat)
            icons.Add(new(FontAwesomeIcon.Plus, "scene_add_participant", Loc.Get("Turns.AddParticipant"),
                () => ImGui.OpenPopup("##add_participant_popup")));

        icons.Add(new(FontAwesomeIcon.EllipsisH, "scene_more", Loc.Get("Sheets.More"), () => ImGui.OpenPopup("##scene_more")));
        return icons;
    }

    private void DrawSceneBarPopups(bool gmAccess, bool combat, List<PlayerData> members)
    {
        var scale = ImGuiHelpers.GlobalScale;

        ImGui.SetNextWindowSize(new Vector2(360f * scale, 0));
        if (ImGui.BeginPopup("##scene_session_popup"))
        {
            DrawGroupLobbyCard(members);
            if (gmAccess) DrawGroupGmCard(members);
            ImGui.EndPopup();
        }

        ImGui.SetNextWindowSize(new Vector2(380f * scale, 0));
        if (ImGui.BeginPopup("##scene_pending_popup"))
        {
            // File vidée (tout le monde admis ou refusé) : la fenêtre n'a plus rien à dire.
            if (session.PendingMembers.Count == 0) ImGui.CloseCurrentPopup();
            DrawPendingRequests();
            ImGui.EndPopup();
        }

        ImGui.SetNextWindowSize(new Vector2(320f * scale, 0));
        if (ImGui.BeginPopup("##roll_history_popup"))
        {
            DrawGmFreeRoll();
            DiceControls.DrawRollHistory(session, maxEntries: 20, showClearButton: true);
            ImGui.EndPopup();
        }

        if (combat && session.CurrentTurnState is { } state)
            DrawAddParticipantPopup(state);

        if (!ImGui.BeginPopup("##scene_more")) return;

        if (ImGui.Selectable(Loc.Get("Gm.UpdateTooltip")))
        {
            session.SyncWaymarks();
            session.BroadcastUpdate();
        }

        if (combat)
        {
            // Passer à la manche suivante sans attendre ceux qui n'ont pas joué, comme le permettait l'ancien bouton.
            if (session.ActiveTurnIndex >= 0 && ImGui.Selectable(Loc.Get("Turns.NextRound")))
                sceneActions.NextRoundNow(force: true);
            if (ImGui.Selectable(Loc.Get("Turns.RerollAll")))
                session.RerollAllInitiative();
            if (ImGui.Selectable(Loc.Get("Turns.Sync")))
                session.BroadcastTurnState();

            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.DangerColor);
            if (ImGui.Selectable(Loc.Get("Turns.EndEncounter")))
                ConfirmInline.Open(TurnsEndConfirmId);
            ImGui.PopStyleColor();
        }

        ImGui.EndPopup();
    }
}
