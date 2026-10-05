#!/usr/bin/env bash
# Builds release zips (one per Jellyfin line) into dist/ and prints their MD5 sums.
#   scripts/package.sh            -> 10.11 (net9.0) and 12 (net10.0)
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET=${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}
VERSION=$(sed -n 's|.*<PluginVersion>\(.*\)</PluginVersion>.*|\1|p' src/Jellyfin.Plugin.MyLists/Jellyfin.Plugin.MyLists.csproj | head -1)
mkdir -p dist
for T in 10.11 12; do
  case $T in 10.11) FW=net9.0; ABI=1011;; 12) FW=net10.0; ABI=1200;; esac
  "$DOTNET" build src/Jellyfin.Plugin.MyLists -c Release -p:JellyfinTarget=$T -v quiet --nologo
  ZIP="dist/MyLists-$VERSION.$ABI-jellyfin-$T.zip"
  rm -f "$ZIP"
  python3 -c 'import sys,zipfile;z=zipfile.ZipFile(sys.argv[1],"w",zipfile.ZIP_DEFLATED);z.write(sys.argv[2],"Jellyfin.Plugin.MyLists.dll")' "$ZIP" "artifacts/bin/$T/Release/$FW/Jellyfin.Plugin.MyLists.dll"
  echo "$ZIP  md5 $(md5sum "$ZIP" | cut -d' ' -f1)"
done
