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
    private bool _working;
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
            _panel.Visible = !_panel.Visible;
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
        _friends = new VBoxContainer(); column.AddChild(_friends);
        _lobby = Input("Lobby ID or steam://joinlobby/... link"); column.AddChild(_lobby);
        column.AddChild(Button("Join lobby", () =>
        {
            if (TryLobby(_lobby.Text, out ulong lobby)) _ = JoinAsync(lobby);
            else _status.Text = "Paste a lobby ID or a Slay the Spire 2 Steam lobby link.";
        }));
        column.AddChild(Button("Close", () => _panel.Visible = false));
        scroll.AddChild(column); margin.AddChild(scroll); _panel.AddChild(margin); _root.AddChild(_panel);
        LoadEndpoint();
        _endpoint.TextChanged += text => { if (text.Trim() != _savedEndpoint) _token.Text = ""; };
    }

    public override void _Process(double delta)
    {
        bool show = _game != null && IsInstanceValid(_game) && _game.MainMenu is { } menu && IsInstanceValid(menu) && menu.IsVisibleInTree();
        _root.Visible = show;
        if (!show) _panel.Visible = false;
    }

    private async Task ConnectAsync()
    {
        if (_working) return;
        if (ModLaunchSettings.Enabled) { _status.Text = "Turn mods off in the launcher and restart first."; return; }
        _working = true;
        try
        {
            _status.Text = "Connecting...";
            if (_client != null) { await _client.DisposeAsync(); _client = null; }
            if (!Uri.TryCreate(_endpoint.Text.Trim(), UriKind.Absolute, out var endpoint)) throw new ArgumentException("Enter a valid server address.");
            _client = await BridgeClient.ConnectAsync(endpoint, _token.Text.Trim(), RuntimeServices.GetLocalPlayerId(), _stop.Token);
            SaveEndpoint(endpoint.AbsoluteUri, _token.Text.Trim());
            JsonElement friends = await _client.ExecuteAsync("friends", ct: _stop.Token);
            foreach (Node child in _friends.GetChildren()) { _friends.RemoveChild(child); child.QueueFree(); }
            int count = 0;
            foreach (var friend in friends.EnumerateArray())
            {
                if (!ulong.TryParse(friend.GetProperty("lobbyId").GetString(), out ulong lobby) || lobby == 0) continue;
                string name = friend.GetProperty("name").GetString() ?? "Friend";
                _friends.AddChild(Button("Join " + name, () => _ = JoinAsync(lobby)));
                count++;
            }
            _status.Text = count == 0 ? "Connected. No friends are hosting a lobby yet. Ask a friend to host the same game version, then connect again, or paste their lobby link." : "Connected. Choose a friend to join.";
        }
        catch (Exception error)
        {
            _status.Text = error is ArgumentException or InvalidOperationException ? error.Message : "Could not connect. Check the server address, pairing token and Tailscale connection.";
            GD.Print("Steam bridge connection: ", error.GetType().Name);
            if (_client != null) { await _client.DisposeAsync(); _client = null; }
        }
        finally { _working = false; }
    }

    private async Task JoinAsync(ulong lobby)
    {
        if (_working) return;
        if (_client?.IsConnected != true) { _status.Text = "Connect to your server first."; return; }
        _working = true;
        var bridge = _client;
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

    public override void _ExitTree()
    {
        _stop.Cancel();
        if (_client != null) _ = _client.DisposeAsync();
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(0, 64) };
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
