#!/usr/bin/env bash
# Throwaway Jellyfin with My Lists + File Transformation and a small public-domain/CC library.
#   dev/demo-server.sh [image-tag=10.11.11] [port=28300]      stop: docker rm -f mylists-demo
set -euo pipefail
cd "$(dirname "$0")/.."
TAG=${1:-10.11.11}; PORT=${2:-28300}; WORK=${WORK:-$PWD/.demo}
DOTNET=${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}
case $TAG in 10.11*) T=10.11;; 12*) T=12;; esac
case $TAG in 10.11*) FT=3.0.1.0/Release-10.11.11;; 12.0*) FT=3.0.1.0/Release-12.0.0;; 12.1*) FT=3.0.1.0/Release-12.1.0;; esac
bash dev/make-demo-media.sh "$WORK/media" >/dev/null
"$DOTNET" build src/Jellyfin.Plugin.MyLists -c Release -p:JellyfinTarget="$T" -v quiet --nologo >/dev/null
rm -rf "$WORK/config/plugins/MyLists"; mkdir -p "$WORK/config/plugins/MyLists"
cp artifacts/bin/"$T"/Release/*/Jellyfin.Plugin.MyLists.dll "$WORK/config/plugins/MyLists/"
if [ ! -d "$WORK/config/plugins/FileTransformation" ]; then
  curl -sfL "https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/releases/download/$FT.zip" -o "$WORK/ft.zip"
  python3 -c 'import sys,zipfile;zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' "$WORK/ft.zip" "$WORK/config/plugins/FileTransformation"
fi
fresh=0; [ -f "$WORK/config/data/jellyfin.db" ] || fresh=1
docker rm -f mylists-demo >/dev/null 2>&1 || true
docker run -d --name mylists-demo -p "$PORT:8096" --user "$(id -u):$(id -g)" -v "$WORK/config:/config" \
  --tmpfs "/cache:uid=$(id -u),gid=$(id -g)" -v "$WORK/media:/media:ro" "jellyfin/jellyfin:$TAG" >/dev/null
for _ in $(seq 1 90); do curl -sf -m 2 "http://localhost:$PORT/System/Info/Public" >/dev/null && break; sleep 2; done
[ $fresh = 1 ] && python3 dev/setup-server.py "http://localhost:$PORT"
echo "demo Jellyfin $TAG on http://localhost:$PORT (admin / test) — My Lists at /MyLists/"
