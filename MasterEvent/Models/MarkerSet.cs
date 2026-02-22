using System;

namespace MasterEvent.Models;

[Serializable]
public class MarkerSet
{
    public string PresetName { get; set; } = string.Empty;
    public MarkerData[] Markers { get; set; } = CreateEmpty();

    public MarkerData this[WaymarkId id]
    {
        get => Markers[(int)id];
        set => Markers[(int)id] = value;
    }

    public MarkerSet DeepCopy()
    {
        var copy = new MarkerSet { PresetName = PresetName };
        for (var i = 0; i < Constants.WaymarkCount; i++)
            copy.Markers[i] = Markers[i].DeepCopy();
        return copy;
    }

    public void ResetAll()
    {
        for (var i = 0; i < Constants.WaymarkCount; i++)
            Markers[i].Reset();
    }

    private static MarkerData[] CreateEmpty()
    {
        var markers = new MarkerData[Constants.WaymarkCount];
        for (var i = 0; i < Constants.WaymarkCount; i++)
            markers[i] = new MarkerData();
        return markers;
    }
}
