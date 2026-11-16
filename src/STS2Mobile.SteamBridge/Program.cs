using STS2Mobile.SteamBridge;

if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess)
    throw new PlatformNotSupportedException("The experimental Steam bridge requires Linux x64.");
var options = BridgeOptions.FromEnvironment();
string secret = File.ReadAllText(options.TokenFile).Trim();
Environment.SetEnvironmentVariable("SteamAppId", BridgeOptions.AppId.ToString());
Environment.SetEnvironmentVariable("SteamGameId", BridgeOptions.AppId.ToString());

var builder = WebApplication.CreateBuilder(args);
// Do not record URLs/query strings supplied by clients, including rejected credential attempts.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
// Steam and the bridge share a private container; only the host's loopback port is published by Compose.
builder.WebHost.UseUrls($"http://0.0.0.0:{options.Port}");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<SteamBackend>();
builder.Services.AddSingleton<IBridgeBackend>(services => services.GetRequiredService<SteamBackend>());
builder.Services.AddHostedService(services => services.GetRequiredService<SteamBackend>());
var app = builder.Build();
BridgeHttp.Map(app, secret);
await app.RunAsync();

public partial class Program;
