using System.Text.Json.Serialization;

namespace MasterEvent.Models;

/// Joueur en attente d'approbation du MJ pour entrer dans le lobby.
public class PendingMember
{
    [JsonPropertyName("playerName")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("playerHash")] public string Hash { get; set; } = string.Empty;

    /// Party FFXIV d'origine.
    [JsonPropertyName("groupId")] public string? GroupId { get; set; }
}
