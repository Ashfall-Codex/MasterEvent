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
    private void DrawMarkersContent()
    {
        if (!session.CanEdit && !HasGmAccess())
        {
            var avail = ImGui.GetContentRegionAvail();
            var text = Loc.Get("Gm.PlayerViewLocked");
            var textSz = ImGui.CalcTextSize(text);
            ImGui.SetCursorPos(new Vector2(
                ImGui.GetCursorPosX() + (avail.X - textSz.X) / 2f,
                ImGui.GetCursorPosY() + (avail.Y - textSz.Y) / 2f));
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), text);
            return;
        }

        DrawHeader();
        ImGui.Separator();

        var hasAnyMarker = false;
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            if (session.CurrentMarkers.Markers[i].HasData)
            {
                hasAnyMarker = true;
                break;
            }
        }

        if (hasAnyMarker)
        {
            if (ImGui.BeginChild("##markers_scroll", new Vector2(0, -30f * ImGuiHelpers.GlobalScale)))
            {
                var first = true;
                for (var i = 0; i < Constants.WaymarkCount; i++)
                {
                    var waymarkId = (WaymarkId)i;
                    var marker = session.CurrentMarkers[waymarkId];

                    if (!marker.HasData)
                        continue;

                    if (!first)
                        ImGui.Spacing();

                    first = false;
                    MarkerCard.DrawEdit(waymarkId, marker,
                        onPlace: () =>
                        {
                            Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.PlaceWaymark"), waymarkId.ToLabel()));
                        },
                        onClear: () => session.ClearMarker(waymarkId),
                        onMove: () => session.MoveMarker(waymarkId),
                        onRoll: statId => session.RollDiceWithStat(waymarkId, statId),
                        hpMode: session.HpMode,
                        showShield: session.ShowShield,
                        showMpBar: session.ShowMpBar,
                        mpMode: session.MpMode);
                }
            }
            ImGui.EndChild();

            DrawAddMarkerSmall();
        }
        else
        {
            DrawAddMarkerCentered();
        }
    }

    private void DrawAddMarkerSmall()
    {
        var iconStr = FontAwesomeIcon.Plus.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(iconStr + "##add_small"))
                OpenFieldMarkerAgent();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Gm.AddMarker"));
            ImGui.EndTooltip();
        }
        ImGui.Separator();
    }

    private void DrawAddMarkerCentered()
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Gm.NoMarkers"));

        var avail = ImGui.GetContentRegionAvail();
        var btnLabel = "+ " + Loc.Get("Gm.AddMarker");
        var btnSize = ImGui.CalcTextSize(btnLabel) + ImGui.GetStyle().FramePadding * 2;

        ImGui.SetCursorPos(new Vector2(
            ImGui.GetCursorPosX() + (avail.X - btnSize.X) / 2f,
            ImGui.GetCursorPosY() + (avail.Y - btnSize.Y) / 2f));

        if (ImGui.Button(btnLabel + "##add_center"))
            OpenFieldMarkerAgent();
    }

    private void DrawHeader()
    {
        var totalWidth = ImGui.GetContentRegionAvail().X;

        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Gm.Title"));
        ImGui.SameLine();

        var statusColor = session.IsConnected
            ? new Vector4(0.2f, 1f, 0.2f, 1f)
            : new Vector4(0.5f, 0.5f, 0.5f, 1f);
        var statusText = session.IsConnected
            ? string.Format(Loc.Get("Gm.Connected"), session.ConnectedPlayerCount)
            : Loc.Get("Gm.Local");
        ImGui.TextColored(statusColor, statusText);

        if (session.ActiveTemplate != null)
        {
            ImGui.SameLine();
            ImGui.TextColored(MasterEventTheme.AccentColor, $"· {session.ActiveTemplate.Name}");
        }

        var framePad = ImGui.GetStyle().FramePadding.X * 2;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var updateBtnWidth = ImGui.CalcTextSize(Loc.Get("Gm.Update")).X + framePad;
        var historyIcon = FontAwesomeIcon.History.ToIconString();
        float historyBtnWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            historyBtnWidth = ImGui.CalcTextSize(historyIcon).X + framePad;
        var totalBtnWidth = historyBtnWidth + spacing + updateBtnWidth;
        var buttonPos = totalWidth - totalBtnWidth;
        if (buttonPos > 0)
            ImGui.SameLine(buttonPos);

        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(historyIcon + "##roll_history"))
                ImGui.OpenPopup("##roll_history_popup");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Dice.History"));
            ImGui.EndTooltip();
        }

        if (ImGui.BeginPopup("##roll_history_popup"))
        {
            ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Dice.History"));
            ImGui.Separator();

            if (session.RollHistory.Count == 0)
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.Get("Dice.NoHistory"));
            }
            else
            {
                for (var hi = 0; hi < session.RollHistory.Count && hi < 20; hi++)
                {
                    var roll = session.RollHistory[hi];
                    var modStr = roll.Modifier >= 0 ? $"+{roll.Modifier}" : roll.Modifier.ToString();
                    var statInfo = roll.StatName != null ? $" [{roll.StatName} {modStr}]" : "";
                    var line = $"{roll.RollerName}: {roll.RawRoll}/{roll.DiceMax}{statInfo} = {roll.Total}";
                    ImGui.TextUnformatted(line);
                }

                ImGui.Separator();
                if (ImGui.Selectable(Loc.Get("Dice.ClearHistory")))
                    session.ClearRollHistory();
            }

            ImGui.EndPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button(Loc.Get("Gm.Update")))
        {
            session.SyncWaymarks();
            session.BroadcastUpdate();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Gm.UpdateTooltip"));
            ImGui.EndTooltip();
        }
    }
}
