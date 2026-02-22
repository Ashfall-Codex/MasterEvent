using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace MasterEvent.Models;

[Serializable]
public class EventTemplate
{
    public string Name { get; set; } = string.Empty;
    public bool ShowHpBar { get; set; } = true;
    public HpMode HpMode { get; set; } = HpMode.Points;
    public bool ShowMpBar { get; set; } = true;
    public HpMode MpMode { get; set; } = HpMode.Points;
    public bool ShowShield { get; set; } = true;
    public int DiceMax { get; set; } = 999;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CounterDefinition>? CounterDefinitions { get; set; }

    public EventTemplate DeepCopy()
    {
        return new EventTemplate
        {
            Name = Name,
            ShowHpBar = ShowHpBar,
            HpMode = HpMode,
            ShowMpBar = ShowMpBar,
            MpMode = MpMode,
            ShowShield = ShowShield,
            DiceMax = DiceMax,
            CounterDefinitions = CounterDefinitions?.Select(c => c.DeepCopy()).ToList(),
        };
    }

    public static EventTemplate CreateDefault()
    {
        return new EventTemplate
        {
            Name = "Standard",
            ShowHpBar = true,
            HpMode = HpMode.Points,
            ShowMpBar = true,
            MpMode = HpMode.Points,
            ShowShield = true,
            DiceMax = 999,
            CounterDefinitions = null,
        };
    }
}
