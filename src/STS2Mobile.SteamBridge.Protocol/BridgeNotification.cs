using System.Text.Json;

namespace STS2Mobile.SteamBridge.Protocol;

/// <summary>Reference-type event boundary between the AOT transport and interpreted game host.</summary>
public sealed class BridgeNotification(JsonElement message)
{
    public JsonElement GetProperty(string name) => message.GetProperty(name);
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
