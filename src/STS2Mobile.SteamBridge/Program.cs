using STS2Mobile.SteamBridge;

if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess)
    throw new PlatformNotSupportedException("The experimental Steam bridge requires Linux x64.");
bool worker = args.Contains("--worker", StringComparer.Ordinal);
var options = BridgeOptions.FromEnvironment();
if (worker) options = options with { Port = 6093, ManageSteam = false, ExitAfterSession = true };
string secret = File.ReadAllText(options.TokenFile).Trim();
if (worker)
{
    Environment.SetEnvironmentVariable("SteamAppId", BridgeOptions.AppId.ToString());
    Environment.SetEnvironmentVariable("SteamGameId", BridgeOptions.AppId.ToString());
}

var builder = WebApplication.CreateBuilder(args.Where(a => a != "--worker").ToArray());
// Do not record URLs/query strings supplied by clients, including rejected credential attempts.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
// Steam and the bridge share a private container; only the host's loopback port is published by Compose.
builder.WebHost.UseUrls($"http://{(worker ? "127.0.0.1" : "0.0.0.0")}:{options.Port}");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<SteamDesktop>();
if (worker)
{
    builder.Services.AddSingleton<SteamBackend>();
    builder.Services.AddSingleton<IBridgeBackend>(services => services.GetRequiredService<SteamBackend>());
    builder.Services.AddHostedService(services => services.GetRequiredService<SteamBackend>());
}
else
{
    builder.Services.AddSingleton<ISteamWorkerFactory, SteamWorkerFactory>();
    builder.Services.AddSingleton<SteamSessionGateway>();
}
var app = builder.Build();
if (worker) BridgeHttp.Map(app, secret);
else
{
    var gateway = app.Services.GetRequiredService<SteamSessionGateway>();
    BridgeHttp.Map(app, secret, () => gateway.Status, gateway.RunAsync);
}
await app.RunAsync();

public partial class Program;
