using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using STS2Mobile.SteamBridge.Protocol;

namespace STS2Mobile.SteamBridge;

public interface ISteamWorkerSession : IAsyncDisposable
{
    WebSocket Socket { get; }
}
public interface ISteamWorkerFactory
{
    Task<ISteamWorkerSession> OpenAsync(CancellationToken ct);
}

/// <summary>The permanent endpoint never initializes Steam. Only its per-phone child process does.</summary>
public sealed class SteamSessionGateway(ISteamWorkerFactory workers, ILogger<SteamSessionGateway>? logger = null)
{
    private int _attached;
    private volatile BridgeStatus _status = new(false, "idle", "0", "", "disabled");
    public BridgeStatus Status => _status;

    public async Task RunAsync(WebSocket phone, CancellationToken stopped)
    {
        if (Interlocked.CompareExchange(ref _attached, 1, 0) != 0)
        {
            try { await phone.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Another phone is already attached.", stopped); }
            catch (WebSocketException) { phone.Abort(); }
            return;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stopped);
        try
        {
            _status = _status with { State = "starting_steam" };
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(15));
            await using var worker = await workers.OpenAsync(startup.Token);
            Task incoming = ForwardAsync(phone, worker.Socket, 8192, false, stop.Token);
            Task outgoing = ForwardAsync(worker.Socket, phone, 65536, true, stop.Token);
            try
            {
                Task ended = await Task.WhenAny(incoming, outgoing);
                logger?.LogInformation("Steam session ended from {Side} ({Result}).",
                    ReferenceEquals(ended, incoming) ? "phone" : "worker",
                    ended.IsFaulted ? ended.Exception?.GetBaseException().GetType().Name : "closed");
            }
            finally
            {
                stop.Cancel();
                phone.Abort();
                worker.Socket.Abort();
                try { await Task.WhenAll(incoming, outgoing); }
                catch (Exception error) when (error is OperationCanceledException or WebSocketException or ArgumentException or JsonException or InvalidOperationException) { }
                _status = _status with { Ready = false, State = "stopping_steam" };
            }
            // Dispose waits for the game worker to exit, then closes this container's Steam client.
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or InvalidOperationException)
        {
            phone.Abort();
        }
        finally
        {
            _status = _status with { Ready = false, State = "idle", SteamId = "0", Name = "" };
            Interlocked.Exchange(ref _attached, 0);
        }
    }

    private async Task ForwardAsync(WebSocket source, WebSocket destination, int textLimit, bool observe, CancellationToken ct)
    {
        byte[] buffer = new byte[8192];
        while (!ct.IsCancellationRequested)
        {
            var message = new ArrayBufferWriter<byte>();
            WebSocketMessageType? type = null;
            ValueWebSocketReceiveResult part;
            do
            {
                part = await source.ReceiveAsync(buffer.AsMemory(), ct);
                if (part.MessageType == WebSocketMessageType.Close) return;
                if (type != null && type != part.MessageType) throw new ArgumentException("Mixed frame types.");
                type = part.MessageType;
                int limit = type == WebSocketMessageType.Text ? textLimit : BridgePacket.HeaderSize + BridgePacket.MaxPayload;
                if (message.WrittenCount + part.Count > limit) throw new ArgumentException("Bridge message exceeds limit.");
                message.Write(buffer.AsSpan(0, part.Count));
            } while (!part.EndOfMessage);
            if (observe && type == WebSocketMessageType.Text)
            {
                using var document = JsonDocument.Parse(message.WrittenMemory);
                var root = document.RootElement;
                string? kind = root.GetProperty("type").GetString();
                if (kind is "hello" or "status")
                {
                    var status = root.GetProperty(kind == "hello" ? "status" : "data");
                    _status = new(status.GetProperty("ready").GetBoolean(), status.GetProperty("state").GetString()!,
                        "0", "", status.GetProperty("transport").GetString()!);
                }
            }
            await destination.SendAsync(message.WrittenMemory, part.MessageType, true, ct);
        }
    }
}
