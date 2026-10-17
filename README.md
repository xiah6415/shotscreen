# 素顏 Shot

A private, native macOS screenshot utility for the common capture workflow: no subscription, cloud, account, or third-party framework.

## Requirements

- macOS 14 Sonoma or later
- Xcode 15.4+ / Swift 5.10+
- Apple Silicon or Intel Mac (native universal builds)

## Build and run

```sh
swift build
swift test
swift run SuYanShot
```

## Install as an app

```sh
scripts/make-app.sh --install
```

Builds a release binary, packages it as `素顏 Shot.app` (menu-bar only), installs it to `/Applications`, and launches it. Re-run after pulling changes. Add it under System Settings → General → Login Items to start at login.

Run `scripts/make-signing-cert.sh` once per Mac first. It creates a self-signed `SuYanShot Local Signing` identity in the login keychain; without it the app is ad-hoc signed and macOS asks for Screen Recording again after every rebuild.

## Permissions

Grant **Screen Recording** access when macOS asks, then relaunch the app if needed. Global shortcuts can require **Accessibility** access. Both are managed in System Settings → Privacy & Security.

## Current features

- Menu-bar application shell
- Region selection overlay with drag, Escape cancellation, dimensions, Retina-aware conversion, and multi-display overlays
- Full-display and shareable-window capture with ScreenCaptureKit
- Configurable 3/5/10-second delayed capture
- Clipboard copy, PNG export, and floating preview with Copy / Save / Annotate / drag-out
- All-in-One (⌘⇧5): select and adjust a region, then capture it, start a scroll capture, or switch to window / full screen
- Scroll capture: select a region, scroll down by hand (or turn on auto scroll, which stops every few screens to ask before continuing), and the frames are stitched into one tall image, with a live preview beside the region
- Clipboard history (⌘⇧V): text, rich text, images and files, searchable cards along the bottom of the screen, filters (常用 / 文字 / 圖片 / 檔案) and custom tags, paste straight into the current app; stored locally, skips password-manager content
- Phone access (在手機上開啟…): scan a QR code to browse, search and copy the clipboard history in the phone's browser on the same Wi-Fi, and send text or photos back to the Mac; token-protected, local network only, stops after 30 idle minutes
- Annotation editor: arrows, boxes, ellipses, lines, pen, highlighter, text, numbered steps, pixelate, undo/redo
- Global hotkeys for region, window, full screen, and delayed capture, customizable in 設定… (⌘,)

## Known limitations

- Interactive verification needs a logged-in desktop session plus Screen Recording permission.
- This checkout currently uses macOS Command Line Tools rather than full Xcode; `swift build` passes, but XCTest needs a full Xcode toolchain to run here.
- Scroll Capture is an explicit stub: it does not record frames, detect overlap, or stitch images.
- Annotation and settings UI are intentionally minimal extension points.

## Future roadmap

1. **Scroll Capture technical spike**: manually scrolled frame acquisition, filtering, overlap detection, and stitching.
2. Annotation editor.
3. Recording/GIF only after the capture core is stable.
