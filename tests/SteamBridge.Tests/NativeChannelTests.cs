using STS2Mobile.SteamBridge.Protocol;

namespace STS2Mobile.SteamBridge.Tests;

public sealed class NativeChannelTests
{
    [Fact]
    public void SocketMessagesReportingNoChannelReachThePhoneOnChannelZero()
    {
        // Valve's connection receive path leaves m_nChannel at its allocation value of -1.
        int? channel = NativeChannels.ForBridge("sockets", -1, [0]);
        Assert.Equal(0, channel);
        var frame = new BridgePacket(76561198000000001, channel!.Value, true, [1, 2, 3]).Encode();
        Assert.Equal(0, BridgePacket.Decode(frame).Channel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void MessagesKeepConfiguredChannels(int native) =>
        Assert.Equal(native, NativeChannels.ForBridge("messages", native, [0, 3]));

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void MessagesRefuseOtherChannels(int native) =>
        Assert.Null(NativeChannels.ForBridge("messages", native, [0, 3]));
}
