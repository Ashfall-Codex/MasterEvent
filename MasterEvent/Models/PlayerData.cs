using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MasterEvent.Models;

public class PlayerData
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("hp")]   public int Hp { get; set; } = 100;
    [JsonPropertyName("hpMax")] public int HpMax { get; set; } = 100;
    [JsonPropertyName("mp")]   public int Mp { get; set; } = 100;
    [JsonPropertyName("mpMax")] public int MpMax { get; set; } = 100;
    [JsonPropertyName("shield")] public int Shield { get; set; }
    [JsonPropertyName("counters")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CustomCounter>? Counters { get; set; }
    [JsonPropertyName("stats")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StatValue>? Stats { get; set; }
    [JsonPropertyName("tempMod")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TempModifier { get; set; }
    [JsonPropertyName("tempTurns")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TempModTurns { get; set; }
    [JsonPropertyName("isGm")] public bool IsGm { get; set; }
    [JsonPropertyName("canEdit")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CanEdit { get; set; }
    [JsonIgnore] public bool IsConnected { get; set; }
    [JsonIgnore] public bool IsAlliancePlayer { get; set; }
}
