using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Platform;
using STS2Mobile.SteamBridge.Protocol;

namespace STS2Mobile.GameHost;

/// <summary>Experimental vanilla multiplayer entry point. No traffic until the player presses Connect.</summary>
public sealed partial class SteamBridgeMenu : CanvasLayer
{
    private readonly CancellationTokenSource _stop = new();
    private NGame? _game;
    private Control _root = null!;
    private Button _open = null!;
    private PanelContainer _panel = null!;
    private LineEdit _endpoint = null!, _token = null!, _lobby = null!;
    private Label _status = null!;
    private VBoxContainer _friends = null!;
    private BridgeClient? _client;
    private BridgeClient? _gameBridge;
    private CancellationTokenSource? _connectionStop;
    private bool _working;
    private sealed record FriendUpdate(JsonElement Friends);
    private FriendUpdate? _pendingFriends;
    private int _serverDisconnected;
    private string _savedEndpoint = "";
    private const string EndpointFile = "user://steam-bridge-endpoint.txt";

    public void Attach(NGame game) => _game = game;

    public override void _Ready()
    {
        Layer = 90;
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        _open = Button("Steam Bridge", () =>
        {
            if (_panel.Visible) ClosePanel();
            else _panel.Visible = true;
            if (ModLaunchSettings.Enabled) _status.Text = "Turn mods off in the launcher and restart before joining unmodified games.";
        });
        _open.AnchorLeft = _open.AnchorRight = 1;
        _open.OffsetLeft = -330; _open.OffsetRight = -24;
        _open.OffsetTop = 24; _open.OffsetBottom = 92;
        _root.AddChild(_open);
        _panel = new PanelContainer { Visible = false };
        _panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _panel.AnchorLeft = 0.06f; _panel.AnchorRight = 0.94f;
        _panel.AnchorTop = 0.12f; _panel.AnchorBottom = 0.9f;
        var margin = new MarginContainer();
        foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 24);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 14);
        column.AddChild(Label("Steam Bridge — experimental", 32));
        column.AddChild(Label("Join a friend's unmodified game through your Linux Steam session. Everyone needs the same game version. Mods must be off.", 24));
        column.AddChild(Label("Server address", 24));
        _endpoint = Input("wss://your-server/bridge"); column.AddChild(_endpoint);
        column.AddChild(Label("Pairing token", 24));
        _token = Input("Paste your server's pairing token"); _token.Secret = true; column.AddChild(_token);
        column.AddChild(Button("Connect to server", () => _ = ConnectAsync()));
        _status = Label("Connect to find friends hosting a game, or paste a Steam lobby link below.", 24); column.AddChild(_status);
        column.AddChild(Button("Refresh friends", () => _ = RefreshFriendsAsync()));
        _friends = new VBoxContainer(); column.AddChild(_friends);
        _lobby = Input("Lobby ID or steam://joinlobby/... link"); column.AddChild(_lobby);
        column.AddChild(Button("Join lobby", () =>
        {
            if (TryLobby(_lobby.Text, out ulong lobby)) _ = JoinAsync(lobby);
            else _status.Text = "Paste a lobby ID or a Slay the Spire 2 Steam lobby link.";
        }));
        column.AddChild(Button("Disconnect from server", Disconnect));
        column.AddChild(Button("Close", ClosePanel));
        scroll.AddChild(column); margin.AddChild(scroll); _panel.AddChild(margin); _root.AddChild(_panel);
        LoadEndpoint();
        _endpoint.TextChanged += text => { if (text.Trim() != _savedEndpoint) _token.Text = ""; };
    }

    public override void _Process(double delta)
    {
        bool show = _game != null && IsInstanceValid(_game) && _game.MainMenu is { } menu && IsInstanceValid(menu) && menu.IsVisibleInTree();
        var update = Interlocked.Exchange(ref _pendingFriends, null);
        if (update != null && _client?.IsConnected == true) RenderFriends(update.Friends);
        if (Interlocked.Exchange(ref _serverDisconnected, 0) != 0 && _client?.IsConnected != true)
            _status.Text = "Server connection ended. Connect again to refresh Steam and rejoin.";
        _root.Visible = show;
        if (!show)
        {
            _panel.Visible = false;
            if (_client != null) Disconnect();
        }
    }

    private async Task ConnectAsync()
    {
        if (_working) return;
        if (_client?.IsConnected == true) { await RefreshFriendsAsync(); return; }
        if (_gameBridge?.IsConnected == true) { _status.Text = "Disconnect the current game before opening another server session."; return; }
        _gameBridge = null;
        if (ModLaunchSettings.Enabled) { _status.Text = "Turn mods off in the launcher and restart first."; return; }
        _working = true;
        _connectionStop?.Dispose();
        _connectionStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        CancellationToken connecting = _connectionStop.Token;
        try
        {
            _status.Text = "Starting Steam on your server... This can take up to 90 seconds.";
            if (_client != null) { DetachMenuCallbacks(_client); await _client.DisposeAsync(); _client = null; }
            if (!Uri.TryCreate(_endpoint.Text.Trim(), UriKind.Absolute, out var endpoint)) throw new ArgumentException("Enter a valid server address.");
            _client = await BridgeClient.ConnectAsync(endpoint, _token.Text.Trim(), RuntimeServices.GetLocalPlayerId(), connecting);
            _client.EventReceived += OnMenuEvent;
            _client.Disconnected += OnMenuDisconnected;
            Interlocked.Exchange(ref _serverDisconnected, 0);
            connecting.ThrowIfCancellationRequested();
            SaveEndpoint(endpoint.AbsoluteUri, _token.Text.Trim());
            _status.Text = "Connected. Loading Steam friends and invitations...";
            RenderFriends(await _client.ExecuteAsync("friends", ct: connecting));
        }
        catch (Exception error)
        {
            _status.Text = error is ArgumentException or InvalidOperationException ? error.Message : "Could not connect. Check the server address, pairing token and Tailscale connection.";
            GD.Print("Steam bridge connection: ", error.GetType().Name);
            if (_client != null) { DetachMenuCallbacks(_client); await _client.DisposeAsync(); _client = null; }
        }
        finally { _working = false; }
    }

    private async Task RefreshFriendsAsync()
    {
        if (_working) return;
        if (_client?.IsConnected != true) { _status.Text = "Connect to your server first."; return; }
        _working = true;
        try
        {
            _status.Text = "Refreshing Steam friends and invitations...";
            RenderFriends(await _client.ExecuteAsync("friends", ct: _stop.Token));
        }
        catch (Exception error)
        {
            _status.Text = "Could not refresh friends. Check the connection, then try again.";
            GD.Print("Steam bridge refresh: ", error.GetType().Name);
        }
        finally { _working = false; }
    }

    private void RenderFriends(JsonElement friends)
    {
        foreach (Node child in _friends.GetChildren()) { _friends.RemoveChild(child); child.QueueFree(); }
        int joinable = 0, playing = 0;
        foreach (var friend in friends.EnumerateArray())
        {
            string name = friend.GetProperty("name").GetString() ?? "Friend";
            playing++;
            if (!ulong.TryParse(friend.GetProperty("lobbyId").GetString(), out ulong lobby) || lobby == 0)
            {
                _friends.AddChild(Label(name + " is playing; waiting for Steam's lobby details.", 24));
                continue;
            }
            bool invited = friend.TryGetProperty("invited", out var invite) && invite.GetBoolean();
            _friends.AddChild(Button((invited ? "Join invitation from " : "Join ") + name, () => _ = JoinAsync(lobby)));
            joinable++;
        }
        _status.Text = joinable > 0 ? "Connected. Choose a friend or invitation to join." : playing > 0
            ? "Connected. Steam sees friends playing, but has not provided a joinable lobby yet. Refresh, ask for a new invite, or paste a lobby link."
            : "Connected. Waiting for Steam to advertise a joinable lobby or deliver an invitation. Refresh, ask your friend to send a new invite while connected, or paste their lobby link.";
    }

    private void OnMenuEvent(JsonElement message)
    {
        if (message.GetProperty("type").GetString() == "friends")
            Interlocked.Exchange(ref _pendingFriends, new FriendUpdate(message.GetProperty("data").Clone()));
    }
    private void OnMenuDisconnected() => Interlocked.Exchange(ref _serverDisconnected, 1);
    private void DetachMenuCallbacks(BridgeClient bridge)
    {
        bridge.EventReceived -= OnMenuEvent;
        bridge.Disconnected -= OnMenuDisconnected;
    }

    private async Task JoinAsync(ulong lobby)
    {
        if (_working) return;
        if (_client?.IsConnected != true) { _status.Text = "Connect to your server first."; return; }
        _working = true;
        var bridge = _client;
        DetachMenuCallbacks(bridge);
        _gameBridge = bridge;
        _client = null;
        var initializer = new SteamBridgeConnectionInitializer(bridge, lobby);
        try
        {
            _panel.Visible = false;
            var menu = _game?.MainMenu ?? throw new InvalidOperationException("Return to the main menu before joining.");
            await OpenJoinScreenAsync(menu, initializer);
            _status.Text = "Lobby join finished. If the game showed a mismatch, match its version and turn off gameplay mods.";
        }
        catch (Exception error)
        {
            _status.Text = "The game could not join that lobby. Check that your friend is hosting the same game version.";
            GD.Print("Steam bridge join: ", error.GetType().Name);
        }
        finally
        {
            await initializer.ReleaseIfUnusedAsync();
            _working = false;
        }
    }

    private static Task OpenJoinScreenAsync(object menu, IClientConnectionInitializer initializer)
    {
        // Use the normal join flow on the cached screen without opening the desktop friend picker.
        // Opening that picker runs FastMpJoin when Steamworks is absent, creating a competing ENet connection.
        Type menuType = menu.GetType();
        menuType.GetMethod("OpenMultiplayerSubmenu", Type.EmptyTypes)!.Invoke(menu, null);
        Type screenType = menuType.Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NJoinFriendScreen", true)!;
        object stack = menuType.GetProperty("SubmenuStack")!.GetValue(menu)!;
        object screen = stack.GetType().GetMethod("GetSubmenuType", [typeof(Type)])!.Invoke(stack, [screenType])!;
        return (Task)screenType.GetMethod("JoinGameAsync", [typeof(IClientConnectionInitializer)])!.Invoke(screen, [initializer])!;
    }

    private void LoadEndpoint()
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(EndpointFile);
            if (!File.Exists(path)) return;
            _savedEndpoint = File.ReadAllText(path).Trim();
            _endpoint.Text = _savedEndpoint;
            if (OperatingSystem.IsIOS())
            {
                byte[]? token = Keychain().Read(TokenAccount(_savedEndpoint));
                if (token != null) { _token.Text = Encoding.UTF8.GetString(token); CryptographicOperations.ZeroMemory(token); }
            }
        }
        catch (Exception error) { GD.Print("Steam bridge settings: ", error.GetType().Name); }
    }

    private void SaveEndpoint(string endpoint, string token)
    {
        // Bind stored credentials to the exact selected endpoint; never send an old token to an edited host.
        if (OperatingSystem.IsIOS())
        {
            byte[] bytes = Encoding.UTF8.GetBytes(token);
            try { Keychain().Write(TokenAccount(endpoint), bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        File.WriteAllText(ProjectSettings.GlobalizePath(EndpointFile), endpoint);
        _savedEndpoint = endpoint;
    }

    private static AppleKeychain Keychain() => new(AppleKeychain.ApplicationIdentifier() + ".steam-bridge");
    private static string TokenAccount(string endpoint) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)));

    private static bool TryLobby(string text, out ulong lobby)
    {
        text = text.Trim();
        if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out lobby)) return lobby != 0;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "steam" || uri.Host != "joinlobby") return false;
        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] == "2868840" && ulong.TryParse(parts[1], out lobby) && lobby != 0;
    }

    private void ClosePanel()
    {
        _panel.Visible = false;
        // Hiding the panel does not end an authenticated foreground game session.
    }

    private void Disconnect()
    {
        _connectionStop?.Cancel();
        if (_client != null) { DetachMenuCallbacks(_client); _ = _client.DisposeAsync(); _client = null; }
        if (_gameBridge != null) { _ = _gameBridge.DisposeAsync(); _gameBridge = null; }
        if (_status != null && IsInstanceValid(_status)) _status.Text = "Disconnected. Steam on your server will close.";
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationPaused) Disconnect();
    }

    public override void _ExitTree()
    {
        _stop.Cancel();
        Disconnect();
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, ClipText = true, CustomMinimumSize = new Vector2(0, 64) };
        button.AddThemeFontSizeOverride("font_size", 26);
        button.Pressed += action;
        return button;
    }
    private static Label Label(string text, int size)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        label.AddThemeFontSizeOverride("font_size", size);
        return label;
    }
    private static LineEdit Input(string placeholder)
    {
        var input = new LineEdit { PlaceholderText = placeholder, CustomMinimumSize = new Vector2(0, 64) };
        input.AddThemeFontSizeOverride("font_size", 26);
        return input;
    }
}
