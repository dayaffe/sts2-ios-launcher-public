using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length is < 1 or > 2 || !File.Exists(args[0]) || (args.Length == 2 && args[1] != "--include-transport-il"))
{
    Console.Error.WriteLine("Usage: SteamBridgeInspector <owned desktop sts2.dll> [--include-transport-il]");
    return 2;
}
// Read metadata without loading or executing game code. Detailed transport IL is opt-in for private inspection only.
using var assembly = AssemblyDefinition.ReadAssembly(args[0]);
var allTypes = assembly.MainModule.Types.SelectMany(Walk).ToArray();
var types = allTypes.Where(t => t.FullName.Contains("Multiplayer", StringComparison.Ordinal)
    || t.FullName.Contains("Platform.Steam", StringComparison.Ordinal)).ToArray();
var report = new
{
    schema = 1,
    gameAssemblySha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(args[0]))),
    assemblyVersion = assembly.Name.Version.ToString(),
    steamworksReference = assembly.MainModule.AssemblyReferences.FirstOrDefault(a => a.Name == "Steamworks.NET")?.Version.ToString(),
    networking = types.SelectMany(t => t.Methods).Where(m => m.HasBody).Select(m => new
    {
        method = m.FullName,
        calls = m.Body.Instructions.Where(i => i.Operand is MethodReference called
            && called.DeclaringType.Namespace == "Steamworks").Select(i => ((MethodReference)i.Operand).FullName).Distinct().ToArray(),
        // Integer constants near calls help locate virtual ports and channel tags; game payloads are never dumped.
        constants = m.Body.Instructions.Where(i => i.OpCode.Code is Code.Ldc_I4 or Code.Ldc_I4_S
            or Code.Ldc_I4_0 or Code.Ldc_I4_1 or Code.Ldc_I4_2 or Code.Ldc_I4_3 or Code.Ldc_I4_4
            or Code.Ldc_I4_5 or Code.Ldc_I4_6 or Code.Ldc_I4_7 or Code.Ldc_I4_8).Select(i => i.ToString()).ToArray(),
    }).Where(m => m.calls.Length != 0).ToArray(),
    transportApi = allTypes.Where(t => t.Name is "NetClient" or "INetClientHandler" or "INetHandler" or "SteamClient" or "PlatformType"
        or "SteamClientConnectionInitializer" or "IClientConnectionInitializer" or "INetClientGameService" or "NetError"
        or "NetErrorInfo" or "NetTransferMode" or "NMainMenu" or "NSubmenu" or "NSubmenuStack" or "NMultiplayerSubmenu" or "NJoinFriendScreen" or "PeerVersionInfo").Select(t => new
    {
        type = t.FullName,
        baseType = t.BaseType?.FullName,
        interfaces = t.Interfaces.Select(i => i.InterfaceType.FullName).ToArray(),
        fields = t.Fields.Where(f => f.IsPublic || f.IsFamily || t.Name is "NMainMenu" or "NJoinFriendScreen").Select(f => new { name = f.FullName, constant = f.Constant }).ToArray(),
        methods = t.Methods.Select(m => new { name = m.FullName, m.IsVirtual, m.IsAbstract, m.IsPublic, m.IsFamily }).ToArray(),
    }).ToArray(),
    joinEntryPoints = allTypes.SelectMany(t => t.Methods).Where(m => m.Parameters.Any(p => p.ParameterType.FullName.Contains("IClientConnectionInitializer", StringComparison.Ordinal)))
        .Select(m => new { method = m.FullName, m.IsStatic, m.IsPublic }).ToArray(),
    transportIl = args.Length == 2 ? allTypes.Where(t => t.FullName.Contains(".SteamClient", StringComparison.Ordinal)
        || t.Name == "SteamUtil" || t.FullName.Contains("NMainMenu/<JoinGame", StringComparison.Ordinal)
        || t.FullName.Contains("NetClientGameService", StringComparison.Ordinal)
        || t.FullName.Contains("NetHostGameService", StringComparison.Ordinal)
        || t.FullName.Contains("NetServerGameService", StringComparison.Ordinal)
        || t.FullName.Contains("NJoinFriendScreen", StringComparison.Ordinal)
        || t.FullName.Contains("Multiplayer.Game.JoinFlow", StringComparison.Ordinal)
        || t.Name is "NSubmenu" or "NSubmenuStack" or "NMainMenuSubmenuStack" or "NMultiplayerSubmenu" or "PeerVersionInfo" or "PlatformUtil" or "SteamPlatformUtilStrategy" or "NullPlatformUtilStrategy"
        || t.Methods.Any(m => m.HasBody && m.Body.Instructions.Any(i => i.Operand is FieldReference f && f.DeclaringType.Name == "PeerVersionInfo")))
        .SelectMany(t => t.Methods).Where(m => m.HasBody).Select(m => new
        { method = m.FullName, instructions = m.Body.Instructions.Select(i => i.ToString()).ToArray() }).ToArray() : null,
};
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return 0;

static IEnumerable<TypeDefinition> Walk(TypeDefinition type)
{
    yield return type;
    foreach (TypeDefinition nested in type.NestedTypes.SelectMany(Walk)) yield return nested;
}
