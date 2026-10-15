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
</dict>
</plist>
PLIST

echo "→ Ad-hoc signing"
codesign --force --sign - --identifier "$BUNDLE_ID" "$APP"

echo "✓ Built $APP"

if [[ "${1:-}" == "--install" ]]; then
    DEST="/Applications/$APP_NAME.app"
    pkill -x "$EXECUTABLE" 2>/dev/null || true
    rm -rf "$DEST"
    cp -R "$APP" "$DEST"
    echo "✓ Installed $DEST"
    open "$DEST"
fi
