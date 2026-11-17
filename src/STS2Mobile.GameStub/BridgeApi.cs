// Multiplayer transport signatures inspected in the owner's desktop build and QuickMatch's public adapter.
// Compile-time declarations only; never distributed as game implementation.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MegaCrit.Sts2.Core.Platform
{
    public enum PlatformType { None = 0, Steam = 1 }
}

namespace MegaCrit.Sts2.Core.Entities.Multiplayer
{
    // Errors are resolved by name at runtime: the game renumbered these between versions.
    public enum NetError { }
    public struct NetErrorInfo
    {
        public NetErrorInfo(NetError reason, bool selfInitiated = false) => throw null;
    }
}

namespace MegaCrit.Sts2.Core.Multiplayer.Transport
{
    using Entities.Multiplayer;
    using Platform;
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
        protected NetClient(INetClientHandler handler) => throw null;
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
    using Entities.Multiplayer;
    using Platform;
    public interface IClientConnectionInitializer
    {
        PlatformType Platform { get; }
        ulong? GetHostId();
        Task<NetErrorInfo?> Connect(Game.INetClientGameService gameService, CancellationToken cancelToken = default);
    }
}
