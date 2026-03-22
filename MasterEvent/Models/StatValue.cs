using System;

namespace MasterEvent.Models;

[Serializable]
public class StatValue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public int Modifier { get; set; }

    public StatValue DeepCopy()
    {
        return new StatValue
        {
            Id = Id,
            Name = Name,
            Modifier = Modifier,
        };
    }

    public bool ContentEquals(StatValue other)
    {
        return Id == other.Id
            && Name == other.Name
            && Modifier == other.Modifier;
    }
}
