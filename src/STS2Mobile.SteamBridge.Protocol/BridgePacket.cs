using System.Buffers.Binary;

namespace STS2Mobile.SteamBridge.Protocol;

/// <summary>A packet's payload is the game's original bytes; only this envelope belongs to the bridge.</summary>
public sealed record BridgePacket(ulong PeerId, int Channel, bool Reliable, byte[] Payload)
{
    public const int Version = 1;
    public const int HeaderSize = 16;
    public const int MaxPayload = 64 * 1024;
    public const int MaxChannel = 255;

    public byte[] Encode()
    {
        if (PeerId == 0 || Channel is < 0 or > MaxChannel || Payload.Length is 0 or > MaxPayload)
            throw new ArgumentException("Invalid bridge packet.");
        byte[] frame = new byte[HeaderSize + Payload.Length];
        "S2B1"u8.CopyTo(frame);
        frame[4] = Reliable ? (byte)1 : (byte)0;
        frame[5] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)Channel);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(8), PeerId);
        Payload.CopyTo(frame, HeaderSize);
        return frame;
    }

    public static BridgePacket Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length <= HeaderSize || frame.Length > HeaderSize + MaxPayload || !frame[..4].SequenceEqual("S2B1"u8)
            || frame[4] > 1 || frame[5] != 0)
            throw new ArgumentException("Invalid bridge packet header or size.");
        int channel = BinaryPrimitives.ReadUInt16LittleEndian(frame[6..]);
        ulong peer = BinaryPrimitives.ReadUInt64LittleEndian(frame[8..]);
        if (channel > MaxChannel || peer == 0) throw new ArgumentException("Invalid bridge peer or channel.");
        return new BridgePacket(peer, channel, frame[4] == 1, frame[HeaderSize..].ToArray());
    }
}
