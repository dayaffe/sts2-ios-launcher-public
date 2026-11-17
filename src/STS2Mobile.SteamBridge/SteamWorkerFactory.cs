using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;

namespace STS2Mobile.SteamBridge;

public sealed class SteamWorkerFactory(BridgeOptions options, SteamDesktop desktop) : ISteamWorkerFactory
{
    public async Task<ISteamWorkerSession> OpenAsync(CancellationToken ct)
    {
        Process? process = null;
        ClientWebSocket? socket = null;
        try
        {
            desktop.Start();
            var start = new ProcessStartInfo(Environment.ProcessPath!);
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--worker");
            process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Steam game worker.");
            string token = File.ReadAllText(options.TokenFile).Trim();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("Steam game worker exited during startup.");
                socket = new ClientWebSocket();
                socket.Options.Proxy = null;
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
                socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
                try
                {
                    await socket.ConnectAsync(new Uri("ws://127.0.0.1:6093/bridge"), ct);
                    return new Session(process, socket, desktop);
                }
                catch (WebSocketException)
                {
                    socket.Dispose(); socket = null;
                    await Task.Delay(100, ct);
                }
            }
        }
        catch
        {
            socket?.Dispose();
            await StopWorkerAsync(process);
            desktop.Stop();
            throw;
        }
    }

    private static async Task StopWorkerAsync(Process? process)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally { process.Dispose(); }
    }

    private sealed class Session(Process process, ClientWebSocket socket, SteamDesktop desktop) : ISteamWorkerSession
    {
        public WebSocket Socket => socket;
        public async ValueTask DisposeAsync()
        {
            socket.Abort(); socket.Dispose();
            // Steam must observe its game process exit before we ask the desktop client to close.
            await StopWorkerAsync(process);
            desktop.Stop();
        }
    }
}
