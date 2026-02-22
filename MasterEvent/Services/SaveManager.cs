using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MasterEvent.Models;

namespace MasterEvent.Services;

public class SaveManager
{
    private readonly string presetsDir;

    public SaveManager(string pluginConfigDir)
    {
        presetsDir = Path.Combine(pluginConfigDir, "presets");
        Directory.CreateDirectory(presetsDir);
    }

    public void SavePreset(MarkerSet markerSet, string name)
    {
        var preset = markerSet.DeepCopy();
        preset.PresetName = name;
        var path = GetPresetPath(name);
        var json = JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public MarkerSet? LoadPreset(string name)
    {
        var path = GetPresetPath(name);
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<MarkerSet>(json);
    }

    public void DeletePreset(string name)
    {
        var path = GetPresetPath(name);
        if (File.Exists(path))
            File.Delete(path);
    }

    public List<string> GetPresetNames()
    {
        var names = new List<string>();
        if (!Directory.Exists(presetsDir))
            return names;

        foreach (var file in Directory.GetFiles(presetsDir, "*.json"))
            names.Add(Path.GetFileNameWithoutExtension(file));

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    private string GetPresetPath(string name)
    {
        var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(presetsDir, safeName + ".json");
    }
}
