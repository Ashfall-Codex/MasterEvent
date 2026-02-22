using System;

namespace MasterEvent.Models;

[Serializable]
public class GmCache
{
    public DateTime SavedAt { get; set; }
    public MarkerData[] Markers { get; set; } = Array.Empty<MarkerData>();
    public string HpMode { get; set; } = string.Empty;
    public string MpMode { get; set; } = string.Empty;
    public bool ShowMpBar { get; set; }
    public bool ShowShield { get; set; }
    public int DiceMax { get; set; } = 999;
    public EventTemplate? ActiveTemplate { get; set; }
}
