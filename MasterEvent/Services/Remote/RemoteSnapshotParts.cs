using System;
using System.Collections.Generic;
using MasterEvent.Communication;
using MasterEvent.Communication.Remote;
using MasterEvent.Localization;
using MasterEvent.Models;

namespace MasterEvent.Services.Remote;

// Ce que les instantanés du MJ et du joueur écrivent de la même façon. Thread framework uniquement.
internal static class RemoteSnapshotParts
{
    private const int MaxRolls = 20;

    // shown et online : les comptes de la barre de séance, que chaque vue établit à sa manière.
    public static RemoteLink Link(SessionManager session, bool suspended, int shown, int online) => new()
    {
        State = session.LinkState switch
        {
            RelayLinkState.InSession => "inSession",
            RelayLinkState.ServerOnly => "serverOnly",
            RelayLinkState.Reconnecting => "reconnecting",
            _ => "offline",
        },
        LobbyCode = session.IsLobbyMode ? session.LobbyCode : null,
        OnlinePlayers = session.ConnectedPlayerCount,
        Suspended = suspended,
        PartyShown = shown,
        PartyOnline = online,
    };

    public static RemoteDisplay Display(SessionManager session) => new()
    {
        HpMode = ModeName(session.HpMode),
        MpMode = ModeName(session.MpMode),
        ShowMp = session.ShowMpBar,
        ShowShield = session.ShowShield,
        HpLabel = VitalLabels.Hp,
        MpLabel = VitalLabels.Mp,
        StatTarget = VitalLabels.ActiveTemplate is { StatResolution: StatResolution.Target },
    };

    private static string ModeName(HpMode mode) => mode == HpMode.Percentage ? "percent" : "points";

    public static string? EorzeaHour(SessionManager session)
        => session.CurrentEorzeaTime is { } seconds ? $"{WeatherService.SecondsToHour(seconds):00}:00" : null;

    // RollHistory est déjà rangé du plus récent au plus ancien (AddRollToHistory insère en tête).
    public static IReadOnlyList<RemoteRoll> Rolls(SessionManager session)
    {
        var history = session.RollHistory;
        var count = Math.Min(MaxRolls, history.Count);
        if (count == 0) return [];

        var rolls = new RemoteRoll[count];
        for (var i = 0; i < count; i++)
            rolls[i] = Roll(history[i]);
        return rolls;
    }

    private static RemoteRoll Roll(DiceResult r) => new()
    {
        Roller = r.RollerName,
        Stat = r.StatName,
        Raw = r.RawRoll,
        Mod = r.Modifier,
        Total = r.Total,
        DiceMax = r.DiceMax,
        Target = r.Target,
        Success = r.Success,
        // Partagé sans copie : le tableau d'un jet n'est jamais modifié après sa création.
        Dice = r.IndividualRolls,
        At = UnixMs(r.Timestamp),
    };

    public static long UnixMs(DateTime timestamp) => new DateTimeOffset(timestamp).ToUnixTimeMilliseconds();

    // Format « N » de RemoteNpc.Id et des Ref de PNJ.
    public static string NpcRef(Guid networkId) => networkId.ToString("N");

    public static string AttitudeName(Attitude attitude) => attitude switch
    {
        Attitude.Hostile => "hostile",
        Attitude.Friendly => "friendly",
        _ => "neutral",
    };

    public static IReadOnlyList<RemoteStat> MapStats(List<StatValue>? stats)
    {
        if (stats is not { Count: > 0 }) return [];

        var result = new RemoteStat[stats.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = new RemoteStat(stats[i].Id ?? string.Empty, stats[i].Name, stats[i].Modifier);
        return result;
    }

    public static IReadOnlyList<RemoteCounter> MapCounters(List<CustomCounter>? counters)
    {
        if (counters is not { Count: > 0 }) return [];

        var result = new RemoteCounter[counters.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var c = counters[i];
            result[i] = new RemoteCounter(c.Name, c.Value, c.Max, HexColor(c.ColorR, c.ColorG, c.ColorB));
        }
        return result;
    }

    private static string HexColor(float r, float g, float b)
        => $"#{Channel(r):x2}{Channel(g):x2}{Channel(b):x2}";

    private static int Channel(float value) => (int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
}
