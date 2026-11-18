using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using STS2Mobile.SteamBridge.Protocol;

namespace STS2Mobile.SteamBridge;

public static class BridgeSession
{
    // What the server may hold for a phone that reads slowly, for example during a host's burst at run start.
    // The limit is in bytes because a burst of many small game messages is normal and harmless.
    public const long MaxQueuedBytes = 8 * 1024 * 1024;
    private sealed record Outgoing(byte[] Bytes, WebSocketMessageType Type);

    public static async Task RunAsync(WebSocket socket, IBridgeBackend backend, CancellationToken stopped, ILogger? logger = null)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopped);
        CancellationToken ct = cancellation.Token;
        var outgoing = Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });
        long queuedBytes = 0;
        void Enqueue(Outgoing frame)
        {
            long queued = Interlocked.Add(ref queuedBytes, frame.Bytes.Length);
            if (queued <= MaxQueuedBytes && outgoing.Writer.TryWrite(frame)) return;
            if (queued > MaxQueuedBytes && !cancellation.IsCancellationRequested)
                logger?.LogWarning("Ending the phone session: {Bytes} bytes are waiting for the phone, over the {Limit} byte limit.", queued, MaxQueuedBytes);
            socket.Abort();
            cancellation.Cancel();
        }
        void Control(object message) => Enqueue(new(JsonSerializer.SerializeToUtf8Bytes(message, BridgeCommand.Json), WebSocketMessageType.Text));
        // Queue the greeting before subscribing to native events, which can arrive synchronously during attachment.
        Control(new { type = "hello", protocol = BridgePacket.Version, status = backend.Status });
        if (!backend.TryAttach(e => Control(e), p => Enqueue(new(p.Encode(), WebSocketMessageType.Binary))))
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Another phone is already attached.", ct);
            return;
        }
        Task sending = SendLoopAsync();
        Task commandTask = Task.CompletedTask;
        try
        {
            byte[] buffer = new byte[8192];
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = new ArrayBufferWriter<byte>();
                WebSocketMessageType? type = null;
                ValueWebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    if (type != null && type != part.MessageType) throw new ArgumentException("Mixed message types.");
                    type = part.MessageType;
                    int limit = type == WebSocketMessageType.Text ? 8192 : BridgePacket.HeaderSize + BridgePacket.MaxPayload;
                    if (message.WrittenCount + part.Count > limit) throw new ArgumentException("Message exceeds bridge limit.");
                    message.Write(buffer.AsSpan(0, part.Count));
                } while (!part.EndOfMessage);
                if (type == WebSocketMessageType.Binary)
                {
                    await backend.SendAsync(BridgePacket.Decode(message.WrittenSpan), ct);
                    continue;
                }
                BridgeCommand command = BridgeCommand.Parse(message.WrittenSpan);
                if (!commandTask.IsCompleted)
                {
                    Control(new { type = "result", id = command.Id, ok = false, error = "Another Steam operation is pending." });
                    continue;
                }
                // Keep receiving so a dropped phone cancels an outstanding native lobby join immediately.
                commandTask = HandleCommandAsync(command);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or ArgumentException or JsonException or InvalidOperationException)
        {
            // Cancellation is the normal end of a session, or follows a cause that was already logged. The gateway ends
            // every worker session by aborting its socket, so an unclosed connection is routine.
            if (error is WebSocketException { WebSocketErrorCode: WebSocketError.ConnectionClosedPrematurely })
                logger?.LogInformation("Phone session ended: the connection closed.");
            else if (error is not OperationCanceledException)
                logger?.LogWarning("Ending the phone session: {Kind}: {Reason}", error.GetType().Name, error.Message);
            socket.Abort();
        }
        finally
        {
            cancellation.Cancel();
            outgoing.Writer.TryComplete();
            try { await commandTask; }
            catch (Exception) { socket.Abort(); }
            finally { await backend.DetachAsync(); }
            try { await sending; } catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
            if (socket.State == WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session ended.", CancellationToken.None);
        }

        async Task HandleCommandAsync(BridgeCommand command)
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
            request.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                object? data = await backend.ExecuteAsync(command, request.Token).WaitAsync(request.Token);
                Control(new { type = "result", id = command.Id, ok = true, data });
            }
            catch (OperationCanceledException)
            {
                if (!ct.IsCancellationRequested)
                    Control(new { type = "result", id = command.Id, ok = false, error = "Steam request timed out." });
            }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException)
            {
                Control(new { type = "result", id = command.Id, ok = false, error = error.Message });
            }
        }

        async Task SendLoopAsync()
        {
            try
            {
                await foreach (Outgoing frame in outgoing.Reader.ReadAllAsync(ct))
                {
                    await socket.SendAsync(frame.Bytes.AsMemory(), frame.Type, true, ct);
                    Interlocked.Add(ref queuedBytes, -frame.Bytes.Length);
                }
            }
            finally { cancellation.Cancel(); }
        }
    }
}
