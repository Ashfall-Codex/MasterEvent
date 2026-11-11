using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

public sealed partial class GmWindow
{
    private const string TurnsEndConfirmId = "turns_end";

    private void DrawGroupHeader(TurnGroup group, TurnState state, int firstMemberIdx)
    {
        ImGui.PushID("grp_" + group.Id);

        // Checkbox HasActed partagé
        var hasActed = group.HasActed;
        if (ImGui.Checkbox("##grp_acted", ref hasActed))
            sceneActions.SetActedAt(firstMemberIdx, hasActed);
        ImGui.SameLine();

        // Icône chaîne + label éditable
        var linkIcon = FontAwesomeIcon.Link.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.TextSecondary, linkIcon);
        ImGui.SameLine();

        var label = group.Label;
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##grp_label", ref label, 64))
            session.RenameGroup(group.Id, label);

        // Boutons à droite : déplacer le groupe
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var rightPos = ImGui.GetContentRegionMax().X
            - (IconButtonWidth(FontAwesomeIcon.AngleDoubleUp) + IconButtonWidth(FontAwesomeIcon.AngleDoubleDown) + spacing);
        if (rightPos > ImGui.GetCursorPosX())
            ImGui.SameLine(rightPos);

        if (QuietIconButton(FontAwesomeIcon.AngleDoubleUp, "grp_up", Loc.Get("Turns.MoveGroupUp")))
            session.MoveGroupUp(group.Id);
        ImGui.SameLine();
        if (QuietIconButton(FontAwesomeIcon.AngleDoubleDown, "grp_down", Loc.Get("Turns.MoveGroupDown")))
            session.MoveGroupDown(group.Id);

        ImGui.PopID();
    }

    private IVitalEntity? ResolveTurnEntity(TurnEntry entry)
    {
        if (entry.IsMarker && entry.WaymarkIndex is { } wi
            && wi >= 0 && wi < Constants.WaymarkCount)
            return session.CurrentMarkers.Markers[wi];

        if (entry.NpcId is { } npcId && Guid.TryParse(npcId, out var netId))
            return npcManager?.FindByNetworkId(netId);

        return null;
    }

    // Entrée de menu de regroupement / détachement selon l'état de l'entrée et de ses voisines.
    private void DrawMergeMenuItem(TurnState state, TurnEntry entry, int i)
    {
        // Déjà dans un groupe : on peut seulement le quitter.
        if (entry.GroupId != null)
        {
            if (ImGui.Selectable(Loc.Get("Turns.Unmerge")))
                session.RemoveFromGroup(i);
            return;
        }

        // Solo : se regrouper avec la suivante si elle est solo, sinon rejoindre le groupe précédent.
        var nextIdx = i + 1;
        var hasNextSolo = nextIdx < state.Entries.Count && state.Entries[nextIdx].GroupId == null;
        var prevGroupId = i > 0 ? state.Entries[i - 1].GroupId : null;
        var canMerge = hasNextSolo || prevGroupId != null;

        ImGui.BeginDisabled(!canMerge);
        var label = !hasNextSolo && prevGroupId != null ? "Turns.MergeWithPrevious" : "Turns.MergeWithNext";
        if (ImGui.Selectable(Loc.Get(label)))
        {
            if (hasNextSolo) session.CreateGroup(i, nextIdx);
            else if (prevGroupId != null) session.AddToGroup(i, prevGroupId);
        }
        ImGui.EndDisabled();
    }

    private void DrawAddParticipantPopup(TurnState state)
    {
        if (!ImGui.BeginPopup("##add_participant_popup")) return;

        // Collecter les participants déjà présents
        var existingWaymarks = new HashSet<int>(
            state.Entries.Where(e => e.WaymarkIndex.HasValue).Select(e => e.WaymarkIndex!.Value));
        var existingPlayers = new HashSet<string>(
            state.Entries.Where(e => e.PlayerHash != null).Select(e => e.PlayerHash!));

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

        hasItems |= DrawAddNpcItems();

        // Joueurs disponibles (non encore dans le combat)
        hasItems |= DrawAddPlayerItems(existingPlayers);

        if (!hasItems)
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Turns.NoAvailableParticipants"));

        ImGui.EndPopup();
    }

    private bool DrawAddNpcItems()
    {
        var hasItems = false;
        var existingNpcs = session.CurrentTurnState?.Entries
            .Where(e => e.NpcId != null).Select(e => e.NpcId!).ToHashSet() ?? [];

        foreach (var npc in npcManager?.Instances.Where(n => !n.IsReplicated && n.IsAlive) ?? [])
        {
            var npcId = npc.NetworkId.ToString("N");
            if (existingNpcs.Contains(npcId)) continue;

            hasItems = true;
            var npcIcon = FontAwesomeIcon.UserFriends.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(MasterEventTheme.NpcColor, npcIcon);
            ImGui.SameLine();
            if (ImGui.Selectable(npc.DisplayName + "##add_n_" + npcId))
            {
                session.AddTurnParticipant(new TurnEntry
                {
                    NpcId = npcId,
                    Name = npc.DisplayName,
                });
                ImGui.CloseCurrentPopup();
            }
        }

        return hasItems;
    }

    private bool DrawAddPlayerItems(HashSet<string> existingPlayers)
    {
        var hasItems = false;
        foreach (var player in session.PartyMembers
                     .Where(p => (!p.IsGm || session.GmIsPlayer) && !existingPlayers.Contains(p.Hash)))
        {
            hasItems = true;
            if (UmbraPortraits?.GetForPlayer(player, session.LocalPlayerHash) is { } portrait)
            {
                var side = ImGui.GetTextLineHeight();
                ActorRow.DrawPortrait(ImGui.GetWindowDrawList(), portrait, ImGui.GetCursorScreenPos(), side);
                ImGui.Dummy(new Vector2(side, side));
            }
            else
            {
                var userIcon = FontAwesomeIcon.User.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(MasterEventTheme.PlayerColor, userIcon);
            }
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

        return hasItems;
    }
}
