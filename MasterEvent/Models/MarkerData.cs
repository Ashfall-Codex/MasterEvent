using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace MasterEvent.Models;

[Serializable]
public class MarkerData
{
    public string Name { get; set; } = string.Empty;
    public int Hp { get; set; } = 100;
    public int Mp { get; set; } = 100;
    public int HpMax { get; set; } = 100;
    public int MpMax { get; set; } = 100;
    public int Shield { get; set; }
    public Attitude Attitude { get; set; } = Attitude.Neutral;
    public bool IsBoss { get; set; }
    public bool IsVisible { get; set; }
    public int TempModifier { get; set; }
    public int TempModTurns { get; set; }

    [JsonPropertyName("x")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float X { get; set; }

    [JsonPropertyName("y")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Y { get; set; }

    [JsonPropertyName("z")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Z { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CustomCounter>? Counters { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StatValue>? Stats { get; set; }

    // Ephemeral roll state (not serialized)
    [JsonIgnore] public int LastRollResult { get; set; }
    [JsonIgnore] public int LastRollMax { get; set; }

    public bool HasData => !string.IsNullOrEmpty(Name) || IsVisible || IsBoss || Hp != 100 || Mp != 100 || HpMax != 100 || MpMax != 100 || Shield != 0 || Attitude != Attitude.Neutral || TempModifier != 0 || TempModTurns != 0 || (Counters != null && Counters.Count > 0) || (Stats != null && Stats.Count > 0);

    /// <summary>
    /// Copie tous les champs transmissibles depuis un autre MarkerData.
    /// Point unique de copie champ par champ — tout nouveau champ doit être ajouté ici.
    /// </summary>
    public void CopyFrom(MarkerData src)
    {
        Name = src.Name;
        Hp = src.Hp;
        Mp = src.Mp;
        HpMax = src.HpMax;
        MpMax = src.MpMax;
        Shield = src.Shield;
        Counters = src.Counters?.Select(c => c.DeepCopy()).ToList();
        Stats = src.Stats?.Select(s => s.DeepCopy()).ToList();
        Attitude = src.Attitude;
        IsBoss = src.IsBoss;
        IsVisible = src.IsVisible;
        TempModifier = src.TempModifier;
        TempModTurns = src.TempModTurns;
        X = src.X;
        Y = src.Y;
        Z = src.Z;
    }

    public void Reset()
    {
        Name = string.Empty;
        Hp = 100;
        Mp = 100;
        HpMax = 100;
        MpMax = 100;
        Shield = 0;
        Attitude = Attitude.Neutral;
        IsBoss = false;
        IsVisible = false;
        X = 0;
        Y = 0;
        Z = 0;
        TempModifier = 0;
        TempModTurns = 0;
        Counters = null;
        Stats = null;
        LastRollResult = 0;
        LastRollMax = 0;
    }

    public MarkerData DeepCopy()
    {
        return new MarkerData
        {
            Name = Name,
            Hp = Hp,
            Mp = Mp,
            HpMax = HpMax,
            MpMax = MpMax,
            Shield = Shield,
            Attitude = Attitude,
            IsBoss = IsBoss,
            IsVisible = IsVisible,
            TempModifier = TempModifier,
            TempModTurns = TempModTurns,
            X = X,
            Y = Y,
            Z = Z,
            Counters = Counters?.Select(c => c.DeepCopy()).ToList(),
            Stats = Stats?.Select(s => s.DeepCopy()).ToList(),
        };
    }

    // Copie sans les stats (pour le broadcast vers les joueurs).
    public MarkerData DeepCopyWithoutStats()
    {
        return new MarkerData
        {
            Name = Name,
            Hp = Hp,
            Mp = Mp,
            HpMax = HpMax,
            MpMax = MpMax,
            Shield = Shield,
            Attitude = Attitude,
            IsBoss = IsBoss,
            IsVisible = IsVisible,
            TempModifier = TempModifier,
            TempModTurns = TempModTurns,
            X = X,
            Y = Y,
            Z = Z,
            Counters = Counters?.Select(c => c.DeepCopy()).ToList(),
        };
    }

    public bool ContentEquals(MarkerData other)
    {
        return Name == other.Name
            && Hp == other.Hp
            && Mp == other.Mp
            && HpMax == other.HpMax
            && MpMax == other.MpMax
            && Shield == other.Shield
            && Attitude == other.Attitude
            && IsBoss == other.IsBoss
            && IsVisible == other.IsVisible
            && TempModifier == other.TempModifier
            && TempModTurns == other.TempModTurns
            && Math.Abs(X - other.X) < 0.001f
            && Math.Abs(Y - other.Y) < 0.001f
            && Math.Abs(Z - other.Z) < 0.001f
            && CountersEqual(other.Counters)
            && StatsEqual(other.Stats);
    }

    private bool CountersEqual(List<CustomCounter>? other)
    {
        if (Counters == null && other == null) return true;
        if (Counters == null || other == null) return false;
        if (Counters.Count != other.Count) return false;
        for (var i = 0; i < Counters.Count; i++)
            if (!Counters[i].ContentEquals(other[i])) return false;
        return true;
    }

    private bool StatsEqual(List<StatValue>? other)
    {
        if (Stats == null && other == null) return true;
        if (Stats == null || other == null) return false;
        if (Stats.Count != other.Count) return false;
        for (var i = 0; i < Stats.Count; i++)
            if (!Stats[i].ContentEquals(other[i])) return false;
        return true;
    }
}
