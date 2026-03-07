using System.Text.Json.Serialization;

namespace MasterEvent.Models;

public class TurnEntry
{
    [JsonPropertyName("waymarkIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WaymarkIndex { get; set; }

    [JsonPropertyName("playerHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlayerHash { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("initiative")]
    public int Initiative { get; set; }

    [JsonPropertyName("initRoll")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int InitiativeRoll { get; set; }

    [JsonPropertyName("initMod")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int InitiativeModifier { get; set; }

    [JsonPropertyName("initStatName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InitiativeStatName { get; set; }

    [JsonPropertyName("hasActed")]
    public bool HasActed { get; set; }

    [JsonIgnore]
    public bool IsMarker => WaymarkIndex.HasValue;

    public TurnEntry DeepCopy()
    {
        return new TurnEntry
        {
            WaymarkIndex = WaymarkIndex,
            PlayerHash = PlayerHash,
            Name = Name,
            Initiative = Initiative,
            InitiativeRoll = InitiativeRoll,
            InitiativeModifier = InitiativeModifier,
            InitiativeStatName = InitiativeStatName,
            HasActed = HasActed,
        };
    }
}
