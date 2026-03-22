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
    public string DiceFormula { get; set; } = "1d100";

    /// <summary>
    /// Identifiant de la stat liée à l'initiative (null = pas de stat).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InitiativeStatId { get; set; }

    public int DefaultHpMax { get; set; } = 100;
    public int DefaultMpMax { get; set; } = 100;
    public int DefaultPlayerHpMax { get; set; } = 100;
    public int DefaultPlayerMpMax { get; set; } = 100;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CounterDefinition>? CounterDefinitions { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StatDefinition>? StatDefinitions { get; set; }

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
            DiceFormula = DiceFormula,
            InitiativeStatId = InitiativeStatId,
            DefaultHpMax = DefaultHpMax,
            DefaultMpMax = DefaultMpMax,
            DefaultPlayerHpMax = DefaultPlayerHpMax,
            DefaultPlayerMpMax = DefaultPlayerMpMax,
            CounterDefinitions = CounterDefinitions?.Select(c => c.DeepCopy()).ToList(),
            StatDefinitions = StatDefinitions?.Select(s => s.DeepCopy()).ToList(),
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
            DiceFormula = "1d100",
            DefaultHpMax = 100,
            DefaultMpMax = 100,
            DefaultPlayerHpMax = 100,
            DefaultPlayerMpMax = 100,
            CounterDefinitions = null,
            StatDefinitions = null,
        };
    }
}
