# Experimental Linux Steam bridge

Goal: an iPhone running the game locally joins a friend's **unmodified** Steam game through a Linux machine that owns the Steam networking session. The server does not simulate the game or stream video.

This is a prototype. It is **not included or enabled in the v1.2.1 IPA**. The server, shared phone-side client and protocol have automated integration coverage with an in-process test backend; a live vanilla-game handshake has not been verified. Gameplay forwarding defaults to `disabled`. Do not call this working vanilla multiplayer until the acceptance test below passes.

## Components

- `src/STS2Mobile.SteamBridge`: Linux x64 ASP.NET service and Steamworks.NET backend. Steam calls run on one dedicated thread. A single authenticated phone may control a session. Lobby join, matching-account checks, host-only forwarding, bounded queues and disconnect cleanup are implemented.
- `src/STS2Mobile.SteamBridge.Protocol`: shared .NET 9/10 client and wire framing, with no game or Steamworks dependency. The client binds to the launcher's authenticated Steam ID and aborts on identity changes. It never automatically retries gameplay actions.
- `tools/SteamBridgeInspector`: reads the owner's desktop `sts2.dll` as metadata, without executing it. Reports the assembly hash, Steam API call sites, channel/port constants and the public/protected transport API. Reports and game inputs stay in ignored local directories.
- This folder: isolated Steam desktop with browser/VNC access, reproducible dependency downloads, and container-specific namespace settings.

The app's `NetClient` adapter, connection settings UI and vanilla lobby entry point still require implementation against the inspected game API. The experimental server backends (`sockets`, `messages`, `legacy`) are diagnostic implementations; the exact API, reliability and channel framing must be selected from the game inspection, not guessed. In particular the legacy receive API does not expose reliability, so it is not a validated gameplay backend.

## Prepare on Linux

Requires Docker access, Linux x86_64 and the repository's pinned .NET SDK (`10.0.401`, under `tools/ios-dotnet` or on PATH). The publish output is self-contained; the running container needs no SDK.

```sh
python3 ops/steam-bridge/prepare-linux.py --desktop --load-profile
```

`--load-profile` runs a short-lived privileged Docker helper that loads **only** `sts2-steam-bridge` into AppArmor. It writes no host sysctl and changes no other AppArmor profile. Steam itself runs as uid 1000, with no effective capabilities. A `SYS_ADMIN` bounding capability enables namespace syscalls in Docker's default seccomp policy. The prepared syscall profile is Moby's pinned default plus `pivot_root`, required by bubblewrap. The named AppArmor profile permits Steam's nested sandbox; `aa-exec` removes the extra unconfined label that otherwise sends Ubuntu 26.04's children into `unprivileged_userns`.

The AppArmor profile must be loaded again after reboot before starting this container. The setup command preserves existing tokens and Steam data. App state defaults to `~/.local/state/sts2-steam-bridge`, mode 0700; it is outside the checkout. Set `STEAM_BRIDGE_HOME` to choose another private directory. The current Compose image assumes host uid 1000.

Only loopback ports are published:

- `127.0.0.1:6091`: private Steam desktop, protected by a generated VNC password.
- `127.0.0.1:6092`: bridge diagnostics and authenticated WebSocket endpoint.

To sign in from another computer, tunnel the desktop:

```sh
ssh -L 6091:127.0.0.1:6091 <you>@<your-server>
```

Open `http://127.0.0.1:6091/vnc.html?autoconnect=1&resize=scale`. Read the VNC password from the private `.vnc/password` file in the data directory. Sign in **in Steam**, including Steam Guard, and install your owned game. Do not put Steam passwords, cookies, login QR codes, app files, bridge tokens or VNC passwords in issues, source control or CI logs.

The bridge never receives a Steam password: it uses the regular running Steam client. `/health` exposes readiness only, without account identity. `Authorization: Bearer <bridge-token>` is required for `/bridge`; query-string credentials are refused.

## Audit and enable the correct transport

```sh
tools/ios-dotnet/dotnet run --project tools/SteamBridgeInspector -- \
  '/path/to/owned/game/sts2.dll' > .work/steam-bridge/game-transport.json
```

Inspect `SteamClient`, its connection initializer and the packet receive/send methods. Establish the exact Steam API family, virtual port, reliability semantics, game channel framing, initialization callbacks and version/mod/data handshake. Use the matching original game version and no gameplay mods. The server must be signed into the same Steam ID as the launcher.

Only after auditing, pin `STEAM_BRIDGE_STEAM_ID` and select `STEAM_BRIDGE_TRANSPORT` when starting Compose. `connect` refuses a disabled transport and forwarding refuses every peer except the joined lobby's owner. The prototype only joins another host's lobby; it does not host.

The production phone client accepts only `wss://<host>/bridge` on port 443, with normal certificate validation. Put the bridge behind your existing TLS proxy or private network before phone testing. It does not connect to the local plaintext diagnostic port. Do not expose the Steam desktop publicly.

## Protocol

Server greeting: `{"type":"hello","protocol":1,"status":{...}}`. Steam IDs and lobby IDs use decimal strings to preserve all 64 bits.

Text commands have `id` and `type`: `status`, `friends`, `join` (`lobbyId`), `connect` (`virtualPort`, `channels`) and `disconnect`. Replies have `type=result`, the request `id`, `ok`, and `data` or `error`. Control commands are bounded to 8 KiB and have a 20-second deadline. Native transport opening is distinct from the game's successful handshake.

Binary messages preserve an entire original game packet in one WebSocket message: ASCII `S2B1` (4 bytes), reliability byte (0/1), reserved zero byte, little-endian channel u16, peer Steam ID u64, then the unchanged payload. Payload limit is 64 KiB. Oversize, invalid envelopes, unknown peers, unauthorized channels, slow-reader queue overflow and account changes terminate or refuse the session. Game packets and credentials are not logged.

## Validation

```sh
tools/ios-dotnet/dotnet test tests/SteamBridge.Tests
tools/ios-dotnet/dotnet build tools/SteamBridgeInspector
```

Live acceptance: friend hosts an unmodified matching desktop game; the iPhone joins through Linux; both select characters, enter combat, play cards, finish a turn and change rooms with no desync. Then verify dropped connections fail clearly and leave no orphaned lobby. This requires a new experimental IPA with the game adapter, a signed-in Steam account and two players; automated protocol tests do not establish that compatibility.

## Dependency provenance

Steam launcher: Valve's official Linux package, SHA-256 pinned in `Dockerfile`; it updates itself normally at runtime. Steamworks.NET: MIT-licensed NuGet 2024.8.0, locked by NuGet content hash. Its Linux native companion is fetched from the maintainer's release tag and verified by SHA-256. Moby's Apache-2.0 seccomp policy is fetched from commit `f862455628f78f84c937e967e4a03902fb1dee11` and verified before applying the local pivot rule. No game files or Steam credentials are included in source, images or CI artifacts.
