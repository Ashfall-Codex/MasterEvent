using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace MasterEvent.Models;

// Fiche de personnage d'un joueur, liée à un modèle d'événement.

[Serializable]
public class PlayerSheet
{
    public string Name { get; set; } = string.Empty;
    public string TemplateName { get; set; } = string.Empty;
    public int Hp { get; set; } = 100;
    public int HpMax { get; set; } = 100;
    public int Mp { get; set; } = 100;
    public int MpMax { get; set; } = 100;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StatValue>? Stats { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CustomCounter>? Counters { get; set; }

    public PlayerSheet DeepCopy()
    {
        return new PlayerSheet
        {
            Name = Name,
            TemplateName = TemplateName,
            Hp = Hp,
            HpMax = HpMax,
            Mp = Mp,
            MpMax = MpMax,
            Stats = Stats?.Select(s => s.DeepCopy()).ToList(),
            Counters = Counters?.Select(c => c.DeepCopy()).ToList(),
        };
    }

    // Crée une fiche à partir d'un modèle avec les valeurs par défaut.
    public static PlayerSheet FromTemplate(EventTemplate template, string profileName)
    {
        return new PlayerSheet
        {
            Name = profileName,
            TemplateName = template.Name,
            HpMax = template.DefaultPlayerHpMax,
            Hp = template.DefaultPlayerHpMax,
            MpMax = template.DefaultPlayerMpMax,
            Mp = template.DefaultPlayerMpMax,
            Stats = template.StatDefinitions?.Select(sd => sd.ToStatValue()).ToList(),
            Counters = template.CounterDefinitions?.Select(cd => cd.ToCounter()).ToList(),
        };
    }
}
