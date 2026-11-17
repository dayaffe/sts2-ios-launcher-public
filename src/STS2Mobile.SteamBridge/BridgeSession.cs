using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using STS2Mobile.SteamBridge.Protocol;

namespace STS2Mobile.SteamBridge;

public static class BridgeSession
{
    private sealed record Outgoing(byte[] Bytes, WebSocketMessageType Type);

    public static async Task RunAsync(WebSocket socket, IBridgeBackend backend, CancellationToken stopped)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopped);
        CancellationToken ct = cancellation.Token;
        var outgoing = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        void Enqueue(Outgoing frame)
        {
            if (!outgoing.Writer.TryWrite(frame))
            {
                socket.Abort();
                cancellation.Cancel();
            }
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
                    await socket.SendAsync(frame.Bytes.AsMemory(), frame.Type, true, ct);
            }
            finally { cancellation.Cancel(); }
        }
    }
}
