using System.Collections.Generic;
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
            ImGui.TextColored(MasterEventTheme.TextDim, text);
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
        ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Gm.NoMarkers"));

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
            ? MasterEventTheme.SuccessColor
            : MasterEventTheme.TextDim;
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
        var historyIcon = FontAwesomeIcon.Dice.ToIconString();
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
            ImGui.TextUnformatted(Loc.Get("Dice.Title"));
            ImGui.EndTooltip();
        }

        ImGui.SetNextWindowSize(new Vector2(320f * ImGuiHelpers.GlobalScale, 0));
        if (ImGui.BeginPopup("##roll_history_popup"))
        {
            DrawGmFreeRoll();
            DiceControls.DrawRollHistory(session, maxEntries: 20, showClearButton: true);
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

    /// <summary>
    /// Jet libre du MJ, pour une réaction de figurant ou un test hors décor. Sans lui, le
    /// seul moyen de lancer un dé côté MJ était de passer par un marqueur, ou d'ouvrir la
    /// fenêtre joueur. Les tuiles sont celles du modèle actif, avec leur valeur par défaut.
    /// </summary>
    private void DrawGmFreeRoll()
    {
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Dice.GmFreeRoll"));
        ImGuiHelpers.ScaledDummy(2f);

        var rollerName = Loc.Get("Gm.Title");
        var availWidth = ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        const int columns = 3;
        var tileSize = (availWidth - spacing * (columns - 1)) / columns;
        var tileH = tileSize * 0.62f;

        DiceControls.DrawDiceTile(Loc.Get("Dice.NoStat"), null, "gm_roll_simple", tileSize, tileH,
            () => session.RollDiceForNpc(rollerName, null, 0));

        var definitions = session.ActiveTemplate?.StatDefinitions;
        if (definitions == null) return;

        var idx = 1;
        foreach (var definition in definitions)
        {
            if (idx % columns != 0)
                ImGui.SameLine();

            // RollDiceForNpc attend des valeurs, pas des définitions : la valeur par défaut
            // du modèle sert de modificateur, ce qui est le comportement voulu pour un figurant.
            var stats = new List<StatValue>
            {
                new() { Id = definition.Id, Name = definition.Name, Modifier = definition.DefaultValue },
            };
            var statId = definition.Id;
            var modStr = definition.DefaultValue >= 0
                ? $"+{definition.DefaultValue}"
                : definition.DefaultValue.ToString();

            DiceControls.DrawDiceTile(definition.Name, modStr, "gm_roll_" + statId, tileSize, tileH,
                () => session.RollDiceForNpc(rollerName, stats, 0, statId));
            idx++;
        }
    }
}
