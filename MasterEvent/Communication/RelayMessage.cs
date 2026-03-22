using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using MasterEvent.Models;

namespace MasterEvent.Communication;

public class RelayMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("partyId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartyId { get; set; }

    [JsonPropertyName("playerName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlayerName { get; set; }

    [JsonPropertyName("playerHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlayerHash { get; set; }

    [JsonPropertyName("isLeader")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsLeader { get; set; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("markers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MarkerData[]? Markers { get; set; }

    [JsonPropertyName("playerCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int PlayerCount { get; set; }

    [JsonPropertyName("roomKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoomKey { get; set; }

    [JsonPropertyName("rollMarkerName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RollMarkerName { get; set; }

    [JsonPropertyName("rollResult")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RollResult { get; set; }

    [JsonPropertyName("rollMax")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RollMax { get; set; }

    [JsonPropertyName("players")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlayerData[]? Players { get; set; }

    [JsonPropertyName("targetHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetHash { get; set; }

    [JsonPropertyName("canEdit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CanEdit { get; set; }

    [JsonPropertyName("showMpBar")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ShowMpBar { get; set; }

    [JsonPropertyName("showShield")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ShowShield { get; set; }

    [JsonPropertyName("hpMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HpMode { get; set; }

    [JsonPropertyName("mpMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MpMode { get; set; }

    [JsonPropertyName("template")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EventTemplate? Template { get; set; }

    [JsonPropertyName("gmIsPlayer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool GmIsPlayer { get; set; }

    [JsonPropertyName("turnState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TurnState? TurnState { get; set; }

    [JsonPropertyName("statName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatName { get; set; }

    [JsonPropertyName("rollModifier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RollModifier { get; set; }

    [JsonPropertyName("rollTotal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RollTotal { get; set; }

    [JsonPropertyName("rollerHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RollerHash { get; set; }

    [JsonPropertyName("diceFormula")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DiceFormula { get; set; }

    [JsonPropertyName("rollHistory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiceResult[]? RollHistory { get; set; }

    [JsonPropertyName("stats")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StatValue[]? Stats { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this);

    public static RelayMessage? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<RelayMessage>(json); }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[MasterEvent] Failed to deserialize relay message: {ex.Message}");
            return null;
        }
    }
}
