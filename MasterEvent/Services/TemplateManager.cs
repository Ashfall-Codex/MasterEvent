using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MasterEvent.Models;

namespace MasterEvent.Services;

public class TemplateManager
{
    private readonly string templatesDir;

    public TemplateManager(string pluginConfigDir)
    {
        templatesDir = Path.Combine(pluginConfigDir, "templates");
        Directory.CreateDirectory(templatesDir);
    }

    public void SaveTemplate(EventTemplate template)
    {
        var path = GetTemplatePath(template.Name);
        var json = JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public EventTemplate? LoadTemplate(string name)
    {
        var path = GetTemplatePath(name);
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<EventTemplate>(json);
    }

    public void DeleteTemplate(string name)
    {
        var path = GetTemplatePath(name);
        if (File.Exists(path))
            File.Delete(path);
    }

    public List<string> GetTemplateNames()
    {
        var names = new List<string>();
        if (!Directory.Exists(templatesDir))
            return names;

        foreach (var file in Directory.GetFiles(templatesDir, "*.json"))
            names.Add(Path.GetFileNameWithoutExtension(file));

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public EventTemplate GetOrCreateDefault()
    {
        var existing = LoadTemplate("Standard");
        if (existing != null)
            return existing;

        var defaultTemplate = EventTemplate.CreateDefault();
        SaveTemplate(defaultTemplate);
        return defaultTemplate;
    }

    private string GetTemplatePath(string name)
    {
        var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(templatesDir, safeName + ".json");
    }
}
