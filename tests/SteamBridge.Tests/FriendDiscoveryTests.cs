namespace STS2Mobile.SteamBridge.Tests;

public sealed class FriendDiscoveryTests
{
    [Fact]
    public async Task EmptyColdCacheIsPolledUntilTheHostAppears()
    {
        int reads = 0;
        var host = new BridgeFriend("71", "Friend", "109775240000000001");
        var result = await FriendDiscovery.WaitForLobbiesAsync(_ => Task.FromResult(++reads < 3 ? Array.Empty<BridgeFriend>() : new[] { host }),
            TimeSpan.FromSeconds(2), CancellationToken.None, TimeSpan.FromMilliseconds(1));
        Assert.Equal(3, reads);
        Assert.Equal(host, Assert.Single(result));
    }

    [Fact]
    public void PrivateLobbyInvitationDoesNotRequirePublishedGamePresence()
    {
        var directory = new SteamFriendDirectory();
        var now = DateTime.UtcNow;
        directory.Invite(71, 109775240000000001, now);
        var entry = Assert.Single(directory.Snapshot([new(71, "Friend", false, 0)], now));
        Assert.Equal("109775240000000001", entry.LobbyId);
        Assert.True(entry.Invited);
        Assert.Equal("Friend", entry.Name);
    }

    [Fact]
    public void InvitationReplacesIncompletePresenceWithoutDuplicatingTheLobby()
    {
        var directory = new SteamFriendDirectory();
        var now = DateTime.UtcNow;
        directory.Invite(71, 109775240000000001, now);
        Assert.True(Assert.Single(directory.Snapshot([new(71, "Friend", true, 0)], now)).Invited);
        Assert.True(Assert.Single(directory.Snapshot([new(71, "Friend", true, 109775240000000001)], now)).Invited);
        Assert.Empty(directory.Snapshot([], now.AddMinutes(6)));
    }

    [Fact]
    public async Task CancelledDiscoveryDoesNotKeepPollingAfterThePhoneLeaves()
    {
        using var stop = new CancellationTokenSource();
        int reads = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FriendDiscovery.WaitForLobbiesAsync(_ =>
        {
            reads++; stop.Cancel(); return Task.FromResult(Array.Empty<BridgeFriend>());
        }, TimeSpan.FromSeconds(8), stop.Token));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task APlayerWithoutLobbyDataIsRetainedWhenDiscoveryBudgetExpires()
    {
        var person = new BridgeFriend("71", "Friend", "0");
        var result = await FriendDiscovery.WaitForLobbiesAsync(_ => Task.FromResult(new[] { person }),
            TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(person, Assert.Single(result));
    }

    [Fact]
    public async Task JoinableFriendWaitsBrieflyForSteamToDeliverTheirName()
    {
        int reads = 0;
        var loading = new BridgeFriend("71", SteamNames.Display("[unknown]", null, 71), "109775240000000001");
        var named = loading with { Name = "Friend" };
        var result = await FriendDiscovery.WaitForLobbiesAsync(_ => Task.FromResult(++reads < 3 ? new[] { loading } : new[] { named }),
            TimeSpan.FromSeconds(2), CancellationToken.None, TimeSpan.FromMilliseconds(1));
        Assert.Equal(3, reads);
        Assert.Equal("Friend", Assert.Single(result).Name);
    }

    [Fact]
    public async Task JoinableFriendWithoutANameYetIsStillListedOnceTheBudgetEnds()
    {
        var loading = new BridgeFriend("71", SteamNames.Display("[unknown]", null, 71), "109775240000000001");
        var result = await FriendDiscovery.WaitForLobbiesAsync(_ => Task.FromResult(new[] { loading }),
            TimeSpan.FromMilliseconds(20), CancellationToken.None, TimeSpan.FromMilliseconds(1));
        Assert.Equal(loading, Assert.Single(result));
    }

    [Theory]
    [InlineData("Persona", "Nickname", "Persona")]
    [InlineData("[unknown]", "Nickname", "Nickname")]
    [InlineData("[unknown]", null, "Steam friend …0071")]
    [InlineData(" ", "", "Steam friend …0071")]
    public void NamesFallBackToTheAccountsNicknameThenAStablePlaceholder(string persona, string? nickname, string expected)
    {
        string name = SteamNames.Display(persona, nickname, 76561198000000071);
        Assert.Equal(expected, name);
        Assert.Equal(expected.StartsWith("Steam friend", StringComparison.Ordinal), SteamNames.IsPlaceholder(name));
    }
}
