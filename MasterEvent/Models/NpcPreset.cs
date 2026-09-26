using System;
using System.Collections.Generic;

namespace MasterEvent.Models;
[Serializable]
public sealed class NpcPreset
{
    public string Name { get; set; } = "PNJ";
    public NpcAppearance Appearance { get; set; } = new();
    public ushort EmoteId { get; set; }
    public bool EmoteHeld { get; set; }
    public bool WeaponDrawn { get; set; }
    public List<StatValue>? Stats { get; set; }
    public int HpMax { get; set; }
    public Attitude Attitude { get; set; }
    public List<CustomCounter>? Counters { get; set; }
    public bool IsBoss { get; set; }
}
