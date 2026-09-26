using MasterEvent.Models;

namespace MasterEvent.Localization;

public static class VitalLabels
{
    public static EventTemplate? ActiveTemplate { get; set; }

    public static string Hp => Pick(ActiveTemplate?.HpLabel, "Marker.Hp");
    public static string Mp => Pick(ActiveTemplate?.MpLabel, "Marker.Mp");

    public static string HpMax => string.Format(Loc.Get("Config.MaxOf"), Hp);
    public static string MpMax => string.Format(Loc.Get("Config.MaxOf"), Mp);

    public static string PlayerHpMax => string.Format(Loc.Get("Config.PlayerMaxOf"), Hp);
    public static string PlayerMpMax => string.Format(Loc.Get("Config.PlayerMaxOf"), Mp);

    private static string Pick(string? custom, string key)
        => string.IsNullOrWhiteSpace(custom) ? Loc.Get(key) : custom.Trim();
}
