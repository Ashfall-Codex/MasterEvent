using System.Text.Json.Serialization;

namespace MasterEvent.Models;

// Représente un groupe de participants qui jouent pendant la même phase du tour.
public class TurnGroup
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("hasActed")]
    public bool HasActed { get; set; }

    public TurnGroup DeepCopy()
    {
        return new TurnGroup
        {
            Id = Id,
            Label = Label,
            HasActed = HasActed,
        };
    }
}
