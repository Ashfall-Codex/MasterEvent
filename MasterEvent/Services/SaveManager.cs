using System;
using System.Collections.Generic;
using System.IO;
using MasterEvent.Models;

namespace MasterEvent.Services;

public class SaveManager
{
    private readonly string presetsDir;
    private readonly string sheetsDir;
    private readonly string sharedTemplatesPath;

    public SaveManager(string pluginConfigDir)
    {
        presetsDir = Path.Combine(pluginConfigDir, "presets");
        Directory.CreateDirectory(presetsDir);
        sheetsDir = Path.Combine(pluginConfigDir, "sheets");
        Directory.CreateDirectory(sheetsDir);
        sharedTemplatesPath = Path.Combine(pluginConfigDir, "shared_templates.json");
    }

    public void SavePreset(MarkerSet markerSet, string name)
    {
        var preset = markerSet.DeepCopy();
        preset.PresetName = name;
        JsonFileStore.Save(GetPresetPath(name), preset);
    }

    public MarkerSet? LoadPreset(string name)
    {
        return JsonFileStore.TryLoad<MarkerSet>(GetPresetPath(name));
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

    public string SaveSheet(PlayerSheet sheet)
    {
        sheet.StorageKey ??= AllocateSheetKey(sheet);
        JsonFileStore.Save(GetSheetPath(sheet.StorageKey), sheet);
        return sheet.StorageKey;
    }

    public PlayerSheet? LoadSheet(string key)
    {
        var sheet = JsonFileStore.TryLoad<PlayerSheet>(GetSheetPath(key));
        if (sheet != null) sheet.StorageKey = key;
        return sheet;
    }

    private string AllocateSheetKey(PlayerSheet sheet)
    {
        var name = sheet.Name.Trim();
        if (!File.Exists(GetSheetPath(name))) return name;

        var withTemplate = string.IsNullOrWhiteSpace(sheet.TemplateName) ? name : $"{name} ({sheet.TemplateName.Trim()})";
        if (!File.Exists(GetSheetPath(withTemplate))) return withTemplate;

        for (var i = 2; ; i++)
        {
            var candidate = $"{withTemplate} {i}";
            if (!File.Exists(GetSheetPath(candidate))) return candidate;
        }
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

    // Modèles partagés

    public List<SharedTemplate> LoadSharedTemplates()
    {
        return JsonFileStore.TryLoad<List<SharedTemplate>>(sharedTemplatesPath) ?? [];
    }

    public void SaveSharedTemplates(List<SharedTemplate> list)
    {
        JsonFileStore.Save(sharedTemplatesPath, list);
    }

    public void AddSharedTemplate(SharedTemplate shared)
    {
        var list = LoadSharedTemplates();
        // Supprimer un éventuel doublon du même code
        list.RemoveAll(s => s.Code == shared.Code);
        list.Insert(0, shared);
        SaveSharedTemplates(list);
    }

    public void RemoveSharedTemplate(string code)
    {
        var list = LoadSharedTemplates();
        list.RemoveAll(s => s.Code == code);
        SaveSharedTemplates(list);
    }
}
