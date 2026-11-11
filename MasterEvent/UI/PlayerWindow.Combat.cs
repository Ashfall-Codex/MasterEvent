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

// En combat : l'ordre du tour, puis ce qui est visible hors de l'initiative.
public sealed partial class PlayerWindow
{
    // Bloc actif déjà montré : la liste n'y défile qu'une fois, quand il change.
    private string? scrolledActiveBlock;

    private void DrawCombatList(TurnState state)
    {
        CloseHiddenMarkerDetail();
        ListHeader.Draw(Loc.Get("Scene.TurnOrder"), null);
        ImGui.TextColored(MasterEventTheme.MutedTextColor, string.Format(Loc.Get("Turns.SubtitleFmt"), state.DiceMax));
        ImGuiHelpers.ScaledDummy(2f);

        var npcs = FieldNpcs();
        var activeIndex = session.ActiveTurnIndex;
        var activeEntry = activeIndex >= 0 && activeIndex < state.Entries.Count ? state.Entries[activeIndex] : null;
        // Tout le bloc est à lui quand il y joue : n'importe quel membre finit le tour du groupe.
        var myBlock = session.IsLocalPlayerTurn;

        var activeBlock = activeEntry == null ? null : $"{state.Round}:{activeEntry.GroupId ?? activeIndex.ToString()}";
        var scrollPending = activeBlock != null && activeBlock != scrolledActiveBlock;
        scrolledActiveBlock = activeBlock;

        var indent = 16f * ImGuiHelpers.GlobalScale;
        var seenGroups = new HashSet<string>();
        for (var i = 0; i < state.Entries.Count; i++)
        {
            var entry = state.Entries[i];
            var isActive = activeEntry != null
                           && (i == activeIndex || (entry.GroupId != null && entry.GroupId == activeEntry.GroupId));
            var top = ImGui.GetCursorScreenPos().Y;

            // Le MJ peut effacer le nom d'un groupe : l'en-tête n'aurait plus que son icône.
            if (entry.GroupId != null && seenGroups.Add(entry.GroupId) && state.FindGroupFor(entry) is { } group)
                TurnGroupHeader.Draw(string.IsNullOrWhiteSpace(group.Label) ? Loc.Get("Turns.GroupUnnamed") : group.Label);

            var grouped = entry.GroupId != null;
            if (grouped) ImGui.Indent(indent);
            DrawTurnRow(state, entry, i, isActive, myBlock, npcs);
            if (grouped) ImGui.Unindent(indent);

            if (isActive && scrollPending)
            {
                ScrollIntoView(top, ImGui.GetCursorScreenPos().Y);
                scrollPending = false;
            }
        }

        DrawAlsoOnField(state, npcs);
    }

    private void DrawTurnRow(TurnState state, TurnEntry entry, int index, bool isActive, bool myBlock,
        List<NpcInstance> npcs)
    {
        var acted = state.HasEntryActed(entry);
        var mine = entry.PlayerHash != null && entry.PlayerHash == session.LocalPlayerHash;
        var row = new ActorRow.Model
        {
            Id = "tr_" + index,
            Name = entry.Name,
            Prefix = entry.Initiative.ToString(),
            PrefixTooltip = TurnText.InitiativeTooltip(entry),
            Active = isActive,
            Dimmed = acted,
            // Lue par bloc : un membre de groupe a joué quand son groupe a joué.
            Acted = acted,
        };
        if (mine) row.Pills.Add((Loc.Get("PlayerView.YouPill"), MasterEventTheme.PlayerColor with { W = 1f }));

        Action? onClick = null;
        if (entry.WaymarkIndex is { } wi && wi >= 0 && wi < Constants.WaymarkCount)
        {
            var id = (WaymarkId)wi;
            var marker = session.CurrentMarkers[id];
            row.WaymarkIcon = id.ToIconId();
            if (IsShownMarker(marker))
            {
                FillMarker(row, id, marker);
                onClick = () => ToggleMarkerDetail(id);
            }
            else
            {
                // Caché ou sans nom : sa place dans l'ordre, rien de plus.
                row.HasBar = false;
            }
        }
        else if (entry.NpcId != null)
        {
            row.GlyphIcon = FontAwesomeIcon.UserFriends;
            row.GlyphColor = MasterEventTheme.NpcColor;
            if (FindNpc(npcs, entry.NpcId) is { } npc) FillNpc(row, npc);
            else row.HasBar = false;
        }
        else if (entry.PlayerHash is { } hash && session.PartyMembers.FirstOrDefault(p => p.Hash == hash) is { } player)
        {
            FillPlayer(row, player);
            if (mine)
            {
                onClick = FocusMySheet;
            }
            else
            {
                row.Tooltip = PlayerRowTooltip(player);
                onClick = () => OpenPlayerSheet?.Invoke(player);
            }
        }
        else
        {
            row.GlyphColor = MasterEventTheme.TextDim;
            row.HasBar = false;
        }

        // « À vous » sur sa seule ligne : la barre dit déjà que tout le bloc est à lui.
        if (isActive)
            row.Pills.Add((Loc.Get(mine && myBlock ? "PlayerView.YourTurnPill" : "Scene.TheirTurn"), MasterEventTheme.PendingColor));

        if (ActorRow.Draw(row, session.HpMode))
            onClick?.Invoke();
        if (row.Selected)
            DrawMarkerDetail(session.CurrentMarkers[(WaymarkId)entry.WaymarkIndex!.Value]);
    }

    // Ce qui est visible mais hors de l'initiative : marqueurs, PNJ et joueurs absents de l'ordre.
    private void DrawAlsoOnField(TurnState state, List<NpcInstance> npcs)
    {
        var markers = ShownMarkers().Where(id => state.Entries.All(e => e.WaymarkIndex != (int)id)).ToList();
        var extraNpcs = npcs.Where(n => state.Entries.All(e => !IsSameNpc(e.NpcId, n))).ToList();
        var players = OtherPlayers(SessionPlayers())
            .Where(p => state.Entries.All(e => e.PlayerHash != p.Hash)).ToList();
        if (markers.Count + extraNpcs.Count + players.Count == 0) return;

        ImGuiHelpers.ScaledDummy(4f);
        ListHeader.Draw(Loc.Get("PlayerView.AlsoOnField"), null, titleColor: MasterEventTheme.MutedTextColor);
        // La colonne de coche reste réservée : les glyphes s'alignent sur ceux de l'ordre du tour.
        foreach (var id in markers)
            DrawFieldMarkerRow("tr_mk_", id, reserveCheck: true);
        foreach (var npc in extraNpcs)
            DrawFieldNpcRow("tr_npc_", npc, reserveCheck: true);
        foreach (var player in players)
            DrawFieldPlayerRow("tr_pl_", player, reserveCheck: true);
    }

    // Un PNJ d'un autre territoire n'est pas dans la liste : il garde sa place dans l'ordre, sans barre.
    private static NpcInstance? FindNpc(List<NpcInstance> npcs, string npcId)
        => npcs.FirstOrDefault(n => IsSameNpc(npcId, n));

    private static bool IsSameNpc(string? npcId, NpcInstance npc)
        => npcId != null && Guid.TryParse(npcId, out var id) && id == npc.NetworkId;

    private static void ScrollIntoView(float top, float bottom)
    {
        var windowTop = ImGui.GetWindowPos().Y;
        if (top >= windowTop && bottom <= windowTop + ImGui.GetWindowHeight()) return;
        ImGui.SetScrollFromPosY(top - windowTop, 0.25f);
    }
}
