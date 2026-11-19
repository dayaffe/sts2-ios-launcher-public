namespace STS2Mobile.SteamBridge.Protocol;

/// <summary>A member of the joined lobby as the server's Steam client names them; the phone has no Steam client to ask.</summary>
public sealed record BridgeMember(string SteamId, string Name);
