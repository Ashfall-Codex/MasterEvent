using System;
using System.Collections.Generic;
using System.Linq;
using MasterEvent.Communication;
using MasterEvent.Communication.Remote;
using MasterEvent.Models;

namespace MasterEvent.Services.Remote;

// États de la vue joueur, dans l'ordre de PlayerWindow.ResolveState : le premier qui s'applique l'emporte.
public enum PlayerRemoteView
{
    GmPreview,
    Instance,
    VersionRejected,
    JoinRejected,
    AwaitingApproval,
    Offline,
    Reconnecting,
    OutOfSession,
    Joining,
    Handover,
    GmAway,
    InSession,
}

// Ce que la page joueur peut commander maintenant : les gestes de la vue en jeu, avec ses gardes. La même
// fonction remplit state.caps et filtre chaque commande, sans cache. Thread du jeu.
public static class PlayerRemoteCaps
{
    public const string PartyRoom = "party";

    public static IReadOnlyList<string> For(SessionManager session, bool inParty, bool inInstance)
        => For(session, ViewOf(session, inParty, inInstance), inInstance);

    internal static IReadOnlyList<string> For(SessionManager session, PlayerRemoteView view, bool inInstance)
    {
        if (!HasAccess(session, view)) return [];

        // Les tuiles de Ma fiche lancent le dé dans tous les états, comme en jeu.
        var caps = new List<string>(4) { RemoteOps.SelfRoll };
        var primary = session.ResolvePlayerPrimary(inInstance);
        var request = session.PendingRollRequest != null && IsInRoom(view);
        if (request && primary.Kind == PlayerPrimaryKind.Roll) caps.Add(RemoteOps.SelfAnswer);
        if (primary is { CanEndTurn: true, Hold: EndTurnHold.None }) caps.Add(RemoteOps.SelfEndTurn);
        if (request) caps.Add(RemoteOps.SelfDismiss);
        return caps;
    }

    // Copie de PlayerWindow.ResolveState, à garder alignée sur elle.
    public static PlayerRemoteView ViewOf(SessionManager session, bool inParty, bool inInstance)
    {
        var grouped = inParty || session.IsLobbyMode;
        // Seul, UpdateRole fait de chacun le MJ : sans groupe ni lobby, ce n'est pas un aperçu.
        if (session.IsGm && !session.GmIsPlayer && grouped) return PlayerRemoteView.GmPreview;
        if (inInstance) return PlayerRemoteView.Instance;
        if (session.IsVersionRejected) return PlayerRemoteView.VersionRejected;
        if (session.JoinRejection != JoinRejection.None) return PlayerRemoteView.JoinRejected;
        if (session.IsAwaitingApproval) return PlayerRemoteView.AwaitingApproval;

        switch (session.LinkState)
        {
            case RelayLinkState.Offline:
                return PlayerRemoteView.Offline;
            case RelayLinkState.Reconnecting:
                return PlayerRemoteView.Reconnecting;
            case RelayLinkState.ServerOnly:
                return grouped ? PlayerRemoteView.Joining : PlayerRemoteView.OutOfSession;
        }

        if (session.HandoverPending) return PlayerRemoteView.Handover;
        if (!session.CanEdit && session.GmPresence is not { Connected: true }) return PlayerRemoteView.GmAway;
        return PlayerRemoteView.InSession;
    }

    // L'aperçu du MJ n'a ni fiche ni geste ; sans joueur local, RollDiceForPlayer ne lancerait rien.
    internal static bool HasAccess(SessionManager session, PlayerRemoteView view)
        => view != PlayerRemoteView.GmPreview && LocalPlayer(session) != null;

    // Ce que la page renvoie dans cmd.lobby : une commande partie d'un autre lobby ne s'applique pas à celui-ci.
    internal static string? Room(SessionManager session, PlayerRemoteView view)
        => !HasAccess(session, view) ? null : session.IsLobbyMode ? session.LobbyCode : PartyRoom;

    internal static bool IsInRoom(PlayerRemoteView view)
        => view is PlayerRemoteView.InSession or PlayerRemoteView.Handover or PlayerRemoteView.GmAway;

    internal static bool IsSessionLike(PlayerRemoteView view)
        => IsInRoom(view) || view == PlayerRemoteView.Reconnecting;

    internal static PlayerData? LocalPlayer(SessionManager session)
        => session.PartyMembers.FirstOrDefault(p => p.Hash == session.LocalPlayerHash);
}

// Exécute une commande de la page joueur, sur le thread du jeu, par les mêmes méthodes que la vue en jeu.
public sealed class PlayerCommandRunner(SessionManager session, Func<bool> inParty, Func<bool> inInstance)
{
    private const string LogPrefix = "[MasterEvent] Second écran joueur : ";
    // L'inertie de RollOnce : le second clic d'un double clic ne relance rien et ne prend pas la demande.
    private const long RollIntervalMs = 600;

    private long? rolledAt;

    // now : Environment.TickCount64 de l'image en cours. Une exception devient « internal », avec une ligne au journal.
    public RemoteOutcome Run(RemoteCommand command, long now)
    {
        RemoteOutcome outcome;
        try
        {
            outcome = Execute(command, now);
        }
        catch (Exception ex)
        {
            // Ni message ni pile : ils peuvent citer ce que la page a envoyé.
            var site = ex.TargetSite is { } method ? $"{method.DeclaringType?.Name}.{method.Name}" : "?";
            Plugin.Log.Error($"{LogPrefix}commande {command.Op} interrompue ({ex.GetType().Name} dans {site})");
            return RemoteOutcome.Fail(RemoteErrors.Internal);
        }

        var result = outcome.Error ?? (outcome.Changed ? "fait" : "sans effet");
        Plugin.Log.Debug($"{LogPrefix}commande {command.Op} : {result}, état vu {command.Seen}");
        return outcome;
    }

    private RemoteOutcome Execute(RemoteCommand c, long now)
    {
        var instance = inInstance();
        var view = PlayerRemoteCaps.ViewOf(session, inParty(), instance);
        if (!RemoteCaps.Contains(PlayerRemoteCaps.For(session, view, instance), c.Op))
            return RemoteOutcome.Fail(Refusal(c.Op, view, instance));
        if (!string.Equals(c.Lobby, PlayerRemoteCaps.Room(session, view), StringComparison.Ordinal))
            return RemoteOutcome.Fail(RemoteErrors.Stale);

        switch (c.Op)
        {
            case RemoteOps.SelfRoll:
                return Roll(c.Stat, now);
            case RemoteOps.SelfAnswer:
                if (TooSoon(now)) return RemoteOutcome.Fail(RemoteErrors.TooSoon);
                rolledAt = now;
                session.AnswerRollRequest();
                return Done;
            case RemoteOps.SelfEndTurn:
                session.RequestEndOwnTurn();
                return Done;
            case RemoteOps.SelfDismiss:
                session.DismissRollRequest();
                return Done;
            default:
                return RemoteOutcome.Fail(RemoteErrors.Forbidden);
        }
    }

    private static RemoteOutcome Done => new(null, true);

    // Motif d'une op absente des caps, pour que la page dise pourquoi rien ne s'est passé.
    private string Refusal(string op, PlayerRemoteView view, bool instance)
    {
        if (!RemoteCaps.Contains(RemoteOps.Self, op) || !PlayerRemoteCaps.HasAccess(session, view))
            return RemoteErrors.Forbidden;
        if ((op is RemoteOps.SelfEndTurn or RemoteOps.SelfAnswer) && session.LinkState != RelayLinkState.InSession)
            return RemoteErrors.Offline;
        if (op == RemoteOps.SelfEndTurn && session.CurrentTurnState is not { IsActive: true, Entries.Count: > 0 })
            return RemoteErrors.NoCombat;
        if ((op is RemoteOps.SelfAnswer or RemoteOps.SelfDismiss) && session.PendingRollRequest == null)
            return RemoteErrors.Gone;
        if (op == RemoteOps.SelfEndTurn && session.ResolvePlayerPrimary(instance).Hold == EndTurnHold.Sent)
            return RemoteErrors.TooSoon;
        return RemoteErrors.Forbidden;
    }

    // La tuile de la stat demandée prend la demande, comme en jeu (RollDiceForPlayer).
    private RemoteOutcome Roll(string? stat, long now)
    {
        if (stat != null && PlayerRemoteCaps.LocalPlayer(session)?.Stats?.Any(s => s.Id == stat) != true)
            return RemoteOutcome.Fail(RemoteErrors.UnknownStat);
        if (TooSoon(now)) return RemoteOutcome.Fail(RemoteErrors.TooSoon);

        rolledAt = now;
        session.RollDiceForPlayer(session.LocalPlayerHash, stat);
        return Done;
    }

    private bool TooSoon(long now) => rolledAt is { } at && now - at < RollIntervalMs;
}
