#!/usr/bin/env python3
"""Prepare the experimental Linux bridge without changing the released iPhone app."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
WORK = ROOT / '.work/steam-bridge'
PUBLISH = ROOT / '.work/steam-bridge-publish'
DATA = Path(os.environ.get('STEAM_BRIDGE_HOME', Path.home() / '.local/state/sts2-steam-bridge')).resolve()
IMAGE = 'local/sts2-steam-bridge-desktop'
NATIVE_URL = 'https://raw.githubusercontent.com/rlabrecque/Steamworks.NET/2024.8.0/com.rlabrecque.steamworks.net/Plugins/libsteam_api.so'
NATIVE_SHA256 = '0ed3199107e021fa27e19a642148514ca5337563569c48fc094c27f907dd47a7'
SECCOMP_URL = 'https://raw.githubusercontent.com/moby/profiles/f862455628f78f84c937e967e4a03902fb1dee11/seccomp/default.json'
SECCOMP_SHA256 = '3320527d4568265ac97a077609b1bfd4cfd0d789eeb9239b7e094b1526675406'


def verified_download(url, sha256):
    with urllib.request.urlopen(url, timeout=60) as response:
        data = response.read(8 * 1024 * 1024 + 1)
    if hashlib.sha256(data).hexdigest() != sha256:
        raise ValueError('Dependency checksum mismatch: ' + url)
    return data


def private_secret(path, value):
    try:
        descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    except FileExistsError:
        if path.is_symlink() or path.stat().st_uid != os.getuid() or path.stat().st_mode & 0o077:
            raise ValueError('Expected an owner-only secret file: ' + str(path))
    else:
        with os.fdopen(descriptor, 'w') as stream:
            stream.write(value)


def run(arguments):
    environment = os.environ.copy()
    # Keep buildx state local; do not change the user's Docker credential/config directory.
    config = ROOT / '.tools/steam-bridge-docker'
    config.mkdir(parents=True, exist_ok=True)
    environment['DOCKER_CONFIG'] = str(config)
    subprocess.run(arguments, cwd=ROOT, env=environment, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--desktop', action='store_true', help='build and start the private Steam desktop')
    parser.add_argument('--load-profile', action='store_true', help='use a one-shot privileged Docker helper to load ONLY the supplied AppArmor profile')
    args = parser.parse_args()
    if os.uname().machine != 'x86_64':
        parser.error('Linux x86_64 is required.')
    WORK.mkdir(parents=True, exist_ok=True)
    DATA.mkdir(mode=0o700, parents=True, exist_ok=True)
    if DATA.is_symlink() or DATA.stat().st_uid != os.getuid() or DATA.stat().st_mode & 0o077:
        raise ValueError('Steam data must be in a private directory owned by this user.')
    (DATA / '.vnc').mkdir(mode=0o700, exist_ok=True)
    private_secret(DATA / 'bridge-token', secrets.token_hex(32))
    private_secret(DATA / '.vnc/password', secrets.token_hex(4))
    profile = json.loads(verified_download(SECCOMP_URL, SECCOMP_SHA256))
    # Preserve Docker's normal syscall policy, adding just the pivot used by Steam's nested sandbox.
    profile['syscalls'].append({'names': ['pivot_root'], 'action': 'SCMP_ACT_ALLOW',
                                'comment': 'Steam bubblewrap pivots inside its own user/mount namespace.'})
    seccomp = WORK / 'seccomp.json'
    seccomp.write_text(json.dumps(profile, indent=2) + '\n')
    dotnet = shutil.which('dotnet') or str(ROOT / 'tools/ios-dotnet/dotnet')
    run([dotnet, 'publish', 'src/STS2Mobile.SteamBridge', '-c', 'Release', '-r', 'linux-x64',
         '--self-contained', 'true', '-o', str(PUBLISH)])
    (PUBLISH / 'libsteam_api.so').write_bytes(verified_download(NATIVE_URL, NATIVE_SHA256))
    env_file = WORK / 'compose.env'
    previous = {}
    if env_file.exists():
        previous = dict(line.split('=', 1) for line in env_file.read_text().splitlines()
                        if line.startswith(('STEAM_BRIDGE_DESKTOP_BIND=', 'STEAM_BRIDGE_TRANSPORT=', 'STEAM_BRIDGE_STEAM_ID=')))
    def setting(name, default):
        return os.environ.get(name, previous.get(name, default))
    desktop_bind = setting('STEAM_BRIDGE_DESKTOP_BIND', '127.0.0.1')
    if desktop_bind != '127.0.0.1':
        import ipaddress
        address = ipaddress.ip_address(desktop_bind)
        if address not in ipaddress.ip_network('100.64.0.0/10'):
            raise ValueError('Remote desktop binds must be a private tailnet address.')
    transport = setting('STEAM_BRIDGE_TRANSPORT', 'disabled')
    steam_id = setting('STEAM_BRIDGE_STEAM_ID', '')
    if transport not in ('disabled', 'sockets', 'messages', 'legacy'):
        raise ValueError('Unsupported STEAM_BRIDGE_TRANSPORT.')
    if steam_id and (not steam_id.isascii() or not steam_id.isdecimal() or not 0 < int(steam_id) < 2**64):
        raise ValueError('STEAM_BRIDGE_STEAM_ID must be a valid Steam ID.')
    if transport != 'disabled' and not steam_id:
        raise ValueError('Pin STEAM_BRIDGE_STEAM_ID before enabling gameplay forwarding.')
    env_file.write_text(f'STEAM_BRIDGE_HOME={DATA}\nSTEAM_BRIDGE_BUILD={PUBLISH}\nSTEAM_BRIDGE_SECCOMP={seccomp}\n'
                        f'STEAM_BRIDGE_DESKTOP_BIND={desktop_bind}\nSTEAM_BRIDGE_TRANSPORT={transport}\n'
                        f'STEAM_BRIDGE_STEAM_ID={steam_id}\n')
    if args.desktop or args.load_profile:
        run(['docker', 'build', '-t', IMAGE, 'ops/steam-bridge'])
    if args.load_profile:
        run(['docker', 'run', '--rm', '--user', '0:0', '--privileged', '--security-opt', 'apparmor=unconfined',
             '-v', '/sys/kernel/security:/sys/kernel/security',
             '-v', f'{ROOT}/ops/steam-bridge/steam-bridge.apparmor:/tmp/steam-bridge.apparmor:ro',
             '--entrypoint', 'apparmor_parser', IMAGE, '-r', '/tmp/steam-bridge.apparmor'])
    if args.desktop:
        run(['docker', 'run', '--rm', '--user', '1000:1000', '-v', f'{DATA}:/home/steam',
             '--entrypoint', 'bash', IMAGE, '-c',
             'x11vnc -storepasswd "$(cat /home/steam/.vnc/password)" /home/steam/.vnc/passwd >/dev/null'])
        run(['docker', 'compose', '--env-file', str(env_file), '-f', 'ops/steam-bridge/compose.yaml', 'up', '-d'])
        print(f'Steam desktop: http://{desktop_bind}:6091/vnc.html?autoconnect=1&resize=scale')
        print('VNC password file:', DATA / '.vnc/password')
        print(f'Bridge health: http://127.0.0.1:6092/health (transport: {transport})')
    print('Prepared bridge:', PUBLISH)


if __name__ == '__main__':
    main()
