using System.Security.Cryptography;
using System.Text;

namespace STS2Mobile.SteamBridge;

public static class BridgeHttp
{
    public static void Map(WebApplication app, string secret)
    {
        if (secret.Length < 32) throw new InvalidOperationException("Bridge token must contain at least 32 characters.");
        byte[] tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.MapGet("/health", (IBridgeBackend backend) => Results.Json(new { service = "sts2-steam-bridge", protocol = 1, ready = backend.Status.Ready, state = backend.Status.State }));
        app.Map("/bridge", async (HttpContext context, IBridgeBackend backend) =>
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
            await BridgeSession.RunAsync(socket, backend, context.RequestAborted);
        });
    }
}
