using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin;
using MasterEvent.Localization;

namespace MasterEvent.Services;

public sealed class PluginConflictService : IDisposable
{

    private static readonly Dictionary<string, string> KnownConflicts = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Weatherman", "Weatherman" },   // contrôle direct de la météo et de l'heure par zone
        { "Brio", "Brio" },               // EorzeaTimeOverride + ActiveWeather en mode GPose
        { "Ktisis", "Ktisis" },           // éditeur d'environnement (EnvOverride)
    };

    // Anti-spam : une seule notification par fenêtre, un MJ pouvant enchaîner les réglages.
    private const double NoticeCooldownSeconds = 30;

    private readonly IDalamudPluginInterface pluginInterface;
    private IReadOnlyList<string>? cachedConflicts;
    private DateTime lastNotice = DateTime.MinValue;

    public PluginConflictService(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        this.pluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
    }

    public IReadOnlyList<string> DetectedConflicts => cachedConflicts ??= Detect();
    public bool HasConflict => DetectedConflicts.Count > 0;
    public string ConflictNames => string.Join(", ", DetectedConflicts);
    public void Invalidate() => cachedConflicts = null;
    public void NotifyWeatherConflict()
    {
        if (!HasConflict) return;

        var now = DateTime.UtcNow;
        if ((now - lastNotice).TotalSeconds < NoticeCooldownSeconds) return;
        lastNotice = now;

        Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = Loc.Get("Weather.PluginConflictNotifTitle"),
            Content = string.Format(Loc.Get("Weather.PluginConflictNotifContent"), ConflictNames),
            Type = NotificationType.Warning,
            InitialDuration = TimeSpan.FromSeconds(10),
        });
    }

    private IReadOnlyList<string> Detect()
    {
        try
        {
            return pluginInterface.InstalledPlugins
                .Where(p => p.IsLoaded && KnownConflicts.ContainsKey(p.InternalName))
                .Select(p => KnownConflicts[p.InternalName])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[PluginConflictService] Échec du scan des plugins installés : {ex.Message}");
            return [];
        }
    }

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args) => Invalidate();

    public void Dispose()
    {
        pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
    }
}
