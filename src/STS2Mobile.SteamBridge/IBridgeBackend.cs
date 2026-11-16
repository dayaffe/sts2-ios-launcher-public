using STS2Mobile.SteamBridge.Protocol;

namespace STS2Mobile.SteamBridge;

public sealed record BridgeStatus(bool Ready, string State, string SteamId, string Name, string Transport);
public sealed record BridgeEvent(string Type, object? Data = null);

public interface IBridgeBackend
{
    BridgeStatus Status { get; }
    bool TryAttach(Action<BridgeEvent> notify, Action<BridgePacket> packet);
    Task<object?> ExecuteAsync(BridgeCommand command, CancellationToken cancellationToken);
    Task SendAsync(BridgePacket packet, CancellationToken cancellationToken);
    Task DetachAsync();
}
