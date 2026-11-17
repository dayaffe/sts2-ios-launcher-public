using System.Text.Json.Serialization;

namespace STS2Mobile.SteamBridge.Protocol;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BridgeCommand))]
internal partial class BridgeJsonContext : JsonSerializerContext;
