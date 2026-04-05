using System;
using System.Text.Json.Serialization;

namespace MasterEvent.Models;

[Serializable]
public class DiceResult
{
    public string RollerName { get; set; } = string.Empty;
    public string? RollerHash { get; set; }
    public string? StatName { get; set; }
    public int RawRoll { get; set; }
    public int Modifier { get; set; }
    public int Total { get; set; }
    public int DiceMax { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int[]? IndividualRolls { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
