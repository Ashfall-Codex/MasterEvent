using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services.Npc;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

// Hors combat : « Sur le terrain », puis « Joueurs ». Tout se lit, rien ne se règle : un marqueur
// déplie son détail, un joueur ouvre sa fiche.
public sealed partial class PlayerWindow
{
    // Un seul détail ouvert à la fois, comme l'inspecteur du MJ.
    private WaymarkId? expandedMarker;

    private void DrawSceneList()
    {
        CloseHiddenMarkerDetail();
        var markers = ShownMarkers();
        var npcs = FieldNpcs();
        var others = OtherPlayers(SessionPlayers());
        var count = markers.Count + npcs.Count;

        ListHeader.Draw(Loc.Get("Scene.OnField"), count > 0 ? count.ToString() : null);
        if (count == 0)
            LayoutControls.DrawEmptyState(FontAwesomeIcon.MapMarkerAlt, Loc.Get("Terrain.EmptyTitle"),
                Loc.Get("PlayerView.OnFieldEmptyHint"), compact: others.Count > 0);

        foreach (var id in markers)
            DrawFieldMarkerRow("sc_mk_", id, reserveCheck: false);
        foreach (var npc in npcs)
            DrawFieldNpcRow("sc_npc_", npc, reserveCheck: false);

        if (others.Count == 0) return;

        ImGuiHelpers.ScaledDummy(4f);
        ListHeader.Draw(Loc.Get("Group.Players"), others.Count.ToString());
        foreach (var player in others)
            DrawFieldPlayerRow("sc_pl_", player, reserveCheck: false);
    }

    // Les PNJ que le MJ fait vivre dans la scène. Ceux d'un ancien MJ, restés chez ce client, n'en sont pas.
    // En aperçu, ce sont ceux que ce MJ diffuse.
    private List<NpcInstance> FieldNpcs() => viewState == PlayerViewState.GmPreview
        ? npcManager.Instances.Where(n => !n.IsReplicated && n.IsAlive && !n.HoldBroadcast).ToList()
        : npcManager.Instances.Where(n => n.IsReplicated && n.IsAlive).ToList();

    // Un marqueur caché ou sans nom n'existe pas pour les joueurs.
    private static bool IsShownMarker(MarkerData marker) => marker.IsVisible && !string.IsNullOrEmpty(marker.Name);

    private List<WaymarkId> ShownMarkers()
    {
        var shown = new List<WaymarkId>();
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            var id = (WaymarkId)i;
            if (IsShownMarker(session.CurrentMarkers[id])) shown.Add(id);
        }
        return shown;
    }

    // Soi-même a sa carte à côté : la liste ne montre que les autres.
    private List<PlayerData> OtherPlayers(List<PlayerData> players)
        => players.Where(p => p.Hash != session.LocalPlayerHash).ToList();

    private void CloseHiddenMarkerDetail()
    {
        if (expandedMarker is { } id && !IsShownMarker(session.CurrentMarkers[id]))
            expandedMarker = null;
    }

    private void ToggleMarkerDetail(WaymarkId id) => expandedMarker = expandedMarker == id ? null : id;

    private void DrawFieldMarkerRow(string idPrefix, WaymarkId id, bool reserveCheck)
    {
        var marker = session.CurrentMarkers[id];
        var row = new ActorRow.Model
        {
            Id = idPrefix + (int)id,
            Name = marker.Name,
            WaymarkIcon = id.ToIconId(),
            ReserveCheck = reserveCheck,
        };
        FillMarker(row, id, marker);

        if (ActorRow.Draw(row, session.HpMode))
            ToggleMarkerDetail(id);
        if (row.Selected)
            DrawMarkerDetail(marker);
    }

    private void DrawFieldNpcRow(string idPrefix, NpcInstance npc, bool reserveCheck)
    {
        var row = new ActorRow.Model
        {
            Id = idPrefix + npc.NetworkId,
            Name = npc.DisplayName,
            ReserveCheck = reserveCheck,
        };
        FillNpc(row, npc);
        // Rien à déplier : PE, bonus et dernier jet d'un PNJ ne sont pas transmis aux joueurs.
        ActorRow.Draw(row, session.HpMode);
    }

    private void DrawFieldPlayerRow(string idPrefix, PlayerData player, bool reserveCheck)
    {
        var row = new ActorRow.Model
        {
            Id = idPrefix + player.Hash,
            Name = player.Name,
            NameMuted = !player.IsConnected,
            Connected = player.IsConnected,
            ReserveCheck = reserveCheck,
        };
        FillPlayer(row, player);
        if (player.CanEdit) row.Pills.Add((Loc.Get("Group.CoGm"), MasterEventTheme.AccentText));
        row.Tooltip = PlayerRowTooltip(player);

        if (ActorRow.Draw(row, session.HpMode))
            OpenPlayerSheet?.Invoke(player);
    }

    private string? PlayerRowTooltip(PlayerData player)
    {
        var open = OpenPlayerSheet != null ? string.Format(Loc.Get("PlayerView.OpenSheetFmt"), player.Name) : null;
        if (player.IsConnected) return open;
        var away = Loc.Get("Group.Disconnected");
        return open != null ? open + "\n" + away : away;
    }

    // Jamais les stats : celles des marqueurs ne sont pas envoyées aux joueurs, celles des PNJ ne doivent
    // pas se lire ici.
    private void FillMarker(ActorRow.Model row, WaymarkId id, MarkerData marker)
    {
        row.Selected = expandedMarker == id;
        row.Trailing = AttitudeStyle.Label(marker.Attitude);
        row.TrailingColor = AttitudeStyle.Color(marker.Attitude);
        row.Hp = marker.Hp;
        row.HpMax = marker.HpMax;
        row.Shield = session.ShowShield ? marker.Shield : 0;
        row.BarColor = AttitudeStyle.Color(marker.Attitude);
        if (marker.IsBoss) row.Pills.Add((Loc.Get("Marker.Boss"), MasterEventTheme.AccentText));
    }

    private void FillNpc(ActorRow.Model row, NpcInstance npc)
    {
        row.GlyphIcon = npc.IsCreature ? FontAwesomeIcon.Dragon : FontAwesomeIcon.UserFriends;
        row.GlyphColor = MasterEventTheme.NpcColor;
        row.Trailing = AttitudeStyle.Label(npc.Attitude);
        row.TrailingColor = AttitudeStyle.Color(npc.Attitude);
        row.Hp = npc.Hp;
        row.HpMax = npc.HpMax;
        row.Shield = session.ShowShield ? npc.Shield : 0;
        row.BarColor = AttitudeStyle.Color(npc.Attitude);
        if (npc.IsBoss) row.Pills.Add((Loc.Get("Marker.Boss"), MasterEventTheme.AccentText));
    }

    // Ni lettre ni couleur de sous-groupe : chaque client les attribue à sa façon.
    private void FillPlayer(ActorRow.Model row, PlayerData player)
    {
        row.Portrait = UmbraPortraits?.GetForPlayer(player, session.LocalPlayerHash);
        row.GlyphIcon = FontAwesomeIcon.User;
        row.GlyphColor = MasterEventTheme.PlayerColor;
        row.Hp = player.Hp;
        row.HpMax = player.HpMax;
        row.Shield = session.ShowShield ? player.Shield : 0;
        row.BarColor = MasterEventTheme.PlayerColor;
    }

    // Sous la ligne ouverte, en retrait : ce que le MJ partage d'un marqueur en plus de ses PV.
    private void DrawMarkerDetail(MarkerData marker)
    {
        var indent = 28f * ImGuiHelpers.GlobalScale;
        ImGui.Indent(indent);
        var width = Math.Max(1f, ImGui.GetContentRegionAvail().X - MasterEventTheme.ListRightMargin);
        var shown = false;

        if (session.ShowMpBar)
        {
            HpBar.DrawMpBar(marker.Mp, width, session.MpMode, marker.MpMax);
            shown = true;
        }

        if (marker.Counters is { Count: > 0 } counters)
        {
            foreach (var counter in counters)
                CounterBar.Draw(counter, width);
            shown = true;
        }

        if (marker.TempModifier != 0)
        {
            DrawTempModifierPill(marker.TempModifier, marker.TempModTurns);
            shown = true;
        }

        if (marker.LastRollResult > 0)
        {
            ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("PlayerView.LastRoll"));
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.TextStrong, $"{marker.LastRollResult} / {marker.LastRollMax}");
            shown = true;
        }

        if (!shown)
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("PlayerView.NoDetail"));

        ImGui.Unindent(indent);
        ImGuiHelpers.ScaledDummy(2f);
    }
}
