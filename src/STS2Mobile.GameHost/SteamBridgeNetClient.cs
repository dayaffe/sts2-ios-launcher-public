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
    // The game stops calling Update while it loads a scene, and the desktop game lets Steam hold the host's messages
    // meanwhile. What waits here is bounded by bytes, not by a small message count.
    internal const long MaxQueuedBytes = 16 * 1024 * 1024;
    internal const int MaxQueuedPackets = 16384;
    private readonly INetClientHandler _gameHandler;
    private readonly BridgeClient _bridge;
    private readonly ulong _lobby;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<BridgePacket> _incoming = new();
    private readonly Channel<BridgePacket> _outgoing = Channel.CreateBounded<BridgePacket>(MaxQueuedPackets);
    private readonly BridgeConnectionSignal _nativeConnected = new();
    public string? FailureDetail { get; private set; }
    public string? FailureMessage { get; private set; }
    /// <summary>True once the server left the lobby and kept its Steam session for another join.</summary>
    public bool KeptServerSession { get; private set; }
    /// <summary>Completes once the server session is handed back or closed after the game stops using it.</summary>
    public Task Released { get; private set; } = Task.CompletedTask;
    private string _phase = "joining the Steam lobby";
    private string? _lostReason;
    private Task _sending = Task.CompletedTask;
    private bool _connected;
    private bool _commandPending;
    private bool _abandonedCommand;
    private ulong _host;
    private int _queuedPackets;
    private long _queuedBytes;
    private long _outgoingBytes;
    private int _lost;
    private int _closed;

    public SteamBridgeNetClient(INetClientHandler handler, BridgeClient bridge, ulong lobby) : base(handler)
    {
        _gameHandler = handler;
        _bridge = bridge;
        _lobby = lobby;
        bridge.PacketReceived += Receive;
        bridge.EventReceived += OnBridgeEvent;
        bridge.Disconnected += OnBridgeDisconnected;
    }

    public override bool IsConnected => _connected;
    public override ulong NetId => RuntimeServices.GetLocalPlayerId();
    public override ulong HostNetId => _host;
    public override string GetRawLobbyIdentifier() => _lobby.ToString(CultureInfo.InvariantCulture);

    public async Task<NetErrorInfo?> ConnectAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        RuntimeServices.ClearBridgePlayerNames();
        try
        {
            _commandPending = true;
            JsonElement joined = await _bridge.ExecuteAsync("join", _lobby.ToString(CultureInfo.InvariantCulture), ct: linked.Token);
            _host = ulong.Parse(joined.GetProperty("ownerId").GetString()!, CultureInfo.InvariantCulture);
            _phase = "opening the Steam connection";
            await _bridge.ExecuteAsync("connect", virtualPort: 0, channels: [0], ct: linked.Token);
            _commandPending = false;
            _phase = "waiting for Steam to connect";
            await _nativeConnected.WaitAsync(TimeSpan.FromSeconds(20), linked.Token);
            if (Volatile.Read(ref _lost) != 0) throw new InvalidOperationException(_lostReason ?? "Steam connection ended while joining.");
            _connected = true;
            _sending = SendLoopAsync();
            _phase = "starting the game handshake";
            _gameHandler.OnConnectedToHost();
            return null;
        }
        catch (OperationCanceledException error)
        {
            // A join or connect the server never answered may still finish there, so that session is not reused.
            _abandonedCommand = _commandPending;
            FailureDetail = "Join was cancelled while " + _phase + ". " + error.Message;
            FailureMessage = "Join was cancelled while " + _phase + ".";
            Close();
            return Error("CancelledJoin", true);
        }
        catch (Exception error)
        {
            _abandonedCommand = _commandPending;
            FailureDetail = "Failed while " + _phase + ": " + error;
            FailureMessage = "Failed while " + _phase + ": " + error.GetType().Name + ": " + error.Message;
            Close();
            return Error("UnknownNetworkError");
        }
    }

    public override void Update()
    {
        if (Volatile.Read(ref _lost) != 0 && _connected)
        {
            _connected = false;
            // The game shows its own disconnect error; keep the cause for the Steam Bridge panel.
            FailureMessage ??= "The connection to the host ended: " + _lostReason;
            FailureDetail ??= FailureMessage;
            _gameHandler.OnDisconnectedFromHost(_host, Error("UnknownNetworkError"));
            Close();
            return;
        }
        while (_connected && Volatile.Read(ref _lost) == 0 && _incoming.TryDequeue(out var packet))
        {
            Interlocked.Decrement(ref _queuedPackets);
            Interlocked.Add(ref _queuedBytes, -packet.Payload.Length);
            if (packet.PeerId != _host) { Lost("A packet arrived from someone other than the host."); continue; }
            _gameHandler.OnPacketReceived(_host, packet.Payload,
                packet.Reliable ? NetTransferMode.Reliable : NetTransferMode.Unreliable, packet.Channel);
        }
    }

    public override void SendMessageToHost(byte[] bytes, int length, NetTransferMode mode, int channel = 0)
    {
        if (!_connected || length <= 0 || length > bytes.Length || length > BridgePacket.MaxPayload
            || mode is not (NetTransferMode.Reliable or NetTransferMode.Unreliable)) { Lost("The game sent a packet the bridge cannot carry."); return; }
        // The audited vanilla SteamClient ignores its channel argument and sends raw bytes on the connection.
        var packet = new BridgePacket(_host, 0, mode == NetTransferMode.Reliable, bytes.AsSpan(0, length).ToArray());
        if (Interlocked.Add(ref _outgoingBytes, length) > MaxQueuedBytes || !_outgoing.Writer.TryWrite(packet))
            Lost("Packets for the host backed up past the bridge limit; the link to the server is too slow.");
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
        // Only the bridge's read loop adds packets, so the check and the counts below cannot interleave with another add.
        if (Volatile.Read(ref _queuedPackets) >= MaxQueuedPackets || Interlocked.Read(ref _queuedBytes) + packet.Payload.Length > MaxQueuedBytes)
        {
            // While the app is in the background the game reads nothing for as long as the player is away. The host's
            // heartbeats are unreliable and may be dropped; losing a reliable message would desynchronize the game.
            if (packet.Reliable) Lost("Host packets backed up past the bridge limit while the game was not reading them.");
            return;
        }
        Interlocked.Increment(ref _queuedPackets);
        Interlocked.Add(ref _queuedBytes, packet.Payload.Length);
        _incoming.Enqueue(packet);
    }

    private void OnBridgeEvent(BridgeNotification response)
    {
        string? type = response.GetProperty("type").GetString();
        if (type == "connection")
        {
            string? state = response.GetDataString("state");
            // The host's game states its reason here, for example "Player is not in the lobby!".
            string detail = response.GetDataString("detail") is { Length: > 0 } text ? ": " + text : "";
            if (state == "k_ESteamNetworkingConnectionState_Connected") _nativeConnected.Connected();
            else if (state == "k_ESteamNetworkingConnectionState_ClosedByPeer") Lost("The host closed the Steam connection" + detail + ".");
            else if (state == "k_ESteamNetworkingConnectionState_ProblemDetectedLocally") Lost("Steam lost the connection to the host" + detail + ".");
        }
        else if (type == "members")
        {
            foreach (BridgeMember member in response.GetMembers())
                if (ulong.TryParse(member.SteamId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
                    RuntimeServices.SetBridgePlayerName(id, member.Name);
        }
        else if (type == "transport_error") Lost("The server reported a transport error: " + response.GetDataString("reason"));
    }

    private void OnBridgeDisconnected() => Lost("Steam bridge connection ended. " + (_bridge.LastDisconnectReason ?? ""));

    private void Lost(string reason)
    {
        _lostReason ??= reason;
        Interlocked.Exchange(ref _lost, 1);
        _nativeConnected.Failed(_lostReason);
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var packet in _outgoing.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                await _bridge.SendPacketAsync(packet, _stop.Token).ConfigureAwait(false);
                Interlocked.Add(ref _outgoingBytes, -packet.Payload.Length);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or System.Net.WebSockets.WebSocketException)
        {
            Lost("Sending to the server failed: " + error.GetType().Name + ".");
        }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _connected = false;
        _stop.Cancel();
        _outgoing.Writer.TryComplete();
        _bridge.PacketReceived -= Receive;
        _bridge.EventReceived -= OnBridgeEvent;
        _bridge.Disconnected -= OnBridgeDisconnected;
        Released = ReleaseAsync();
    }

    private async Task ReleaseAsync()
    {
        try
        {
            try { await _sending; }
            catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or System.Net.WebSockets.WebSocketException) { }
            // A settled session is reused: the server leaves the lobby and closes the peer, and its Steam session stays
            // up so the next join needs no cold start. Otherwise the bridge closes, which ends the server session.
            if (!_abandonedCommand && _bridge.IsConnected)
            {
                try
                {
                    await _bridge.ExecuteAsync("disconnect");
                    KeptServerSession = true;
                    return;
                }
                catch (Exception error) when (error is InvalidOperationException or TimeoutException or OperationCanceledException
                    or System.Net.WebSockets.WebSocketException) { }
            }
            await _bridge.DisposeAsync();
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or System.Net.WebSockets.WebSocketException) { }
        finally { _stop.Dispose(); }
    }

    private static NetErrorInfo Error(string name, bool selfInitiated = false) => new(Enum.Parse<NetError>(name), selfInitiated);
}

internal sealed class SteamBridgeConnectionInitializer(BridgeClient bridge, ulong lobby) : IClientConnectionInitializer
{
    private SteamBridgeNetClient? _client;
    public string? FailureDetail => _client?.FailureDetail;
    public string? FailureMessage => _client?.FailureMessage;
    /// <summary>True while the game holds an open connection through this join.</summary>
    public bool InUse => _client?.IsConnected == true;
    /// <summary>True when the join is over and the server's Steam session stayed open for another one.</summary>
    public bool KeptServerSession => bridge.IsConnected && (_client == null || _client.KeptServerSession);
    public Task Released => _client?.Released ?? Task.CompletedTask;
    public PlatformType Platform => PlatformType.Steam;
    public ulong? GetHostId() => _client?.HostNetId is > 0 and var id ? id : null;
    public Task<NetErrorInfo?> Connect(INetClientGameService gameService, CancellationToken cancelToken = default)
    {
        _client = new SteamBridgeNetClient(gameService, bridge, lobby);
        gameService.Initialize(_client, PlatformType.Steam);
        return _client.ConnectAsync(cancelToken);
    }
    /// <summary>Completes once a connection the game no longer uses has been handed back or closed.</summary>
    public Task ReleaseIfUnusedAsync() => InUse ? Task.CompletedTask : Released;
}
