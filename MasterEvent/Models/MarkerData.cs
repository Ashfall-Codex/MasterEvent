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

    [JsonPropertyName("x")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float X { get; set; }

    [JsonPropertyName("y")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Y { get; set; }

    [JsonPropertyName("z")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Z { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CustomCounter>? Counters { get; set; }

    // Ephemeral roll state (not serialized)
    [JsonIgnore] public int LastRollResult { get; set; }
    [JsonIgnore] public int LastRollMax { get; set; }

    public bool HasData => !string.IsNullOrEmpty(Name) || IsVisible || IsBoss || Hp != 100 || Mp != 100 || Shield != 0 || Attitude != Attitude.Neutral;

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
        Counters = null;
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
            && Math.Abs(X - other.X) < 0.001f
            && Math.Abs(Y - other.Y) < 0.001f
            && Math.Abs(Z - other.Z) < 0.001f
            && CountersEqual(other.Counters);
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
}
