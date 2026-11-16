using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Steamworks;
using STS2Mobile.SteamBridge.Protocol;

namespace STS2Mobile.SteamBridge;

/// <summary>All Steam API calls and callbacks run on one dedicated thread. No game simulation runs here.</summary>
public sealed class SteamBackend(BridgeOptions options, ILogger<SteamBackend> logger) : BackgroundService, IBridgeBackend
{
    private readonly BlockingCollection<Action> _commands = new(128);
    private volatile BridgeStatus _status = new(false, "waiting_for_steam", "0", "", options.Transport);
    private Action<BridgeEvent>? _notify;
    private Action<BridgePacket>? _packet;
    private int _attached;
    private long _generation;
    private bool _initialized;
    private string? _lastInitError;
    private ulong _lobby;
    private ulong _peer;
    private int[] _channels = [0];
    private HSteamNetConnection _socket;
    private Callback<SteamNetConnectionStatusChangedCallback_t>? _socketStatus;
    private Callback<SteamNetworkingMessagesSessionRequest_t>? _messageRequest;
    private Callback<P2PSessionRequest_t>? _legacyRequest;
    private readonly List<CallResult<LobbyEnter_t>> _joins = [];

    public BridgeStatus Status => _status;

    public bool TryAttach(Action<BridgeEvent> notify, Action<BridgePacket> packet)
    {
        if (Interlocked.CompareExchange(ref _attached, 1, 0) != 0) return false;
        _notify = notify;
        _packet = packet;
        Interlocked.Increment(ref _generation);
        return true;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Factory.StartNew(() =>
    {
        DateTime nextAttempt = DateTime.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                for (int i = 0; i < 32 && _commands.TryTake(out var command); i++) command();
                if (!_initialized && DateTime.UtcNow >= nextAttempt)
                {
                    nextAttempt = DateTime.UtcNow.AddSeconds(5);
                    try
                    {
                        _initialized = SteamAPI.InitEx(out string reason) == ESteamAPIInitResult.k_ESteamAPIInitResult_OK;
                        if (!_initialized && reason != _lastInitError)
                        {
                            _lastInitError = reason;
                            logger.LogWarning("Steam initialization is waiting: {Reason}", reason);
                        }
                        if (_initialized) InstallCallbacks();
                    }
                    catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                    {
                        SetStatus(false, "native_library_unavailable");
                        logger.LogWarning("Steam initialization unavailable ({Kind}).", error.GetType().Name);
                    }
                }
                if (_initialized)
                {
                    SteamAPI.RunCallbacks();
                    bool online = SteamUser.BLoggedOn();
                    ulong id = online ? SteamUser.GetSteamID().m_SteamID : 0;
                    bool identityMatches = options.ExpectedSteamId == 0 || id == options.ExpectedSteamId;
                    bool owns = online && SteamApps.BIsSubscribedApp(new AppId_t(BridgeOptions.AppId));
                    bool ready = online && identityMatches && owns;
                    SetStatus(ready, !online ? "waiting_for_login" : !identityMatches ? "account_mismatch" : !owns ? "game_not_owned" : "ready",
                        id, online ? SteamFriends.GetPersonaName() : "");
                    if (ready)
                    {
                        try { PollPackets(); }
                        catch (InvalidOperationException)
                        {
                            _notify?.Invoke(new("transport_error", new { reason = "Native receive failed or exceeded the bridge limit." }));
                            Disconnect();
                        }
                    }
                    else if (_peer != 0 || _lobby != 0) Disconnect();
                }
                stoppingToken.WaitHandle.WaitOne(5);
            }
        }
        finally
        {
            if (_initialized)
            {
                Disconnect();
                foreach (var join in _joins) join.Cancel();
                _socketStatus?.Dispose();
                _messageRequest?.Dispose();
                _legacyRequest?.Dispose();
                SteamAPI.Shutdown();
            }
            _initialized = false;
            SetStatus(false, "stopping");
            _commands.CompleteAdding();
            while (_commands.TryTake(out var remaining)) remaining();
        }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public Task<object?> ExecuteAsync(BridgeCommand command, CancellationToken cancellationToken)
    {
        if (command.Type == "status") return Task.FromResult<object?>(Status);
        if (command.Type == "join") return JoinAsync(ulong.Parse(command.LobbyId!, CultureInfo.InvariantCulture), cancellationToken);
        return InvokeAsync<object?>(() =>
        {
            RequireReady();
            return command.Type switch
            {
                "friends" => Friends(),
                "connect" => Connect(command),
                "disconnect" => Disconnect(),
                _ => throw new InvalidOperationException("Unsupported operation."),
            };
        }, cancellationToken);
    }

    public Task SendAsync(BridgePacket packet, CancellationToken cancellationToken) => InvokeAsync<object?>(() =>
    {
        RequireReady();
        if (_peer == 0 || packet.PeerId != _peer || !_channels.Contains(packet.Channel))
            throw new InvalidOperationException("Packets may only reach the joined lobby's host on configured channels.");
        bool sent;
        unsafe
        {
            fixed (byte* pointer = packet.Payload)
            {
                var identity = Identity(_peer);
                int flags = packet.Reliable ? Constants.k_nSteamNetworkingSend_Reliable : Constants.k_nSteamNetworkingSend_Unreliable;
                sent = options.Transport switch
                {
                    "legacy" => SteamNetworking.SendP2PPacket(new CSteamID(_peer), packet.Payload, (uint)packet.Payload.Length,
                        packet.Reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliable, packet.Channel),
                    "messages" => SteamNetworkingMessages.SendMessageToUser(ref identity, (nint)pointer, (uint)packet.Payload.Length, flags, packet.Channel) == EResult.k_EResultOK,
                    "sockets" when packet.Channel == 0 => SteamNetworkingSockets.SendMessageToConnection(_socket, (nint)pointer,
                        (uint)packet.Payload.Length, flags, out _) == EResult.k_EResultOK,
                    _ => false,
                };
            }
        }
        if (!sent) throw new InvalidOperationException("Steam refused the packet; verify the audited transport and channel mapping.");
        return null;
    }, cancellationToken);

    public async Task DetachAsync()
    {
        try
        {
            await InvokeAsync<object?>(() =>
            {
                Interlocked.Increment(ref _generation);
                if (_initialized) Disconnect();
                return null;
            }, CancellationToken.None);
        }
        finally
        {
            _notify = null;
            _packet = null;
            Interlocked.Exchange(ref _attached, 0);
        }
    }

    private async Task<object?> JoinAsync(ulong lobby, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await InvokeAsync<object?>(() =>
        {
            RequireReady();
            if (_joins.Count != 0) throw new InvalidOperationException("A lobby join is already pending.");
            Disconnect();
            long generation = Interlocked.Read(ref _generation);
            CallResult<LobbyEnter_t>? result = null;
            result = CallResult<LobbyEnter_t>.Create((entered, failed) =>
            {
                _joins.Remove(result!);
                bool ok = !failed && entered.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess;
                // A timeout or phone disconnect must not leave an orphaned Steam lobby membership.
                if (ct.IsCancellationRequested || generation != Interlocked.Read(ref _generation))
                {
                    if (ok) SteamMatchmaking.LeaveLobby(new CSteamID(entered.m_ulSteamIDLobby));
                    answer.TrySetCanceled(new CancellationToken(true));
                }
                else if (!ok) answer.TrySetException(new InvalidOperationException("Steam refused the lobby join."));
                else
                {
                    _lobby = entered.m_ulSteamIDLobby;
                    ulong owner = SteamMatchmaking.GetLobbyOwner(new CSteamID(_lobby)).m_SteamID;
                    answer.TrySetResult(new { lobbyId = _lobby.ToString(CultureInfo.InvariantCulture), ownerId = owner.ToString(CultureInfo.InvariantCulture) });
                }
            });
            _joins.Add(result);
            result.Set(SteamMatchmaking.JoinLobby(new CSteamID(lobby)));
            return null;
        }, ct);
        return await answer.Task.WaitAsync(ct);
    }

    private object Friends()
    {
        var friends = new List<object>();
        for (int i = 0; i < SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate); i++)
        {
            CSteamID id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
            if (!SteamFriends.GetFriendGamePlayed(id, out FriendGameInfo_t game) || game.m_gameID.AppID().m_AppId != BridgeOptions.AppId) continue;
            friends.Add(new { steamId = id.m_SteamID.ToString(CultureInfo.InvariantCulture), name = SteamFriends.GetFriendPersonaName(id),
                lobbyId = game.m_steamIDLobby.m_SteamID.ToString(CultureInfo.InvariantCulture) });
        }
        return friends;
    }

    private object Connect(BridgeCommand command)
    {
        if (options.Transport == "disabled") throw new InvalidOperationException("Gameplay forwarding is disabled until the desktop transport is audited.");
        if (_lobby == 0 || _peer != 0) throw new InvalidOperationException("Join a lobby before connecting, with no existing connection.");
        _channels = command.Channels is { Length: > 0 } ? command.Channels.Distinct().ToArray() : [0];
        if (options.Transport == "sockets" && !_channels.SequenceEqual([0]))
            throw new InvalidOperationException("Socket channel framing must be audited before using nonzero channels.");
        _peer = SteamMatchmaking.GetLobbyOwner(new CSteamID(_lobby)).m_SteamID;
        if (_peer == 0 || _peer == SteamUser.GetSteamID().m_SteamID)
        {
            _peer = 0;
            throw new InvalidOperationException("This prototype only joins games hosted by another player.");
        }
        SteamNetworkingUtils.InitRelayNetworkAccess();
        if (options.Transport == "sockets")
        {
            var identity = Identity(_peer);
            _socket = SteamNetworkingSockets.ConnectP2P(ref identity, command.VirtualPort, 0, []);
            if (_socket == HSteamNetConnection.Invalid)
            {
                _peer = 0;
                throw new InvalidOperationException("Steam could not create a peer connection.");
            }
        }
        // Opening a native transport is distinct from completing the game's handshake.
        return new { peerId = _peer.ToString(CultureInfo.InvariantCulture), transport = options.Transport, state = "transport_opened" };
    }

    private object? Disconnect()
    {
        Interlocked.Increment(ref _generation);
        if (_socket != HSteamNetConnection.Invalid)
            SteamNetworkingSockets.CloseConnection(_socket, 0, "Bridge disconnected.", false);
        if (_peer != 0)
        {
            if (options.Transport == "legacy") SteamNetworking.CloseP2PSessionWithUser(new CSteamID(_peer));
            else if (options.Transport == "messages")
            {
                var identity = Identity(_peer);
                SteamNetworkingMessages.CloseSessionWithUser(ref identity);
            }
        }
        _socket = HSteamNetConnection.Invalid;
        _peer = 0;
        if (_lobby != 0) SteamMatchmaking.LeaveLobby(new CSteamID(_lobby));
        _lobby = 0;
        return null;
    }

    private void InstallCallbacks()
    {
        _socketStatus = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(change =>
        {
            if (change.m_hConn != _socket) return;
            _notify?.Invoke(new("connection", new { state = change.m_info.m_eState.ToString(), reason = change.m_info.m_eEndReason }));
            if (change.m_info.m_eState is ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
                or ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally) Disconnect();
        });
        _messageRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(request =>
        {
            var peer = request.m_identityRemote;
            if (options.Transport == "messages" && _peer != 0 && peer.GetSteamID64() == _peer)
                SteamNetworkingMessages.AcceptSessionWithUser(ref peer);
        });
        _legacyRequest = Callback<P2PSessionRequest_t>.Create(request =>
        {
            if (options.Transport == "legacy" && _peer != 0 && request.m_steamIDRemote.m_SteamID == _peer)
                SteamNetworking.AcceptP2PSessionWithUser(request.m_steamIDRemote);
        });
    }

    private void PollPackets()
    {
        if (_peer == 0) return;
        if (options.Transport == "legacy")
        {
            foreach (int channel in _channels)
                for (int n = 0; n < 32 && SteamNetworking.IsP2PPacketAvailable(out uint size, channel); n++)
                {
                    if (size is 0 or > BridgePacket.MaxPayload) throw new InvalidOperationException("Steam packet exceeds bridge limit.");
                    byte[] buffer = new byte[size];
                    if (SteamNetworking.ReadP2PPacket(buffer, size, out uint read, out CSteamID remote, channel) && remote.m_SteamID == _peer)
                        // Legacy receive does not expose reliability; that mapping needs validation against the game.
                        _packet?.Invoke(new(_peer, channel, true, buffer.AsSpan(0, (int)read).ToArray()));
                }
            return;
        }
        nint[] messages = new nint[32];
        foreach (int channel in options.Transport == "sockets" ? [0] : _channels)
        {
            int count = options.Transport == "sockets"
                ? SteamNetworkingSockets.ReceiveMessagesOnConnection(_socket, messages, messages.Length)
                : SteamNetworkingMessages.ReceiveMessagesOnChannel(channel, messages, messages.Length);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var message = SteamNetworkingMessage_t.FromIntPtr(messages[i]);
                    if (message.m_identityPeer.GetSteamID64() != _peer || message.m_cbSize is <= 0 or > BridgePacket.MaxPayload) continue;
                    byte[] payload = new byte[message.m_cbSize];
                    Marshal.Copy(message.m_pData, payload, 0, payload.Length);
                    _packet?.Invoke(new(_peer, channel, (message.m_nFlags & Constants.k_nSteamNetworkingSend_Reliable) != 0, payload));
                }
                finally { SteamNetworkingMessage_t.Release(messages[i]); }
            }
        }
    }

    private void RequireReady()
    {
        if (!Status.Ready || Volatile.Read(ref _attached) == 0) throw new InvalidOperationException("Steam is not ready for this phone session.");
    }

    private Task<T> InvokeAsync<T>(Func<T> action, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (_commands.IsAddingCompleted || !_commands.TryAdd(() =>
            {
                if (ct.IsCancellationRequested) { completion.TrySetCanceled(ct); return; }
                try { completion.TrySetResult(action()); } catch (Exception error) { completion.TrySetException(error); }
            })) completion.TrySetException(new InvalidOperationException("Steam command queue is unavailable."));
        }
        catch (InvalidOperationException) { completion.TrySetException(new InvalidOperationException("Steam is stopping.")); }
        return completion.Task.WaitAsync(ct);
    }

    private void SetStatus(bool ready, string state, ulong id = 0, string name = "")
    {
        var next = new BridgeStatus(ready, state, id.ToString(CultureInfo.InvariantCulture), name, options.Transport);
        if (next == _status) return;
        _status = next;
        _notify?.Invoke(new("status", next));
    }

    private static SteamNetworkingIdentity Identity(ulong id)
    {
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID64(id);
        return identity;
    }
}
