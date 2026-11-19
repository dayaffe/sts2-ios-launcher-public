// Minimal game API doubles for exercising the production adapter without game files.
using System;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Platform;

namespace MegaCrit.Sts2.Core.Platform
{
    public enum PlatformType { None = 0, Steam = 1 }
}

namespace MegaCrit.Sts2.Core.Entities.Multiplayer
{
    // Errors are resolved by name at runtime: the game renumbered these between versions.
    public enum NetError { Quit, CancelledJoin, UnknownNetworkError }
    public struct NetErrorInfo
    {
        public NetError Reason;
        public NetErrorInfo(NetError reason, bool selfInitiated = false) => Reason = reason;
    }
}

namespace MegaCrit.Sts2.Core.Multiplayer.Transport
{
    public enum NetTransferMode { None = 0, Unreliable = 1, Reliable = 2 }
    public interface INetHandler
    {
        void OnPacketReceived(ulong senderId, byte[] bytes, NetTransferMode mode, int channel);
    }
    public interface INetClientHandler : INetHandler
    {
        void Initialize(NetClient client, PlatformType platform);
        void OnConnectedToHost();
        void OnDisconnectedFromHost(ulong peer, NetErrorInfo info);
    }
    public abstract class NetClient
    {
        protected NetClient(INetClientHandler handler) { }
        public abstract bool IsConnected { get; }
        public abstract ulong NetId { get; }
        public abstract ulong HostNetId { get; }
        public abstract void Update();
        public abstract void SendMessageToHost(byte[] bytes, int length, NetTransferMode mode, int channel = 0);
        public abstract void DisconnectFromHost(NetError reason, bool now = false);
        public abstract string GetRawLobbyIdentifier();
    }
}

namespace MegaCrit.Sts2.Core.Multiplayer.Game
{
    public interface INetClientGameService : Transport.INetClientHandler { }
}

namespace MegaCrit.Sts2.Core.Multiplayer.Connection
{
    public interface IClientConnectionInitializer
    {
        PlatformType Platform { get; }
        ulong? GetHostId();
        Task<NetErrorInfo?> Connect(Game.INetClientGameService gameService, CancellationToken cancelToken = default);
    }
}

namespace STS2Mobile.Platform
{
    internal static class RuntimeServices
    {
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, string> BridgeNames = new();
        public static ulong GetLocalPlayerId() => 76561198000000001;
        public static void SetBridgePlayerName(ulong playerId, string name) => BridgeNames[playerId] = name;
        public static void ClearBridgePlayerNames() => BridgeNames.Clear();
    }
}
