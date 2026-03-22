using System;

namespace MasterEvent.Models;

[Serializable]
public class StatDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public int DefaultValue { get; set; }

    public StatDefinition DeepCopy()
    {
        return new StatDefinition
        {
            Id = Id,
            Name = Name,
            DefaultValue = DefaultValue,
        };
    }

    // Convertit cette définition en une valeur de stat avec le modificateur par défaut.

    public StatValue ToStatValue()
    {
        return new StatValue
        {
            Id = Id,
            Name = Name,
            Modifier = DefaultValue,
        };
    }
}
