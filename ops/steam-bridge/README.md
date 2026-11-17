# Experimental Linux Steam bridge

Goal: an iPhone running the game locally joins a friend's **unmodified** Steam game through a Linux machine that owns the Steam networking session. The server does not simulate the game or stream video.

This is a prototype. It is **not included in v1.2.1**. Version **1.2.6** includes the experimental phone adapter and pairing UI; forwarding requires an explicitly configured Linux server. The server, phone-side transport adapter, pairing UI and protocol have automated integration coverage with an in-process test backend; a live vanilla-game handshake has not been verified. Gameplay forwarding defaults to `disabled`. Do not call this working vanilla multiplayer until the acceptance test below passes.

## Components

- `src/STS2Mobile.SteamBridge`: Linux x64 ASP.NET service and Steamworks.NET backend. Steam calls run on one dedicated thread. A single authenticated phone may control a session. Lobby join, matching-account checks, host-only forwarding, bounded queues and disconnect cleanup are implemented.
- `src/STS2Mobile.SteamBridge.Protocol`: shared .NET 9/10 client and wire framing, with no game or Steamworks dependency. The client binds to the launcher's authenticated Steam ID and aborts on identity changes. It never automatically retries gameplay actions.
- `tools/SteamBridgeInspector`: reads the owner's desktop `sts2.dll` as metadata, without executing it. Reports the assembly hash, Steam API call sites, channel/port constants and the public/protected transport API. Reports and game inputs stay in ignored local directories.
- This folder: isolated Steam desktop with browser/VNC access, reproducible dependency downloads, and container-specific namespace settings.

Version 1.2.6 includes a `Steam Bridge` button in the game's main menu for the inspected 0.113 multiplayer interface. Pair it with your own server, then choose a hosting friend or paste a Steam lobby link. The pairing token is stored in the iPhone's device-only Keychain and is bound to the selected endpoint. The UI requires mods to be off. Friend discovery waits for Steam's initial presence synchronization, refreshes without restarting Steam, and listens for new lobby invitations while connected. Invitations are retained for five minutes, including invitations to private lobbies. An invite sent while the server was offline may need to be resent after connecting. It invokes the game's normal join flow on its cached screen without opening the desktop friend picker, whose automatic fallback would create a competing transport.

The owned Linux public-beta v0.113.1 assembly was inspected: its SteamClient uses SteamNetworkingSockets.ConnectP2P on virtual port 0, sends the original packet bytes with flags 0 (unreliable) or 8 (reliable), ignores the outgoing game channel argument, and receives the native message's channel. The bridge and phone adapter implement that mapping with `sockets`. This establishes transport framing, not a successful vanilla handshake. The other diagnostic backends (`messages`, `legacy`) remain unvalidated; legacy receive does not expose reliability.

## Steam runs only during a phone session

The permanent HTTP endpoint never initializes Steam's game API. An authenticated phone connection starts the dedicated container's Steam client and a short-lived game worker. The worker owns the native game session; when the phone disconnects, it exits before the controller closes Steam. Stored sign-in credentials remain private on disk, but no Steam client or game worker runs while idle. `/health` reports `ready: false, state: idle` in that state; this is expected.

The iPhone disconnects on backgrounding, leaving the game, or an explicit Disconnect. Closing the pairing panel keeps the connection while the game remains in the foreground; use Disconnect to end it explicitly. An abrupt connection loss is detected by 15-second WebSocket pings with a 15-second reply deadline. Resuming requires an explicit connection; gameplay packets are never replayed. While connected, this session still uses the account for Slay the Spire 2.

`STEAM_BRIDGE_MANAGE_STEAM=1` is set by the supplied Compose file and is accepted only inside Docker. Outside this dedicated container, the service can end its game worker but does not terminate the user's desktop Steam client.

## Prepare on Linux

Requires Docker access, Linux x86_64 and the repository's pinned .NET SDK (`10.0.401`, under `tools/ios-dotnet` or on PATH). The publish output is self-contained; the running container needs no SDK.

```sh
python3 ops/steam-bridge/prepare-linux.py --desktop --load-profile
```

`--load-profile` runs a short-lived privileged Docker helper that loads **only** `sts2-steam-bridge` into AppArmor. It writes no host sysctl and changes no other AppArmor profile. Steam itself runs as uid 1000, with no effective capabilities. A `SYS_ADMIN` bounding capability enables namespace syscalls in Docker's default seccomp policy. The prepared syscall profile is Moby's pinned default plus `pivot_root`, required by bubblewrap. The named AppArmor profile permits Steam's nested sandbox; `aa-exec` removes the extra unconfined label that otherwise sends Ubuntu 26.04's children into `unprivileged_userns`.

The AppArmor profile must be loaded again after reboot before starting this container. The setup command preserves existing tokens, Steam data and the selected transport, account pin and desktop bind in the ignored `.work/steam-bridge/compose.env`. Explicit `STEAM_BRIDGE_*` environment values override those settings on a later setup run. App state defaults to `~/.local/state/sts2-steam-bridge`, mode 0700; it is outside the checkout. Set `STEAM_BRIDGE_HOME` to choose another private directory. The current Compose image assumes host uid 1000.

By default only loopback ports are published:

- `127.0.0.1:6091`: private Steam desktop, protected by a generated VNC password.
- `127.0.0.1:6092`: bridge diagnostics and authenticated WebSocket endpoint.

For private tailnet desktop access, set `STEAM_BRIDGE_DESKTOP_BIND` to this server's Tailscale IPv4 address before setup. Other non-loopback addresses are refused. Alternatively tunnel the default desktop:

```sh
ssh -L 6091:127.0.0.1:6091 <you>@<your-server>
```

Open `http://127.0.0.1:6091/vnc.html?autoconnect=1&resize=scale`. Read the VNC password from the private `.vnc/password` file in the data directory. Steam is normally closed while idle. For initial sign-in or maintenance, open it explicitly:

```sh
docker exec -d sts2-steam-bridge aa-exec -p sts2-steam-bridge -- steam -cef-disable-gpu
```

Sign in **in Steam**, including Steam Guard, install your owned game, and choose **Steam > Exit** when finished. Later phone connections start it automatically using that saved sign-in. Do not put Steam passwords, cookies, login QR codes, app files, bridge tokens or VNC passwords in issues, source control or CI logs.

The bridge never receives a Steam password: it uses the regular running Steam client. `/health` exposes readiness only, without account identity. `Authorization: Bearer <bridge-token>` is required for `/bridge`; query-string credentials are refused.

## Audit and enable the correct transport

```sh
tools/ios-dotnet/dotnet run --project tools/SteamBridgeInspector -- \
  '/path/to/owned/game/sts2.dll' > .work/steam-bridge/game-transport.json
```

Inspect `SteamClient`, its connection initializer and the packet receive/send methods. Establish the exact Steam API family, virtual port, reliability semantics, game channel framing, initialization callbacks and version/mod/data handshake. Use the matching original game version and no gameplay mods. The server must be signed into the same Steam ID as the launcher.

After auditing, pin `STEAM_BRIDGE_STEAM_ID` and select `STEAM_BRIDGE_TRANSPORT=sockets` when starting Compose. The startup script creates a private `bridge-state/steam_appid.txt` and launches the bridge from that directory, alongside the ordinary Steam client. `connect` refuses a disabled transport and forwarding refuses every peer except the joined lobby's owner. The prototype only joins another host's lobby; it does not host.

The production phone client accepts only `wss://<host>/bridge` on port 443, with normal certificate validation. Put the bridge behind your existing TLS proxy or private network before phone testing. It does not connect to the local plaintext diagnostic port. Do not expose the Steam desktop publicly.

## Phone setup

Install version 1.2.6 from this fork's SideStore/AltStore source, or build this checkout with the existing **Build IPA** GitHub Action. Use the same bundle ID and sideloader as your current fork. Enable Tailscale on the iPhone if the server is private, match the host's game version, turn mods off, and open **Steam Bridge** from the game's main menu. Enter `wss://<your-private-server>/bridge` and the token from the server's private `bridge-token` file. Press **Connect to server**, then choose a hosting friend. No credentials or game files belong in the Actions build.

A failed phone connection now shows its stage, exception type and message in the Steam Bridge panel, with the stack trace in the local `full-game-startup.log`. A native Steam connection has reached `Connected` in live tests, but the phone then disconnected before exchanging game packets. The exact phone exception and a successful gameplay session still need live verification.

The prototype only joins another player's Steam lobby. Losing the bridge disconnects the game; it does not replay packets or silently reconnect. A successful server connection or native peer connection is not proof that character selection and gameplay work. Complete the live acceptance test before treating it as supported multiplayer.

## Protocol

Server greeting: `{"type":"hello","protocol":1,"status":{...}}`. Steam IDs and lobby IDs use decimal strings to preserve all 64 bits.

Text commands have `id` and `type`: `status`, `friends`, `join` (`lobbyId`), `connect` (`virtualPort`, `channels`) and `disconnect`. Replies have `type=result`, the request `id`, `ok`, and `data` or `error`. Control commands are bounded to 8 KiB and have a 20-second deadline. Native transport opening is distinct from the game's successful handshake.

Binary messages preserve an entire original game packet in one WebSocket message: ASCII `S2B1` (4 bytes), reliability byte (0/1), reserved zero byte, little-endian channel u16, peer Steam ID u64, then the unchanged payload. Payload limit is 512 KiB, matching the audited Steam send limit. Outgoing queues are bounded to 64 messages. Oversize, invalid envelopes, unknown peers, unauthorized channels, slow-reader queue overflow and account changes terminate or refuse the session. Game packets and credentials are not logged.

## Validation

```sh
tools/ios-dotnet/dotnet test tests/SteamBridge.Tests
tools/ios-dotnet/dotnet build tools/SteamBridgeInspector
```

Live acceptance: friend hosts an unmodified matching desktop game; the iPhone joins through Linux; both select characters, enter combat, play cards, finish a turn and change rooms with no desync. Then verify dropped connections fail clearly and leave no orphaned lobby. This requires the experimental branch IPA, a signed-in Steam account and two players on the exact same game version; automated protocol tests do not establish that compatibility.

## Dependency provenance

Steam launcher: Valve's official Linux package, SHA-256 pinned in `Dockerfile`; it updates itself normally at runtime. Steamworks.NET: MIT-licensed NuGet 2024.8.0, locked by NuGet content hash. Its Linux native companion is fetched from the maintainer's release tag and verified by SHA-256. Moby's Apache-2.0 seccomp policy is fetched from commit `f862455628f78f84c937e967e4a03902fb1dee11` and verified before applying the local pivot rule. No game files or Steam credentials are included in source, images or CI artifacts.
