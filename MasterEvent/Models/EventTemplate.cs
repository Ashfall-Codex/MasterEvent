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
    public bool RollLowerIsBetter { get; set; }
    public int CriticalSuccessThreshold { get; set; }
    public int CriticalFailureThreshold { get; set; }

    // Mode de résolution des jets de stat. La valeur par défaut (Modifier) reproduit le
    // comportement additif d'origine : les modèles déjà enregistrés se rechargent sans changer
    // de règles.
    public StatResolution StatResolution { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InitiativeStatId { get; set; }
    public int MovementQuota { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MovementStatId { get; set; }

    public int DefaultHpMax { get; set; } = 100;
    public int DefaultMpMax { get; set; } = 100;
    public int DefaultPlayerHpMax { get; set; } = 100;
    public int DefaultPlayerMpMax { get; set; } = 100;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CounterDefinition>? CounterDefinitions { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StatDefinition>? StatDefinitions { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceCode { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SourceVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsSubscription { get; set; }

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
            RollLowerIsBetter = RollLowerIsBetter,
            CriticalSuccessThreshold = CriticalSuccessThreshold,
            CriticalFailureThreshold = CriticalFailureThreshold,
            StatResolution = StatResolution,
            InitiativeStatId = InitiativeStatId,
            MovementQuota = MovementQuota,
            MovementStatId = MovementStatId,
            DefaultHpMax = DefaultHpMax,
            DefaultMpMax = DefaultMpMax,
            DefaultPlayerHpMax = DefaultPlayerHpMax,
            DefaultPlayerMpMax = DefaultPlayerMpMax,
            CounterDefinitions = CounterDefinitions?.Select(c => c.DeepCopy()).ToList(),
            StatDefinitions = StatDefinitions?.Select(s => s.DeepCopy()).ToList(),
            SourceCode = SourceCode,
            SourceVersion = SourceVersion,
            IsSubscription = IsSubscription,
        };
    }

    // Détermine si un jet brut est un succès critique selon les règles du modèle.
    public bool IsCriticalSuccess(int rawRoll)
    {
        if (CriticalSuccessThreshold <= 0)
            return rawRoll >= DiceMax; // fallback legacy : le max du dé
        return RollLowerIsBetter
            ? rawRoll <= CriticalSuccessThreshold
            : rawRoll >= CriticalSuccessThreshold;
    }

    // Détermine si un jet brut est un échec critique selon les règles du modèle.
    public bool IsCriticalFailure(int rawRoll)
    {
        if (CriticalFailureThreshold <= 0)
            return rawRoll <= 1; // fallback legacy : 1
        return RollLowerIsBetter
            ? rawRoll >= CriticalFailureThreshold
            : rawRoll <= CriticalFailureThreshold;
    }

    public bool IsSuccess(int rawRoll, int target)
    {
        return RollLowerIsBetter ? rawRoll <= target : rawRoll >= target;
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
