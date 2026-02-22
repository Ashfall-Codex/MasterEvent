using System;

namespace MasterEvent.Models;

[Serializable]
public class CustomCounter
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }
    public int Max { get; set; } = 100;
    public float ColorR { get; set; } = 0.8f;
    public float ColorG { get; set; } = 0.5f;
    public float ColorB { get; set; } = 0.2f;

    public CustomCounter DeepCopy()
    {
        return new CustomCounter
        {
            Id = Id,
            Name = Name,
            Value = Value,
            Max = Max,
            ColorR = ColorR,
            ColorG = ColorG,
            ColorB = ColorB,
        };
    }

    public bool ContentEquals(CustomCounter other)
    {
        return Id == other.Id
            && Name == other.Name
            && Value == other.Value
            && Max == other.Max
            && Math.Abs(ColorR - other.ColorR) < 0.001f
            && Math.Abs(ColorG - other.ColorG) < 0.001f
            && Math.Abs(ColorB - other.ColorB) < 0.001f;
    }
}
