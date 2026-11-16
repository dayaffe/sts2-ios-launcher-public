using System.Text.Json;

namespace STS2Mobile.SteamBridge.Protocol;

/// <summary>Steam IDs travel as decimal strings so JavaScript diagnostics never round their 64-bit values.</summary>
public sealed record BridgeCommand(string Id, string Type, string? LobbyId = null, int VirtualPort = 0, int[]? Channels = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static BridgeCommand Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 8192) throw new ArgumentException("Control message is too large.");
        var command = JsonSerializer.Deserialize<BridgeCommand>(bytes, Json) ?? throw new ArgumentException("Missing command.");
        if (string.IsNullOrWhiteSpace(command.Id) || command.Id.Length > 64 || command.Type is not ("status" or "friends" or "join" or "connect" or "disconnect"))
            throw new ArgumentException("Unknown command or invalid request ID.");
        if (command.Type == "join" && (!ulong.TryParse(command.LobbyId, out ulong lobby) || lobby == 0))
            throw new ArgumentException("Invalid lobby ID.");
        if (command.VirtualPort is < 0 or > 65535 || command.Channels is { Length: > 32 }
            || command.Channels?.Any(channel => channel is < 0 or > BridgePacket.MaxChannel) == true)
            throw new ArgumentException("Invalid virtual port or receive channels.");
        return command;
    }
}
