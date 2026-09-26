using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using MasterEvent.Localization;
using MasterEvent.Models;

namespace MasterEvent.Services;

// Charge les notes de version embarquées, dans la langue active, avec repli sur le français.
public sealed class ChangelogService
{
    private ChangelogDocument? document;
    private string? loadedLanguage;

    public ChangelogDocument? Document
    {
        get
        {
            if (document != null && loadedLanguage == Loc.CurrentLanguage) return document;

            loadedLanguage = Loc.CurrentLanguage;

            // Une culture régionale (« fr-FR ») ne doit pas faire manquer la ressource.
            var code = loadedLanguage.Split('-')[0];
            document = Load(code) ?? Load("fr");
            return document;
        }
    }

    private static ChangelogDocument? Load(string language)
    {
        var name = $"MasterEvent.Resources.changelog-{language}.json";

        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) return null;

            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize<ChangelogDocument>(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[MasterEvent] Notes de version illisibles ({name}) : {ex.Message}");
            return null;
        }
    }
}
