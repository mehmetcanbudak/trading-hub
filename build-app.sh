#!/bin/zsh
# Build Trading Hub.app for Apple Silicon. Usage: ./build-app.sh [--install]
# Stage/sign outside Desktop: iCloud extended attributes can break code signing.
# Optional git-ignored build.local.env can supply BUNDLE_ID and SIGN_IDENTITY.
set -e
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

NAME="Trading Hub"
BUNDLE_ID="app.tradinghub.desktop"
SIGN_IDENTITY=""
[ -f build.local.env ] && source ./build.local.env

# TH_OUT / TH_STAGE override the output/staging directories (defaults: dist and a temporary directory).
OUT="${TH_OUT:-dist}"
STAGE="${TH_STAGE:-${TMPDIR:-/tmp}tradinghub-app}"
APP="$STAGE/$NAME.app"
rm -rf "$OUT/publish" "$STAGE"
mkdir -p "$STAGE"
dotnet publish src/TradingHub.App/TradingHub.App.csproj -c Release -r osx-arm64 --self-contained true -o "$OUT/publish" -v q
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
ditto --noextattr --norsrc "$OUT/publish" "$APP/Contents/MacOS"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$NAME</string>
  <key>CFBundleDisplayName</key><string>$NAME</string>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
  <key>CFBundleExecutable</key><string>TradingHub</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSAppSleepDisabled</key><true/>
  <key>CFBundleIconFile</key><string>tradinghub</string>
</dict>
</plist>
PLIST
cp src/TradingHub.App/Assets/tradinghub.icns "$APP/Contents/Resources/"
xattr -cr "$APP"

# A fixed certificate helps macOS retain permissions across updates; otherwise use ad-hoc signing.
if [ -n "$SIGN_IDENTITY" ] && security find-identity -v -p codesigning 2>/dev/null | grep -q "\"$SIGN_IDENTITY\""; then
  codesign --force --deep --sign "$SIGN_IDENTITY" "$APP"
  codesign --verify --deep --strict "$APP" && echo "Signed with: $SIGN_IDENTITY"
else
  codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || true
  echo "Signed ad hoc"
fi

rm -rf "$OUT/$NAME.app"
ditto "$APP" "$OUT/$NAME.app" 2>/dev/null || true
echo "Built: $APP"
if [ "$1" = "--install" ]; then
  rm -rf "/Applications/$NAME.app"
  ditto --noextattr --norsrc "$APP" "/Applications/$NAME.app"
  codesign --verify --deep --strict "/Applications/$NAME.app" && echo "Installed in /Applications/$NAME.app"
fi
