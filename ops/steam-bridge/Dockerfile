FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN dpkg --add-architecture i386 && apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates curl dbus-x11 file lsof python3 python3-apt xdg-user-dirs xterm zenity xz-utils \
    xvfb xauth x11vnc novnc websockify openbox fonts-dejavu-core apparmor \
    libnss3 libxss1 libasound2t64 libgbm1 libicu74 libssl3t64 \
    libc6:i386 libstdc++6:i386 libgcc-s1:i386 libgl1:i386 libgl1-mesa-dri:i386 \
    libx11-6:i386 libxcb1:i386 libxext6:i386 libxrandr2:i386 libxi6:i386 \
    libasound2t64:i386 libpulse0:i386 libdbus-1-3:i386 libxss1:i386 \
    && rm -rf /var/lib/apt/lists/*
ADD --checksum=sha256:765aba9a0ed339a50226ceb614fcc9879a991ba184098bc8de920efb12c714a4 \
    https://repo.steampowered.com/steam/archive/stable/steam_latest.deb /tmp/steam.deb
RUN dpkg --extract /tmp/steam.deb /tmp/steam-installer && cp -a /tmp/steam-installer/usr/. /usr/ \
    && ln -s /usr/lib/steam/bin_steam.sh /usr/local/bin/steam && rm -rf /tmp/steam.deb /tmp/steam-installer \
    && if getent passwd 1000 >/dev/null; then usermod -l steam -d /home/steam -m "$(getent passwd 1000 | cut -d: -f1)"; else useradd -m -u 1000 steam; fi
COPY start-desktop.sh /usr/local/bin/start-desktop
USER steam
ENV HOME=/home/steam DISPLAY=:99 STEAM_RUNTIME=1
WORKDIR /home/steam
ENTRYPOINT ["/usr/local/bin/start-desktop"]
