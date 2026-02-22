namespace MasterEvent.Models;

public enum WaymarkId
{
    A = 0,
    B = 1,
    C = 2,
    D = 3,
    One = 4,
    Two = 5,
    Three = 6,
    Four = 7,
}

public static class WaymarkIdExtensions
{
    public static string ToLabel(this WaymarkId id) => id switch
    {
        WaymarkId.A => "A",
        WaymarkId.B => "B",
        WaymarkId.C => "C",
        WaymarkId.D => "D",
        WaymarkId.One => "1",
        WaymarkId.Two => "2",
        WaymarkId.Three => "3",
        WaymarkId.Four => "4",
        _ => "?",
    };

    public static uint ToIconId(this WaymarkId id) => id switch
    {
        WaymarkId.A => 61241,
        WaymarkId.B => 61242,
        WaymarkId.C => 61243,
        WaymarkId.D => 61247,
        WaymarkId.One => 61244,
        WaymarkId.Two => 61245,
        WaymarkId.Three => 61246,
        WaymarkId.Four => 61248,
        _ => 61241,
    };
}
