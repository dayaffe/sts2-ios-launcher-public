using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;

namespace STS2Mobile.SteamBridge.Protocol;

/// <summary>
/// The phone-side connection, with no game or Steamworks dependency. Packet callbacks run off the game thread;
/// the game adapter must queue them for Godot's Update. A lost connection is never silently resumed or replayed.
/// </summary>
public sealed class BridgeClient : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private Task _receiving = Task.CompletedTask;
    private int _disposed;

    private BridgeClient(WebSocket socket) => _socket = socket;

    public ulong SteamId { get; private set; }
    public bool IsConnected => !_stop.IsCancellationRequested && _socket.State == WebSocketState.Open;
    public event Action<BridgePacket>? PacketReceived;
    public event Action<JsonElement>? EventReceived;
    public event Action? Disconnected;

    public static async Task<BridgeClient> ConnectAsync(Uri endpoint, string token, ulong expectedSteamId, CancellationToken ct = default)
    {
        if (endpoint.Scheme != "wss" || endpoint.Port != 443 || endpoint.HostNameType != UriHostNameType.Dns || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != "/bridge")
            throw new ArgumentException("Use wss://<your-host>/bridge on port 443.", nameof(endpoint));
        if (token.Length < 32 || token.Contains('\r') || token.Contains('\n')) throw new ArgumentException("Invalid bridge token.", nameof(token));
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
        socket.Options.Proxy = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var transport = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };
            using var invoker = new HttpMessageInvoker(transport);
            await socket.ConnectAsync(endpoint, invoker, timeout.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            return await AttachAsync(socket, expectedSteamId, timeout.Token);
        }
        catch { socket.Dispose(); throw; }
    }

    /// <summary>Accept an already authenticated socket, also used by the in-process integration tests.</summary>
    public static async Task<BridgeClient> AttachAsync(WebSocket socket, ulong expectedSteamId, CancellationToken ct = default)
    {
        if (expectedSteamId == 0) throw new ArgumentException("Bind the connection to the launcher's authenticated Steam ID.", nameof(expectedSteamId));
        var client = new BridgeClient(socket);
        try
        {
            var (kind, bytes) = await ReceiveAsync(socket, ct);
            if (kind != WebSocketMessageType.Text) throw new InvalidOperationException("Missing bridge greeting.");
            using var document = JsonDocument.Parse(bytes);
            JsonElement hello = document.RootElement;
            if (hello.GetProperty("type").GetString() != "hello" || hello.GetProperty("protocol").GetInt32() != BridgePacket.Version)
                throw new InvalidOperationException("Unsupported bridge protocol.");
            JsonElement status = hello.GetProperty("status");
            while (!status.GetProperty("ready").GetBoolean())
            {
                string? state = status.GetProperty("state").GetString();
                if (state is not ("idle" or "starting_steam" or "waiting_for_steam" or "waiting_for_login"))
                    throw new InvalidOperationException("Bridge Steam session is not ready: " + state);
                var update = await ReceiveAsync(socket, ct);
                if (update.Type != WebSocketMessageType.Text) throw new InvalidOperationException("Steam startup ended before readiness.");
                using var next = JsonDocument.Parse(update.Bytes);
                if (next.RootElement.GetProperty("type").GetString() != "status")
                    throw new InvalidOperationException("Unexpected response during Steam startup.");
                status = next.RootElement.GetProperty("data").Clone();
            }
            if (!ulong.TryParse(status.GetProperty("steamId").GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong actual)
                || actual != expectedSteamId)
                throw new InvalidOperationException("Bridge is signed in to a different Steam account.");
            client.SteamId = actual;
            client._receiving = Task.Run(client.ReadLoopAsync);
            return client;
        }
        catch { socket.Abort(); socket.Dispose(); client._stop.Dispose(); throw; }
    }

    public async Task<JsonElement> ExecuteAsync(string type, string? lobbyId = null, int virtualPort = 0, int[]? channels = null, CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException("Bridge is disconnected.");
        string id = Guid.NewGuid().ToString("N");
        var command = new BridgeCommand(id, type, lobbyId, virtualPort, channels);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(command, BridgeJsonContext.Default.BridgeCommand);
        BridgeCommand.Parse(bytes);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            await SendAsync(bytes, WebSocketMessageType.Text, ct);
            return await answer.Task.WaitAsync(TimeSpan.FromSeconds(25), ct);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task SendPacketAsync(BridgePacket packet, CancellationToken ct = default) => SendAsync(packet.Encode(), WebSocketMessageType.Binary, ct);

    private async Task SendAsync(byte[] bytes, WebSocketMessageType type, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await _send.WaitAsync(linked.Token);
        try { await _socket.SendAsync(bytes.AsMemory(), type, true, linked.Token); }
        finally { _send.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var (kind, bytes) = await ReceiveAsync(_socket, _stop.Token);
                if (kind == WebSocketMessageType.Close) break;
                if (kind == WebSocketMessageType.Binary) { PacketReceived?.Invoke(BridgePacket.Decode(bytes)); continue; }
                using var document = JsonDocument.Parse(bytes);
                JsonElement response = document.RootElement;
                if (response.GetProperty("type").GetString() == "result")
                {
                    string? id = response.GetProperty("id").GetString();
                    if (id != null && _pending.TryRemove(id, out var answer))
                    {
                        if (response.GetProperty("ok").GetBoolean()) answer.TrySetResult(response.GetProperty("data").Clone());
                        else answer.TrySetException(new InvalidOperationException(response.GetProperty("error").GetString() ?? "Bridge request failed."));
                    }
                }
                else
                {
                    if (response.GetProperty("type").GetString() == "status")
                    {
                        JsonElement status = response.GetProperty("data");
                        if (!status.GetProperty("ready").GetBoolean() || status.GetProperty("steamId").GetString() != SteamId.ToString(CultureInfo.InvariantCulture))
                            throw new InvalidOperationException("Bridge Steam identity or availability changed.");
                    }
                    EventReceived?.Invoke(response.Clone());
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or JsonException or ArgumentException or InvalidOperationException)
        {
            // The transport ending must fail every outstanding request; game actions are never replayed.
        }
        finally
        {
            _stop.Cancel();
            _socket.Abort();
            foreach (var answer in _pending.Values) answer.TrySetException(new InvalidOperationException("Bridge disconnected."));
            _pending.Clear();
            Disconnected?.Invoke();
        }
    }

    private static async Task<(WebSocketMessageType Type, byte[] Bytes)> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[8192];
        var message = new ArrayBufferWriter<byte>();
        WebSocketMessageType? type = null;
        ValueWebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (part.MessageType == WebSocketMessageType.Close) return (part.MessageType, []);
            if (type != null && type != part.MessageType) throw new ArgumentException("Mixed frame types.");
            type = part.MessageType;
            int max = type == WebSocketMessageType.Text ? 64 * 1024 : BridgePacket.HeaderSize + BridgePacket.MaxPayload;
            if (message.WrittenCount + part.Count > max) throw new ArgumentException("Bridge message exceeds limit.");
            message.Write(buffer.AsSpan(0, part.Count));
        } while (!part.EndOfMessage);
        return (part.MessageType, message.WrittenSpan.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _socket.Abort();
        await _receiving;
        _socket.Dispose();
        _stop.Dispose();
        _send.Dispose();
    }
}
