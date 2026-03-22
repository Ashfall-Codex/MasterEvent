using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MasterEvent.Models;

namespace MasterEvent.Services;

public class SaveManager
{
    private readonly string presetsDir;
    private readonly string sheetsDir;

    public SaveManager(string pluginConfigDir)
    {
        presetsDir = Path.Combine(pluginConfigDir, "presets");
        Directory.CreateDirectory(presetsDir);
        sheetsDir = Path.Combine(pluginConfigDir, "sheets");
        Directory.CreateDirectory(sheetsDir);
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

    // Fiches de personnage

    public void SaveSheet(PlayerSheet sheet)
    {
        var path = GetSheetPath(sheet.Name);
        var json = JsonSerializer.Serialize(sheet, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public PlayerSheet? LoadSheet(string name)
    {
        var path = GetSheetPath(name);
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PlayerSheet>(json);
    }

    public void DeleteSheet(string name)
    {
        var path = GetSheetPath(name);
        if (File.Exists(path))
            File.Delete(path);
    }

    public List<string> GetSheetNames()
    {
        var names = new List<string>();
        if (!Directory.Exists(sheetsDir))
            return names;

        foreach (var file in Directory.GetFiles(sheetsDir, "*.json"))
            names.Add(Path.GetFileNameWithoutExtension(file));

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    private string GetSheetPath(string name)
    {
        var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(sheetsDir, safeName + ".json");
    }
}
