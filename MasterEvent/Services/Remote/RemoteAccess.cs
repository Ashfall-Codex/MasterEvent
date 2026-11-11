using System;
using System.Buffers;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;

namespace MasterEvent.Services.Remote;

// Ticket et jetons de session du second écran. Seule leur empreinte SHA-256 reste en mémoire.
// Le thread du jeu émet et révoque, le thread réseau vérifie : tout passe par le même verrou.
internal sealed class RemoteAccess
{
    public const int MaxSessions = 3;
    public const int RememberedCommands = 64;
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(120);
    private const int SecretBytes = 32;

    private readonly Lock gate = new();
    private readonly List<Session> sessions = [];
    private readonly long ticketLifetimeMs;
    private byte[]? ticketHash;
    private long ticketExpiresAt;

    public RemoteAccess() : this(TicketLifetime)
    {
    }

    // Le banc d'essai raccourcit la durée du ticket ; la page n'a aucun moyen d'y toucher.
    internal RemoteAccess(TimeSpan ticketLifetime) => ticketLifetimeMs = (long)ticketLifetime.TotalMilliseconds;

    public enum Verdict
    {
        Refused,
        Accepted,
        TooManySessions,
    }

    public enum CommandVerdict
    {
        Queued,
        Duplicate,
        Full,
        Revoked,
    }

    // Token n'est rempli que pour une session neuve : c'est le seul moment où il quitte cette classe.
    public readonly record struct Grant(Verdict Verdict, Session? Session = null, string? Token = null);

    public sealed class Session
    {
        // Lus et écrits sous le verrou de RemoteAccess.
        private readonly HashSet<string> seenIds = new(StringComparer.Ordinal);
        private readonly Queue<string> seenOrder = new();

        internal Session(byte[] hash, long now)
        {
            Hash = hash;
            Connections = 1;
            LastActive = now;
        }

        internal byte[] Hash { get; }
        internal int Connections { get; set; }
        internal long LastActive { get; set; }

        internal bool HasSeen(string id) => seenIds.Contains(id);

        internal void Remember(string id)
        {
            seenIds.Add(id);
            seenOrder.Enqueue(id);
            if (seenOrder.Count > RememberedCommands) seenIds.Remove(seenOrder.Dequeue());
        }
    }

    // Sessions dont au moins une page est connectée : deux onglets sur le même jeton comptent pour une.
    public int ConnectedCount
    {
        get
        {
            lock (gate)
            {
                var count = 0;
                foreach (var session in sessions)
                {
                    if (session.Connections > 0) count++;
                }
                return count;
            }
        }
    }

    // Remplace le ticket précédent : un seul lien vivant à la fois.
    public string IssueTicket()
    {
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        var hash = SHA256.HashData(secret);
        lock (gate)
        {
            ticketHash = hash;
            ticketExpiresAt = Environment.TickCount64 + ticketLifetimeMs;
        }
        return Convert.ToHexStringLower(secret);
    }

    // Un ticket reconnu est consommé, même quand la session est ensuite refusée.
    public Grant RedeemTicket(string ticket)
    {
        if (!TryHash(ticket, out var hash)) return default;
        lock (gate)
        {
            if (ticketHash is null || !CryptographicOperations.FixedTimeEquals(hash, ticketHash)) return default;
            ticketHash = null;
            var now = Environment.TickCount64;
            if (now > ticketExpiresAt) return default;

            if (sessions.Count >= MaxSessions)
            {
                // La session d'une page fermée cède sa place ; trois pages ouvertes, non.
                var idle = OldestIdle();
                if (idle is null) return new Grant(Verdict.TooManySessions);
                sessions.Remove(idle);
            }

            var secret = RandomNumberGenerator.GetBytes(SecretBytes);
            var session = new Session(SHA256.HashData(secret), now);
            sessions.Add(session);
            return new Grant(Verdict.Accepted, session, Convert.ToHexStringLower(secret));
        }
    }

    // Plusieurs pages peuvent partager un jeton : un onglet dupliqué recopie le sessionStorage.
    public Grant ResumeSession(string token)
    {
        if (!TryHash(token, out var hash)) return default;
        lock (gate)
        {
            Session? found = null;
            foreach (var session in sessions)
            {
                if (CryptographicOperations.FixedTimeEquals(hash, session.Hash)) found = session;
            }
            if (found is null) return default;

            found.Connections++;
            found.LastActive = Environment.TickCount64;
            return new Grant(Verdict.Accepted, found);
        }
    }

    // Une session révoquée entre-temps n'est plus dans la liste : la libérer ne change rien.
    public void Release(Session session)
    {
        lock (gate)
        {
            if (session.Connections > 0) session.Connections--;
            session.LastActive = Environment.TickCount64;
        }
    }

    // Les ids sont ceux de la session, onglets confondus : un onglet dupliqué recopie le jeton. Un id n'est
    // retenu qu'une fois la commande en file, sous le même verrou : refusée pour « busy », elle peut repartir telle quelle.
    public CommandVerdict TryQueueCommand<T>(Session session, string id, ChannelWriter<T> inbox, T item)
    {
        lock (gate)
        {
            if (!sessions.Contains(session)) return CommandVerdict.Revoked;
            if (session.HasSeen(id)) return CommandVerdict.Duplicate;
            if (!inbox.TryWrite(item)) return CommandVerdict.Full;
            session.Remember(id);
            return CommandVerdict.Queued;
        }
    }

    public void RevokeAll()
    {
        lock (gate)
        {
            ticketHash = null;
            sessions.Clear();
        }
    }

    private Session? OldestIdle()
    {
        Session? oldest = null;
        foreach (var session in sessions)
        {
            if (session.Connections == 0 && (oldest is null || session.LastActive < oldest.LastActive))
                oldest = session;
        }
        return oldest;
    }

    private static bool TryHash(string text, out byte[] hash)
    {
        Span<byte> secret = stackalloc byte[SecretBytes];
        if (text.Length != SecretBytes * 2
            || Convert.FromHexString(text, secret, out _, out var written) != OperationStatus.Done
            || written != SecretBytes)
        {
            hash = [];
            return false;
        }
        hash = SHA256.HashData(secret);
        return true;
    }
}
