using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using STS2Mobile.SteamBridge.Protocol;

namespace STS2Mobile.SteamBridge.Tests;

public sealed class BridgeFlowTests
{
    private const string Token = "test-only-token-not-a-real-credential-0123456789";
    private const ulong SteamId = 76561198000000001;
    private const ulong HostId = 76561198000000002;

    [Fact]
    public async Task HttpEntryRequiresBearerAndNeverAcceptsAQueryStringToken()
    {
        await using var fixture = await Fixture.StartAsync();
        using HttpClient http = fixture.App.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/bridge")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/bridge?token=" + Token)).StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/bridge")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/bridge")).StatusCode);
        string health = await http.GetStringAsync("/health");
        Assert.DoesNotContain(SteamId.ToString(), health);
        Assert.DoesNotContain(Token, health);
    }

    [Fact]
    public async Task PhoneAccountMismatchClosesSessionWithoutSendingAnythingToSteam()
    {
        await using var fixture = await Fixture.StartAsync();
        WebSocket socket = await fixture.OpenAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => BridgeClient.AttachAsync(socket, SteamId + 1));
        await fixture.Backend.Detached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(fixture.Backend.Sent);
        Assert.Empty(fixture.Backend.Commands);
    }

    [Fact]
    public async Task AuthenticatedPhoneJoinsAndExchangesUntouchedPacketsWithItsHost()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var client = await BridgeClient.AttachAsync(await fixture.OpenAsync(), SteamId);
        Assert.Equal(SteamId, client.SteamId);
        JsonElement joined = await client.ExecuteAsync("join", "109775240000000001");
        Assert.Equal("109775240000000001", joined.GetProperty("lobbyId").GetString());
        var received = new TaskCompletionSource<BridgePacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PacketReceived += packet => received.TrySetResult(packet);
        byte[] bytes = [0, 255, 3, 0, 10, 20, 30];
        await client.SendPacketAsync(new BridgePacket(HostId, 7, false, bytes));
        BridgePacket echoed = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(bytes, echoed.Payload);
        Assert.Equal(7, echoed.Channel);
        Assert.False(echoed.Reliable);
        Assert.Equal(HostId, echoed.PeerId);
        Assert.Single(fixture.Backend.Sent);
    }

    [Fact]
    public async Task FragmentedCommandsAreReassembledBeforeExecution()
    {
        await using var fixture = await Fixture.StartAsync();
        using WebSocket socket = await fixture.OpenAsync();
        await ReadAsync(socket); // hello
        byte[] command = Encoding.UTF8.GetBytes("{\"id\":\"r1\",\"type\":\"join\",\"lobbyId\":\"109775240000000001\"}");
        await socket.SendAsync(command.AsMemory(0, 10), WebSocketMessageType.Text, false, CancellationToken.None);
        await socket.SendAsync(command.AsMemory(10), WebSocketMessageType.Text, true, CancellationToken.None);
        using var result = JsonDocument.Parse(await ReadAsync(socket));
        Assert.Equal("r1", result.RootElement.GetProperty("id").GetString());
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Single(fixture.Backend.Commands);
        socket.Abort();
    }

    [Fact]
    public async Task SecondPhoneCannotTakeOverAnActiveSteamSession()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var first = await BridgeClient.AttachAsync(await fixture.OpenAsync(), SteamId);
        using WebSocket second = await fixture.OpenAsync();
        byte[] buffer = new byte[1024];
        var result = await second.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, second.CloseStatus);
        Assert.True(first.IsConnected);
    }

    [Fact]
    public async Task SteamIdentityChangingDisconnectsPhoneAndFailsPendingRequests()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var client = await BridgeClient.AttachAsync(await fixture.OpenAsync(), SteamId);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += () => disconnected.TrySetResult();
        fixture.Backend.HoldFriends = true;
        Task<JsonElement> pending = client.ExecuteAsync("friends");
        await fixture.Backend.FriendsStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Backend.Notify!(new("status", fixture.Backend.Status with { SteamId = (SteamId + 1).ToString() }));
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        await fixture.Backend.Detached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync("friends"));
    }

    [Fact]
    public async Task GreetingPrecedesNativeEventsRaisedDuringAttachment()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Backend.NotifyDuringAttach = true;
        await using var client = await BridgeClient.AttachAsync(await fixture.OpenAsync(), SteamId);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task PhoneDroppingDuringASteamRequestCancelsItAndReleasesTheLease()
    {
        await using var fixture = await Fixture.StartAsync();
        WebSocket socket = await fixture.OpenAsync();
        await using var client = await BridgeClient.AttachAsync(socket, SteamId);
        fixture.Backend.HoldFriends = true;
        Task<JsonElement> pending = client.ExecuteAsync("friends");
        await fixture.Backend.FriendsStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        socket.Abort();
        await fixture.Backend.Detached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
    }

    [Fact]
    public async Task MalformedBinaryPacketClosesTheSessionAndNeverReachesSteam()
    {
        await using var fixture = await Fixture.StartAsync();
        using WebSocket socket = await fixture.OpenAsync();
        await ReadAsync(socket);
        await socket.SendAsync(new byte[] { 1, 2, 3 }.AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None);
        await fixture.Backend.Detached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(fixture.Backend.Sent);
    }

    private static async Task<byte[]> ReadAsync(WebSocket socket)
    {
        byte[] buffer = new byte[8192];
        var result = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
        return buffer.AsSpan(0, result.Count).ToArray();
    }

    private sealed class Fixture(WebApplication app, FakeBackend backend) : IAsyncDisposable
    {
        public WebApplication App => app;
        public FakeBackend Backend => backend;
        public static async Task<Fixture> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var backend = new FakeBackend();
            builder.Services.AddSingleton<IBridgeBackend>(backend);
            WebApplication app = builder.Build();
            BridgeHttp.Map(app, Token);
            await app.StartAsync();
            return new(app, backend);
        }
        public Task<WebSocket> OpenAsync()
        {
            var client = app.GetTestServer().CreateWebSocketClient();
            client.ConfigureRequest = request => request.Headers["Authorization"] = "Bearer " + Token;
            return client.ConnectAsync(new Uri("http://localhost/bridge"), CancellationToken.None);
        }
        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private sealed class FakeBackend : IBridgeBackend
    {
        private int _attached;
        private Action<BridgePacket>? _packet;
        public Action<BridgeEvent>? Notify { get; private set; }
        public List<BridgePacket> Sent { get; } = [];
        public List<BridgeCommand> Commands { get; } = [];
        public TaskCompletionSource Detached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldFriends { get; set; }
        public bool NotifyDuringAttach { get; set; }
        public TaskCompletionSource FriendsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BridgeStatus Status => new(true, "ready", SteamId.ToString(), "test account", "sockets");
        public bool TryAttach(Action<BridgeEvent> notify, Action<BridgePacket> packet)
        {
            if (Interlocked.CompareExchange(ref _attached, 1, 0) != 0) return false;
            Notify = notify;
            _packet = packet;
            if (NotifyDuringAttach) notify(new("status", Status));
            return true;
        }
        public Task<object?> ExecuteAsync(BridgeCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (command.Type == "friends" && HoldFriends)
            {
                FriendsStarted.TrySetResult();
                return HoldAsync(ct);
            }
            return Task.FromResult<object?>(new { lobbyId = command.LobbyId, ownerId = HostId.ToString() });
        }
        private static async Task<object?> HoldAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
        public Task SendAsync(BridgePacket packet, CancellationToken ct)
        {
            Sent.Add(packet);
            _packet!(packet);
            return Task.CompletedTask;
        }
        public Task DetachAsync()
        {
            Interlocked.Exchange(ref _attached, 0);
            Detached.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
