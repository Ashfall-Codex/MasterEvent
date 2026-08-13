using System.Text.Json.Serialization;

namespace MasterEvent.Models;

public sealed class NpcSyncData
{
    // Identifiant réseau stable, partagé par tous les clients (Guid "N").
    [JsonPropertyName("id")]
    public string NetworkId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "PNJ";

    // Territoire (TerritoryType) d'ancrage : seuls les membres présents dans
    // ce même territoire répliquent le PNJ.
    [JsonPropertyName("territory")]
    public ushort Territory { get; set; }

    [JsonPropertyName("appearance")]
    public NpcAppearance Appearance { get; set; } = new();

    // Position et rotation figées par le GM (monde), pour un placement identique
    // chez tous les récepteurs.
    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("z")]
    public float Z { get; set; }

    [JsonPropertyName("rot")]
    public float Rotation { get; set; }

    public ushort EmoteId { get; set; }

    public bool EmoteHeld { get; set; }

    public bool WeaponDrawn { get; set; }
}
