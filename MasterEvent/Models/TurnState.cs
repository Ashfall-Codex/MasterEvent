using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace MasterEvent.Models;

public class TurnState
{
    [JsonPropertyName("entries")]
    public List<TurnEntry> Entries { get; set; } = new();

    [JsonPropertyName("round")]
    public int Round { get; set; } = 1;

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    [JsonPropertyName("diceMax")]
    public int DiceMax { get; set; } = 20;

    public TurnState DeepCopy()
    {
        return new TurnState
        {
            Entries = Entries.Select(e => e.DeepCopy()).ToList(),
            Round = Round,
            IsActive = IsActive,
            DiceMax = DiceMax,
        };
    }
}
