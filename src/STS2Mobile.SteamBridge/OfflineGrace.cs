namespace STS2Mobile.SteamBridge;

/// <summary>Rides out a brief loss of Steam's server connection, which an established peer connection can survive.</summary>
public sealed class OfflineGrace(TimeSpan limit)
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(20);
    private DateTime? _since;

    public TimeSpan Limit => limit;

    /// <summary>True while a ready session should keep its readiness although Steam reports it is offline.</summary>
    public bool Holds(bool online, bool ready, DateTime now)
    {
        if (online || !ready)
        {
            _since = null;
            return false;
        }
        _since ??= now;
        return now - _since.Value < limit;
    }
}
