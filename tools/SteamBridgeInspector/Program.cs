using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: SteamBridgeInspector <owned desktop sts2.dll>");
    return 2;
}
// Read metadata without loading or executing any code from the game. No method bodies are exported.
using var assembly = AssemblyDefinition.ReadAssembly(args[0]);
var types = assembly.MainModule.Types.SelectMany(Walk).Where(t => t.FullName.Contains("Multiplayer", StringComparison.Ordinal)
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
    transportApi = types.Where(t => t.Name is "NetClient" or "INetClientHandler" or "INetHandler" or "SteamClient"
        or "SteamClientConnectionInitializer" or "IClientConnectionInitializer" or "INetClientGameService" or "NetError"
        or "NetErrorInfo" or "NetTransferMode").Select(t => new
    {
        type = t.FullName,
        fields = t.Fields.Where(f => f.IsPublic || f.IsFamily).Select(f => new { name = f.FullName, constant = f.Constant }).ToArray(),
        methods = t.Methods.Select(m => new { name = m.FullName, m.IsVirtual, m.IsAbstract, m.IsPublic, m.IsFamily }).ToArray(),
    }).ToArray(),
};
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return 0;

static IEnumerable<TypeDefinition> Walk(TypeDefinition type)
{
    yield return type;
    foreach (TypeDefinition nested in type.NestedTypes.SelectMany(Walk)) yield return nested;
}
