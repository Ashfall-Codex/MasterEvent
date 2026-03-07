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
        if (ws == null) return;

        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Leaving", CancellationToken.None);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[MasterEvent] WebSocket close error: {ex.Message}");
        }

        cts?.Cancel();
        ws?.Dispose();
        ws = null;
        cts?.Dispose();
        cts = null;
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

    /// <summary>
    /// Dequeue connection events and messages received from the relay.
    /// Call this from Framework.Update (main thread).
    /// </summary>
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
        var buffer = new byte[8192];

        try
        {
            while (!token.IsCancellationRequested && ws?.State == WebSocketState.Open)
            {
                // Accumuler les frames jusqu'à EndOfMessage pour gérer la fragmentation
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
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

        connectionEvents.Enqueue(false);

        // Auto-reconnect with backoff
        if (!token.IsCancellationRequested && !disposed)
        {
            await ReconnectWithBackoff(token);
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

            try
            {
                ws?.Dispose();
                ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(serverUrl), token);
                connectionEvents.Enqueue(true);
                _ = Task.Run(() => ReceiveLoop(token));
                return;
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug($"[MasterEvent] Reconnect attempt {attempt + 1} failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        disposed = true;
        cts?.Cancel();
        try { ws?.Dispose(); } catch (Exception ex) { Plugin.Log.Debug($"[MasterEvent] Dispose error: {ex.Message}"); }
        ws = null;
        cts?.Dispose();
        cts = null;
    }
}
