using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MasterEvent.Communication.Remote;

namespace MasterEvent.Services.Remote;

public sealed record WebFile(string ContentType, byte[] Body);

// ReceivedAt : Environment.TickCount64 à l'arrivée, d'où l'âge de la commande au moment de l'exécuter.
public readonly record struct InboundCommand(long ConnectionId, RemoteCommand Command, long ReceivedAt);

// Sert la vue MJ au navigateur du même PC et reçoit ses commandes. Les méthodes publiques viennent du thread
// du jeu : elles déposent la dernière valeur et réveillent les sessions, sans jamais attendre une socket.
// Les commandes lues sur le thread réseau attendent dans une file que le thread du jeu vide (TryTakeCommand).
public sealed class LocalGmServer : IDisposable
{
    private const string LogPrefix = "[MasterEvent] Second écran : ";
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    public const string Revoked = "revoked";
    public const string Ended = "ended";
    private const string Unloaded = "unloaded";
    private const int MaxConnections = 8;
    private const int MaxHeaderBytes = 8 * 1024;
    private const int MaxMessageBytes = 16 * 1024;
    private const int MaxMessagesPerSecond = 20;
    private const int MaxQueuedCommands = 32;
    private const int MaxPendingAcks = 64;
    private const WebSocketCloseStatus Refused = (WebSocketCloseStatus)4401;
    private const WebSocketCloseStatus AuthTimedOut = (WebSocketCloseStatus)4408;
    private const WebSocketCloseStatus TooManySessions = (WebSocketCloseStatus)4409;

    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    private readonly Lock gate = new();
    private readonly HashSet<Connection> connections = [];
    // Avec Wait, TryWrite échoue aussitôt quand la file est pleine, d'où « busy » ; DropWrite jetterait la commande sans le dire.
    private readonly Channel<InboundCommand> inbox = Channel.CreateBounded<InboundCommand>(
        new BoundedChannelOptions(MaxQueuedCommands) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Dictionary<string, WebFile> files;
    private readonly RemoteAccess access;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task acceptLoop;
    private readonly string host;
    private readonly string origin;
    private readonly string policy;
    private volatile Payload? hello;
    private volatile Payload? state;
    private volatile Payload? notes;
    private volatile Payload? portraits;
    private volatile bool disposed;
    private long payloadSeq;
    private long stateSeq;
    private long nextConnectionId;
    private long sessionsOpened;

    public LocalGmServer(IReadOnlyDictionary<string, WebFile> files) : this(files, RemoteAccess.TicketLifetime)
    {
    }

    internal LocalGmServer(IReadOnlyDictionary<string, WebFile> files, TimeSpan ticketLifetime)
    {
        this.files = new Dictionary<string, WebFile>(files, StringComparer.Ordinal);
        access = new RemoteAccess(ticketLifetime);
        listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        host = $"127.0.0.1:{Port}";
        origin = $"http://{host}";
        policy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src data:; " +
                 $"connect-src ws://{host}; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        acceptLoop = Task.Run(AcceptLoopAsync);
        Plugin.Log.Information($"{LogPrefix}à l'écoute sur {host}");
    }

    public int Port { get; }

    public bool IsListening => !disposed && !acceptLoop.IsCompleted;

    public int SessionCount => access.ConnectedCount;

    // Sessions ouvertes par un ticket depuis le démarrage ; une reprise par jeton (F5, onglet dupliqué) ne compte pas.
    public long SessionsOpened => Interlocked.Read(ref sessionsOpened);

    // Vide après Dispose : il n'y a plus rien à ouvrir.
    public string IssueTicketUrl()
    {
        if (disposed) return string.Empty;
        var ticket = access.IssueTicket();
        Plugin.Log.Information($"{LogPrefix}nouveau lien d'accès émis");
        return $"{origin}/#t={ticket}";
    }

    public void SetHello(string json)
    {
        if (!disposed) hello = NewPayload(json);
    }

    // utf8 est gardé tel quel et partagé par les sessions : l'appelant ne le modifie plus ensuite.
    // seq : celui du state, repris par les réponses que le serveur donne seul (busy, duplicate, badArgs).
    public void PublishState(byte[] utf8, long seq)
    {
        if (disposed) return;
        Volatile.Write(ref stateSeq, seq);
        state = new Payload(Interlocked.Increment(ref payloadSeq), utf8);
        WakeSessions();
    }

    public void PublishNotes(string json)
    {
        if (disposed) return;
        notes = NewPayload(json);
        WakeSessions();
    }

    // Canal à part, comme les notes : un état qui change ne renvoie pas les images avec lui.
    public void PublishPortraits(string json)
    {
        if (disposed) return;
        portraits = NewPayload(json);
        WakeSessions();
    }

    // Thread du jeu, seul lecteur de la file.
    public bool TryTakeCommand(out InboundCommand command) => inbox.Reader.TryRead(out command);

    // Faux pour une page fermée ou coupée entre-temps : sa commande n'a plus personne pour attendre la réponse.
    public bool IsLive(long connectionId)
    {
        lock (gate)
            return !disposed && Find(connectionId) is { Session: not null, CloseStatus: null };
    }

    // Réponse perdue si la page est déjà partie.
    public void Acknowledge(long connectionId, RemoteAck ack)
    {
        lock (gate)
        {
            if (!disposed) Find(connectionId)?.EnqueueAck(ack);
        }
    }

    // reason part dans le bye : « revoked » quand le MJ coupe, « ended » quand le pont coupe d'office.
    public void RevokeAll(string reason = Revoked)
    {
        var closed = 0;
        lock (gate)
        {
            if (disposed) return;
            access.RevokeAll();
            foreach (var connection in connections)
            {
                if (connection.Session is null) continue;
                connection.Shutdown(reason, WebSocketCloseStatus.NormalClosure);
                closed++;
            }
            // Après la révocation, plus rien n'entre : ce qui attendait encore ne s'exécutera pas.
            DropQueuedCommands();
        }
        Plugin.Log.Information($"{LogPrefix}accès coupé, {closed} page(s) fermée(s)");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            access.RevokeAll();
            foreach (var connection in connections)
                connection.Shutdown(Unloaded, WebSocketCloseStatus.EndpointUnavailable);
            inbox.Writer.TryComplete();
            DropQueuedCommands();
        }
        _ = stopping.CancelAsync();
        listener.Dispose();
        Plugin.Log.Information($"{LogPrefix}arrêté");
    }

    private Payload NewPayload(string json) => new(Interlocked.Increment(ref payloadSeq), json);

    private void DropQueuedCommands()
    {
        while (inbox.Reader.TryRead(out _))
        {
        }
    }

    // Sous le verrou : huit connexions au plus.
    private Connection? Find(long connectionId)
    {
        foreach (var connection in connections)
        {
            if (connection.Id == connectionId) return connection;
        }
        return null;
    }

    private void WakeSessions()
    {
        lock (gate)
        {
            foreach (var connection in connections)
            {
                if (connection.Session is not null) connection.Wake();
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        var token = stopping.Token;
        try
        {
            while (true)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptSocketAsync(token);
                }
                catch (SocketException ex) when (!token.IsCancellationRequested)
                {
                    // Sous Wine, une connexion coupée par RST avant l'accept lève InvalidArgument : l'écoute continue.
                    Plugin.Log.Debug($"{LogPrefix}accept refusé ({ex.SocketErrorCode})");
                    await Task.Delay(50, token);
                    continue;
                }
                Admit(socket);
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"{LogPrefix}écoute arrêtée : {ex}");
            listener.Stop();
        }
    }

    private void Admit(Socket socket)
    {
        Connection? connection = null;
        lock (gate)
        {
            // Plafond avant toute allocation : un processus local ne doit pas épuiser les descripteurs du jeu.
            if (!disposed && connections.Count < MaxConnections)
            {
                connection = new Connection(socket, ++nextConnectionId);
                connections.Add(connection);
            }
        }
        if (connection is null) socket.Dispose();
        else _ = Task.Run(() => HandleAsync(connection));
    }

    private async Task HandleAsync(Connection connection)
    {
        try
        {
            var head = await ReadHeadAsync(connection);
            if (head is not null) await RouteAsync(connection, Request.Parse(head));
        }
        catch (Exception ex) when (IsDisconnect(ex))
        {
            // Page fermée, délai dépassé ou plugin déchargé : la connexion s'arrête là.
        }
        catch (Exception ex)
        {
            // Tâche lancée sans attente : sans ce filet, l'erreur disparaîtrait sans trace.
            Plugin.Log.Warning($"{LogPrefix}connexion interrompue : {ex}");
        }
        finally
        {
            RemoteAccess.Session? session;
            lock (gate)
            {
                connections.Remove(connection);
                session = connection.Session;
                if (session is not null) access.Release(session);
            }
            connection.Dispose();
            if (session is not null) Plugin.Log.Debug($"{LogPrefix}page fermée, {SessionCount} session(s) encore connectée(s)");
        }
    }

    private static async Task<string?> ReadHeadAsync(Connection connection)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(connection.Abort.Token);
        deadline.CancelAfter(HeaderTimeout);
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await connection.Stream.ReadAsync(buffer.AsMemory(length), deadline.Token);
            if (read == 0) return null;
            // Le séparateur peut chevaucher deux lectures.
            var from = Math.Max(0, length - 3);
            length += read;
            var end = buffer.AsSpan(from, length - from).IndexOf("\r\n\r\n"u8);
            if (end >= 0) return Encoding.Latin1.GetString(buffer, 0, from + end);
        }
        Refuse("en-têtes trop longs");
        return null;
    }

    private async Task RouteAsync(Connection connection, Request? request)
    {
        if (request is null)
        {
            await RespondAsync(connection, "400 Bad Request");
            return;
        }
        // Un site tiers résolu vers 127.0.0.1 (DNS rebinding) arrive avec son propre nom d'hôte.
        if (request["Host"] != host)
        {
            Refuse("requête refusée, Host inattendu");
            await RespondAsync(connection, "403 Forbidden");
            return;
        }
        // Une page tierce peut inclure app.js ou viser le WebSocket : seules la nôtre et l'ouverture directe passent.
        if (request["Sec-Fetch-Site"] is not (null or "none" or "same-origin"))
        {
            Refuse("requête refusée, Sec-Fetch-Site inattendu");
            await RespondAsync(connection, "403 Forbidden");
            return;
        }
        if (request.Method != "GET")
        {
            await RespondAsync(connection, "405 Method Not Allowed", "Allow: GET\r\n");
            return;
        }

        if (request.Target == "/ws")
            await UpgradeAsync(connection, request);
        else if (files.TryGetValue(request.Target, out var file))
            await RespondAsync(connection, "200 OK", file.ContentType, file.Body, null);
        else
            await RespondAsync(connection, "404 Not Found");
    }

    private Task RespondAsync(Connection connection, string status, string? extraHeaders = null) =>
        RespondAsync(connection, status, "text/plain; charset=utf-8", Encoding.ASCII.GetBytes(status), extraHeaders);

    private async Task RespondAsync(Connection connection, string status, string contentType, byte[] body, string? extraHeaders)
    {
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            extraHeaders +
            $"Content-Security-Policy: {policy}\r\n" +
            "X-Frame-Options: DENY\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Cross-Origin-Resource-Policy: same-origin\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");
        // Un seul envoi : en-têtes et corps séparés attendraient l'accusé de réception différé.
        var response = new byte[head.Length + body.Length];
        head.CopyTo(response, 0);
        body.CopyTo(response, head.Length);
        await WriteAsync(connection, response);
    }

    private static async Task WriteAsync(Connection connection, byte[] bytes)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(connection.Abort.Token);
        deadline.CancelAfter(WriteTimeout);
        await connection.Stream.WriteAsync(bytes, deadline.Token);
    }

    private async Task UpgradeAsync(Connection connection, Request request)
    {
        // N'importe quelle page ouverte dans le navigateur peut viser 127.0.0.1 : seule la nôtre passe.
        if (request["Origin"] != origin)
        {
            Refuse("WebSocket refusé, Origin inattendue");
            await RespondAsync(connection, "403 Forbidden");
            return;
        }
        var key = request["Sec-WebSocket-Key"];
        if (!string.Equals(request["Upgrade"], "websocket", StringComparison.OrdinalIgnoreCase)
            || !HasToken(request["Connection"], "upgrade")
            || !IsValidKey(key))
        {
            await RespondAsync(connection, "400 Bad Request");
            return;
        }
        if (request["Sec-WebSocket-Version"] != "13")
        {
            await RespondAsync(connection, "426 Upgrade Required", "Sec-WebSocket-Version: 13\r\n");
            return;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        await WriteAsync(connection, Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n"));

        using var socket = WebSocket.CreateFromStream(connection.Stream, new WebSocketCreationOptions
        {
            IsServer = true,
            KeepAliveInterval = KeepAliveInterval,
        });
        lock (gate)
        {
            connection.IsWebSocket = true;
            if (disposed) connection.Shutdown(Unloaded, WebSocketCloseStatus.EndpointUnavailable);
        }
        await RunSessionAsync(connection, socket);
    }

    private async Task RunSessionAsync(Connection connection, WebSocket socket)
    {
        var receiving = ReceiveLoopAsync(connection, socket);
        try
        {
            await SendLoopAsync(connection, socket);
            // La trame de fermeture est partie : le pair a ce délai pour répondre, puis la socket est coupée.
            connection.Abort.CancelAfter(CloseGrace);
        }
        finally
        {
            await receiving;
        }
    }

    // Seul émetteur de la socket, fermeture comprise. Une session lente ne lit que la dernière valeur.
    private async Task SendLoopAsync(Connection connection, WebSocket socket)
    {
        try
        {
            if (hello is { } greeting) await SendAsync(connection, socket, greeting.Utf8);
            while (true)
            {
                if (connection.CloseStatus is { } status)
                {
                    if (connection.ByeReason is { } reason)
                        await SendAsync(connection, socket, JsonSerializer.SerializeToUtf8Bytes(new RemoteBye(reason), RemoteView.Json));
                    await socket.CloseOutputAsync(status, null, connection.Abort.Token);
                    return;
                }
                if (connection.TakeWelcome() is { } token)
                    await SendAsync(connection, socket, JsonSerializer.SerializeToUtf8Bytes(new RemoteWelcome(token), RemoteView.Json));
                if (connection.Session is not null)
                {
                    while (connection.TryTakeAck(out var ack))
                        await SendAsync(connection, socket, JsonSerializer.SerializeToUtf8Bytes(ack, RemoteView.Json));
                    if (state is { } current && current.Seq != connection.SentState)
                    {
                        await SendAsync(connection, socket, current.Utf8);
                        connection.SentState = current.Seq;
                    }
                    if (notes is { } text && text.Seq != connection.SentNotes)
                    {
                        await SendAsync(connection, socket, text.Utf8);
                        connection.SentNotes = text.Seq;
                    }
                    if (portraits is { } images && images.Seq != connection.SentPortraits)
                    {
                        await SendAsync(connection, socket, images.Utf8);
                        connection.SentPortraits = images.Seq;
                    }
                }
                await connection.WaitAsync();
            }
        }
        catch (Exception ex)
        {
            // Quelle que soit la cause, la boucle de réception ne doit pas attendre seule.
            connection.Abort.Cancel();
            if (!IsDisconnect(ex)) throw;
        }
    }

    private static async Task SendAsync(Connection connection, WebSocket socket, byte[] utf8)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(connection.Abort.Token);
        // Un onglet figé ne garde pas sa place indéfiniment : sa page reviendra avec sa session.
        deadline.CancelAfter(SendTimeout);
        await socket.SendAsync(utf8, WebSocketMessageType.Text, true, deadline.Token);
    }

    private async Task ReceiveLoopAsync(Connection connection, WebSocket socket)
    {
        var buffer = new byte[MaxMessageBytes];
        var token = connection.Abort.Token;
        try
        {
            // Annuler une réception abandonne la socket sans trame de fermeture : le délai se mesure à côté.
            var pending = ReadMessageAsync(socket, buffer, token);
            Message first;
            try
            {
                first = await pending.WaitAsync(AuthTimeout, token);
            }
            catch (TimeoutException)
            {
                Refuse("pas d'authentification à temps");
                connection.RequestClose(AuthTimedOut);
                await DrainAsync(socket, buffer, token, pending);
                return;
            }

            if (first.Kind == MessageKind.Closed)
            {
                connection.RequestClose(WebSocketCloseStatus.NormalClosure);
                return;
            }
            WebSocketCloseStatus status;
            if (first.Kind == MessageKind.TooBig)
            {
                Refuse("message trop long avant l'authentification");
                status = WebSocketCloseStatus.MessageTooBig;
            }
            else
            {
                status = Authenticate(connection, buffer.AsMemory(0, first.Length), first.Kind == MessageKind.Text)
                         ?? await ReadCommandsAsync(connection, socket, buffer, token);
            }

            connection.RequestClose(status);
            if (status != WebSocketCloseStatus.NormalClosure) await DrainAsync(socket, buffer, token, null);
        }
        catch (Exception ex)
        {
            connection.Abort.Cancel();
            if (!IsDisconnect(ex)) throw;
        }
    }

    // Null quand la session est ouverte, sinon le code de fermeture.
    private WebSocketCloseStatus? Authenticate(Connection connection, ReadOnlyMemory<byte> message, bool isText)
    {
        if (!isText || !TryReadAuth(message, out var ticket, out var token))
        {
            Refuse("authentification illisible");
            return Refused;
        }

        RemoteAccess.Grant grant = default;
        lock (gate)
        {
            if (disposed) return Refused;
            if (token is not null) grant = access.ResumeSession(token);
            if (grant.Verdict != RemoteAccess.Verdict.Accepted && ticket is not null) grant = access.RedeemTicket(ticket);
            if (grant is { Verdict: RemoteAccess.Verdict.Accepted, Session: { } session })
                connection.Authorize(session, grant.Token);
        }

        switch (grant.Verdict)
        {
            case RemoteAccess.Verdict.Accepted:
                if (grant.Token is not null) Interlocked.Increment(ref sessionsOpened);
                var how = grant.Token is null ? "reprise" : "ouverte";
                Plugin.Log.Information($"{LogPrefix}session {how}, {SessionCount} connectée(s)");
                return null;
            case RemoteAccess.Verdict.TooManySessions:
                Refuse($"déjà {RemoteAccess.MaxSessions} sessions ouvertes, connexion refusée");
                return TooManySessions;
            default:
                Refuse("ticket ou session refusés");
                return Refused;
        }
    }

    private static bool TryReadAuth(ReadOnlyMemory<byte> json, out string? ticket, out string? session)
    {
        ticket = null;
        session = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !type.ValueEquals("auth"))
                return false;
            if (root.TryGetProperty("ticket", out var t) && t.ValueKind == JsonValueKind.String) ticket = t.GetString();
            if (root.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.String) session = s.GetString();
            return ticket is not null || session is not null;
        }
        // InvalidOperationException : surrogate isolé dans une chaîne, que GetString ou ValueEquals refuse.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    // Après l'authentification, chaque message texte est une commande ; un message binaire est ignoré.
    // L'entrée reste bornée en taille et en débit, quoi que contiennent les messages.
    private async Task<WebSocketCloseStatus> ReadCommandsAsync(Connection connection, WebSocket socket, byte[] buffer,
        CancellationToken token)
    {
        var windowStart = Environment.TickCount64;
        var count = 0;
        while (true)
        {
            var message = await ReadMessageAsync(socket, buffer, token);
            if (message.Kind == MessageKind.Closed) return WebSocketCloseStatus.NormalClosure;
            if (message.Kind == MessageKind.TooBig)
            {
                Refuse("message trop long, session fermée");
                return WebSocketCloseStatus.MessageTooBig;
            }

            var now = Environment.TickCount64;
            if (now - windowStart >= 1000)
            {
                windowStart = now;
                count = 0;
            }
            if (++count > MaxMessagesPerSecond)
            {
                Refuse("trop de messages, session fermée");
                return WebSocketCloseStatus.PolicyViolation;
            }

            if (message.Kind == MessageKind.Text) QueueCommand(connection, buffer.AsMemory(0, message.Length), now);
        }
    }

    // Les refus qui ne dépendent pas du jeu répondent d'ici, sans passer par son thread.
    private void QueueCommand(Connection connection, ReadOnlyMemory<byte> json, long now)
    {
        if (RemoteCommandReader.TryRead(json, out var command, out var id, out var error))
        {
            var verdict = access.TryQueueCommand(connection.Session!, command.Id, inbox.Writer,
                new InboundCommand(connection.Id, command, now));
            switch (verdict)
            {
                case RemoteAccess.CommandVerdict.Queued:
                case RemoteAccess.CommandVerdict.Revoked:
                    return;
                case RemoteAccess.CommandVerdict.Duplicate:
                    error = RemoteErrors.Duplicate;
                    break;
                default:
                    error = RemoteErrors.Busy;
                    break;
            }
        }

        if (id is null)
        {
            Refuse("commande illisible, sans réponse");
            return;
        }
        Refuse($"commande refusée ({error})");
        connection.EnqueueAck(new RemoteAck(id, false, error, false, Volatile.Read(ref stateSeq)));
    }

    // Lire jusqu'à la trame de fermeture du pair : fermer avec des octets non lus enverrait un RST,
    // et le navigateur perdrait le code de fermeture.
    private static async Task DrainAsync(WebSocket socket, byte[] buffer, CancellationToken token, Task<Message>? pending)
    {
        if (pending is not null && (await pending).Kind == MessageKind.Closed) return;
        while ((await ReadMessageAsync(socket, buffer, token)).Kind != MessageKind.Closed)
        {
        }
    }

    private static async Task<Message> ReadMessageAsync(WebSocket socket, byte[] buffer, CancellationToken token)
    {
        var length = 0;
        while (length < buffer.Length)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(length), token);
            if (result.MessageType == WebSocketMessageType.Close) return new Message(MessageKind.Closed, 0);
            length += result.Count;
            if (result.EndOfMessage)
            {
                var kind = result.MessageType == WebSocketMessageType.Text ? MessageKind.Text : MessageKind.Binary;
                return new Message(kind, length);
            }
        }
        return new Message(MessageKind.TooBig, length);
    }

    private static bool HasToken(string? header, string token)
    {
        if (header is null) return false;
        foreach (var part in header.Split(','))
        {
            if (string.Equals(part.Trim(), token, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsValidKey([NotNullWhen(true)] string? key)
    {
        Span<byte> nonce = stackalloc byte[18];
        return key is { Length: 24 } && Convert.TryFromBase64String(key, nonce, out var written) && written == 16;
    }

    private static bool IsDisconnect(Exception ex) =>
        ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or WebSocketException;

    // Les refus ne citent jamais la valeur reçue : elle vient d'un tiers et pourrait forger des lignes.
    private static void Refuse(string reason) => Plugin.Log.Debug($"{LogPrefix}{reason}");

    // Un texte est encodé au premier envoi, hors du thread du jeu, puis partagé par les sessions ; une
    // valeur remplacée avant d'être envoyée ne coûte rien.
    private sealed class Payload
    {
        private readonly string? json;
        private byte[]? utf8;

        public Payload(long seq, string json)
        {
            Seq = seq;
            this.json = json;
        }

        public Payload(long seq, byte[] utf8)
        {
            Seq = seq;
            this.utf8 = utf8;
        }

        public long Seq { get; }
        public byte[] Utf8 => utf8 ??= Encoding.UTF8.GetBytes(json!);
    }

    private enum MessageKind
    {
        Text,
        Binary,
        TooBig,
        Closed,
    }

    private readonly record struct Message(MessageKind Kind, int Length);

    private sealed class Request
    {
        private readonly Dictionary<string, string> headers;

        private Request(string method, string target, Dictionary<string, string> headers)
        {
            Method = method;
            Target = target;
            this.headers = headers;
        }

        public string Method { get; }
        public string Target { get; }
        public string? this[string name] => headers.GetValueOrDefault(name);

        public static Request? Parse(string head)
        {
            var lines = head.Split("\r\n");
            var start = lines[0].Split(' ');
            if (start.Length != 3 || start[2] != "HTTP/1.1") return null;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.AsSpan(1))
            {
                var colon = line.IndexOf(':');
                // Ni ligne repliée ni espace avant les deux-points, et aucun doublon : un en-tête en double
                // laisserait l'émetteur choisir la valeur lue.
                if (colon <= 0 || line.AsSpan(0, colon).ContainsAny(' ', '\t')) return null;
                if (!headers.TryAdd(line[..colon], line[(colon + 1)..].Trim(' ', '\t'))) return null;
            }
            return new Request(start[0], start[1], headers);
        }
    }

    private sealed class Connection : IDisposable
    {
        private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
        private readonly Queue<RemoteAck> acks = new();
        private volatile RemoteAccess.Session? session;
        private volatile string? byeReason;
        private string? welcome;
        private int closeStatus;
        private bool acksDropped;

        public Connection(Socket socket, long id)
        {
            Id = id;
            Stream = new NetworkStream(socket, ownsSocket: true);
        }

        public long Id { get; }
        public NetworkStream Stream { get; }
        public CancellationTokenSource Abort { get; } = new();
        public RemoteAccess.Session? Session => session;
        public string? ByeReason => byeReason;
        public WebSocketCloseStatus? CloseStatus =>
            Volatile.Read(ref closeStatus) is var status and not 0 ? (WebSocketCloseStatus)status : null;

        // Lus et écrits sous le verrou du serveur.
        public bool IsWebSocket { get; set; }

        // Propres à la boucle d'envoi.
        public long SentState { get; set; } = -1;
        public long SentNotes { get; set; } = -1;
        public long SentPortraits { get; set; } = -1;

        public void Authorize(RemoteAccess.Session granted, string? token)
        {
            welcome = token;
            session = granted;
            Wake();
        }

        public string? TakeWelcome() => Interlocked.Exchange(ref welcome, null);

        // Thread réseau (refus immédiats) et thread du jeu (Acknowledge). Une page qui ne lit plus perd les
        // réponses les plus anciennes, pas la mémoire du jeu.
        public void EnqueueAck(RemoteAck ack)
        {
            bool firstDrop;
            lock (acks)
            {
                firstDrop = acks.Count >= MaxPendingAcks && !acksDropped;
                if (acks.Count >= MaxPendingAcks)
                {
                    acks.Dequeue();
                    acksDropped = true;
                }
                acks.Enqueue(ack);
            }
            if (firstDrop) Refuse("réponses en attente perdues, la page ne lit plus");
            Wake();
        }

        public bool TryTakeAck([NotNullWhen(true)] out RemoteAck? ack)
        {
            lock (acks)
                return acks.TryDequeue(out ack);
        }

        // La première demande l'emporte : un bye ne remplace pas un 1009 déjà décidé.
        public void RequestClose(WebSocketCloseStatus status)
        {
            Interlocked.CompareExchange(ref closeStatus, (int)status, 0);
            Wake();
        }

        public void Wake() => wake.Writer.TryWrite(true);

        public ValueTask<bool> WaitAsync() => wake.Reader.ReadAsync(Abort.Token);

        // Appelé sous le verrou du serveur, souvent depuis le thread du jeu : rien n'y attend.
        public void Shutdown(string reason, WebSocketCloseStatus status)
        {
            if (!IsWebSocket)
            {
                _ = Abort.CancelAsync();
                return;
            }
            byeReason ??= reason;
            RequestClose(status);
            Abort.CancelAfter(CloseGrace);
        }

        public void Dispose()
        {
            Stream.Dispose();
            Abort.Dispose();
        }
    }
}
