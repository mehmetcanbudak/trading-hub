#!/usr/bin/env bash
# Build self-contained Windows x64 and ARM64 installers from macOS or Linux.
# Requires .NET SDK 10 and NSIS (macOS: brew install makensis).
# Usage: ./build-windows.sh [version]
set -euo pipefail
cd -- "$(dirname -- "$0")"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# NSIS reads the multilingual installer source using the current UTF-8 locale.
export LC_ALL=en_US.UTF-8 LANG=en_US.UTF-8
command -v makensis >/dev/null || { echo "NSIS is required (macOS: brew install makensis)." >&2; exit 1; }
VERSION="${1:-}"
if [[ -z "$VERSION" ]]; then
  VERSION="$(dotnet msbuild src/TradingHub.App/TradingHub.App.csproj -nologo -getProperty:Version)"
fi
mkdir -p dist
for ARCH in x64 arm64; do
  OUT="dist/win-$ARCH"
  rm -rf -- "$OUT"
  dotnet publish src/TradingHub.App/TradingHub.App.csproj -c Release -r "win-$ARCH" --self-contained true -o "$OUT" -v q -p:Version="$VERSION"
  makensis -V2 -DVERSION="$VERSION" -DARCH="$ARCH" -DSRC="$PWD/$OUT" -DOUT="$PWD/dist/TradingHub-Setup-$ARCH.exe" installer/TradingHub.nsi
  echo "Built: dist/TradingHub-Setup-$ARCH.exe"
done
