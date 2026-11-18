using System.Security.Cryptography;
using System.Text;

namespace STS2Mobile.SteamBridge;

public static class BridgeHttp
{
    public static void Map(WebApplication app, string secret)
    {
        var backend = app.Services.GetRequiredService<IBridgeBackend>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(BridgeSession));
        Map(app, secret, () => backend.Status, (socket, ct) => BridgeSession.RunAsync(socket, backend, ct, logger));
    }

    public static void Map(WebApplication app, string secret, Func<BridgeStatus> status,
        Func<System.Net.WebSockets.WebSocket, CancellationToken, Task> run)
    {
        if (secret.Length < 32) throw new InvalidOperationException("Bridge token must contain at least 32 characters.");
        byte[] tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(15) });
        app.MapGet("/health", () => Results.Json(new { service = "sts2-steam-bridge", protocol = 1, ready = status().Ready, state = status().State }));
        app.Map("/bridge", async (HttpContext context) =>
        {
            string auth = context.Request.Headers.Authorization.ToString();
            if (!auth.StartsWith("Bearer ", StringComparison.Ordinal) || !CryptographicOperations.FixedTimeEquals(
                tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(auth[7..]))))
            {
                context.Response.StatusCode = 401;
                return;
            }
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await run(socket, context.RequestAborted);
        });
    }
}
