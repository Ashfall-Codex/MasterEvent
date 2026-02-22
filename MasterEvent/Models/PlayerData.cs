using System.Text.Json.Serialization;

namespace MasterEvent.Models;

public class PlayerData
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("hp")]   public int Hp { get; set; } = 100;
    [JsonPropertyName("isGm")] public bool IsGm { get; set; }
    [JsonPropertyName("canEdit")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CanEdit { get; set; }
    [JsonIgnore] public bool IsConnected { get; set; }
}
