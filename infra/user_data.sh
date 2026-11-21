#!/bin/bash
# First-boot setup: Caddy, swap, game user/dirs, systemd unit.
set -euxo pipefail

# 1 GB swap: t3.micro only has 1 GB RAM.
if [ ! -f /swapfile ]; then
  fallocate -l 1G /swapfile
  chmod 600 /swapfile
  mkswap /swapfile
  swapon /swapfile
  echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi

# Caddy from the official repo.
apt-get update
apt-get install -y debian-keyring debian-archive-keyring apt-transport-https curl gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' > /etc/apt/sources.list.d/caddy-stable.list
apt-get update
apt-get install -y caddy

# Deploy targets are owned by the ssh user; the service runs as an unprivileged user.
useradd --system --no-create-home --shell /usr/sbin/nologin game || true
mkdir -p /opt/game /srv/web
chown ubuntu:ubuntu /opt/game /srv/web
echo '<h1>nov26jam: no build deployed yet</h1>' > /srv/web/index.html
chown ubuntu:ubuntu /srv/web/index.html

cat > /etc/caddy/Caddyfile <<'CADDYFILE'
${caddyfile}
CADDYFILE
systemctl reload caddy || systemctl restart caddy

cat > /etc/systemd/system/game-server.service <<'UNIT'
${game_service}
UNIT
systemctl daemon-reload
systemctl enable game-server.service

# Let the ssh user restart the game server without a password (used by deploy.sh).
echo 'ubuntu ALL=(root) NOPASSWD: /usr/bin/systemctl restart game-server' > /etc/sudoers.d/game-server
chmod 440 /etc/sudoers.d/game-server
