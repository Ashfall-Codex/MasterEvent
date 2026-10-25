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

    private void DrawTurnsContent()
    {
        var state = session.CurrentTurnState;

        if (state is not { IsActive: true })
        {
            // Combat terminé ailleurs (co-MJ, déconnexion) : la question de fin n'a plus d'objet.
            if (ConfirmInline.IsOpen(TurnsEndConfirmId)) ConfirmInline.Close();

            PageHeader.Draw(FontAwesomeIcon.ListOl, Loc.Get("Turns.Title"));
            if (LayoutControls.DrawEmptyState(FontAwesomeIcon.ListOl, Loc.Get("Turns.NoEncounterTitle"),
                    Loc.Get("Turns.StartTooltip"), Loc.Get("Turns.Start"), FontAwesomeIcon.Play))
                session.StartEncounter();
            return;
        }

        // Combat en cours : le dé d'initiative passe dans le sous-titre, le tour reste en tête de barre.
        PageHeader.Draw(FontAwesomeIcon.ListOl, Loc.Get("Turns.Title"),
            string.Format(Loc.Get("Turns.SubtitleFmt"), state.DiceMax));
        DrawTurnsHeader(state);
        ImGuiHelpers.ScaledDummy(2f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

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

    // Barre du combat : le tour à gauche, l'unique action principale (tour suivant) à droite ; dessous,
    // la progression et les actions secondaires (ajouter, menu). « Terminer » demande confirmation.
    private void DrawTurnsHeader(TurnState state)
    {
        var style = ImGui.GetStyle();
        var height = ImGui.GetFrameHeight() * 1.2f;
        var totalWidth = ImGui.GetContentRegionAvail().X - CardRightMargin;

        // Ligne 1 : « Tour 1 » et « Tour suivant »
        var nextLabel = Loc.Get("Turns.NextRound");
        var nextWidth = Buttons.PrimaryWidth(nextLabel);
        var origin = ImGui.GetCursorScreenPos();

        ImGui.SetCursorScreenPos(origin + new Vector2(0, (height - ImGui.GetTextLineHeight()) / 2f));
        ImGui.TextColored(MasterEventTheme.AccentText, string.Format(Loc.Get("Turns.Round"), state.Round));

        ImGui.SetCursorScreenPos(new Vector2(origin.X + totalWidth - nextWidth, origin.Y));
        if (PrimaryButton(nextLabel + "##next_round", nextWidth, height))
            session.NextRound();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Turns.NextRoundTooltip"));

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + height + 4f * ImGuiHelpers.GlobalScale));

        // Ligne 2 : confirmation de fin, ou progression + actions secondaires
        if (ConfirmInline.IsOpen(TurnsEndConfirmId))
        {
            if (ConfirmInline.DrawQuestion(TurnsEndConfirmId, Loc.Get("Turns.EndConfirm"), Loc.Get("Turns.EndConfirmYes")))
                session.EndEncounter();
            return;
        }

        var (blocksTotal, blocksActed) = CountBlocks(state);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(MasterEventTheme.MutedTextColor,
            string.Format(Loc.Get("Turns.Progress"), blocksActed, blocksTotal));

        var iconSpacing = style.ItemSpacing.X;
        var actionsWidth = IconButtonWidth(FontAwesomeIcon.Plus) + iconSpacing + IconButtonWidth(FontAwesomeIcon.EllipsisH);
        ImGui.SameLine(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - actionsWidth - CardRightMargin);

        if (QuietIconButton(FontAwesomeIcon.Plus, "add_participant", Loc.Get("Turns.AddParticipant")))
            ImGui.OpenPopup("##add_participant_popup");
        DrawAddParticipantPopup(state);
        ImGui.SameLine();

        if (QuietIconButton(FontAwesomeIcon.EllipsisH, "turns_menu", Loc.Get("Sheets.More")))
            ImGui.OpenPopup("##turns_menu");

        if (ImGui.BeginPopup("##turns_menu"))
        {
            if (ImGui.Selectable(Loc.Get("Turns.RerollAll")))
                session.RerollAllInitiative();
            if (ImGui.Selectable(Loc.Get("Turns.Sync")))
                session.BroadcastTurnState();

            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.DangerColor);
            if (ImGui.Selectable(Loc.Get("Turns.EndEncounter")))
                ConfirmInline.Open(TurnsEndConfirmId);
            ImGui.PopStyleColor();
            ImGui.EndPopup();
        }
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

    private void DrawEntryRollPopup(TurnEntry entry)
    {
        if (!ImGui.BeginPopup("##roll_stat_popup")) return;

        if (ResolveTurnEntity(entry) is { } entity)
        {
            DiceControls.DrawRollStatMenu(entity, "turn_entry", statId => session.RollDiceFor(entity, statId));
        }
        else
        {
            // PNJ retiré entre-temps, ou marqueur effacé : on le dit plutôt que d'afficher un
            // menu vide sans explication.
            ImGui.TextDisabled(Loc.Get("Turns.RollUnavailable"));
        }

        ImGui.EndPopup();
    }

    private void DrawEntryRow(TurnState state, TurnEntry entry, int i)
    {
        ImGui.PushID(i);

        var isGrouped = entry.GroupId != null;
        var blockActed = state.HasEntryActed(entry);

        // Checkbox HasActed individuelle (cachée pour les entries d'un groupe : l'en-tête du groupe la porte)
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
            // Violet des PNJ, comme dans le bandeau tactique.
            var npcIcon = FontAwesomeIcon.UserFriends.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(MasterEventTheme.NpcColor, npcIcon);
            ImGui.SameLine();
        }
        else
        {
            var portraitName = entry.PlayerHash is { } hash
                ? session.PartyMembers.FirstOrDefault(p => p.Hash == hash)?.Name ?? entry.Name
                : entry.Name;

            if (!DrawPortraitThumbnail(portraitName, blockActed ? 0.45f : 1f))
            {
                var userIcon = FontAwesomeIcon.User.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    ImGui.TextColored(MasterEventTheme.PlayerColor, userIcon);
                ImGui.SameLine();
            }
        }

        // Nom, grisé si le bloc a joué
        var nameColor = blockActed ? MasterEventTheme.TextDim : MasterEventTheme.TextStrong;
        ImGui.TextColored(nameColor, entry.Name);
        ImGui.SameLine();

        // Initiative
        ImGui.TextColored(MasterEventTheme.MutedTextColor, $"[{entry.Initiative}]");
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

        // Actions à droite : ordre, jet de stat (marqueur ou PNJ), puis le menu des actions rares.
        // Un joueur lance ses propres dés depuis sa fenêtre : pas de jet pour lui ici.
        var canRollFor = entry.IsMarker || entry.IsNpc;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var buttonCount = canRollFor ? 4 : 3;
        var rightPos = ImGui.GetContentRegionMax().X
            - (IconButtonWidth(FontAwesomeIcon.ChevronUp) + IconButtonWidth(FontAwesomeIcon.ChevronDown)
               + IconButtonWidth(FontAwesomeIcon.EllipsisH)
               + (canRollFor ? IconButtonWidth(FontAwesomeIcon.DiceD20) : 0f) + spacing * buttonCount);
        if (rightPos > ImGui.GetCursorPosX())
            ImGui.SameLine(rightPos);

        // Monter / descendre : autorisé si le voisin est du même groupe (ou tous deux solos)
        var canMoveUp = i > 0 && state.Entries[i - 1].GroupId == entry.GroupId;
        ImGui.BeginDisabled(!canMoveUp);
        if (QuietIconButton(FontAwesomeIcon.ChevronUp, "up", Loc.Get("Turns.MoveUp")))
            session.MoveParticipantUp(i);
        ImGui.EndDisabled();
        ImGui.SameLine();

        var canMoveDown = i < state.Entries.Count - 1 && state.Entries[i + 1].GroupId == entry.GroupId;
        ImGui.BeginDisabled(!canMoveDown);
        if (QuietIconButton(FontAwesomeIcon.ChevronDown, "down", Loc.Get("Turns.MoveDown")))
            session.MoveParticipantDown(i);
        ImGui.EndDisabled();

        if (canRollFor)
        {
            ImGui.SameLine();
            if (QuietIconButton(FontAwesomeIcon.DiceD20, "roll_stat", Loc.Get("Turns.RollFor")))
                ImGui.OpenPopup("##roll_stat_popup");
            DrawEntryRollPopup(entry);
        }

        ImGui.SameLine();
        if (QuietIconButton(FontAwesomeIcon.EllipsisH, "entry_menu", Loc.Get("Sheets.More")))
            ImGui.OpenPopup("##entry_menu");

        if (ImGui.BeginPopup("##entry_menu"))
        {
            if (ImGui.Selectable(Loc.Get("Turns.Reroll")))
                session.RerollInitiative(i);

            DrawMergeMenuItem(state, entry, i);

            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.DangerColor);
            if (ImGui.Selectable(Loc.Get("Turns.Remove")))
                session.RemoveTurnParticipant(i);
            ImGui.PopStyleColor();
            ImGui.EndPopup();
        }

        ImGui.PopID();
        ImGui.Spacing();
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

        // Joueurs disponibles (non encore dans le combat)
        foreach (var player in session.PartyMembers
                     .Where(p => (!p.IsGm || session.GmIsPlayer) && !existingPlayers.Contains(p.Hash)))
        {

            hasItems = true;
            var userIcon = FontAwesomeIcon.User.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(MasterEventTheme.PlayerColor, userIcon);
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
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Turns.NoAvailableParticipants"));

        ImGui.EndPopup();
    }
}
