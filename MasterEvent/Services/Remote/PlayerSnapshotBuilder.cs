using System;
using System.Collections.Generic;
using System.Linq;
using MasterEvent.Communication;
using MasterEvent.Communication.Remote;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services.Npc;

namespace MasterEvent.Services.Remote;

// Instantané de la vue joueur pour le second écran, avec ses filtres : marqueurs montrés seulement, PNJ
// répliqués vivants, aucune stat d'autrui. Ne lit jamais Configuration.LeaderToken, RoomFileToken,
// RelayRoomKey (clé de salle), MasterEventAccountId, ni les jetons du second écran.
public sealed class PlayerSnapshotBuilder
{
    private const int MaxRolls = 20;

    private readonly SessionManager session;
    private readonly NpcManager npcs;
    private readonly Func<bool> inParty;
    private readonly Func<bool> inInstance;
    private readonly Func<bool> consentValid;

    // consentValid : Configuration.IsRgpdConsentValid, qui choisit le texte de l'état hors ligne.
    public PlayerSnapshotBuilder(SessionManager session, NpcManager npcs, Func<bool> inParty, Func<bool> inInstance,
        Func<bool>? consentValid = null)
    {
        this.session = session;
        this.npcs = npcs;
        this.inParty = inParty;
        this.inInstance = inInstance;
        this.consentValid = consentValid ?? (() => true);
    }

    // Thread framework uniquement : SessionManager se lit sans verrou et NpcInstance.IsAlive lit la mémoire du jeu.
    public RemotePlayerState Build(long seq)
    {
        var instance = inInstance();
        var view = PlayerRemoteCaps.ViewOf(session, inParty(), instance);
        var players = SessionPlayers();
        var link = BuildLink(players, instance);
        if (!PlayerRemoteCaps.HasAccess(session, view) || PlayerRemoteCaps.LocalPlayer(session) is not { } local)
            return new RemotePlayerState { Seq = seq, Access = "none", Link = link };

        var inRoom = PlayerRemoteCaps.IsInRoom(view);
        var combatShown = inRoom && session.LinkState == RelayLinkState.InSession && !instance
                          && session.CurrentTurnState is { IsActive: true, Entries.Count: > 0 };
        var fieldNpcs = npcs.Instances.Where(n => n.IsReplicated && n.IsAlive).ToList();
        var others = players.Where(p => p.Hash != session.LocalPlayerHash).ToList();

        return new RemotePlayerState
        {
            Seq = seq,
            Access = "player",
            Caps = PlayerRemoteCaps.For(session, view, instance),
            Room = PlayerRemoteCaps.Room(session, view),
            View = ViewName(view),
            CombatPresent = session.CurrentTurnState is { IsActive: true, Entries.Count: > 0 },
            ViewDetail = BuildViewDetail(),
            Link = link,
            Display = BuildDisplay(),
            Gm = session.GmPresence is { } gm ? new RemotePlayerGm(gm.Name, gm.Connected) : null,
            Template = session.ActiveTemplate?.Name,
            LowerIsBetter = session.ActiveTemplate?.RollLowerIsBetter ?? false,
            Primary = BuildPrimary(session.ResolvePlayerPrimary(instance)),
            Request = inRoom && session.PendingRollRequest is { } request
                ? new RemotePlayerRequest(request.StatId, request.StatName, request.Threshold, request.StatMissing)
                : null,
            Announcement = PlayerRemoteCaps.IsSessionLike(view) ? session.LastGmAnnouncement : null,
            Ambiance = BuildAmbiance(),
            Me = BuildMe(local, combatShown),
            Field = BuildField(fieldNpcs),
            Players = others.Select(BuildMate).ToList(),
            Turn = combatShown && session.CurrentTurnState is { } state ? BuildTurn(state, fieldNpcs, others) : null,
            Rolls = BuildRolls(),
            LastRoll = BuildLastRoll(),
        };
    }

    // Le MJ de la scène n'est pas un joueur, sauf s'il participe (PlayerWindow.SessionPlayers).
    private List<PlayerData> SessionPlayers()
    {
        var gmHash = session.GmPresence?.Hash;
        return session.PartyMembers.Where(p => session.GmIsPlayer || p.Hash != gmHash).ToList();
    }

    // Les comptes de la puce de salle, qui comptent soi-même.
    private RemoteLink BuildLink(List<PlayerData> players, bool instance) => new()
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
        Suspended = instance,
        PartyShown = players.Count,
        PartyOnline = players.Count(p => p.IsConnected),
    };

    private static string ViewName(PlayerRemoteView view) => view switch
    {
        PlayerRemoteView.Instance => "instance",
        PlayerRemoteView.VersionRejected => "versionRejected",
        PlayerRemoteView.JoinRejected => "joinRejected",
        PlayerRemoteView.AwaitingApproval => "awaitingApproval",
        PlayerRemoteView.Offline => "offline",
        PlayerRemoteView.Reconnecting => "reconnecting",
        PlayerRemoteView.OutOfSession => "outOfSession",
        PlayerRemoteView.Joining => "joining",
        PlayerRemoteView.Handover => "handover",
        PlayerRemoteView.GmAway => "gmAway",
        _ => "inSession",
    };

    private RemotePlayerViewDetail BuildViewDetail()
    {
        // Comme PlayerWindow.RejectionHint : un refus du MJ hors lobby n'a pas de code à citer.
        var rejection = session.JoinRejection switch
        {
            JoinRejection.None => null,
            JoinRejection.Denied when session.IsLobbyMode => "denied",
            JoinRejection.RoomLimit => "roomLimit",
            JoinRejection.RateLimited => "rateLimited",
            _ => "invalid",
        };
        return new RemotePlayerViewDetail(session.IsLobbyMode ? session.LobbyCode : null, session.RejectedMinVersion,
            rejection, consentValid());
    }

    private RemoteDisplay BuildDisplay() => new()
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

    private static RemotePlayerPrimary? BuildPrimary(PlayerPrimary primary)
    {
        var kind = primary.Kind switch
        {
            PlayerPrimaryKind.Roll => "roll",
            PlayerPrimaryKind.EndTurn => "endTurn",
            _ => null,
        };
        if (kind is null) return null;

        var hold = primary.Hold switch
        {
            EndTurnHold.Sent => "sent",
            EndTurnHold.Handover => "handover",
            EndTurnHold.GmAway => "gmAway",
            _ => "none",
        };
        return new RemotePlayerPrimary(kind, primary.StatName, primary.CanEndTurn, primary.GroupTurn, hold);
    }

    // Seulement ce que le MJ a imposé (PlayerWindow.AmbianceText) : le joueur garde sinon la météo du jeu.
    private RemoteAmbiance? BuildAmbiance()
    {
        if (!session.IsAmbianceReceived) return null;

        var weather = session.CurrentWeatherId != 0
            ? session.CurrentWeatherName ?? session.CurrentWeatherId.ToString()
            : null;
        var time = session.CurrentEorzeaTime is { } seconds ? $"{WeatherService.SecondsToHour(seconds):00}:00" : null;
        return weather is null && time is null ? null : new RemoteAmbiance { Weather = weather, Time = time };
    }

    private RemotePlayerSheet BuildMe(PlayerData local, bool combatShown) => new()
    {
        Name = local.Name,
        Hash = local.Hash,
        Sheet = session.LoadedSheetName,
        Hp = local.Hp,
        HpMax = local.HpMax,
        Mp = local.Mp,
        MpMax = local.MpMax,
        Shield = session.ShowShield ? local.Shield : 0,
        Counters = MapCounters(local.Counters),
        TempModifier = local.TempModifier,
        TempRounds = local.TempModTurns,
        MoveLeft = local.MoveLeft,
        MoveMax = local.MoveMax,
        Stats = MapStats(local.Stats),
        MyTurn = combatShown && session.IsLocalPlayerTurn,
    };

    // Un marqueur caché ou sans nom n'existe pas pour les joueurs ; ses stats ne leur sont jamais envoyées.
    private static bool IsShownMarker(MarkerData marker) => marker.IsVisible && !string.IsNullOrEmpty(marker.Name);

    private List<WaymarkId> ShownMarkers()
    {
        var shown = new List<WaymarkId>();
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            var id = (WaymarkId)i;
            if (IsShownMarker(session.CurrentMarkers[id])) shown.Add(id);
        }
        return shown;
    }

    private List<RemotePlayerField> BuildField(List<NpcInstance> fieldNpcs)
    {
        var field = new List<RemotePlayerField>();
        var showMp = session.ShowMpBar;
        foreach (var id in ShownMarkers())
        {
            var m = session.CurrentMarkers[id];
            var rolled = m.LastRollResult > 0;
            field.Add(new RemotePlayerField
            {
                Kind = "marker",
                Ref = id.ToLabel(),
                Name = m.Name,
                Attitude = AttitudeName(m.Attitude),
                Hp = m.Hp,
                HpMax = m.HpMax,
                Shield = session.ShowShield ? m.Shield : 0,
                Boss = m.IsBoss,
                Mp = showMp ? m.Mp : null,
                MpMax = showMp ? m.MpMax : null,
                Counters = MapCounters(m.Counters),
                TempModifier = m.TempModifier,
                TempRounds = m.TempModTurns,
                LastRoll = rolled ? m.LastRollResult : null,
                LastRollMax = rolled ? m.LastRollMax : null,
            });
        }

        // Ni stats ni compteurs : NpcSyncData les transporte, la vue ne les montre pas.
        foreach (var npc in fieldNpcs)
        {
            field.Add(new RemotePlayerField
            {
                Kind = "npc",
                Ref = NpcRef(npc.NetworkId),
                Name = npc.DisplayName,
                Creature = npc.IsCreature,
                Attitude = AttitudeName(npc.Attitude),
                Hp = npc.Hp,
                HpMax = npc.HpMax,
                Shield = session.ShowShield ? npc.Shield : 0,
                Boss = npc.IsBoss,
            });
        }
        return field;
    }

    // Ni stats, ni compteurs, ni éther, ni bonus, ni sous-groupe : la liste en jeu ne les montre pas.
    private RemotePlayerMate BuildMate(PlayerData p) => new()
    {
        Name = p.Name,
        Hash = p.Hash,
        Connected = p.IsConnected,
        Hp = p.Hp,
        HpMax = p.HpMax,
        Shield = session.ShowShield ? p.Shield : 0,
        CoGm = p.CanEdit,
    };

    private RemotePlayerTurn BuildTurn(TurnState state, List<NpcInstance> fieldNpcs, List<PlayerData> others)
    {
        var (total, acted) = state.CountBlocks();
        var activeIndex = session.ActiveTurnIndex;
        var activeEntry = activeIndex >= 0 && activeIndex < state.Entries.Count ? state.Entries[activeIndex] : null;

        // Comme la puce du tour : un groupe sans nom laisserait la puce sans personne.
        string? turnOf = null;
        if (activeEntry != null)
        {
            var label = state.FindGroupFor(activeEntry)?.Label;
            turnOf = string.IsNullOrWhiteSpace(label) ? activeEntry.Name : label;
        }

        var entries = new RemotePlayerTurnEntry[state.Entries.Count];
        for (var i = 0; i < entries.Length; i++)
        {
            var e = state.Entries[i];
            var (kind, reference) = TurnRef(e, fieldNpcs);
            var label = state.FindGroupFor(e)?.Label;
            entries[i] = new RemotePlayerTurnEntry
            {
                Name = e.Name,
                Initiative = e.Initiative,
                Roll = e.InitiativeRoll,
                Mod = e.InitiativeModifier,
                Stat = e.InitiativeStatName,
                Acted = state.HasEntryActed(e),
                Active = activeEntry != null
                         && (i == activeIndex || (e.GroupId != null && e.GroupId == activeEntry.GroupId)),
                Mine = e.PlayerHash != null && e.PlayerHash == session.LocalPlayerHash,
                Kind = kind,
                Ref = reference,
                Group = e.GroupId,
                GroupLabel = string.IsNullOrWhiteSpace(label) ? null : label,
            };
        }

        var alsoField = ShownMarkers().Where(id => state.Entries.All(e => e.WaymarkIndex != (int)id))
            .Select(id => id.ToLabel())
            .Concat(fieldNpcs.Where(n => state.Entries.All(e => !IsSameNpc(e.NpcId, n))).Select(n => NpcRef(n.NetworkId)))
            .ToList();
        var alsoPlayers = others.Where(p => state.Entries.All(e => e.PlayerHash != p.Hash)).Select(p => p.Hash).ToList();

        return new RemotePlayerTurn
        {
            Round = state.Round,
            Acted = acted,
            Total = total,
            DiceMax = state.DiceMax,
            ActiveIndex = activeIndex,
            TurnOf = turnOf,
            Entries = entries,
            AlsoOnField = new RemotePlayerAlsoOnField(alsoField, alsoPlayers),
        };
    }

    // Même résolution que PlayerWindow.DrawTurnRow : un marqueur caché, un PNJ absent du terrain ou un joueur
    // parti gardent leur place dans l'ordre, sans barre.
    private (string Kind, string? Ref) TurnRef(TurnEntry entry, List<NpcInstance> fieldNpcs)
    {
        if (entry.WaymarkIndex is { } wi && wi >= 0 && wi < Constants.WaymarkCount)
        {
            var id = (WaymarkId)wi;
            return ("marker", IsShownMarker(session.CurrentMarkers[id]) ? id.ToLabel() : null);
        }

        if (entry.NpcId != null)
        {
            var npc = fieldNpcs.FirstOrDefault(n => IsSameNpc(entry.NpcId, n));
            return ("npc", npc != null ? NpcRef(npc.NetworkId) : null);
        }

        if (entry.PlayerHash is { } hash && session.PartyMembers.Any(p => p.Hash == hash))
            return ("player", hash);

        return ("other", null);
    }

    private static bool IsSameNpc(string? npcId, NpcInstance npc)
        => npcId != null && Guid.TryParse(npcId, out var id) && id == npc.NetworkId;

    private static string NpcRef(Guid networkId) => networkId.ToString("N");

    // RollHistory est déjà rangé du plus récent au plus ancien (AddRollToHistory insère en tête).
    private IReadOnlyList<RemoteRoll> BuildRolls()
    {
        var history = session.RollHistory;
        var count = Math.Min(MaxRolls, history.Count);
        if (count == 0) return [];

        var rolls = new RemoteRoll[count];
        for (var i = 0; i < count; i++)
        {
            var r = history[i];
            rolls[i] = new RemoteRoll
            {
                Roller = r.RollerName,
                Stat = r.StatName,
                Raw = r.RawRoll,
                Mod = r.Modifier,
                Total = r.Total,
                DiceMax = r.DiceMax,
                Target = r.Target,
                Success = r.Success,
                Dice = r.IndividualRolls,
                At = new DateTimeOffset(r.Timestamp).ToUnixTimeMilliseconds(),
            };
        }
        return rolls;
    }

    // Posé à la fin de l'animation du dé, comme en jeu. La part de la stat se déduit pour un jet ancien.
    private RemotePlayerRoll? BuildLastRoll()
    {
        if (session.LastOwnRoll is not { } r) return null;

        return new RemotePlayerRoll
        {
            Roller = r.RollerName,
            Stat = r.StatName,
            Raw = r.RawRoll,
            Mod = r.Modifier,
            StatMod = r.StatName != null ? r.StatModifier ?? r.Modifier - (r.TempModifier ?? 0) : null,
            TempMod = r.TempModifier,
            Total = r.Total,
            DiceMax = r.DiceMax,
            Target = r.Target,
            Success = r.Success,
            Dice = r.IndividualRolls,
            At = new DateTimeOffset(r.Timestamp).ToUnixTimeMilliseconds(),
        };
    }

    private static string AttitudeName(Attitude attitude) => attitude switch
    {
        Attitude.Hostile => "hostile",
        Attitude.Friendly => "friendly",
        _ => "neutral",
    };

    private static IReadOnlyList<RemoteStat> MapStats(List<StatValue>? stats)
    {
        if (stats is not { Count: > 0 }) return [];

        var result = new RemoteStat[stats.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = new RemoteStat(stats[i].Id ?? string.Empty, stats[i].Name, stats[i].Modifier);
        return result;
    }

    private static IReadOnlyList<RemoteCounter> MapCounters(List<CustomCounter>? counters)
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
