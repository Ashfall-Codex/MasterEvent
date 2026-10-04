using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MasterEvent.Models;

namespace MasterEvent.Services.Npc;

public sealed class NpcPresetStore
{
    private const int MaxNameLength = 48;

    private readonly string directory;

    public NpcPresetStore(string pluginConfigDir)
    {
        directory = Path.Combine(pluginConfigDir, "presets", "npc");
    }

    public IReadOnlyList<string> GetNames()
    {
        try
        {
            if (!Directory.Exists(directory)) return [];
            return Directory.GetFiles(directory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[NpcPresets] Lecture du dossier impossible : {ex.Message}");
            return [];
        }
    }

    public NpcPreset? Load(string name)
    {
        var path = PathFor(name);
        if (path is null) return null;

        var preset = JsonFileStore.TryLoad<NpcPreset>(path);
        if (preset is null) return null;

        // Le nom du fichier fait autorité

        preset.Name = name;
        preset.Appearance.Name = name;
        return preset;
    }

    public bool Save(NpcPreset preset, out string? error)
    {
        error = null;
        var path = PathFor(preset.Name);
        if (path is null)
        {
            error = "Nom invalide.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory);
            JsonFileStore.Save(path, preset);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Plugin.Log.Warning($"[NpcPresets] Écriture de '{preset.Name}' impossible : {ex.Message}");
            return false;
        }
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (path is null || !File.Exists(path)) return;

        try { File.Delete(path); }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[NpcPresets] Suppression de '{name}' impossible : {ex.Message}");
        }
    }

    public bool Exists(string name) => PathFor(name) is { } p && File.Exists(p);

    private string? PathFor(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength) return null;
        if (trimmed.Any(c => Path.GetInvalidFileNameChars().Contains(c))) return null;
        if (trimmed is "." or "..") return null;

        return Path.Combine(directory, trimmed + ".json");
    }
}
