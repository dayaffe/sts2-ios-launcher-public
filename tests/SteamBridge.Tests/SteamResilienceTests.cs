namespace STS2Mobile.SteamBridge.Tests;

public sealed class SteamResilienceTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BriefSteamServerLossKeepsAReadySessionUntilTheLimit()
    {
        var grace = new OfflineGrace(TimeSpan.FromSeconds(20));
        Assert.True(grace.Holds(online: false, ready: true, Start));
        Assert.True(grace.Holds(online: false, ready: true, Start.AddSeconds(19)));
        Assert.False(grace.Holds(online: false, ready: true, Start.AddSeconds(20)));
    }

    [Fact]
    public void ReconnectingStartsTheNextLossWithAFullGracePeriod()
    {
        var grace = new OfflineGrace(TimeSpan.FromSeconds(20));
        Assert.True(grace.Holds(online: false, ready: true, Start));
        Assert.False(grace.Holds(online: true, ready: true, Start.AddSeconds(15)));
        Assert.True(grace.Holds(online: false, ready: true, Start.AddSeconds(30)));
        Assert.True(grace.Holds(online: false, ready: true, Start.AddSeconds(49)));
    }

    [Fact]
    public void SessionThatWasNeverReadyGetsNoGrace() =>
        Assert.False(new OfflineGrace(TimeSpan.FromSeconds(20)).Holds(online: false, ready: false, Start));

    [Theory]
    [InlineData(2u, "no longer exists")]
    [InlineData(3u, "does not admit this account")]
    [InlineData(4u, "full")]
    [InlineData(15u, "limiting lobby joins")]
    [InlineData(99u, "Steam response 99")]
    public void LobbyRefusalsExplainTheSteamResponse(uint response, string expected) =>
        Assert.Contains(expected, LobbyRefusal.Describe(ioFailure: false, response));

    [Fact]
    public void LobbyServiceFailureIsDistinctFromARefusal() =>
        Assert.Contains("could not reach", LobbyRefusal.Describe(ioFailure: true, 0));
}
