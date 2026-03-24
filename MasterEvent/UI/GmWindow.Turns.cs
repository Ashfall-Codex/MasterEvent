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

        // Progress counter
        var actedCount = state.Entries.Count(e => e.HasActed);
        var progressText = string.Format(Loc.Get("Turns.Progress"), actedCount, state.Entries.Count);
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), progressText);

        ImGuiHelpers.ScaledDummy(2f);

        // Participant list
        if (ImGui.BeginChild("##turns_scroll", Vector2.Zero))
        {
            for (var i = 0; i < state.Entries.Count; i++)
            {
                var entry = state.Entries[i];

                ImGui.PushID(i);

                // Checkbox for HasActed
                var hasActed = entry.HasActed;
                if (ImGui.Checkbox("##acted_" + i, ref hasActed))
                    session.ToggleHasActed(i);
                ImGui.SameLine();

                // Icon: waymark or user
                var iconSize = ImGui.GetFrameHeight();
                if (entry.IsMarker && entry.WaymarkIndex.HasValue)
                {
                    var waymarkId = (WaymarkId)entry.WaymarkIndex.Value;
                    var iconId = waymarkId.ToIconId();
                    var wrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
                    ImGui.Image(wrap.Handle, new Vector2(iconSize, iconSize));
                    ImGui.SameLine();
                }
                else
                {
                    var userIcon = FontAwesomeIcon.User.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    {
                        ImGui.TextColored(new Vector4(0.227f, 0.604f, 1f, 0.8f), userIcon);
                    }
                    ImGui.SameLine();
                }

                // Name — greyed out if already acted
                var nameColor = entry.HasActed ? new Vector4(0.5f, 0.5f, 0.5f, 1f) : new Vector4(1f, 1f, 1f, 1f);
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

                // Action buttons on the right
                var upIcon = FontAwesomeIcon.ChevronUp.ToIconString();
                var downIcon = FontAwesomeIcon.ChevronDown.ToIconString();
                var diceIcon = FontAwesomeIcon.Dice.ToIconString();
                var trashIcon = FontAwesomeIcon.Trash.ToIconString();
                float upBtnW, downBtnW, diceBtnW, trashBtnW;
                var framePad = ImGui.GetStyle().FramePadding.X * 2;
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    upBtnW = ImGui.CalcTextSize(upIcon).X + framePad;
                    downBtnW = ImGui.CalcTextSize(downIcon).X + framePad;
                    diceBtnW = ImGui.CalcTextSize(diceIcon).X + framePad;
                    trashBtnW = ImGui.CalcTextSize(trashIcon).X + framePad;
                }
                var spacing = ImGui.GetStyle().ItemSpacing.X;
                var buttonsWidth = upBtnW + downBtnW + diceBtnW + trashBtnW + spacing * 3;
                var childWidth = ImGui.GetContentRegionMax().X;
                var rightPos = childWidth - buttonsWidth;
                if (rightPos > ImGui.GetCursorPosX())
                    ImGui.SameLine(rightPos);

                // Move up
                var isFirst = i == 0;
                if (isFirst) ImGui.BeginDisabled();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.Button(upIcon + "##up_" + i))
                        session.MoveParticipantUp(i);
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(Loc.Get("Turns.MoveUp"));
                    ImGui.EndTooltip();
                }
                if (isFirst) ImGui.EndDisabled();

                // Move down
                ImGui.SameLine();
                var isLast = i == state.Entries.Count - 1;
                if (isLast) ImGui.BeginDisabled();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.Button(downIcon + "##down_" + i))
                        session.MoveParticipantDown(i);
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(Loc.Get("Turns.MoveDown"));
                    ImGui.EndTooltip();
                }
                if (isLast) ImGui.EndDisabled();

                // Re-roll
                ImGui.SameLine();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.Button(diceIcon + "##reroll_" + i))
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
                    if (ImGui.Button(trashIcon + "##remove_" + i))
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
        }
        ImGui.EndChild();
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
