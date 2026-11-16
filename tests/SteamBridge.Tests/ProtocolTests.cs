using System.Buffers.Binary;
using STS2Mobile.SteamBridge.Protocol;

namespace STS2Mobile.SteamBridge.Tests;

public sealed class ProtocolTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FullSizePacketsPreserveEveryPayloadByteAndTheFullSteamId(bool reliable)
    {
        byte[] payload = new byte[BridgePacket.MaxPayload];
        new Random(71).NextBytes(payload);
        var original = new BridgePacket(ulong.MaxValue, 255, reliable, payload);
        var decoded = BridgePacket.Decode(original.Encode());
        Assert.Equal(original.PeerId, decoded.PeerId);
        Assert.Equal(original.Channel, decoded.Channel);
        Assert.Equal(original.Reliable, decoded.Reliable);
        Assert.Equal(payload, decoded.Payload);
    }

    [Fact]
    public void InvalidOrOversizedEnvelopesCannotReachSteam()
    {
        byte[] valid = new BridgePacket(71, 2, true, [1, 2, 3]).Encode();
        for (int length = 0; length <= BridgePacket.HeaderSize; length++)
            Assert.Throws<ArgumentException>(() => BridgePacket.Decode(valid.AsSpan(0, length)));
        foreach (int offset in new[] { 0, 3, 4, 5 })
        {
            byte[] corrupt = (byte[])valid.Clone();
            corrupt[offset] = 255;
            Assert.Throws<ArgumentException>(() => BridgePacket.Decode(corrupt));
        }
        byte[] channel = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(channel.AsSpan(6), 256);
        Assert.Throws<ArgumentException>(() => BridgePacket.Decode(channel));
        byte[] peer = (byte[])valid.Clone();
        peer.AsSpan(8, 8).Clear();
        Assert.Throws<ArgumentException>(() => BridgePacket.Decode(peer));
        Assert.Throws<ArgumentException>(() => new BridgePacket(71, 0, true, new byte[BridgePacket.MaxPayload + 1]).Encode());
    }

    [Theory]
    [InlineData("{\"id\":\"1\",\"type\":\"join\",\"lobbyId\":\"0\"}")]
    [InlineData("{\"id\":\"1\",\"type\":\"connect\",\"channels\":[-1]}")]
    [InlineData("{\"id\":\"1\",\"type\":\"connect\",\"virtualPort\":65536}")]
    [InlineData("{\"id\":\"1\",\"type\":\"arbitrary_steam_api\"}")]
    [InlineData("{\"id\":null,\"type\":\"status\"}")]
    public void InvalidCommandsAreRejected(string json) => Assert.Throws<ArgumentException>(() => BridgeCommand.Parse(System.Text.Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData("ws://example.com/bridge")]
    [InlineData("wss://example.com:9000/bridge")]
    [InlineData("wss://example.com/bridge?token=secret")]
    [InlineData("wss://user:password@example.com/bridge")]
    [InlineData("wss://example.com/other")]
    public async Task PhoneRejectsUnsafeOrUnexpectedEndpointsBeforeConnecting(string endpoint) =>
        await Assert.ThrowsAsync<ArgumentException>(() => BridgeClient.ConnectAsync(new Uri(endpoint), new string('t', 64), 71));
}
