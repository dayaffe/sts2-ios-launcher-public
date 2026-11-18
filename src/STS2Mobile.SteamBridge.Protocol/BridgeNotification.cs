using System.Text.Json;

namespace STS2Mobile.SteamBridge.Protocol;

/// <summary>Reference-type event boundary between the AOT transport and interpreted game host.</summary>
public sealed class BridgeNotification(JsonElement message)
{
    public JsonElement GetProperty(string name) => message.GetProperty(name);

    /// <summary>A string field of the notification's data, or null; keeps optional-field lookups in compiled code.</summary>
    public string? GetDataString(string name) =>
        message.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Keep generic task/timer machinery in the AOT transport assembly.</summary>
public sealed class BridgeConnectionSignal
{
    private readonly TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Connected() => _connected.TrySetResult(true);
    public void Failed(string reason) => _connected.TrySetException(new InvalidOperationException(reason));
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct) =>
        await _connected.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
}
