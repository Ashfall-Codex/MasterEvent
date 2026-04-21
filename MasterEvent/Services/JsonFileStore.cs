using System;
using System.IO;
using System.Text.Json;

namespace MasterEvent.Services;

public static class JsonFileStore
{
    public static readonly JsonSerializerOptions DefaultOptions = new()
    {
        WriteIndented = true,
    };

    public static T? TryLoad<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[JsonFileStore] Échec du chargement de '{path}' : {ex.Message}");
            return null;
        }
    }

    public static bool Save<T>(string path, T value)
    {
        try
        {
            var json = JsonSerializer.Serialize(value, DefaultOptions);
            File.WriteAllText(path, json);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[JsonFileStore] Échec de l'écriture de '{path}' : {ex.Message}");
            return false;
        }
    }
}
