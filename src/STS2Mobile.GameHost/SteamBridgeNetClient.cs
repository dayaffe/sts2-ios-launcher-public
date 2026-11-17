using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Platform;
using STS2Mobile.Platform;
using STS2Mobile.SteamBridge.Protocol;

namespace STS2Mobile.GameHost;

/// <summary>Vanilla game packets through the Linux Steam session. Callbacks into the game run only from Update/Connect.</summary>
internal sealed class SteamBridgeNetClient : NetClient
{
    private readonly INetClientHandler _gameHandler;
    private readonly BridgeClient _bridge;
    private readonly ulong _lobby;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<BridgePacket> _incoming = new();
    private readonly Channel<BridgePacket> _outgoing = Channel.CreateBounded<BridgePacket>(64);
    private readonly TaskCompletionSource _nativeConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _sending = Task.CompletedTask;
    private bool _connected;
    private ulong _host;
    private int _queued;
    private int _lost;
    private int _closed;

    public SteamBridgeNetClient(INetClientHandler handler, BridgeClient bridge, ulong lobby) : base(handler)
    {
        _gameHandler = handler;
        _bridge = bridge;
        _lobby = lobby;
        bridge.PacketReceived += Receive;
        bridge.EventReceived += OnBridgeEvent;
        bridge.Disconnected += Lost;
    }

    public override bool IsConnected => _connected;
    public override ulong NetId => RuntimeServices.GetLocalPlayerId();
    public override ulong HostNetId => _host;
    public override string GetRawLobbyIdentifier() => _lobby.ToString(CultureInfo.InvariantCulture);

    public async Task<NetErrorInfo?> ConnectAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        try
        {
            JsonElement joined = await _bridge.ExecuteAsync("join", _lobby.ToString(CultureInfo.InvariantCulture), ct: linked.Token);
            _host = ulong.Parse(joined.GetProperty("ownerId").GetString()!, CultureInfo.InvariantCulture);
            await _bridge.ExecuteAsync("connect", virtualPort: 0, channels: [0], ct: linked.Token);
            await _nativeConnected.Task.WaitAsync(TimeSpan.FromSeconds(20), linked.Token);
            if (Volatile.Read(ref _lost) != 0) throw new InvalidOperationException("Steam connection ended while joining.");
            _connected = true;
            _sending = SendLoopAsync();
            _gameHandler.OnConnectedToHost();
            return null;
        }
        catch (OperationCanceledException)
        {
            Close();
            return Error("CancelledJoin", true);
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or System.Net.WebSockets.WebSocketException)
        {
            Close();
            return Error("UnknownNetworkError");
        }
    }

    public override void Update()
    {
        if (Volatile.Read(ref _lost) != 0 && _connected)
        {
            _connected = false;
            _gameHandler.OnDisconnectedFromHost(_host, Error("UnknownNetworkError"));
            Close();
            return;
        }
        while (_connected && Volatile.Read(ref _lost) == 0 && _incoming.TryDequeue(out var packet))
        {
            Interlocked.Decrement(ref _queued);
            if (packet.PeerId != _host) { Lost(); continue; }
            _gameHandler.OnPacketReceived(_host, packet.Payload,
                packet.Reliable ? NetTransferMode.Reliable : NetTransferMode.Unreliable, packet.Channel);
        }
    }

    public override void SendMessageToHost(byte[] bytes, int length, NetTransferMode mode, int channel = 0)
    {
        if (!_connected || length <= 0 || length > bytes.Length || length > BridgePacket.MaxPayload
            || mode is not (NetTransferMode.Reliable or NetTransferMode.Unreliable)) { Lost(); return; }
        // The audited vanilla SteamClient ignores its channel argument and sends raw bytes on the connection.
        var packet = new BridgePacket(_host, 0, mode == NetTransferMode.Reliable, bytes.AsSpan(0, length).ToArray());
        if (!_outgoing.Writer.TryWrite(packet)) Lost();
    }

    public override void DisconnectFromHost(NetError reason, bool now = false)
    {
        bool connected = _connected;
        _connected = false;
        Close();
        if (connected) _gameHandler.OnDisconnectedFromHost(_host, new NetErrorInfo(reason, true));
    }

    private void Receive(BridgePacket packet)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        if (Interlocked.Increment(ref _queued) > 64) { Interlocked.Decrement(ref _queued); Lost(); return; }
        _incoming.Enqueue(packet);
    }

    private void OnBridgeEvent(JsonElement response)
    {
        string? type = response.GetProperty("type").GetString();
        if (type == "connection")
        {
            string? state = response.GetProperty("data").GetProperty("state").GetString();
            if (state == "k_ESteamNetworkingConnectionState_Connected") _nativeConnected.TrySetResult();
            else if (state is "k_ESteamNetworkingConnectionState_ClosedByPeer" or "k_ESteamNetworkingConnectionState_ProblemDetectedLocally") Lost();
        }
        else if (type == "transport_error") Lost();
    }

    private void Lost()
    {
        Interlocked.Exchange(ref _lost, 1);
        _nativeConnected.TrySetException(new InvalidOperationException("Steam bridge connection ended."));
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var packet in _outgoing.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                await _bridge.SendPacketAsync(packet, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or System.Net.WebSockets.WebSocketException) { Lost(); }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _stop.Cancel();
        _outgoing.Writer.TryComplete();
        _bridge.PacketReceived -= Receive;
        _bridge.EventReceived -= OnBridgeEvent;
        _bridge.Disconnected -= Lost;
        _ = DisposeAsync();
    }

    private async Task DisposeAsync()
    {
        try { await _bridge.DisposeAsync(); await _sending; }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or System.Net.WebSockets.WebSocketException) { }
        finally { _stop.Dispose(); }
    }

    private static NetErrorInfo Error(string name, bool selfInitiated = false) => new(Enum.Parse<NetError>(name), selfInitiated);
}

internal sealed class SteamBridgeConnectionInitializer(BridgeClient bridge, ulong lobby) : IClientConnectionInitializer
{
    private SteamBridgeNetClient? _client;
    public PlatformType Platform => PlatformType.Steam;
    public ulong? GetHostId() => _client?.HostNetId is > 0 and var id ? id : null;
    public Task<NetErrorInfo?> Connect(INetClientGameService gameService, CancellationToken cancelToken = default)
    {
        _client = new SteamBridgeNetClient(gameService, bridge, lobby);
        gameService.Initialize(_client, PlatformType.Steam);
        return _client.ConnectAsync(cancelToken);
    }
    public ValueTask ReleaseIfUnusedAsync() => _client == null ? bridge.DisposeAsync() : ValueTask.CompletedTask;
}
