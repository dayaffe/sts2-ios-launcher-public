namespace STS2Mobile.SteamBridge;

/// <summary>Maps a received native message's channel onto the bridge packet channel.</summary>
public static class NativeChannels
{
    /// <summary>Returns null when the message arrived on a channel this session does not accept.</summary>
    public static int? ForBridge(string transport, int nativeChannel, IReadOnlyCollection<int> channels)
    {
        // Steam allocates every message with m_nChannel = -1 and only ISteamNetworkingMessages assigns one, so
        // connection messages always report -1. The game's SteamClient ignores the channel; the bridge uses 0.
        if (transport == "sockets") return 0;
        return channels.Contains(nativeChannel) ? nativeChannel : null;
    }
}
