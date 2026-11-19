using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Steamworks;
using STS2Mobile.SteamBridge.Protocol;

namespace STS2Mobile.SteamBridge;

/// <summary>All Steam API calls and callbacks run on one dedicated thread. No game simulation runs here.</summary>
public sealed class SteamBackend(BridgeOptions options, ILogger<SteamBackend> logger, SteamDesktop desktop, IHostApplicationLifetime lifetime) : BackgroundService, IBridgeBackend
{
    private readonly BlockingCollection<Action> _commands = new(128);
    // Phone packets bypass the command queue: queueing one costs a channel write, not a round trip to the Steam thread.
    private readonly Channel<BridgePacket> _sends = Channel.CreateBounded<BridgePacket>(new BoundedChannelOptions(1024) { SingleReader = true });
    private readonly AutoResetEvent _wake = new(false);
    private readonly nint[] _received = new nint[64];
    private readonly OfflineGrace _offline = new(OfflineGrace.Default);
    private bool _holdingOffline;
    private volatile BridgeStatus _status = new(false, "idle", "0", "", options.Transport);
    private Action<BridgeEvent>? _notify;
    private Action<BridgePacket>? _packet;
    private int _attached;
    private int _steamRequested;
    private long _generation;
    private bool _initialized;
    private string? _lastInitError;
    private ulong _lobby;
    private ulong _peer;
    private int[] _channels = [0];
    private HSteamNetConnection _socket;
    private long _sentPackets, _receivedPackets, _droppedPackets;
    private Callback<SteamNetConnectionStatusChangedCallback_t>? _socketStatus;
    private Callback<SteamNetworkingMessagesSessionRequest_t>? _messageRequest;
    private Callback<P2PSessionRequest_t>? _legacyRequest;
    private readonly List<CallResult<LobbyEnter_t>> _joins = [];
    private readonly SteamFriendDirectory _friends = new();
    private readonly Dictionary<ulong, DateTime> _presenceRequests = [];
    private readonly Dictionary<ulong, DateTime> _nameRequests = [];
    private BridgeFriend[] _publishedFriends = [];
    private BridgeMember[] _publishedMembers = [];
    private DateTime _nextFriendsUpdate;
    private DateTime _nextMembersUpdate;
    private Callback<LobbyInvite_t>? _lobbyInvite;
    private Callback<GameLobbyJoinRequested_t>? _joinRequested;
    private Callback<PersonaStateChange_t>? _personaChange;
    private Callback<LobbyChatUpdate_t>? _lobbyChange;

    public BridgeStatus Status => _status;

    public bool TryAttach(Action<BridgeEvent> notify, Action<BridgePacket> packet)
    {
        if (Interlocked.CompareExchange(ref _attached, 1, 0) != 0) return false;
        _notify = notify;
        _packet = packet;
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _steamRequested, 1);
        _wake.Set();
        return true;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Factory.StartNew(() =>
    {
        DateTime nextAttempt = DateTime.MinValue;
        WaitHandle[] wakeups = [_wake, stoppingToken.WaitHandle];
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                for (int i = 0; i < 32 && _commands.TryTake(out var command); i++) command();
                for (int i = 0; i < 256 && _sends.Reader.TryRead(out var packet); i++) Send(packet);
                if (Volatile.Read(ref _steamRequested) == 0)
                {
                    if (_initialized) ShutdownSteam();
                    SetStatus(false, "idle");
                    WaitHandle.WaitAny(wakeups, 20);
                    continue;
                }
                if (!_initialized && DateTime.UtcNow >= nextAttempt)
                {
                    nextAttempt = DateTime.UtcNow.AddSeconds(1);
                    try
                    {
                        SetStatus(false, "starting_steam");
                        desktop.Start();
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
                if (_initialized) Tick();
                // Commands and phone packets wake this thread at once; an open peer connection is polled more often.
                WaitHandle.WaitAny(wakeups, _peer != 0 ? 1 : 5);
            }
        }
        finally
        {
            ShutdownSteam();
            _initialized = false;
            SetStatus(false, "stopping");
            _commands.CompleteAdding();
            _sends.Writer.TryComplete();
            while (_commands.TryTake(out var remaining)) remaining();
        }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Tick()
    {
        SteamAPI.RunCallbacks();
        bool online = SteamUser.BLoggedOn();
        bool holding = _offline.Holds(online, _status.Ready, DateTime.UtcNow);
        if (holding != _holdingOffline)
        {
            _holdingOffline = holding;
            if (holding) logger.LogWarning("Steam lost its server connection; keeping the session for up to {Seconds} seconds.", _offline.Limit.TotalSeconds);
            else if (online) logger.LogInformation("Steam reconnected to its servers within the grace period.");
            else logger.LogWarning("Steam stayed offline past the grace period; ending the session.");
        }
        if (holding)
        {
            Service();
            return;
        }
        ulong id = online ? SteamUser.GetSteamID().m_SteamID : 0;
        bool identityMatches = options.ExpectedSteamId == 0 || id == options.ExpectedSteamId;
        bool owns = online && SteamApps.BIsSubscribedApp(new AppId_t(BridgeOptions.AppId));
        bool ready = online && identityMatches && owns;
        SetStatus(ready, !online ? "waiting_for_login" : !identityMatches ? "account_mismatch" : !owns ? "game_not_owned" : "ready",
            id, online ? SteamFriends.GetPersonaName() : "");
        if (ready) Service();
        else if (_peer != 0 || _lobby != 0) Disconnect();
    }

    private void Service()
    {
        try
        {
            PollPackets();
            // Friend presence only feeds the phone's join menu; a connected game has no use for it.
            if (_peer == 0 && DateTime.UtcNow >= _nextFriendsUpdate)
            {
                _nextFriendsUpdate = DateTime.UtcNow.AddSeconds(2);
                var snapshot = FriendsSnapshot();
                if (!_publishedFriends.SequenceEqual(snapshot))
                {
                    _publishedFriends = snapshot;
                    _notify?.Invoke(new("friends", snapshot));
                }
            }
            // The phone's game names other players through these; it has no Steam client of its own.
            if (_lobby != 0 && DateTime.UtcNow >= _nextMembersUpdate)
            {
                _nextMembersUpdate = DateTime.UtcNow.AddSeconds(2);
                var members = MembersSnapshot();
                if (!_publishedMembers.SequenceEqual(members))
                {
                    _publishedMembers = members;
                    _notify?.Invoke(new("members", members));
                }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            logger.LogWarning("Closing the Steam peer transport: {Reason}", error.Message);
            _notify?.Invoke(new("transport_error", new { reason = "Native receive failed or exceeded the bridge limit." }));
            Disconnect();
        }
    }

    public Task<object?> ExecuteAsync(BridgeCommand command, CancellationToken cancellationToken)
    {
        logger.LogInformation("Bridge operation: {Operation}.", command.Type);
        if (command.Type == "status") return Task.FromResult<object?>(Status);
        if (command.Type == "friends") return FriendsAsync(cancellationToken);
        if (command.Type == "join") return JoinAsync(ulong.Parse(command.LobbyId!, CultureInfo.InvariantCulture), cancellationToken);
        return InvokeAsync<object?>(() =>
        {
            RequireReady();
            return command.Type switch
            {
                "connect" => Connect(command),
                "disconnect" => Disconnect(),
                _ => throw new InvalidOperationException("Unsupported operation."),
            };
        }, cancellationToken);
    }

    /// <summary>Completes once the packet is queued; the Steam thread sends queued packets in arrival order.</summary>
    public async Task SendAsync(BridgePacket packet, CancellationToken cancellationToken)
    {
        await _sends.Writer.WriteAsync(packet, cancellationToken);
        _wake.Set();
    }

    private void Send(BridgePacket packet)
    {
        // Packets for any other peer or channel never reach Steam. Like the game's own SteamClient, a packet that Steam
        // refuses is dropped rather than ending the session.
        if (_peer == 0 || packet.PeerId != _peer || !_channels.Contains(packet.Channel))
        {
            Drop("no open connection to that host and channel");
            return;
        }
        string? failure;
        unsafe
        {
            fixed (byte* pointer = packet.Payload)
            {
                var identity = Identity(_peer);
                int flags = packet.Reliable ? Constants.k_nSteamNetworkingSend_Reliable : Constants.k_nSteamNetworkingSend_Unreliable;
                failure = options.Transport switch
                {
                    "legacy" => SteamNetworking.SendP2PPacket(new CSteamID(_peer), packet.Payload, (uint)packet.Payload.Length,
                        packet.Reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliable, packet.Channel) ? null : "SendP2PPacket failed",
                    "messages" => Failure(SteamNetworkingMessages.SendMessageToUser(ref identity, (nint)pointer, (uint)packet.Payload.Length, flags, packet.Channel)),
                    "sockets" => Failure(SteamNetworkingSockets.SendMessageToConnection(_socket, (nint)pointer, (uint)packet.Payload.Length, flags, out _)),
                    _ => "transport disabled",
                };
            }
        }
        if (failure != null) Drop("Steam refused it: " + failure);
        else if (++_sentPackets == 1) logger.LogInformation("First phone packet forwarded: bytes={Bytes}, reliable={Reliable}, channel={Channel}.", packet.Payload.Length, packet.Reliable, packet.Channel);

        static string? Failure(EResult result) => result == EResult.k_EResultOK ? null : result.ToString();
    }

    private void Drop(string reason)
    {
        // The first drop and every hundredth after it are logged, so a flood of refusals cannot fill the log.
        if (++_droppedPackets % 100 == 1) logger.LogWarning("Dropped phone packet {Count}: {Reason}.", _droppedPackets, reason);
    }

    public async Task DetachAsync()
    {
        try
        {
            await InvokeAsync<object?>(() =>
            {
                Interlocked.Increment(ref _generation);
                Volatile.Write(ref _steamRequested, 0);
                ShutdownSteam();
                while (_sends.Reader.TryRead(out _)) { }
                SetStatus(false, "idle");
                return null;
            }, CancellationToken.None);
        }
        finally
        {
            _notify = null;
            _packet = null;
            Interlocked.Exchange(ref _attached, 0);
            if (options.ExitAfterSession) lifetime.StopApplication();
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
                else if (!ok)
                {
                    string reason = LobbyRefusal.Describe(failed, entered.m_EChatRoomEnterResponse);
                    logger.LogWarning("Steam refused the lobby join: ioFailure={IoFailure}, result={Result} ({Reason}).", failed, entered.m_EChatRoomEnterResponse, reason);
                    answer.TrySetException(new InvalidOperationException("Steam refused the lobby join: " + reason + "."));
                }
                else
                {
                    logger.LogInformation("Steam lobby join succeeded.");
                    _lobby = entered.m_ulSteamIDLobby;
                    _nextMembersUpdate = DateTime.MinValue;
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

    private async Task<object?> FriendsAsync(CancellationToken ct) =>
        await FriendDiscovery.WaitForLobbiesAsync(token => InvokeAsync(() =>
        {
            RequireReady();
            return FriendsSnapshot(requestPresence: true);
        }, token), TimeSpan.FromSeconds(8), ct);

    private BridgeFriend[] FriendsSnapshot(bool requestPresence = false)
    {
        var presence = new List<FriendPresence>();
        int count = Math.Min(SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate), 512);
        for (int i = 0; i < count; i++)
        {
            CSteamID id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
            bool playing = SteamFriends.GetFriendGamePlayed(id, out FriendGameInfo_t game)
                && game.m_gameID.AppID().m_AppId == BridgeOptions.AppId;
            if (requestPresence && (!_presenceRequests.TryGetValue(id.m_SteamID, out DateTime requested)
                || DateTime.UtcNow - requested >= TimeSpan.FromSeconds(30)))
            {
                SteamFriends.RequestUserInformation(id, true);
                SteamFriends.RequestFriendRichPresence(id);
                _presenceRequests[id.m_SteamID] = DateTime.UtcNow;
            }
            presence.Add(new(id.m_SteamID, Name(id), playing, playing ? game.m_steamIDLobby.m_SteamID : 0));
        }
        return _friends.Snapshot(presence, DateTime.UtcNow);
    }

    private BridgeMember[] MembersSnapshot()
    {
        var lobby = new CSteamID(_lobby);
        int count = Math.Min(SteamMatchmaking.GetNumLobbyMembers(lobby), 16);
        var members = new BridgeMember[count];
        for (int i = 0; i < count; i++)
        {
            CSteamID id = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
            members[i] = new(id.m_SteamID.ToString(CultureInfo.InvariantCulture), Name(id));
        }
        return members;
    }

    private string Name(CSteamID id)
    {
        bool self = id == SteamUser.GetSteamID();
        string persona = self ? SteamFriends.GetPersonaName() : SteamFriends.GetFriendPersonaName(id);
        // A freshly started Steam client reports "[unknown]" until a persona arrives. Ask for it; PersonaStateChange_t
        // then refreshes the friend and member lists.
        if (SteamNames.IsUnknown(persona) && !self && (!_nameRequests.TryGetValue(id.m_SteamID, out DateTime asked)
            || DateTime.UtcNow - asked >= TimeSpan.FromSeconds(5)))
        {
            SteamFriends.RequestUserInformation(id, true);
            _nameRequests[id.m_SteamID] = DateTime.UtcNow;
        }
        return SteamNames.Display(persona, self ? null : SteamFriends.GetPlayerNickname(id), id.m_SteamID);
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
        _sentPackets = _receivedPackets = _droppedPackets = 0;
        logger.LogInformation("Opening Steam peer transport: kind={Transport}, virtualPort={Port}.", options.Transport, command.VirtualPort);
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
            logger.LogInformation("Closing Steam peer transport: sentPackets={Sent}, receivedPackets={Received}, droppedPackets={Dropped}.",
                _sentPackets, _receivedPackets, _droppedPackets);
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
        _publishedMembers = [];
        return null;
    }

    private void InstallCallbacks()
    {
        _lobbyInvite = Callback<LobbyInvite_t>.Create(invite =>
        {
            if (new CGameID(invite.m_ulGameID).AppID().m_AppId != BridgeOptions.AppId) return;
            _friends.Invite(invite.m_ulSteamIDUser, invite.m_ulSteamIDLobby, DateTime.UtcNow);
            _nextFriendsUpdate = DateTime.MinValue;
        });
        _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(invite =>
        {
            _friends.Invite(invite.m_steamIDFriend.m_SteamID, invite.m_steamIDLobby.m_SteamID, DateTime.UtcNow);
            _nextFriendsUpdate = DateTime.MinValue;
        });
        _personaChange = Callback<PersonaStateChange_t>.Create(_ =>
        {
            _nextFriendsUpdate = DateTime.MinValue;
            _nextMembersUpdate = DateTime.MinValue;
        });
        _lobbyChange = Callback<LobbyChatUpdate_t>.Create(change =>
        {
            if (change.m_ulSteamIDLobby == _lobby) _nextMembersUpdate = DateTime.MinValue;
        });
        _socketStatus = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(change =>
        {
            if (change.m_hConn != _socket) return;
            // The host's game explains its closes here, for example "Player is not in the lobby!".
            string detail = change.m_info.m_szEndDebug;
            logger.LogInformation("Steam peer state: {State}, endReason={Reason}, detail={Detail}.", change.m_info.m_eState, change.m_info.m_eEndReason, detail);
            _notify?.Invoke(new("connection", new { state = change.m_info.m_eState.ToString(), reason = change.m_info.m_eEndReason, detail }));
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
        foreach (int channel in options.Transport == "sockets" ? [0] : _channels)
        {
            int count = options.Transport == "sockets"
                ? SteamNetworkingSockets.ReceiveMessagesOnConnection(_socket, _received, _received.Length)
                : SteamNetworkingMessages.ReceiveMessagesOnChannel(channel, _received, _received.Length);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var message = SteamNetworkingMessage_t.FromIntPtr(_received[i]);
                    if (message.m_identityPeer.GetSteamID64() != _peer) continue;
                    if (message.m_cbSize is <= 0 or > BridgePacket.MaxPayload
                        || NativeChannels.ForBridge(options.Transport, message.m_nChannel, _channels) is not int packetChannel)
                        throw new InvalidOperationException($"Steam packet refused: bytes={message.m_cbSize}, channel={message.m_nChannel}.");
                    byte[] payload = new byte[message.m_cbSize];
                    Marshal.Copy(message.m_pData, payload, 0, payload.Length);
                    if (++_receivedPackets == 1) logger.LogInformation("First host packet received: bytes={Bytes}, flags={Flags}, channel={Channel}.", payload.Length, message.m_nFlags, message.m_nChannel);
                    _packet?.Invoke(new(_peer, packetChannel, (message.m_nFlags & Constants.k_nSteamNetworkingSend_Reliable) != 0, payload));
                }
            }
            finally
            {
                // Steam transfers ownership of the whole batch, including entries after a rejected packet.
                for (int i = 0; i < count; i++) SteamNetworkingMessage_t.Release(_received[i]);
            }
        }
    }

    private void ShutdownSteam()
    {
        if (_initialized)
        {
            Disconnect();
            foreach (var join in _joins) join.Cancel();
            _joins.Clear();
            _lobbyInvite?.Dispose(); _lobbyInvite = null;
            _joinRequested?.Dispose(); _joinRequested = null;
            _personaChange?.Dispose(); _personaChange = null;
            _lobbyChange?.Dispose(); _lobbyChange = null;
            _socketStatus?.Dispose(); _socketStatus = null;
            _messageRequest?.Dispose(); _messageRequest = null;
            _legacyRequest?.Dispose(); _legacyRequest = null;
            SteamAPI.Shutdown();
            _initialized = false;
            _holdingOffline = false;
        }
        desktop.Stop();
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
            else _wake.Set();
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
