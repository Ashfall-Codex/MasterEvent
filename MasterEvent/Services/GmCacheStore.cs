using System;
using System.IO;
using MasterEvent.Models;

namespace MasterEvent.Services;

// Persistance du GmCache côté disque.

public class GmCacheStore
{
    private const int ThrottleSeconds = 5;
    private const int MaxAgeHours = 2;

    private readonly string cachePath;
    private DateTime lastSave;

    public GmCacheStore(string pluginConfigDir)
    {
        cachePath = Path.Combine(pluginConfigDir, "gm_cache.json");
    }

    // Met à jour SavedAt automatiquement. No-op si le throttle n'est pas écoulé.
    public void Save(GmCache cache)
    {
        var now = DateTime.UtcNow;
        if ((now - lastSave).TotalSeconds < ThrottleSeconds) return;
        lastSave = now;
        cache.SavedAt = now;
        JsonFileStore.Save(cachePath, cache);
    }

    // Charge le cache s'il existe et n'est pas expiré (>2h). Le cache expiré est supprimé automatiquement.
    public GmCache? Load()
    {
        var cache = JsonFileStore.TryLoad<GmCache>(cachePath);
        if (cache == null) return null;

        if ((DateTime.UtcNow - cache.SavedAt).TotalHours > MaxAgeHours)
        {
            Delete();
            return null;
        }

        return cache;
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[GmCacheStore] Échec de la suppression du cache : {ex.Message}");
        }
    }
}
