using System;
using System.Collections.Generic;
using MasterEvent.Communication.Remote;
using MasterEvent.Models;
using MasterEvent.Services.Npc;

namespace MasterEvent.Services.Remote;

// Instantané de l'onglet Scène pour le second écran, avec les mêmes filtres et libellés que lui.
// Ne lit jamais Configuration.LeaderToken, RoomFileToken, RelayRoomKey (clé de salle), MasterEventAccountId, ni rien d'UmbraSync.
public sealed class GmSnapshotBuilder
{
    private readonly SessionManager session;
    private readonly NpcManager npcs;
    private readonly Func<bool> relaySuspended;

    // relaySuspended : vrai quand Plugin a coupé le relais en entrant en instance.
    public GmSnapshotBuilder(SessionManager session, NpcManager npcs, Func<bool> relaySuspended)
    {
        this.session = session;
        this.npcs = npcs;
        this.relaySuspended = relaySuspended;
    }

    // Thread framework uniquement : SessionManager se lit sans verrou et NpcInstance.IsAlive lit la mémoire du jeu.
    public RemoteState Build(long seq)
    {
        var link = BuildLink();
        var caps = RemoteCaps.For(session);

        // Mêmes droits que l'onglet Scène : le co-MJ promu voit une Scène réduite aux marqueurs et aux jets.
        var gmAccess = session.HasGmAccess;
        if (!gmAccess && !session.IsPromoted)
            return new RemoteState { Seq = seq, Access = "none", Caps = caps, Link = link };

        return new RemoteState
        {
            Seq = seq,
            Access = gmAccess ? "gm" : "coGm",
            Caps = caps,
            Rules = gmAccess ? new RemoteRules(session.DiceMax, session.ActiveTemplate?.RollLowerIsBetter ?? false) : null,
            Link = link,
            Display = RemoteSnapshotParts.Display(session),
            Primary = BuildPrimary(gmAccess),
            Party = gmAccess ? BuildParty() : [],
            Markers = BuildMarkers(),
            Npcs = gmAccess ? BuildNpcs() : [],
            Turn = gmAccess ? BuildTurn() : null,
            Pending = gmAccess && (session.IsGm || session.IsPromoted) ? BuildPending() : [],
            Rolls = RemoteSnapshotParts.Rolls(session),
            Ambiance = gmAccess ? BuildAmbiance() : new RemoteAmbiance(),
        };
    }

    // Compte de GmWindow.BuildSceneChips : le MJ n'y figure que s'il joue aussi.
    private RemoteLink BuildLink()
    {
        var shown = 0;
        var online = 0;
        foreach (var p in session.PartyMembers)
        {
            if (p.IsGm && !session.GmIsPlayer) continue;
            shown++;
            if (p.IsConnected) online++;
        }
        return RemoteSnapshotParts.Link(session, relaySuspended(), shown, online);
    }

    private RemotePrimary? BuildPrimary(bool gmAccess)
    {
        var (kind, _, name) = session.ResolveScenePrimary(gmAccess);
        return kind switch
        {
            ScenePrimaryKind.Start => new RemotePrimary("start"),
            ScenePrimaryKind.EndTurn => new RemotePrimary("endTurn", name),
            ScenePrimaryKind.NextRound => new RemotePrimary("nextRound"),
            _ => null,
        };
    }

    // Filtre de GmWindow.DrawScenePlayers : le MJ n'y figure que s'il joue aussi.
    private IReadOnlyList<RemotePlayer> BuildParty()
    {
        var members = session.PartyMembers;
        var party = new List<RemotePlayer>(members.Count);
        foreach (var p in members)
        {
            if (p.IsGm && !session.GmIsPlayer) continue;

            party.Add(new RemotePlayer
            {
                Hash = p.Hash,
                Name = p.Name,
                Connected = p.IsConnected,
                IsGm = p.IsGm,
                IsCoGm = p.CanEdit,
                Hp = p.Hp,
                HpMax = p.HpMax,
                Mp = p.Mp,
                MpMax = p.MpMax,
                Shield = p.Shield,
                Stats = RemoteSnapshotParts.MapStats(p.Stats),
                Counters = RemoteSnapshotParts.MapCounters(p.Counters),
                TempMod = p.TempModifier,
                TempTurns = p.TempModTurns,
                MoveLeft = p.MoveLeft,
                MoveMax = p.MoveMax,
                Group = session.IsLobbyMode ? p.GroupLabel : null,
                AwaitingRoll = session.IsAwaitingRoll(p.Hash),
            });
        }
        return party;
    }

    private IReadOnlyList<RemoteMarker> BuildMarkers()
    {
        var markers = new List<RemoteMarker>(Constants.WaymarkCount);
        for (var i = 0; i < Constants.WaymarkCount; i++)
        {
            var id = (WaymarkId)i;
            var m = session.CurrentMarkers[id];
            if (!m.HasData) continue;

            markers.Add(new RemoteMarker
            {
                Id = id.ToLabel(),
                // Vide pour une balise sans nom : la page affiche alors « Marqueur X » en grisé, comme la Scène.
                Name = m.Name ?? string.Empty,
                Attitude = RemoteSnapshotParts.AttitudeName(m.Attitude),
                Boss = m.IsBoss,
                Visible = m.IsVisible,
                Hp = m.Hp,
                HpMax = m.HpMax,
                Mp = m.Mp,
                MpMax = m.MpMax,
                Shield = m.Shield,
                Stats = RemoteSnapshotParts.MapStats(m.Stats),
                Counters = RemoteSnapshotParts.MapCounters(m.Counters),
                TempMod = m.TempModifier,
                TempTurns = m.TempModTurns,
            });
        }
        return markers;
    }

    // La Scène élague les PNJ disparus avant de dessiner (PruneDead) : ici on les saute sans toucher à la liste.
    private IReadOnlyList<RemoteNpc> BuildNpcs()
    {
        var instances = npcs.Instances;
        if (instances.Count == 0) return [];

        var list = new List<RemoteNpc>(instances.Count);
        foreach (var npc in instances)
        {
            if (!npc.IsAlive) continue;

            list.Add(new RemoteNpc
            {
                Id = RemoteSnapshotParts.NpcRef(npc.NetworkId),
                Name = npc.DisplayName,
                Creature = npc.IsCreature,
                Attitude = RemoteSnapshotParts.AttitudeName(npc.Attitude),
                Boss = npc.IsBoss,
                Hp = npc.Hp,
                HpMax = npc.HpMax,
                Shield = npc.Shield,
                Stats = RemoteSnapshotParts.MapStats(npc.Stats),
                Counters = RemoteSnapshotParts.MapCounters(npc.Counters),
                TempMod = npc.TempModifier,
            });
        }
        return list;
    }

    private RemoteTurn? BuildTurn()
    {
        if (session.CurrentTurnState is not { } state) return null;

        var entries = new RemoteTurnEntry[state.Entries.Count];
        for (var i = 0; i < entries.Length; i++)
        {
            var e = state.Entries[i];
            var (kind, reference) = TurnRef(e);
            var group = state.FindGroupFor(e);
            entries[i] = new RemoteTurnEntry
            {
                Name = e.Name,
                Kind = kind,
                Ref = reference,
                Initiative = e.Initiative,
                Roll = e.InitiativeRoll,
                Mod = e.InitiativeModifier,
                Stat = e.InitiativeStatName,
                HasActed = state.HasEntryActed(e),
                GroupId = group?.Id,
                Group = group?.Label,
            };
        }

        return new RemoteTurn
        {
            Active = state.IsActive,
            Encounter = session.EncounterSerial,
            Round = state.Round,
            ActiveIndex = session.ActiveTurnIndex,
            DiceMax = state.DiceMax,
            Entries = entries,
        };
    }

    // Même ordre de résolution que GmWindow.SelectTurnEntry : balise, puis PNJ, puis joueur.
    private static (string Kind, string? Ref) TurnRef(TurnEntry entry)
    {
        if (entry.WaymarkIndex is { } wi)
            return ("marker", wi >= 0 && wi < Constants.WaymarkCount ? ((WaymarkId)wi).ToLabel() : null);

        // Ramené au format « N » de RemoteNpc.Id, quel que soit celui de l'entrée.
        if (entry.NpcId is { } npcId)
            return ("npc", Guid.TryParse(npcId, out var id) ? RemoteSnapshotParts.NpcRef(id) : npcId);

        return ("player", entry.PlayerHash);
    }

    // Regroupement de GmWindow.DrawPendingRequests : une décision par sous-groupe, dans l'ordre d'arrivée.
    private IReadOnlyList<RemotePendingGroup> BuildPending()
    {
        var pending = session.PendingMembers;
        if (pending.Count == 0) return [];

        var groups = new List<(string? GroupId, List<RemotePendingMember> Members)>();
        foreach (var p in pending)
        {
            var index = p.GroupId == null ? -1 : groups.FindIndex(g => g.GroupId == p.GroupId);
            var member = new RemotePendingMember(p.Name, p.Hash);
            if (index >= 0) groups[index].Members.Add(member);
            else groups.Add((p.GroupId, [member]));
        }

        var result = new RemotePendingGroup[groups.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = new RemotePendingGroup(session.FindLobbyGroupLabel(groups[i].GroupId), groups[i].GroupId != null,
                groups[i].Members);
        return result;
    }

    // Même source que GmWindow.WeatherStatusText : nom appliqué, sinon la liste de la zone, sinon l'identifiant.
    private RemoteAmbiance BuildAmbiance()
    {
        string? weather = null;
        var weatherId = session.CurrentWeatherId;
        if (weatherId != 0)
        {
            weather = !string.IsNullOrEmpty(session.CurrentWeatherName)
                ? session.CurrentWeatherName
                : session.GetAvailableWeathers().GetValueOrDefault(weatherId) ?? weatherId.ToString();
        }

        return new RemoteAmbiance { Weather = weather, Time = RemoteSnapshotParts.EorzeaHour(session) };
    }
}
