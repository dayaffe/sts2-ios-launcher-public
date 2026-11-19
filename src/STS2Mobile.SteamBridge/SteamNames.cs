using System.Globalization;

namespace STS2Mobile.SteamBridge;

/// <summary>Display names while Steam is still delivering personas, which it reports as "[unknown]" until they arrive.</summary>
public static class SteamNames
{
    private const string PlaceholderPrefix = "Steam friend …";

    public static bool IsUnknown(string? name) => string.IsNullOrWhiteSpace(name) || name == "[unknown]";

    /// <summary>The persona name when Steam has it, then the account's own nickname for that user, then a stable placeholder.</summary>
    public static string Display(string? persona, string? nickname, ulong steamId) =>
        !IsUnknown(persona) ? persona! : !IsUnknown(nickname) ? nickname! : PlaceholderPrefix + (steamId % 10000).ToString("D4", CultureInfo.InvariantCulture);

    public static bool IsPlaceholder(string name) => name.StartsWith(PlaceholderPrefix, StringComparison.Ordinal);
}
