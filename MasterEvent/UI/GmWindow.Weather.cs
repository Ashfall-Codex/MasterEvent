using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Services;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

public sealed partial class GmWindow
{
    private void DrawWeatherContent()
    {
        var availWidth = ImGui.GetContentRegionAvail().X;

        LayoutControls.DrawTabHeader(FontAwesomeIcon.CloudSunRain, Loc.Get("Weather.Title"));

        DrawWeatherConflictWarning(availWidth);

        // Invalider le cache si la zone a changé
        var currentTerritory = Plugin.ClientState.TerritoryType;
        if (currentTerritory != cachedTerritoryId)
        {
            cachedWeatherList = null;
            cachedTerritoryId = currentTerritory;
        }

        cachedWeatherList ??= session.GetAvailableWeathers();

        // ── Sélecteur météo avec icônes ──
        var currentName = selectedWeatherId != 0 && cachedWeatherList.TryGetValue(selectedWeatherId, out var name)
            ? name
            : Loc.Get("Weather.None");

        // Prévisualiser l'icône de la météo sélectionnée dans le combo
        var previewIconId = selectedWeatherId != 0 ? session.GetWeatherIconId(selectedWeatherId) : 0u;
        var iconSize = new Vector2(ImGui.GetTextLineHeight(), ImGui.GetTextLineHeight());

        ImGui.SetNextItemWidth(availWidth);
        if (ImGui.BeginCombo("##weather_combo", ""))
        {
            if (ImGui.Selectable(Loc.Get("Weather.None"), selectedWeatherId == 0))
                selectedWeatherId = 0;

            foreach (var (id, weatherName) in cachedWeatherList)
            {
                var isSelected = selectedWeatherId == id;
                var wIconId = session.GetWeatherIconId(id);

                // Icône météo + nom (centrage vertical)
                if (wIconId != 0)
                {
                    var tex = Plugin.TextureProvider.GetFromGameIcon(wIconId).GetWrapOrDefault();
                    if (tex != null)
                    {
                        var itemCursorY = ImGui.GetCursorPosY();
                        ImGui.SetCursorPosY(itemCursorY + ImGui.GetStyle().FramePadding.Y);
                        ImGui.Image(tex.Handle, iconSize);
                        ImGui.SameLine();
                        ImGui.SetCursorPosY(itemCursorY);
                    }
                }

                if (ImGui.Selectable($"{weatherName}##{id}", isSelected))
                    selectedWeatherId = id;
            }
            ImGui.EndCombo();
        }

        // Dessiner l'icône + nom par-dessus le combo (prévisualisation)
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() - availWidth + 8f * ImGuiHelpers.GlobalScale);
        var previewCursorY = ImGui.GetCursorPosY();
        if (previewIconId != 0)
        {
            var tex = Plugin.TextureProvider.GetFromGameIcon(previewIconId).GetWrapOrDefault();
            if (tex != null)
            {
                // Centrer l'icône verticalement dans le cadre du combo
                ImGui.SetCursorPosY(previewCursorY + ImGui.GetStyle().FramePadding.Y);
                ImGui.Image(tex.Handle, iconSize);
                ImGui.SameLine();
                ImGui.SetCursorPosY(previewCursorY);
            }
        }
        ImGui.TextUnformatted(currentName);

        ImGuiHelpers.ScaledDummy(4f);

        // Bouton appliquer météo
        var canSend = selectedWeatherId != 0;
        if (!canSend) ImGui.BeginDisabled();
        if (ImGui.Button(Loc.Get("Weather.Apply") + "##apply_weather", new Vector2(availWidth, 0)))
        {
            var weatherName = cachedWeatherList.GetValueOrDefault(selectedWeatherId, selectedWeatherId.ToString());

            // Appliquer localement au MJ
            session.ApplyWeather(selectedWeatherId);
            Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.WeatherSet"), weatherName));
            Plugin.PluginConflicts.NotifyWeatherConflict();

            // Broadcast aux joueurs si connecté
            if (session.IsConnected)
                session.BroadcastWeather(selectedWeatherId, weatherName);
        }
        if (!canSend) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Weather.ApplyTooltip"));
            ImGui.EndTooltip();
        }

        // Bouton rafraîchir + bouton reset
        ImGuiHelpers.ScaledDummy(2f);
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(FontAwesomeIcon.Sync.ToIconString() + "##refresh_weather"))
                cachedWeatherList = null;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Weather.Refresh"));
            ImGui.EndTooltip();
        }

        ImGui.SameLine();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (ImGui.Button(FontAwesomeIcon.Undo.ToIconString() + "##reset_weather_time"))
            {
                // Reset météo
                session.ApplyWeather(0);
                selectedWeatherId = 0;

                // Reset heure
                session.ClearTime();
                selectedHour = -1;

                Plugin.ChatGui.Print(Loc.Get("Chat.WeatherTimeReset"));

                // Broadcast aux joueurs si connecté
                if (session.IsConnected)
                {
                    session.BroadcastWeather(0, "");
                    session.BroadcastTime(null);
                }
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Weather.ResetAll"));
            ImGui.EndTooltip();
        }

        // ── Slider heure éorzéenne ──
        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Weather.Time"));
        ImGui.Spacing();

        // Initialiser le slider à l'heure courante
        if (selectedHour < 0)
            selectedHour = WeatherService.SecondsToHour(WeatherService.GetCurrentEorzeaTimeSeconds());

        ImGui.SetNextItemWidth(availWidth);
        ImGui.SliderInt("##time_slider", ref selectedHour, 0, 23, $"{selectedHour:00}:00");

        ImGuiHelpers.ScaledDummy(4f);

        if (ImGui.Button(Loc.Get("Weather.TimeApply") + "##apply_time", new Vector2(availWidth, 0)))
        {
            var seconds = WeatherService.HourToSeconds(selectedHour);

            // Appliquer localement au MJ
            session.ApplyTime(seconds);
            Plugin.ChatGui.Print(string.Format(Loc.Get("Chat.TimeSet"), $"{selectedHour:00}:00"));
            Plugin.PluginConflicts.NotifyWeatherConflict();

            // Broadcast aux joueurs si connecté
            if (session.IsConnected)
                session.BroadcastTime(seconds);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Weather.TimeTooltip"));
            ImGui.EndTooltip();
        }
    }

    private static void DrawWeatherConflictWarning(float availWidth)
    {
        var conflicts = Plugin.PluginConflicts;

        if (conflicts.HasConflict)
        {
            LayoutControls.DrawNotice(
                string.Format(Loc.Get("Weather.PluginConflictDetected"), conflicts.ConflictNames),
                MasterEventTheme.WarningColor);
            AttachConflictTooltip();
            ImGuiHelpers.ScaledDummy(6f);
            return;
        }

        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.MutedTextColor, FontAwesomeIcon.InfoCircle.ToIconString());
        ImGui.SameLine();

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + availWidth - 20f * ImGuiHelpers.GlobalScale);
        ImGui.TextColored(MasterEventTheme.MutedTextColor, Loc.Get("Weather.PluginConflictWarning"));
        ImGui.PopTextWrapPos();
        AttachConflictTooltip();

        ImGuiHelpers.ScaledDummy(6f);
    }

    private static void AttachConflictTooltip()
    {
        if (!ImGui.IsItemHovered()) return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(400f * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(Loc.Get("Weather.PluginConflictTooltip"));
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

}
