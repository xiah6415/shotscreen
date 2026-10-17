#!/bin/zsh
# Build SuYanShot in release mode and package it as a menu-bar .app bundle.
#
#   scripts/make-app.sh            # build to .build/app/素顏 Shot.app
#   scripts/make-app.sh --install  # also copy to /Applications and relaunch
set -euo pipefail

APP_NAME="素顏 Shot"
EXECUTABLE="SuYanShot"
BUNDLE_ID="local.suyanshot.app"
VERSION="0.1.0"

ROOT="${0:A:h:h}"
cd "$ROOT"

echo "→ Building release binary"
swift build -c release --product "$EXECUTABLE"
BIN_DIR="$(swift build -c release --show-bin-path)"

APP="$ROOT/.build/app/$APP_NAME.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/$EXECUTABLE" "$APP/Contents/MacOS/$EXECUTABLE"

BUILD_NUMBER="$(git rev-list --count HEAD 2>/dev/null || echo 1)"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleExecutable</key>
    <string>$EXECUTABLE</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$BUILD_NUMBER</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSScreenCaptureUsageDescription</key>
    <string>素顏 Shot 需要螢幕錄製權限才能截圖。</string>
    <key>NSLocalNetworkUsageDescription</key>
    <string>素顏 Shot 會在你按「在手機上開啟」時，讓同一個網路裡的手機看到剪貼簿紀錄。</string>
</dict>
</plist>
PLIST

# A stable identity keeps Screen Recording / Accessibility grants across rebuilds
# (see scripts/make-signing-cert.sh); fall back to ad-hoc, which macOS treats as a new app every build.
SIGNING_IDENTITY="SuYanShot Local Signing"
SIGNING_HASH="$(security find-identity -p codesigning | awk -v name="\"$SIGNING_IDENTITY\"" 'index($0, name) { print $2; exit }')"
if [[ -n "$SIGNING_HASH" ]]; then
    echo "→ Signing with '$SIGNING_IDENTITY'"
    codesign --force --sign "$SIGNING_HASH" --identifier "$BUNDLE_ID" "$APP"
else
    echo "→ Ad-hoc signing (run scripts/make-signing-cert.sh once to keep permissions across rebuilds)"
    codesign --force --sign - --identifier "$BUNDLE_ID" "$APP"
fi

echo "✓ Built $APP"

if [[ "${1:-}" == "--install" ]]; then
    DEST="/Applications/$APP_NAME.app"
    pkill -x "$EXECUTABLE" 2>/dev/null || true
    # Wait for the old instance to exit, or `open` below can fail with error -600.
    for _ in {1..50}; do pgrep -x "$EXECUTABLE" >/dev/null || break; sleep 0.1; done
    rm -rf "$DEST"
    cp -R "$APP" "$DEST"
    echo "✓ Installed $DEST"
    # LaunchServices can still report the old instance for a moment (error -600); retry once.
    open "$DEST" 2>/dev/null || { sleep 1; open "$DEST"; }
fi
