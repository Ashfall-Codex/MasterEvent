using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;

namespace MasterEvent.UI;

public sealed partial class GmWindow
{
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

        // Progress counter — compte les "blocs" (groupes et solos) qui ont joué, pas les entries individuelles
        var (blocksTotal, blocksActed) = CountBlocks(state);
        var progressText = string.Format(Loc.Get("Turns.Progress"), blocksActed, blocksTotal);
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), progressText);

        ImGuiHelpers.ScaledDummy(2f);

        // Liste des participants (avec regroupements)
        if (ImGui.BeginChild("##turns_scroll", Vector2.Zero))
        {
            var seenGroupIds = new HashSet<string>();
            for (var i = 0; i < state.Entries.Count; i++)
            {
                var entry = state.Entries[i];

                // Header de groupe : dessiné avant le premier membre rencontré
                if (entry.GroupId != null && seenGroupIds.Add(entry.GroupId))
                {
                    var group = state.Groups.FirstOrDefault(g => g.Id == entry.GroupId);
                    if (group != null)
                        DrawGroupHeader(group, state, i);
                }

                // Ligne entry avec indentation si membre d'un groupe
                var isGrouped = entry.GroupId != null;
                if (isGrouped) ImGui.Indent(16f);
                DrawEntryRow(state, entry, i);
                if (isGrouped) ImGui.Unindent(16f);
            }
        }
        ImGui.EndChild();
    }

    // Compte les blocs (groupe = 1 bloc, solo = 1 bloc) et combien ont joué.
    private static (int Total, int Acted) CountBlocks(TurnState state)
    {
        var total = 0;
        var acted = 0;
        var seen = new HashSet<string>();
        foreach (var entry in state.Entries)
        {
            if (entry.GroupId != null)
            {
                if (!seen.Add(entry.GroupId)) continue;
                var group = state.Groups.FirstOrDefault(g => g.Id == entry.GroupId);
                total++;
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

    private void DrawGroupHeader(TurnGroup group, TurnState state, int firstMemberIdx)
    {
        ImGui.PushID("grp_" + group.Id);

        // Checkbox HasActed partagé
        var hasActed = group.HasActed;
        if (ImGui.Checkbox("##grp_acted", ref hasActed))
            session.ToggleHasActed(firstMemberIdx);
        ImGui.SameLine();

        // Icône chaîne + label éditable
        var linkIcon = FontAwesomeIcon.Link.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(new Vector4(0.8f, 0.6f, 0.2f, 1f), linkIcon);
        ImGui.SameLine();

        var label = group.Label;
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##grp_label", ref label, 64))
            session.RenameGroup(group.Id, label);

        // Boutons à droite : Move group up/down
        var upIcon = FontAwesomeIcon.AngleDoubleUp.ToIconString();
        var downIcon = FontAwesomeIcon.AngleDoubleDown.ToIconString();
        float upW, downW;
        var framePad = ImGui.GetStyle().FramePadding.X * 2;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            upW = ImGui.CalcTextSize(upIcon).X + framePad;
            downW = ImGui.CalcTextSize(downIcon).X + framePad;
        }
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var rightPos = ImGui.GetContentRegionMax().X - (upW + downW + spacing);
        if (rightPos > ImGui.GetCursorPosX())
            ImGui.SameLine(rightPos);

        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(upIcon + "##grp_up"))
                session.MoveGroupUp(group.Id);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Turns.MoveGroupUp"));
            ImGui.EndTooltip();
        }

        ImGui.SameLine();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(downIcon + "##grp_down"))
                session.MoveGroupDown(group.Id);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Turns.MoveGroupDown"));
            ImGui.EndTooltip();
        }

        ImGui.PopID();
    }

    private void DrawEntryRow(TurnState state, TurnEntry entry, int i)
    {
        ImGui.PushID(i);

        var isGrouped = entry.GroupId != null;
        var blockActed = state.HasEntryActed(entry);

        // Checkbox HasActed individuelle (cachée pour les entries dans un groupe — géré par le header)
        if (!isGrouped)
        {
            var hasActed = entry.HasActed;
            if (ImGui.Checkbox("##acted_" + i, ref hasActed))
                session.ToggleHasActed(i);
            ImGui.SameLine();
        }
        else
        {
            // Espaceur pour aligner visuellement avec les solos
            var cbW = ImGui.GetFrameHeight();
            ImGui.Dummy(new Vector2(cbW, cbW));
            ImGui.SameLine();
        }

        // Icône : waymark ou user
        var iconSize = ImGui.GetFrameHeight();
        if (entry.IsMarker && entry.WaymarkIndex.HasValue)
        {
            var waymarkId = (WaymarkId)entry.WaymarkIndex.Value;
            var iconId = waymarkId.ToIconId();
            var wrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(iconSize, iconSize));
            ImGui.SameLine();
        }
        else if (entry.IsNpc)
        {
            var npcIcon = FontAwesomeIcon.UserFriends.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(new Vector4(0.68f, 0.50f, 0.92f, 0.9f), npcIcon);
            ImGui.SameLine();
        }
        else
        {
            var userIcon = FontAwesomeIcon.User.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(new Vector4(0.227f, 0.604f, 1f, 0.8f), userIcon);
            ImGui.SameLine();
        }

        // Nom — grisé si le bloc a joué
        var nameColor = blockActed ? new Vector4(0.5f, 0.5f, 0.5f, 1f) : new Vector4(1f, 1f, 1f, 1f);
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

        // Boutons d'action à droite
        var upIcon = FontAwesomeIcon.ChevronUp.ToIconString();
        var downIcon = FontAwesomeIcon.ChevronDown.ToIconString();
        var mergeIcon = isGrouped ? FontAwesomeIcon.Unlink.ToIconString() : FontAwesomeIcon.Link.ToIconString();
        var diceIcon = FontAwesomeIcon.Dice.ToIconString();
        var trashIcon = FontAwesomeIcon.Trash.ToIconString();
        float upW, downW, mergeW, diceW, trashW;
        var framePad = ImGui.GetStyle().FramePadding.X * 2;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            upW = ImGui.CalcTextSize(upIcon).X + framePad;
            downW = ImGui.CalcTextSize(downIcon).X + framePad;
            mergeW = ImGui.CalcTextSize(mergeIcon).X + framePad;
            diceW = ImGui.CalcTextSize(diceIcon).X + framePad;
            trashW = ImGui.CalcTextSize(trashIcon).X + framePad;
        }
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var rightPos = ImGui.GetContentRegionMax().X - (upW + downW + mergeW + diceW + trashW + spacing * 4);
        if (rightPos > ImGui.GetCursorPosX())
            ImGui.SameLine(rightPos);

        // Move up — autorisé si le voisin supérieur est du même groupe (ou tous deux solos)
        var canMoveUp = i > 0 && state.Entries[i - 1].GroupId == entry.GroupId;
        if (!canMoveUp) ImGui.BeginDisabled();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(upIcon + "##up"))
                session.MoveParticipantUp(i);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Turns.MoveUp"));
            ImGui.EndTooltip();
        }
        if (!canMoveUp) ImGui.EndDisabled();

        // Move down
        ImGui.SameLine();
        var canMoveDown = i < state.Entries.Count - 1 && state.Entries[i + 1].GroupId == entry.GroupId;
        if (!canMoveDown) ImGui.BeginDisabled();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(downIcon + "##down"))
                session.MoveParticipantDown(i);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Turns.MoveDown"));
            ImGui.EndTooltip();
        }
        if (!canMoveDown) ImGui.EndDisabled();

        // Merge / Unmerge
        ImGui.SameLine();
        DrawMergeButton(state, entry, i, mergeIcon);

        // Re-roll
        ImGui.SameLine();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(diceIcon + "##reroll"))
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
            if (ImGui.Button(trashIcon + "##remove"))
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

    // Bouton de fusion/défusion selon l'état de l'entry et de ses voisins.
    private void DrawMergeButton(TurnState state, TurnEntry entry, int i, string iconStr)
    {
        // Cas 1 : déjà dans un groupe → bouton "Détacher"
        if (entry.GroupId != null)
        {
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                if (ImGui.Button(iconStr + "##unmerge"))
                    session.RemoveFromGroup(i);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Turns.Unmerge"));
                ImGui.EndTooltip();
            }
            return;
        }

        // Cas 2 : solo — fusionner avec le voisin suivant si solo, ou rejoindre le groupe précédent
        var nextIdx = i + 1;
        var hasNextSolo = nextIdx < state.Entries.Count && state.Entries[nextIdx].GroupId == null;
        var prevIdx = i - 1;
        var prevGroupId = prevIdx >= 0 ? state.Entries[prevIdx].GroupId : null;

        if (!hasNextSolo && prevGroupId == null) ImGui.BeginDisabled();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(iconStr + "##merge"))
            {
                if (hasNextSolo)
                    session.CreateGroup(i, nextIdx);
                else if (prevGroupId != null)
                    session.AddToGroup(i, prevGroupId);
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            var tipKey = hasNextSolo ? "Turns.MergeWithNext" : prevGroupId != null ? "Turns.MergeWithPrevious" : "Turns.MergeWithNext";
            ImGui.TextUnformatted(Loc.Get(tipKey));
            ImGui.EndTooltip();
        }
        if (!hasNextSolo && prevGroupId == null) ImGui.EndDisabled();
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

        var existingNpcs = session.CurrentTurnState?.Entries
            .Where(e => e.NpcId != null).Select(e => e.NpcId!).ToHashSet() ?? [];

        foreach (var npc in npcManager?.Instances.Where(n => !n.IsReplicated && n.IsAlive) ?? [])
        {
            var npcId = npc.NetworkId.ToString("N");
            if (existingNpcs.Contains(npcId)) continue;

            hasItems = true;
            var npcIcon = FontAwesomeIcon.UserFriends.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(new Vector4(0.7f, 0.55f, 0.9f, 0.9f), npcIcon);
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

        // Joueurs disponibles (non encore dans le combat)
        foreach (var player in session.PartyMembers
                     .Where(p => (!p.IsGm || session.GmIsPlayer) && !existingPlayers.Contains(p.Hash)))
        {

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
}
