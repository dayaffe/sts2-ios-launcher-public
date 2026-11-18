namespace STS2Mobile.SteamBridge;

/// <summary>Plain-language reasons for a refused lobby join, shown in the phone's Steam Bridge panel.</summary>
public static class LobbyRefusal
{
    // EChatRoomEnterResponse values from the Steamworks SDK. Plain numbers keep this testable without Steamworks.NET,
    // whose managed assembly resolves only for the linux-x64 runtime.
    public static string Describe(bool ioFailure, uint response) => ioFailure
        ? "Steam could not reach its lobby service; try again"
        : response switch
        {
            2 => "the lobby no longer exists; ask your friend to host again or send a new invite", // DoesntExist
            3 => "the lobby does not admit this account; it may be private or closed to new players", // NotAllowed
            4 => "the lobby is full", // Full
            6 => "this account is banned from the lobby", // Banned
            7 => "limited Steam accounts cannot join lobbies", // Limited
            9 => "this account has a Steam Community ban", // CommunityBan
            10 => "a lobby member has blocked this account", // MemberBlockedYou
            11 => "this account has blocked a lobby member", // YouBlockedMember
            15 => "Steam is limiting lobby joins; wait a minute and try again", // RatelimitExceeded
            _ => $"Steam response {response}",
        };
}
