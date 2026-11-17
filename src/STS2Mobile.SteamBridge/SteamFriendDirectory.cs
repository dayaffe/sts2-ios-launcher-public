using System.Globalization;

namespace STS2Mobile.SteamBridge;

public sealed record FriendPresence(ulong SteamId, string Name, bool Playing, ulong LobbyId);
public sealed record BridgeFriend(string SteamId, string Name, string LobbyId, bool Invited = false);

/// <summary>Merge asynchronously populated presence with explicit Steam invitations, including private lobbies.</summary>
public sealed class SteamFriendDirectory
{
    private sealed record Invitation(ulong Friend, ulong Lobby, DateTime Expires);
    private readonly Dictionary<ulong, Invitation> _invitations = [];

    public void Invite(ulong friend, ulong lobby, DateTime now)
    {
        if (lobby == 0) return;
        if (_invitations.Count >= 64 && !_invitations.ContainsKey(lobby))
            _invitations.Remove(_invitations.MinBy(pair => pair.Value.Expires).Key);
        _invitations[lobby] = new(friend, lobby, now.AddMinutes(5));
    }

    public BridgeFriend[] Snapshot(IReadOnlyList<FriendPresence> presence, DateTime now)
    {
        foreach (var expired in _invitations.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) _invitations.Remove(expired);
        var rows = new List<BridgeFriend>();
        foreach (var friend in presence.Where(f => f.Playing).Take(64))
            rows.Add(Row(friend.SteamId, friend.Name, friend.LobbyId, false));
        foreach (var invite in _invitations.Values)
        {
            var person = presence.FirstOrDefault(p => p.SteamId == invite.Friend);
            rows.RemoveAll(row => row.LobbyId == invite.Lobby.ToString(CultureInfo.InvariantCulture)
                || row.SteamId == invite.Friend.ToString(CultureInfo.InvariantCulture) && row.LobbyId == "0");
            rows.Add(Row(invite.Friend, person?.Name ?? "Steam invitation", invite.Lobby, true));
        }
        return rows.Take(64).ToArray();
    }

    private static BridgeFriend Row(ulong id, string name, ulong lobby, bool invited) =>
        new(id.ToString(CultureInfo.InvariantCulture), name[..Math.Min(name.Length, 128)], lobby.ToString(CultureInfo.InvariantCulture), invited);
}

public static class FriendDiscovery
{
    public static async Task<BridgeFriend[]> WaitForLobbiesAsync(Func<CancellationToken, Task<BridgeFriend[]>> read,
        TimeSpan budget, CancellationToken ct, TimeSpan? pollInterval = null)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await read(ct).ConfigureAwait(false);
            if (snapshot.Any(friend => friend.LobbyId != "0") || System.Diagnostics.Stopwatch.GetElapsedTime(start) >= budget)
                return snapshot;
            await Task.Delay(pollInterval ?? TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }
    }
}
