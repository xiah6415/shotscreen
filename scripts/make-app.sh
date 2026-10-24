#!/bin/zsh
# Build SuYanShot in release mode and package it as a menu-bar .app bundle.
#
#   scripts/make-app.sh            # build to .build/app/Glean.app (shown as 拾光 on Chinese systems)
#   scripts/make-app.sh --install  # also copy to /Applications and relaunch
set -euo pipefail

APP_NAME="Glean"
# Shown by Finder, Launchpad and Spotlight when the system language is Chinese.
DISPLAY_NAME_ZH="拾光"
# Older builds were installed under this name; --install removes them.
OLD_APP_NAMES=("素顏 Shot")
EXECUTABLE="SuYanShot"
BUNDLE_ID="local.suyanshot.app"
ROOT="${0:A:h:h}"
cd "$ROOT"
# The one place the version lives; scripts/release.sh tags releases with it and the app compares it with GitHub.
VERSION="$(tr -d '[:space:]' < VERSION)"

# Universal binary: Apple Silicon + Intel. `--arch a --arch b` needs full Xcode, so build each slice
# separately (Command Line Tools can cross-compile) and join them with lipo.
UNIVERSAL="$ROOT/.build/app/universal"
mkdir -p "$UNIVERSAL"
SLICES=()
for arch in arm64 x86_64; do
    echo "→ Building release binary ($arch)"
    swift build -c release --product "$EXECUTABLE" --arch "$arch" --scratch-path "$ROOT/.build/$arch"
    SLICES+=("$(swift build -c release --arch "$arch" --scratch-path "$ROOT/.build/$arch" --show-bin-path)/$EXECUTABLE")
done
lipo -create "${SLICES[@]}" -output "$UNIVERSAL/$EXECUTABLE"
BIN_DIR="$UNIVERSAL"

APP="$ROOT/.build/app/$APP_NAME.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/$EXECUTABLE" "$APP/Contents/MacOS/$EXECUTABLE"

echo "→ Building app icon"
ICONSET="$ROOT/.build/app/AppIcon.iconset"
rm -rf "$ICONSET"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
    sips -z $size $size Resources/AppIcon.png --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    sips -z $((size * 2)) $((size * 2)) Resources/AppIcon.png --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
rm -rf "$ICONSET"

for region in zh-Hant zh_TW zh-Hant-TW; do
    mkdir -p "$APP/Contents/Resources/$region.lproj"
    printf 'CFBundleDisplayName = "%s";\nCFBundleName = "%s";\n' "$DISPLAY_NAME_ZH" "$DISPLAY_NAME_ZH" > "$APP/Contents/Resources/$region.lproj/InfoPlist.strings"
done

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
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>LSHasLocalizedDisplayName</key>
    <true/>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
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
    <string>拾光需要螢幕錄製權限才能截圖和錄影。</string>
    <key>NSMicrophoneUsageDescription</key>
    <string>拾光在你打開「錄麥克風」時，把你的聲音錄進影片。</string>
    <key>NSLocalNetworkUsageDescription</key>
    <string>拾光會在你按「在手機上開啟」時，讓同一個網路裡的手機看到剪貼簿紀錄。</string>
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
    for old in "${OLD_APP_NAMES[@]}"; do
        [[ -d "/Applications/$old.app" ]] && rm -rf "/Applications/$old.app" && echo "✓ Removed old /Applications/$old.app"
    done
    cp -R "$APP" "$DEST"
    echo "✓ Installed $DEST"
    # LaunchServices can still report the old instance for a moment (error -600); retry once.
    open "$DEST" 2>/dev/null || { sleep 1; open "$DEST"; }
fi
