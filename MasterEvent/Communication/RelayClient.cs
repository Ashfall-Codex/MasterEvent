using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MasterEvent.Communication;

public class RelayClient : IDisposable
{
    public event Action<RelayMessage>? OnMessageReceived;
    public event Action? OnConnected;
    public event Action? OnDisconnected;

    public bool IsConnected => ws?.State == WebSocketState.Open;

    private ClientWebSocket? ws;
    private CancellationTokenSource? cts;
    private readonly ConcurrentQueue<RelayMessage> incomingQueue = new();
    private readonly ConcurrentQueue<bool> connectionEvents = new(); // true = connected, false = disconnected
    private string serverUrl = string.Empty;
    private bool disposed;

    public async Task ConnectAsync(string url)
    {
        if (IsConnected) await DisconnectAsync();

        // Capturer et nettoyer les anciennes instances avant d'écraser les champs,
        // au cas où DisconnectAsync() n'a pas été appelé ou n'a pas encore terminé
        var oldWs = ws;
        var oldCts = cts;
        oldCts?.Cancel();
        oldWs?.Dispose();
        oldCts?.Dispose();

        serverUrl = url;
        ws = new ClientWebSocket();
        cts = new CancellationTokenSource();
        var token = cts.Token;

        try
        {
            await ws.ConnectAsync(new Uri(url), token);
            connectionEvents.Enqueue(true);
            _ = Task.Run(() => ReceiveLoop(token));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[MasterEvent] WebSocket connect failed: {ex.Message}");
            connectionEvents.Enqueue(false);

            // Auto-reconnect on initial connection failure
            if (!token.IsCancellationRequested && !disposed)
            {
                _ = Task.Run(() => ReconnectWithBackoff(token));
            }
        }
    }

    public async Task DisconnectAsync()
    {
        var localWs = ws;
        var localCts = cts;
        ws = null;
        cts = null;

        if (localWs == null) return;
        localCts?.Cancel();

        try
        {
            if (localWs.State == WebSocketState.Open)
                await localWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "Leaving", CancellationToken.None);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[MasterEvent] WebSocket close error: {ex.Message}");
        }

        localWs.Dispose();
        localCts?.Dispose();
        connectionEvents.Enqueue(false);
    }

    public async Task SendAsync(RelayMessage message)
    {
        if (ws?.State != WebSocketState.Open) return;

        try
        {
            var json = message.Serialize();
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[MasterEvent] WebSocket send failed: {ex.Message}");
        }
    }
    public void ProcessIncoming()
    {
        while (connectionEvents.TryDequeue(out var connected))
        {
            if (connected)
                OnConnected?.Invoke();
            else
                OnDisconnected?.Invoke();
        }

        while (incomingQueue.TryDequeue(out var msg))
        {
            OnMessageReceived?.Invoke(msg);
        }
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        // Capturer la référence locale pour éviter un NullReferenceException
        var socket = ws;
        if (socket == null) return;

        var buffer = new byte[8192];

        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                // Accumuler les frames jusqu'à EndOfMessage pour gérer la fragmentation
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    var msg = RelayMessage.Deserialize(json);
                    if (msg != null)
                        incomingQueue.Enqueue(msg);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[MasterEvent] WebSocket receive error: {ex.Message}");
        }

        // Émettre un événement de déconnexion et tenter la reconnexion seulement si
        if (!token.IsCancellationRequested)
        {
            connectionEvents.Enqueue(false);

            if (!disposed)
            {
                await ReconnectWithBackoff(token);
            }
        }
    }

    private async Task ReconnectWithBackoff(CancellationToken token)
    {
        var delays = new[] { 1000, 2000, 4000, 8000, 15000, 30000 };
        for (var attempt = 0; !token.IsCancellationRequested && !disposed; attempt++)
        {
            var delay = delays[Math.Min(attempt, delays.Length - 1)];
            Plugin.Log.Info($"[MasterEvent] Reconnecting in {delay}ms (attempt {attempt + 1})...");

            try { await Task.Delay(delay, token); }
            catch (OperationCanceledException) { return; }

            // Vérifier une dernière fois après le délai
            if (token.IsCancellationRequested) return;

            try
            {
                var newWs = new ClientWebSocket();
                try
                {
                    await newWs.ConnectAsync(new Uri(serverUrl), token);
                }
                catch
                {
                    newWs.Dispose();
                    throw;
                }

                ws = newWs;
                connectionEvents.Enqueue(true);
                _ = Task.Run(() => ReceiveLoop(token));
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Plugin.Log.Debug($"[MasterEvent] Reconnect attempt {attempt + 1} failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        disposed = true;
        var localWs = ws;
        var localCts = cts;
        ws = null;
        cts = null;

        localCts?.Cancel();
        try { localWs?.Dispose(); } catch (Exception ex) { Plugin.Log.Debug($"[MasterEvent] Dispose error: {ex.Message}"); }
        localCts?.Dispose();
    }
}
