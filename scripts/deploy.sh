#!/usr/bin/env bash
# Push local builds to the game box.
#   scripts/deploy.sh            # server + web
#   scripts/deploy.sh server     # just the dedicated server (restarts it)
#   scripts/deploy.sh web        # just the web build
# Env overrides: DEPLOY_HOST (default jam.pmullins.dev; use the Elastic IP before DNS resolves),
#                DEPLOY_KEY (default ~/.ssh/nov26jam)
set -euo pipefail

HOST="${DEPLOY_HOST:-jam.pmullins.dev}"
KEY="${DEPLOY_KEY:-$HOME/.ssh/nov26jam}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WHAT="${1:-all}"

remote() { ssh -i "$KEY" -o StrictHostKeyChecking=accept-new "ubuntu@$HOST" "$@"; }

deploy_server() {
  [ -f "$ROOT/Builds/Server/Server.x86_64" ] || { echo "No server build at Builds/Server" >&2; exit 1; }
  echo "==> Uploading server build"
  tar -C "$ROOT/Builds/Server" --exclude="*_BackUpThisFolder_ButDontShipItWithYourGame" -czf - . | remote \
    'rm -rf /opt/game/* && tar -xzf - -C /opt/game && chmod +x /opt/game/Server.x86_64 && sudo systemctl restart game-server'
  remote 'sleep 2; systemctl status game-server --no-pager | head -5'
}

deploy_web() {
  [ -f "$ROOT/Builds/Web/index.html" ] || { echo "No web build at Builds/Web" >&2; exit 1; }
  echo "==> Uploading web build"
  tar -C "$ROOT/Builds/Web" -czf - . | remote 'rm -rf /srv/web/* && tar -xzf - -C /srv/web'
}

case "$WHAT" in
  server) deploy_server ;;
  web) deploy_web ;;
  all) deploy_server; deploy_web ;;
  *) echo "usage: $0 [server|web|all]" >&2; exit 2 ;;
esac
echo "==> Done: https://$HOST/"
