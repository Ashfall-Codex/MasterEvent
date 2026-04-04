using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

public sealed partial class GmWindow
{
    private void DrawGroupContent()
    {
        var availWidth = ImGui.GetContentRegionAvail().X;

        ImGuiHelpers.ScaledDummy(6f);

        var iconStr = FontAwesomeIcon.Users.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSz = ImGui.CalcTextSize(iconStr);
        const float scale = 1.6f;
        var scaledSz = iconSz * scale;
        var pos = ImGui.GetCursorScreenPos();
        var iconX = pos.X + (availWidth - scaledSz.X) / 2f;
        ImGui.Dummy(new Vector2(0, scaledSz.Y));
        var dl = ImGui.GetWindowDrawList();
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, new Vector2(iconX, pos.Y), ImGui.GetColorU32(MasterEventTheme.AccentColor), iconStr);
        ImGui.PopFont();

        ImGuiHelpers.ScaledDummy(4f);

        var titleSz = ImGui.CalcTextSize(Loc.Get("Group.Title"));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - titleSz.X) / 2f);
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Group.Title"));

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        // Section Mode Alliance
        if (!session.IsAllianceMode)
        {
            if (ImGui.Button(Loc.Get("Alliance.Enable") + "##enable_alliance"))
                onEnableAlliance?.Invoke();
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(Loc.Get("Alliance.EnableTooltip"));
                ImGui.EndTooltip();
            }
        }
        else
        {
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Alliance.RoomCode"));
            ImGui.SameLine();
            var code = session.AllianceRoomCode ?? "";
            var spaced = string.Join("  ", code.ToCharArray());
            ImGui.TextUnformatted(spaced);

            if (ImGui.Button(Loc.Get("Alliance.Copy") + "##copy_alliance"))
                ImGui.SetClipboardText(session.AllianceRoomCode ?? "");

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.7f, 0.2f, 0.2f, 1f));
            if (ImGui.Button(Loc.Get("Alliance.Disable") + "##disable_alliance"))
                onDisableAlliance?.Invoke();
            ImGui.PopStyleColor(2);
        }

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        if (ImGui.BeginChild("##group_scroll", Vector2.Zero))
        {
            // GM section
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Group.Gm"));
            ImGui.Spacing();

            var hasGm = false;
            foreach (var player in session.PartyMembers.Where(p => p.IsGm))
            {
                hasGm = true;
                DrawGroupMember(player, true);
            }

            if (!hasGm)
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "—");

            // GM as player checkbox
            var gmIsPlayer = session.GmIsPlayer;
            if (ImGui.Checkbox(Loc.Get("Group.GmIsPlayer"), ref gmIsPlayer))
            {
                session.GmIsPlayer = gmIsPlayer;
                configuration.GmIsPlayer = gmIsPlayer;
                configuration.Save();
                session.BroadcastPlayerUpdate();

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

            ImGuiHelpers.ScaledDummy(4f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);

            // Players section
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Group.Players"));
            ImGui.Spacing();

            var hasPlayers = false;
            foreach (var player in session.PartyMembers.Where(p => !p.IsGm || session.GmIsPlayer))
            {
                hasPlayers = true;
                DrawGroupMember(player, false);
                ImGui.Spacing();
            }

            if (!hasPlayers)
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Group.NoPlayers"));
        }
        ImGui.EndChild();
    }

    private void DrawGroupMember(PlayerData player, bool isGmSection)
    {
        var availWidth = ImGui.GetContentRegionAvail().X;

        // Crown icon for GM
        if (isGmSection)
        {
            var crownStr = FontAwesomeIcon.Crown.ToIconString();
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                ImGui.TextColored(MasterEventTheme.AccentColor, crownStr);
            }
            ImGui.SameLine();
        }

        // Player name
        ImGui.TextUnformatted(player.Name);
        ImGui.SameLine();

        // Co-GM badge
        if (!isGmSection && player.CanEdit)
        {
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Group.CoGm"));
            ImGui.SameLine();
        }

        // Connection indicator
        var connColor = player.IsConnected
            ? new Vector4(0.2f, 1f, 0.2f, 1f)
            : new Vector4(0.5f, 0.5f, 0.5f, 1f);
        var connTooltip = player.IsConnected
            ? Loc.Get("Group.Connected")
            : Loc.Get("Group.Disconnected");
        ImGui.TextColored(connColor, "●");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(connTooltip);
            ImGui.EndTooltip();
        }

        // Promote/demote button (only real GM can promote non-GM players)
        if (!isGmSection && session.IsGm && !player.IsGm)
        {
            ImGui.SameLine();
            var promoteIcon = player.CanEdit
                ? FontAwesomeIcon.UserMinus.ToIconString()
                : FontAwesomeIcon.UserPlus.ToIconString();
            var promoteTooltip = player.CanEdit
                ? Loc.Get("Group.Demote")
                : Loc.Get("Group.Promote");
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                if (ImGui.Button(promoteIcon + "##promote_" + player.Hash))
                    session.PromotePlayer(player.Hash, !player.CanEdit);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(promoteTooltip);
                ImGui.EndTooltip();
            }
        }

        // HP/EP/Shield/Counter controls (only for non-GM players)
        if (!isGmSection)
        {
            var pmBtnW = ImGui.GetFrameHeight();
            var barSpacing = ImGui.GetStyle().ItemSpacing.X;
            var barFramePad = ImGui.GetStyle().FramePadding.X * 2;
            float editBtnW;
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                editBtnW = ImGui.CalcTextSize(FontAwesomeIcon.Pen.ToIconString()).X + barFramePad;
            var barRightPad = editBtnW + barSpacing;

            if (session.CanEdit)
            {
                // --- Shield ---
                if (session.ShowShield)
                {
                    var shield = player.Shield;
                    var shieldMax = session.HpMode == HpMode.Percentage ? 100 : player.HpMax;
                    if (ImGui.Button($"-##psh_dec_{player.Hash}", new Vector2(pmBtnW, 0)))
                    {
                        shield = Math.Max(0, shield - 1);
                        session.SetPlayerShield(player.Hash, shield);
                    }
                    ImGui.SameLine();
                    ImGui.TextColored(MasterEventTheme.ShieldOverlayColor, $"{Loc.Get("Marker.Shield")}: {player.Shield}");
                    ImGui.SameLine();
                    if (ImGui.Button($"+##psh_inc_{player.Hash}", new Vector2(pmBtnW, 0)))
                    {
                        shield = Math.Min(shieldMax, shield + 1);
                        session.SetPlayerShield(player.Hash, shield);
                    }
                }

                // --- HP ---
                if (session.HpMode == HpMode.Points)
                {
                    var editIcon = FontAwesomeIcon.Pen.ToIconString();
                    using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    {
                        if (ImGui.Button($"{editIcon}##phmax_edit_{player.Hash}"))
                            ImGui.OpenPopup($"phmax_popup_{player.Hash}");
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(Loc.Get("Marker.HpMax"));
                        ImGui.EndTooltip();
                    }
                    if (ImGui.BeginPopup($"phmax_popup_{player.Hash}"))
                    {
                        ImGui.TextUnformatted(Loc.Get("Marker.HpMax"));
                        ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
                        var editHpMax = player.HpMax;
                        if (ImGui.InputInt($"##phmax_{player.Hash}", ref editHpMax))
                        {
                            if (editHpMax < 1) editHpMax = 1;
                            if (editHpMax > 99999) editHpMax = 99999;
                            session.SetPlayerHpMax(player.Hash, editHpMax);
                        }
                        ImGui.EndPopup();
                    }
                    ImGui.SameLine();
                }

                var hp = player.Hp;
                var hpClampMax = session.HpMode == HpMode.Percentage ? 100 : player.HpMax;
                if (ImGui.Button($"-##php_dec_{player.Hash}", new Vector2(pmBtnW, 0)))
                {
                    hp = Math.Max(0, hp - 1);
                    session.SetPlayerHp(player.Hash, hp);
                }
                ImGui.SameLine();
                var hpBarWidth = ImGui.GetContentRegionAvail().X - pmBtnW - barSpacing - barRightPad;
                HpBar.Draw(player.Hp, Attitude.Neutral, hpBarWidth, session.HpMode, hpMax: player.HpMax,
                    shield: session.ShowShield ? player.Shield : 0);
                ImGui.SameLine();
                if (ImGui.Button($"+##php_inc_{player.Hash}", new Vector2(pmBtnW, 0)))
                {
                    hp = Math.Min(hpClampMax, hp + 1);
                    session.SetPlayerHp(player.Hash, hp);
                }

                // --- EP ---
                if (session.ShowMpBar)
                {
                    var mp = player.Mp;
                    var mpClampMax = session.MpMode == HpMode.Percentage ? 100 : player.MpMax;

                    if (session.MpMode == HpMode.Points)
                    {
                        var editIcon = FontAwesomeIcon.Pen.ToIconString();
                        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        {
                            if (ImGui.Button($"{editIcon}##pmpmax_edit_{player.Hash}"))
                                ImGui.OpenPopup($"pmpmax_popup_{player.Hash}");
                        }
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip();
                            ImGui.TextUnformatted(Loc.Get("Marker.MpMax"));
                            ImGui.EndTooltip();
                        }
                        if (ImGui.BeginPopup($"pmpmax_popup_{player.Hash}"))
                        {
                            ImGui.TextUnformatted(Loc.Get("Marker.MpMax"));
                            ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
                            var editMpMax = player.MpMax;
                            if (ImGui.InputInt($"##pmpmax_{player.Hash}", ref editMpMax))
                            {
                                if (editMpMax < 1) editMpMax = 1;
                                if (editMpMax > 99999) editMpMax = 99999;
                                session.SetPlayerMpMax(player.Hash, editMpMax);
                            }
                            ImGui.EndPopup();
                        }
                        ImGui.SameLine();
                    }

                    if (ImGui.Button($"-##pmp_dec_{player.Hash}", new Vector2(pmBtnW, 0)))
                    {
                        mp = Math.Max(0, mp - 1);
                        session.SetPlayerMp(player.Hash, mp);
                    }
                    ImGui.SameLine();
                    var mpBarWidth = ImGui.GetContentRegionAvail().X - pmBtnW - barSpacing - barRightPad;
                    HpBar.DrawMpBar(player.Mp, mpBarWidth, session.MpMode, player.MpMax);
                    ImGui.SameLine();
                    if (ImGui.Button($"+##pmp_inc_{player.Hash}", new Vector2(pmBtnW, 0)))
                    {
                        mp = Math.Min(mpClampMax, mp + 1);
                        session.SetPlayerMp(player.Hash, mp);
                    }
                }

                // --- Counters ---
                if (player.Counters != null)
                {
                    for (var ci = 0; ci < player.Counters.Count; ci++)
                    {
                        var counter = player.Counters[ci];

                        var editIcon = FontAwesomeIcon.Pen.ToIconString();
                        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                        {
                            if (ImGui.Button($"{editIcon}##pcnt_edit_{player.Hash}_{ci}"))
                                ImGui.OpenPopup($"pcnt_popup_{player.Hash}_{ci}");
                        }
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip();
                            ImGui.TextUnformatted(Loc.Get("Counter.Edit"));
                            ImGui.EndTooltip();
                        }
                        if (ImGui.BeginPopup($"pcnt_popup_{player.Hash}_{ci}"))
                        {
                            ImGui.TextUnformatted(Loc.Get("Counter.Edit"));
                            ImGui.Separator();
                            var cMax = counter.Max;
                            ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                            if (ImGui.InputInt($"##pcnt_max_{player.Hash}_{ci}", ref cMax))
                            {
                                if (cMax < 1) cMax = 1;
                                counter.Max = cMax;
                                counter.Value = cMax;
                                session.BroadcastPlayerUpdate();
                            }
                            ImGui.EndPopup();
                        }
                        ImGui.SameLine();

                        if (ImGui.Button($"-##pcnt_dec_{player.Hash}_{ci}", new Vector2(pmBtnW, 0)))
                        {
                            counter.Value = Math.Max(0, counter.Value - 1);
                            session.BroadcastPlayerUpdate();
                        }
                        ImGui.SameLine();
                        var cntBarWidth = ImGui.GetContentRegionAvail().X - pmBtnW - barSpacing - barRightPad;
                        CounterBar.Draw(counter, cntBarWidth);
                        ImGui.SameLine();
                        if (ImGui.Button($"+##pcnt_inc_{player.Hash}_{ci}", new Vector2(pmBtnW, 0)))
                        {
                            counter.Value = Math.Min(counter.Max, counter.Value + 1);
                            session.BroadcastPlayerUpdate();
                        }
                    }
                }

                // --- Bonus/malus temporaire ---
                var tempIcon = FontAwesomeIcon.Magic.ToIconString();
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    if (ImGui.Button($"{tempIcon}##ptemp_edit_{player.Hash}"))
                        ImGui.OpenPopup($"ptemp_popup_{player.Hash}");
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(Loc.Get("Marker.TempMod"));
                    ImGui.EndTooltip();
                }
                if (player.TempModifier != 0)
                {
                    ImGui.SameLine();
                    var tempStr = player.TempModifier >= 0 ? $"+{player.TempModifier}" : player.TempModifier.ToString();
                    var tempColor = player.TempModifier > 0
                        ? new Vector4(0.2f, 0.8f, 0.2f, 1f)
                        : new Vector4(1f, 0.4f, 0.4f, 1f);
                    ImGui.TextColored(tempColor, tempStr);
                    if (player.TempModTurns > 0)
                    {
                        ImGui.SameLine(0, 2f * ImGuiHelpers.GlobalScale);
                        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"({player.TempModTurns}t)");
                    }
                }
                if (ImGui.BeginPopup($"ptemp_popup_{player.Hash}"))
                {
                    ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Marker.TempMod"));
                    ImGui.Separator();
                    ImGui.TextUnformatted(Loc.Get("Marker.TempModValue"));
                    ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
                    var tempMod = player.TempModifier;
                    if (ImGui.InputInt($"##ptemp_val_{player.Hash}", ref tempMod))
                    {
                        player.TempModifier = tempMod;
                        session.BroadcastPlayerUpdate();
                    }
                    ImGui.TextUnformatted(Loc.Get("Marker.TempModTurns"));
                    ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
                    var tempTurns = player.TempModTurns;
                    if (ImGui.InputInt($"##ptemp_turns_{player.Hash}", ref tempTurns))
                    {
                        if (tempTurns < 0) tempTurns = 0;
                        player.TempModTurns = tempTurns;
                        session.BroadcastPlayerUpdate();
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(Loc.Get("Marker.TempModTurnsHint"));
                        ImGui.EndTooltip();
                    }
                    ImGui.Spacing();
                    if (ImGui.SmallButton(Loc.Get("Marker.TempModReset") + $"##ptemp_reset_{player.Hash}"))
                    {
                        player.TempModifier = 0;
                        player.TempModTurns = 0;
                        session.BroadcastPlayerUpdate();
                    }
                    ImGui.EndPopup();
                }
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
    }
}
