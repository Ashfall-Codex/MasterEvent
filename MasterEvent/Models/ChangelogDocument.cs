using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MasterEvent.Models;

// Notes de version embarquées, une ressource par langue.
public sealed class ChangelogDocument
{
    [JsonPropertyName("tagline")] public string Tagline { get; set; } = string.Empty;
    [JsonPropertyName("entries")] public List<ChangelogEntry> Entries { get; set; } = [];
}

public sealed class ChangelogEntry
{
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("date")] public string Date { get; set; } = string.Empty;
    [JsonPropertyName("headline")] public string? Headline { get; set; }
    [JsonPropertyName("sections")] public List<ChangelogSection> Sections { get; set; } = [];
}

public sealed class ChangelogSection
{
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;

    // Nom d'une icône FontAwesome, résolu à l'affichage. Une icône inconnue est simplement ignorée.
    [JsonPropertyName("icon")] public string? Icon { get; set; }

    [JsonPropertyName("items")] public List<string> Items { get; set; } = [];
}
