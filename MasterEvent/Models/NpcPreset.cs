using System;

namespace MasterEvent.Models;
[Serializable]
public sealed class NpcPreset
{
    public string Name { get; set; } = "PNJ";
    public NpcAppearance Appearance { get; set; } = new();
    public ushort EmoteId { get; set; }
    public bool EmoteHeld { get; set; }
    public bool WeaponDrawn { get; set; }
}
