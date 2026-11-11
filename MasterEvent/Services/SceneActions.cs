using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using MasterEvent.Models;
using MasterEvent.Services.Npc;

namespace MasterEvent.Services;

public enum SceneTargetKind { Player, Marker, Npc }

public enum VitalField { Hp, Mp, Shield }

// Ref : empreinte du joueur, libellé du marqueur (WaymarkId.ToLabel) ou NetworkId du PNJ.
public readonly record struct SceneTarget(SceneTargetKind Kind, string Ref)
{
    public static SceneTarget Player(string hash) => new(SceneTargetKind.Player, hash);
    public static SceneTarget Marker(WaymarkId id) => new(SceneTargetKind.Marker, id.ToLabel());
    public static SceneTarget Npc(Guid networkId) => new(SceneTargetKind.Npc, networkId.ToString("N"));
}

// Entrée de l'ordre des tours. Group (TurnGroup.Id) prime sur Kind et Ref et vise tout le groupe.
public readonly record struct TurnTarget(SceneTargetKind Kind, string? Ref, string? Group = null);

public enum SceneError
{
    None, Forbidden, Offline, NoCombat, Stale, Gone, Grouped, Hidden, NoField,
    Unnamed, UnknownStat, Empty, TooLong, BadArgs,
}

public readonly record struct SceneResult(SceneError Error, bool Changed)
{
    public bool Ok => Error == SceneError.None;
    public static SceneResult Done => new(SceneError.None, true);
    public static SceneResult Unchanged => new(SceneError.None, false);
    public static SceneResult Fail(SceneError error) => new(error, false);
}

// Les gestes de la Scène, communs à l'onglet en jeu et au second écran. Thread du jeu uniquement :
// SessionManager se lit sans verrou et NpcInstance.IsAlive lit la mémoire du jeu. Chaque méthode
// garde les droits, les bornes et les diffusions du geste en jeu ; la politique propre à la page
// s'ajoute par-dessus, jamais ici.
public sealed class SceneActions(SessionManager session, NpcManager? npcs)
{
    public const int AnnounceMaxChars = 180;

    // En lobby, PlayerData.IsGm désigne le chef du groupe en jeu, que la passation ne déplace pas.
    // Contrôle propre à la page : en jeu, ni le menu contextuel ni l'inspecteur ne le font.
    public bool IsSceneGmOrSelf(string? hash)
        => !string.IsNullOrEmpty(hash) && (hash == session.LocalPlayerHash || hash == session.SceneLeaderHash);

    public SceneResult StartEncounter()
    {
        if (!session.HasGmAccess) return SceneResult.Fail(SceneError.Forbidden);
        // SessionManager.StartEncounter remplacerait le combat en cours : nouveaux jets, round 1, coches perdues.
        if (session.CurrentTurnState is { IsActive: true }) return SceneResult.Unchanged;

        session.StartEncounter();
        return SceneResult.Done;
    }

    public SceneResult EndTurnAt(int index)
    {
        if (!TryCombat(out var state, out var error)) return SceneResult.Fail(error);
        if (index < 0 || index >= state.Entries.Count) return SceneResult.Fail(SceneError.Gone);
        return EndTurnCore(state, index);
    }

    public SceneResult EndTurn(int encounter, int round, TurnTarget expected)
    {
        if (!TryCombat(out var state, out var error)) return SceneResult.Fail(error);
        if (encounter != session.EncounterSerial || round != state.Round) return SceneResult.Fail(SceneError.Stale);
        if (FindEntry(state, expected) is not { } index) return SceneResult.Fail(SceneError.Gone);
        return EndTurnCore(state, index);
    }

    // ToggleHasActed inverse la valeur courante : un second envoi, ou le joueur qui a fini seul entre-temps,
    // rouvrirait le tour. Seul l'acteur attendu est coché, et seulement s'il n'a pas encore joué.
    private SceneResult EndTurnCore(TurnState state, int index)
    {
        if (state.HasEntryActed(state.Entries[index])) return SceneResult.Unchanged;

        var active = session.ActiveTurnIndex;
        if (active < 0 || !SameBlock(state, active, index)) return SceneResult.Fail(SceneError.Stale);

        session.ToggleHasActed(active);
        return SceneResult.Done;
    }

    // force : la manche suivante du menu Plus, qui n'attend pas ceux qui n'ont pas joué.
    public SceneResult NextRoundNow(bool force)
    {
        if (!TryCombat(out _, out var error)) return SceneResult.Fail(error);
        return NextRoundCore(force);
    }

    public SceneResult NextRound(int encounter, int round, bool force)
    {
        if (!TryCombat(out var state, out var error)) return SceneResult.Fail(error);
        if (encounter != session.EncounterSerial) return SceneResult.Fail(SceneError.Stale);
        // Déjà passé : un double envoi sauterait un round et décompterait deux fois les bonus temporaires.
        if (state.Round > round) return SceneResult.Unchanged;
        if (state.Round < round) return SceneResult.Fail(SceneError.Stale);
        return NextRoundCore(force);
    }

    private SceneResult NextRoundCore(bool force)
    {
        if (!force && session.ActiveTurnIndex >= 0) return SceneResult.Fail(SceneError.Stale);

        session.NextRound();
        return SceneResult.Done;
    }

    // L'index d'une entrée groupée vise tout son groupe ; en jeu, c'est celui de son premier membre.
    public SceneResult SetActedAt(int index, bool value)
    {
        if (!TryCombat(out var state, out var error)) return SceneResult.Fail(error);
        if (index < 0 || index >= state.Entries.Count) return SceneResult.Fail(SceneError.Gone);
        return SetActedCore(state, index, value);
    }

    public SceneResult SetActed(int encounter, int round, TurnTarget target, bool value)
    {
        if (!TryCombat(out var state, out var error)) return SceneResult.Fail(error);
        if (encounter != session.EncounterSerial || round != state.Round) return SceneResult.Fail(SceneError.Stale);
        if (FindEntry(state, target) is not { } index) return SceneResult.Fail(SceneError.Gone);
        // En jeu, une entrée groupée n'a pas de case : seul l'en-tête de son groupe la coche.
        if (target.Group == null && state.FindGroupFor(state.Entries[index]) != null)
            return SceneResult.Fail(SceneError.Grouped);
        return SetActedCore(state, index, value);
    }

    private SceneResult SetActedCore(TurnState state, int index, bool value)
    {
        if (state.HasEntryActed(state.Entries[index]) == value) return SceneResult.Unchanged;

        session.ToggleHasActed(index);
        return SceneResult.Done;
    }

    public SceneResult AdjustVital(SceneTarget target, VitalField field, int delta)
        => ApplyVital(target, field, current => (long)current + delta);

    public SceneResult SetVital(SceneTarget target, VitalField field, int value)
        => ApplyVital(target, field, _ => value);

    // Bornes de l'onglet Scène et des inspecteurs : 0, puis 100 en mode pourcentage ou le maximum de la cible.
    // Comme les lignes de la Scène, l'écriture et la diffusion ont lieu même quand la valeur ne bouge pas.
    private SceneResult ApplyVital(SceneTarget target, VitalField field, Func<int, long> wanted)
    {
        if (!Enum.IsDefined(field) || string.IsNullOrEmpty(target.Ref)) return SceneResult.Fail(SceneError.BadArgs);

        switch (target.Kind)
        {
            case SceneTargetKind.Player:
            {
                if (!session.HasGmAccess) return SceneResult.Fail(SceneError.Forbidden);
                if (IsHidden(field)) return SceneResult.Fail(SceneError.Hidden);
                var player = session.PartyMembers.FirstOrDefault(p => p.Hash == target.Ref);
                if (player == null) return SceneResult.Fail(SceneError.Gone);

                // Les setters bornent encore au maximum du joueur : le plafond s'aligne sur eux pour que
                // la réponse dise vrai, même en mode pourcentage avec un maximum sous 100.
                var before = ReadVital(player, field);
                var max = field == VitalField.Mp ? player.MpMax : player.HpMax;
                var value = Bound(wanted(before), Math.Min(Cap(field, player.HpMax, player.MpMax), max));
                switch (field)
                {
                    case VitalField.Hp: session.SetPlayerHp(player.Hash, value); break;
                    case VitalField.Mp: session.SetPlayerMp(player.Hash, value); break;
                    default: session.SetPlayerShield(player.Hash, value); break;
                }
                return ReadVital(player, field) == before ? SceneResult.Unchanged : SceneResult.Done;
            }

            case SceneTargetKind.Marker:
            {
                if (!session.CanEdit) return SceneResult.Fail(SceneError.Forbidden);
                if (!WaymarkIdExtensions.TryParseLabel(target.Ref, out var id)) return SceneResult.Fail(SceneError.BadArgs);
                if (IsHidden(field)) return SceneResult.Fail(SceneError.Hidden);
                if (!IsMarkerInScene(id)) return SceneResult.Fail(SceneError.Gone);

                // Sans diffusion immédiate : CheckAutoBroadcast envoie les marqueurs au plus une seconde après.
                var marker = session.CurrentMarkers[id];
                var before = ReadVital(marker, field);
                var value = Bound(wanted(before), Cap(field, marker.HpMax, marker.MpMax));
                switch (field)
                {
                    case VitalField.Hp: marker.Hp = value; break;
                    case VitalField.Mp: marker.Mp = value; break;
                    default: marker.Shield = value; break;
                }
                return value == before ? SceneResult.Unchanged : SceneResult.Done;
            }

            case SceneTargetKind.Npc:
            {
                if (!session.HasGmAccess) return SceneResult.Fail(SceneError.Forbidden);
                if (!Guid.TryParse(target.Ref, out var networkId)) return SceneResult.Fail(SceneError.BadArgs);
                if (IsHidden(field)) return SceneResult.Fail(SceneError.Hidden);
                if (FindNpc(networkId) is not { } npc) return SceneResult.Fail(SceneError.Gone);
                if (field == VitalField.Mp) return SceneResult.Fail(SceneError.NoField);

                var before = field == VitalField.Hp ? npc.Hp : npc.Shield;
                var value = Bound(wanted(before), Cap(field, npc.HpMax, 0));
                if (field == VitalField.Hp) npc.Hp = value;
                else npc.Shield = value;
                npcs!.NotifyChanged();
                return value == before ? SceneResult.Unchanged : SceneResult.Done;
            }

            default:
                return SceneResult.Fail(SceneError.BadArgs);
        }
    }

    private bool IsHidden(VitalField field)
        => (field == VitalField.Mp && !session.ShowMpBar) || (field == VitalField.Shield && !session.ShowShield);

    private int Cap(VitalField field, int hpMax, int mpMax)
    {
        var mp = field == VitalField.Mp;
        var mode = mp ? session.MpMode : session.HpMode;
        if (mode == HpMode.Percentage) return 100;
        return mp ? mpMax : hpMax;
    }

    private static int Bound(long value, int cap) => (int)Math.Clamp(value, 0L, Math.Max(cap, 0));

    private static int ReadVital(PlayerData player, VitalField field) => field switch
    {
        VitalField.Hp => player.Hp,
        VitalField.Mp => player.Mp,
        _ => player.Shield,
    };

    private static int ReadVital(MarkerData marker, VitalField field) => field switch
    {
        VitalField.Hp => marker.Hp,
        VitalField.Mp => marker.Mp,
        _ => marker.Shield,
    };

    public SceneResult Roll(SceneTarget target, string? statId)
    {
        if (string.IsNullOrEmpty(target.Ref)) return SceneResult.Fail(SceneError.BadArgs);

        IVitalEntity entity;
        switch (target.Kind)
        {
            case SceneTargetKind.Marker:
                if (!session.CanEdit) return SceneResult.Fail(SceneError.Forbidden);
                if (!WaymarkIdExtensions.TryParseLabel(target.Ref, out var id)) return SceneResult.Fail(SceneError.BadArgs);
                if (!IsMarkerInScene(id)) return SceneResult.Fail(SceneError.Gone);
                entity = session.CurrentMarkers[id];
                break;

            case SceneTargetKind.Npc:
                if (!session.HasGmAccess) return SceneResult.Fail(SceneError.Forbidden);
                if (!Guid.TryParse(target.Ref, out var networkId)) return SceneResult.Fail(SceneError.BadArgs);
                if (FindNpc(networkId) is not { } npc) return SceneResult.Fail(SceneError.Gone);
                entity = npc;
                break;

            default:
                return SceneResult.Fail(SceneError.BadArgs);
        }

        // ExecuteRoll sortirait sans rien dire ; une stat introuvable y donnerait un jet sans stat.
        if (string.IsNullOrWhiteSpace(entity.EntityName)) return SceneResult.Fail(SceneError.Unnamed);
        if (statId != null && entity.Stats?.Any(s => s.Id == statId) != true)
            return SceneResult.Fail(SceneError.UnknownStat);

        session.RollDiceFor(entity, statId);
        return SceneResult.Done;
    }

    public SceneResult RequestRoll(string playerHash, string? statId, int threshold)
    {
        if (!session.CanEdit) return SceneResult.Fail(SceneError.Forbidden);
        if (!session.IsRelayConnected) return SceneResult.Fail(SceneError.Offline);
        if (session.PartyMembers.FirstOrDefault(p => p.Hash == playerHash) is not { } player)
            return SceneResult.Fail(SceneError.Gone);

        StatValue? stat = null;
        if (statId != null && (stat = player.Stats?.FirstOrDefault(s => s.Id == statId)) == null)
            return SceneResult.Fail(SceneError.UnknownStat);

        session.RequestRoll(player, stat, threshold);
        return SceneResult.Done;
    }

    public SceneResult Admit(string memberHash)
    {
        if (!CanAnswerQueue(out var error)) return SceneResult.Fail(error);
        // AdmitPending enverrait quand même, et le relais ignorerait la trame sans rien dire.
        if (session.PendingMembers.All(p => p.Hash != memberHash)) return SceneResult.Fail(SceneError.Gone);

        session.AdmitPending(memberHash);
        return SceneResult.Done;
    }

    // Refuser le seul représentant laisserait ses coéquipiers dans la file, et la demande paraîtrait revenir
    // aussitôt sous un autre nom. confirmed : les membres que la page a montrés en demandant confirmation ;
    // si le sous-groupe compte désormais quelqu'un d'autre, personne n'est refusé.
    public SceneResult DenyGroup(string memberHash, IReadOnlyCollection<string>? confirmed = null)
    {
        if (!CanAnswerQueue(out var error)) return SceneResult.Fail(error);
        var representative = session.PendingMembers.FirstOrDefault(p => p.Hash == memberHash);
        if (representative == null) return SceneResult.Fail(SceneError.Gone);

        var group = session.PendingMembers
            .Where(m => m.Hash == memberHash
                        || (representative.GroupId != null && m.GroupId == representative.GroupId))
            .ToList();
        if (confirmed != null && group.Any(m => !confirmed.Contains(m.Hash))) return SceneResult.Fail(SceneError.Stale);

        foreach (var member in group)
            session.DenyPending(member.Hash);
        return SceneResult.Done;
    }

    private bool CanAnswerQueue(out SceneError error)
    {
        if (!session.IsRelayConnected) error = SceneError.Offline;
        else if (!session.IsGm && !session.IsPromoted) error = SceneError.Forbidden;
        else error = SceneError.None;
        return error == SceneError.None;
    }

    // Aucun droit vérifié ici : la fenêtre d'annonce en jeu reste ouverte d'une image à l'autre et envoie
    // sans revérifier le rôle. La page passe par caps.
    public SceneResult Announce(string? text)
    {
        var message = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (message.Length == 0) return SceneResult.Fail(SceneError.Empty);
        if (message.Length > AnnounceMaxChars) return SceneResult.Fail(SceneError.TooLong);

        session.ShowGmAnnouncement(message);
        return SceneResult.Done;
    }

    private bool TryCombat([NotNullWhen(true)] out TurnState? state, out SceneError error)
    {
        state = null;
        if (!session.HasGmAccess)
        {
            error = SceneError.Forbidden;
            return false;
        }

        if (session.CurrentTurnState is not { IsActive: true } current)
        {
            error = SceneError.NoCombat;
            return false;
        }

        state = current;
        error = SceneError.None;
        return true;
    }

    // Une ligne de tour garde son marqueur même vidé : ResolveTurnEntity ne teste pas HasData.
    private bool IsMarkerInScene(WaymarkId id)
        => session.CurrentMarkers[id].HasData
           || (session.CurrentTurnState is { IsActive: true } state && state.Entries.Any(e => e.WaymarkIndex == (int)id));

    private NpcInstance? FindNpc(Guid networkId)
        => npcs?.FindByNetworkId(networkId) is { IsAlive: true } npc ? npc : null;

    private static int? FindEntry(TurnState state, TurnTarget target)
    {
        if (target.Group is { } groupId)
        {
            if (state.Groups.All(g => g.Id != groupId)) return null;
            var first = state.Entries.FindIndex(e => e.GroupId == groupId);
            return first >= 0 ? first : null;
        }

        if (string.IsNullOrEmpty(target.Ref)) return null;
        var index = state.Entries.FindIndex(e => Matches(e, target.Kind, target.Ref));
        return index >= 0 ? index : null;
    }

    // Même résolution que GmSnapshotBuilder.TurnRef : balise, puis PNJ, puis joueur.
    private static bool Matches(TurnEntry entry, SceneTargetKind kind, string reference)
    {
        if (entry.WaymarkIndex is { } wi)
            return kind == SceneTargetKind.Marker && wi >= 0 && wi < Constants.WaymarkCount
                   && ((WaymarkId)wi).ToLabel() == reference;
        if (entry.NpcId is { } npcId)
            return kind == SceneTargetKind.Npc && NpcKey(npcId) == NpcKey(reference);
        return kind == SceneTargetKind.Player && entry.PlayerHash == reference;
    }

    private static string NpcKey(string id) => Guid.TryParse(id, out var guid) ? guid.ToString("N") : id;

    // Les membres d'un groupe restent contigus et partagent leur coche.
    private static bool SameBlock(TurnState state, int a, int b)
        => a == b || (state.FindGroupFor(state.Entries[a]) is { } group && state.Entries[b].GroupId == group.Id);
}
