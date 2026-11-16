#!/bin/bash
set -euo pipefail
if [ "${STEAM_BRIDGE_PROFILE_ENTERED:-}" != 1 ]; then
    export STEAM_BRIDGE_PROFILE_ENTERED=1
    exec aa-exec -p sts2-steam-bridge -- "$0" "$@"
fi
umask 077
mkdir -p "$HOME/.vnc" "$HOME/logs"
# Password is provisioned in the private persistent home, never in the image or repository.
test -s "$HOME/.vnc/passwd" || { echo 'Provision .vnc/passwd before starting the desktop.' >&2; exit 1; }
rm -f /tmp/.X99-lock /tmp/.X11-unix/X99
Xvfb :99 -screen 0 1280x800x24 -nolisten tcp -ac &
xvfb_pid=$!
trap 'kill "$xvfb_pid" 2>/dev/null || true' EXIT
python3 - <<'PY'
import ctypes, time
x = ctypes.CDLL('libX11.so.6')
x.XOpenDisplay.restype = ctypes.c_void_p
x.XCloseDisplay.argtypes = [ctypes.c_void_p]
for attempt in range(100):
    display = x.XOpenDisplay(None)
    if display:
        x.XCloseDisplay(display)
        break
    time.sleep(0.1)
else:
    raise SystemExit('X server did not become ready.')
PY
dbus-run-session -- bash -c '
    openbox > "$HOME/logs/openbox.log" 2>&1 &
    x11vnc -display :99 -forever -shared -rfbauth "$HOME/.vnc/passwd" -rfbport 5900 -localhost > "$HOME/logs/vnc.log" 2>&1 &
    steam -cef-disable-gpu > "$HOME/logs/steam.log" 2>&1 &
    if [ -x /opt/bridge/STS2Mobile.SteamBridge ]; then
        mkdir -p "$HOME/bridge-state"
        printf "%s\n" 2868840 > "$HOME/bridge-state/steam_appid.txt"
        (cd "$HOME/bridge-state" && /opt/bridge/STS2Mobile.SteamBridge) > "$HOME/logs/bridge.log" 2>&1 &
    fi
    websockify --web=/usr/share/novnc/ 6080 localhost:5900 > "$HOME/logs/web-desktop.log" 2>&1
'
