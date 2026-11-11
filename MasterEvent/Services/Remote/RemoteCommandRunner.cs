using System;
using System.Collections.Generic;
using System.Linq;
using MasterEvent.Communication.Remote;

namespace MasterEvent.Services.Remote;

// Exécute une commande de la page, sur le thread du jeu. Les droits sont recalculés à chaque commande
// (RemoteCaps), puis SceneActions applique ceux du geste en jeu. Les règles propres à la page, plus
// étroites que le jeu, vivent ici et nulle part ailleurs.
public sealed class RemoteCommandRunner(SessionManager session, SceneActions actions)
{
    private const string LogPrefix = "[MasterEvent] Second écran : ";
    public const int AnnounceMaxLines = 4;
    // Durée minimale d'affichage d'une annonce : la suivante la remplacerait avant qu'on la lise.
    private const long AnnounceIntervalMs = 3000;
    // Chaque demande imprime une ligne chez tous et ouvre une fenêtre chez la cible.
    private const long RollRequestIntervalMs = 2000;

    private readonly Dictionary<string, long> requestedAt = new(StringComparer.Ordinal);
    private long? announcedAt;

    // now : Environment.TickCount64 de l'image en cours.
    public RemoteOutcome Run(RemoteCommand command, long now)
    {
        var length = command.Op == RemoteOps.Announce ? $", {command.Text?.Length ?? 0} caractères" : string.Empty;
        return RemoteCommandParts.Run(LogPrefix, command, () => Execute(command, now), length);
    }

    private RemoteOutcome Execute(RemoteCommand c, long now)
    {
        if (!RemoteCaps.Contains(RemoteCaps.For(session), c.Op))
            return RemoteOutcome.Fail(RemoteCaps.IsOfflineOnly(session, c.Op) ? RemoteErrors.Offline : RemoteErrors.Forbidden);
        // Les marqueurs survivent à un changement de salle : une page restée sur l'ancien lobby n'agit pas sur le nouveau.
        if (!string.Equals(c.Lobby, session.LobbyCode, StringComparison.Ordinal)) return RemoteOutcome.Fail(RemoteErrors.Stale);
        if (!session.HasGmAccess && (c.Kind != "marker" || c.Stat != null)) return RemoteOutcome.Fail(RemoteErrors.Forbidden);

        return c.Op switch
        {
            RemoteOps.TurnStart => From(actions.StartEncounter()),
            RemoteOps.TurnEnd => TryTurnTarget(c, out var target)
                ? From(actions.EndTurn(c.Encounter, c.Round, target))
                : RemoteOutcome.Fail(RemoteErrors.BadArgs),
            RemoteOps.TurnNextRound => From(actions.NextRound(c.Encounter, c.Round, force: false)),
            RemoteOps.TurnSetActed => TryTurnTarget(c, out var entry)
                ? From(actions.SetActed(c.Encounter, c.Round, entry, c.Value))
                : RemoteOutcome.Fail(RemoteErrors.BadArgs),
            RemoteOps.Vitals => Vitals(c),
            RemoteOps.Roll => TryKind(c.Kind, out var kind) && c.Ref is { } reference
                ? From(actions.Roll(new SceneTarget(kind, reference), c.Stat))
                : RemoteOutcome.Fail(RemoteErrors.BadArgs),
            RemoteOps.RollRequest => RollRequest(c, now),
            RemoteOps.Admit => From(actions.Admit(c.Hash ?? string.Empty)),
            RemoteOps.Deny => From(actions.DenyGroup(c.Hash ?? string.Empty, c.Hashes)),
            RemoteOps.Announce => Announce(c, now),
            _ => RemoteOutcome.Fail(RemoteErrors.BadArgs),
        };
    }

    private RemoteOutcome Vitals(RemoteCommand c)
    {
        if (!TryKind(c.Kind, out var kind) || c.Ref is not { } reference || !TryField(c.Field, out var field))
            return RemoteOutcome.Fail(RemoteErrors.BadArgs);
        // La page, comme la colonne Joueurs, ne montre pas le MJ qui ne joue pas.
        if (kind == SceneTargetKind.Player && !session.GmIsPlayer
            && session.PartyMembers.FirstOrDefault(p => p.Hash == reference) is { IsGm: true })
            return RemoteOutcome.Fail(RemoteErrors.Gone);

        var target = new SceneTarget(kind, reference);
        if (c.Delta is { } delta) return From(actions.AdjustVital(target, field, delta));
        return c.Set is { } value ? From(actions.SetVital(target, field, value)) : RemoteOutcome.Fail(RemoteErrors.BadArgs);
    }

    private RemoteOutcome RollRequest(RemoteCommand c, long now)
    {
        var hash = c.Player ?? string.Empty;
        if (session.PartyMembers.FirstOrDefault(p => p.Hash == hash) is not { } player) return RemoteOutcome.Fail(RemoteErrors.Gone);
        // En lobby, IsGm désigne le chef du groupe en jeu, que la passation ne déplace pas : le MJ de la scène se
        // reconnaît aussi à son empreinte. Son propre client ouvrirait la demande.
        if (player.IsGm || actions.IsSceneGmOrSelf(hash)) return RemoteOutcome.Fail(RemoteErrors.BadArgs);
        // En jeu, une demande à un absent se perd et laisse l'attente affichée.
        if (!player.IsConnected) return RemoteOutcome.Fail(RemoteErrors.TargetOffline);
        if (requestedAt.TryGetValue(hash, out var at) && now - at < RollRequestIntervalMs)
            return RemoteOutcome.Fail(RemoteErrors.TooSoon);

        var result = actions.RequestRoll(hash, c.Stat, c.Threshold);
        if (result.Ok)
        {
            foreach (var old in requestedAt.Where(e => now - e.Value >= RollRequestIntervalMs).Select(e => e.Key).ToList())
                requestedAt.Remove(old);
            requestedAt[hash] = now;
        }
        return From(result);
    }

    // Les bornes du champ en jeu, qui ne laisse passer ni plus de 180 caractères ni caractère de contrôle,
    // plus une limite de lignes : l'annonce s'affiche en bandeau chez tous.
    private RemoteOutcome Announce(RemoteCommand c, long now)
    {
        var text = (c.Text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (text.Length == 0) return RemoteOutcome.Fail(RemoteErrors.Empty);
        if (text.Length > SceneActions.AnnounceMaxChars) return RemoteOutcome.Fail(RemoteErrors.TooLong);
        if (text.Any(ch => ch != '\n' && char.IsControl(ch))) return RemoteOutcome.Fail(RemoteErrors.BadArgs);
        if (text.Count(ch => ch == '\n') >= AnnounceMaxLines) return RemoteOutcome.Fail(RemoteErrors.TooManyLines);
        if (announcedAt is { } at && now - at < AnnounceIntervalMs) return RemoteOutcome.Fail(RemoteErrors.TooSoon);

        var result = actions.Announce(text);
        if (result.Ok) announcedAt = now;
        return From(result);
    }

    private static bool TryTurnTarget(RemoteCommand c, out TurnTarget target)
    {
        target = default;
        if (c.Group is { } group)
        {
            target = new TurnTarget(SceneTargetKind.Player, null, group);
            return true;
        }
        if (!TryKind(c.Kind, out var kind) || c.Ref is not { } reference) return false;
        target = new TurnTarget(kind, reference);
        return true;
    }

    private static bool TryKind(string? kind, out SceneTargetKind value)
    {
        value = kind switch
        {
            "player" => SceneTargetKind.Player,
            "marker" => SceneTargetKind.Marker,
            "npc" => SceneTargetKind.Npc,
            _ => (SceneTargetKind)(-1),
        };
        return Enum.IsDefined(value);
    }

    private static bool TryField(string? field, out VitalField value)
    {
        value = field switch
        {
            "hp" => VitalField.Hp,
            "mp" => VitalField.Mp,
            "shield" => VitalField.Shield,
            _ => (VitalField)(-1),
        };
        return Enum.IsDefined(value);
    }

    private static RemoteOutcome From(SceneResult result) => result.Ok
        ? new RemoteOutcome(null, result.Changed)
        : RemoteOutcome.Fail(Code(result.Error));

    private static string Code(SceneError error) => error switch
    {
        SceneError.Forbidden => RemoteErrors.Forbidden,
        SceneError.Offline => RemoteErrors.Offline,
        SceneError.NoCombat => RemoteErrors.NoCombat,
        SceneError.Stale => RemoteErrors.Stale,
        SceneError.Gone => RemoteErrors.Gone,
        SceneError.Grouped => RemoteErrors.Grouped,
        SceneError.Hidden => RemoteErrors.Hidden,
        SceneError.NoField => RemoteErrors.NoField,
        SceneError.Unnamed => RemoteErrors.Unnamed,
        SceneError.UnknownStat => RemoteErrors.UnknownStat,
        SceneError.Empty => RemoteErrors.Empty,
        SceneError.TooLong => RemoteErrors.TooLong,
        _ => RemoteErrors.BadArgs,
    };
}
