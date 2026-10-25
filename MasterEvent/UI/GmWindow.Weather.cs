using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using MasterEvent.Localization;
using MasterEvent.Services;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

// Onglet « Météo » : une seule carte décrit l'ambiance voulue (météo et heure), un seul bouton
// l'applique à la scène. L'en-tête dit ce qui est appliqué, sans passer par le chat.
public sealed partial class GmWindow
{
    // Heure fixée dans la carte. Désactivée, le curseur suit l'heure du jeu et l'application rend
    // à chacun son heure.
    private bool ambianceFixTime;
    private List<IconCombo.Entry>? cachedWeatherEntries;

    // Raccourcis d'heure : l'heure ronde la plus parlante de chaque moment de la journée.
    // Un cran par heure, les quarts de journée marqués et gradués dessous plutôt qu'une icône à
    // chaque bout.
    private static readonly NotchedSlider.Options TimeSlider = new()
    {
        Notches = 24,
        MajorEvery = 6,
        MajorLabel = hour => $"{hour:00}:00",
    };

    private static readonly (string Key, int Hour)[] TimeShortcuts =
    [
        ("Weather.Dawn", 6),
        ("Weather.Noon", 12),
        ("Weather.Dusk", 18),
        ("Weather.Night", 0),
    ];

    private void DrawWeatherContent()
    {
        PageHeader.Draw(FontAwesomeIcon.CloudSunRain, Loc.Get("Weather.Title"), WeatherStatusText());

        DrawWeatherConflictWarning();
        DrawTabNotice(Tab.Weather);

        if (ImGui.BeginChild("##weather_scroll", Vector2.Zero))
            DrawAmbianceCard();
        ImGui.EndChild();
    }

    // « Appliqué : Pluie, 14:00 », d'après ce qui a réellement été appliqué, pas d'après la carte.
    private string WeatherStatusText()
    {
        var weatherId = session.CurrentWeatherId;
        var time = session.CurrentEorzeaTime;
        if (weatherId == 0 && time == null) return Loc.Get("Weather.StatusGame");

        string weather;
        if (weatherId == 0)
            weather = Loc.Get("Weather.None");
        else if (!string.IsNullOrEmpty(session.CurrentWeatherName))
            weather = session.CurrentWeatherName;
        else
            weather = cachedWeatherList?.GetValueOrDefault(weatherId) ?? weatherId.ToString();
        var hour = time is { } seconds
            ? $"{WeatherService.SecondsToHour(seconds):00}:00"
            : Loc.Get("Weather.GameTime");
        return string.Format(Loc.Get("Weather.StatusFmt"), weather, hour);
    }

    private void DrawAmbianceCard()
    {
        // Première ouverture : la carte reprend ce qui est déjà appliqué.
        if (selectedHour < 0)
        {
            selectedWeatherId = session.CurrentWeatherId;
            ambianceFixTime = session.CurrentEorzeaTime != null;
            selectedHour = WeatherService.SecondsToHour(
                session.CurrentEorzeaTime ?? WeatherService.GetCurrentEorzeaTimeSeconds());
        }

        // Invalider le cache si la zone a changé
        var currentTerritory = Plugin.ClientState.TerritoryType;
        if (currentTerritory != cachedTerritoryId)
        {
            cachedWeatherList = null;
            cachedTerritoryId = currentTerritory;
        }

        if (cachedWeatherList == null)
        {
            cachedWeatherList = session.GetAvailableWeathers();
            cachedWeatherEntries = BuildWeatherEntries(cachedWeatherList);

            // Une météo d'une autre zone n'a plus de sens ici : on revient à celle de la zone.
            if (selectedWeatherId != 0 && !cachedWeatherList.ContainsKey(selectedWeatherId))
                selectedWeatherId = 0;
        }

        LayoutControls.BeginCard(Loc.Get("Weather.Ambiance"), FontAwesomeIcon.CloudSun);
        var width = LayoutControls.CardContentWidth;

        // ── Météo : la première entrée rend celle de la zone ──
        var weatherKey = (int)selectedWeatherId;
        if (IconCombo.Draw("weather_combo", cachedWeatherEntries!, ref weatherKey, width))
            selectedWeatherId = (byte)weatherKey;

        ImGuiHelpers.ScaledDummy(4f);

        // ── Heure ──
        var rowStart = ImGui.GetCursorPosX();
        ToggleSwitch.Draw("##ambiance_fix_time", Loc.Get("Weather.FixTime"), ref ambianceFixTime,
            Loc.Get("Weather.TimeTooltip"));

        // Tant que l'heure n'est pas fixée, le curseur montre celle du jeu ; le toucher la fixe.
        if (!ambianceFixTime)
            selectedHour = WeatherService.SecondsToHour(WeatherService.GetCurrentEorzeaTimeSeconds());

        // L'heure en face de la bascule, en clair si elle est fixée, en retrait si c'est celle du jeu.
        var hourText = $"{selectedHour:00}:00";
        ImGui.SameLine(rowStart + width - ImGui.CalcTextSize(hourText).X);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ambianceFixTime ? MasterEventTheme.TextStrong : MasterEventTheme.MutedTextColor, hourText);

        // Même curseur à crans que la transparence (voir TimeSlider).
        var hourValue = selectedHour / 23f;
        NotchedSlider.Draw("##time_slider", ref hourValue, width, TimeSlider, out var moved);
        if (moved)
        {
            selectedHour = (int)MathF.Round(hourValue * 23f);
            ambianceFixTime = true;
        }

        DrawTimeShortcuts(width);

        ImGuiHelpers.ScaledDummy(4f);

        // ── Actions : une seule pleine, le retour au jeu en discret ──
        if (PrimaryButton(Loc.Get("Weather.Apply") + "##apply_ambiance", width))
            ApplyAmbiance();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Get("Weather.ApplyTooltip"));

        if (QuietButton(Loc.Get("Weather.ResetAll") + "##reset_ambiance", width))
            ResetAmbiance();

        LayoutControls.EndCard();
    }

    private List<IconCombo.Entry> BuildWeatherEntries(Dictionary<byte, string> weathers)
    {
        var entries = new List<IconCombo.Entry>(weathers.Count + 1)
        {
            new(0, Loc.Get("Weather.None"), Icon: FontAwesomeIcon.MapMarkerAlt),
        };
        foreach (var (id, name) in weathers)
            entries.Add(new IconCombo.Entry(id, name, session.GetWeatherIconId(id)));
        return entries;
    }

    // Quatre raccourcis sur une ligne, ou deux par ligne si la carte est trop étroite.
    private void DrawTimeShortcuts(float width)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var widest = 0f;
        foreach (var (key, _) in TimeShortcuts)
            widest = Math.Max(widest, TextButtonWidth(Loc.Get(key)));

        var perRow = widest * TimeShortcuts.Length + spacing * (TimeShortcuts.Length - 1) <= width
            ? TimeShortcuts.Length
            : 2;
        var buttonWidth = (width - spacing * (perRow - 1)) / perRow;

        for (var i = 0; i < TimeShortcuts.Length; i++)
        {
            var (key, hour) = TimeShortcuts[i];
            if (i % perRow != 0) ImGui.SameLine();

            if (ToggleButton(Loc.Get(key) + "##time_shortcut_" + hour, ambianceFixTime && selectedHour == hour, buttonWidth))
            {
                selectedHour = hour;
                ambianceFixTime = true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{hour:00}:00");
        }
    }

    // Applique la carte telle qu'elle est : météo choisie (ou celle de la zone), heure fixée (ou celle
    // du jeu). Seul ce qui change part chez les joueurs, qui reçoivent une ligne de chat par envoi ;
    // un clic sans changement renvoie tout, de quoi rattraper une scène désynchronisée.
    private void ApplyAmbiance()
    {
        var weatherName = selectedWeatherId != 0
            ? cachedWeatherList?.GetValueOrDefault(selectedWeatherId) ?? selectedWeatherId.ToString()
            : string.Empty;
        uint? time = ambianceFixTime ? WeatherService.HourToSeconds(selectedHour) : null;

        var weatherChanged = selectedWeatherId != session.CurrentWeatherId;
        var timeChanged = time != session.CurrentEorzeaTime;
        var resendAll = !weatherChanged && !timeChanged;

        if (weatherChanged || resendAll)
        {
            session.ApplyWeather(selectedWeatherId);
            // Le nom sert à l'en-tête et au renvoi automatique quand un joueur arrive.
            session.CurrentWeatherName = weatherName;
            if (session.IsConnected)
                session.BroadcastWeather(selectedWeatherId, weatherName);
        }

        if (timeChanged || resendAll)
        {
            if (time is { } seconds) session.ApplyTime(seconds);
            else session.ClearTime();
            if (session.IsConnected)
                session.BroadcastTime(time);
        }

        if (selectedWeatherId != 0 || time != null)
            Plugin.PluginConflicts.NotifyWeatherConflict();

        ShowTabNotice(Tab.Weather, Loc.Get("Weather.AppliedNotice"));
    }

    private void ResetAmbiance()
    {
        session.ApplyWeather(0);
        selectedWeatherId = 0;
        session.ClearTime();
        ambianceFixTime = false;

        // Envoyé même si rien n'était appliqué ici : après un rechargement du plugin, les joueurs
        // peuvent encore garder l'ambiance d'avant.
        if (session.IsConnected)
        {
            session.BroadcastWeather(0, "");
            session.BroadcastTime(null);
        }

        ShowTabNotice(Tab.Weather, Loc.Get("Weather.ResetNotice"));
    }

    // Un avertissement seulement quand un plugin concurrent est réellement chargé.
    private static void DrawWeatherConflictWarning()
    {
        var conflicts = Plugin.PluginConflicts;
        if (!conflicts.HasConflict) return;

        var start = ImGui.GetCursorScreenPos();
        LayoutControls.DrawNotice(
            string.Format(Loc.Get("Weather.PluginConflictDetected"), conflicts.ConflictNames),
            MasterEventTheme.WarningColor);
        var end = new Vector2(start.X + ImGui.GetContentRegionAvail().X, ImGui.GetCursorScreenPos().Y);

        if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(start, end))
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(400f * ImGuiHelpers.GlobalScale);
            ImGui.TextUnformatted(Loc.Get("Weather.PluginConflictTooltip"));
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        ImGuiHelpers.ScaledDummy(4f);
    }
}
