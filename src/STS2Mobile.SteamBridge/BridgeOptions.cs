namespace STS2Mobile.SteamBridge;

public sealed record BridgeOptions(string TokenFile, string Transport, ulong ExpectedSteamId, int Port, bool ManageSteam = false, bool ExitAfterSession = false)
{
    public const uint AppId = 2868840;

    public static BridgeOptions FromEnvironment()
    {
        string token = Environment.GetEnvironmentVariable("STEAM_BRIDGE_TOKEN_FILE") ?? throw new InvalidOperationException("Set STEAM_BRIDGE_TOKEN_FILE to a private token file.");
        string transport = Environment.GetEnvironmentVariable("STEAM_BRIDGE_TRANSPORT") ?? "disabled";
        if (transport is not ("disabled" or "sockets" or "messages" or "legacy"))
            throw new InvalidOperationException("Unsupported STEAM_BRIDGE_TRANSPORT.");
        string? expectedText = Environment.GetEnvironmentVariable("STEAM_BRIDGE_STEAM_ID");
        ulong expected = 0;
        if (!string.IsNullOrEmpty(expectedText) && (!ulong.TryParse(expectedText, out expected) || expected == 0))
            throw new InvalidOperationException("STEAM_BRIDGE_STEAM_ID must be a valid Steam ID.");
        if (transport != "disabled" && expected == 0)
            throw new InvalidOperationException("Bind the bridge to STEAM_BRIDGE_STEAM_ID before enabling transport.");
        int port = int.Parse(Environment.GetEnvironmentVariable("STEAM_BRIDGE_PORT") ?? "6092");
        if (port is < 1024 or > 65535) throw new InvalidOperationException("Invalid bridge port.");
        bool manageSteam = Environment.GetEnvironmentVariable("STEAM_BRIDGE_MANAGE_STEAM") == "1";
        if (manageSteam && !File.Exists("/.dockerenv"))
            throw new InvalidOperationException("Steam lifecycle management requires the dedicated Docker container.");
        return new(token, transport, expected, port, manageSteam);
    }
}
