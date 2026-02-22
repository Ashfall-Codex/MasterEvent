using System;

namespace MasterEvent.Models;

[Serializable]
public class CounterDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public int DefaultMax { get; set; } = 100;
    public float ColorR { get; set; } = 0.8f;
    public float ColorG { get; set; } = 0.5f;
    public float ColorB { get; set; } = 0.2f;

    public CounterDefinition DeepCopy()
    {
        return new CounterDefinition
        {
            Id = Id,
            Name = Name,
            DefaultMax = DefaultMax,
            ColorR = ColorR,
            ColorG = ColorG,
            ColorB = ColorB,
        };
    }

    public CustomCounter ToCounter()
    {
        return new CustomCounter
        {
            Id = Id,
            Name = Name,
            Value = DefaultMax,
            Max = DefaultMax,
            ColorR = ColorR,
            ColorG = ColorG,
            ColorB = ColorB,
        };
    }
}
