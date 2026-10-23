# 拾光 / Glean

Formerly 素顏 Shot.

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

Builds a release binary, packages it as `Glean.app` (menu-bar only, shown as 拾光 on Chinese systems, with the icon from `Resources/AppIcon.png`), installs it to `/Applications`, and launches it. Re-run after pulling changes. Add it under System Settings → General → Login Items to start at login.

Run `scripts/make-signing-cert.sh` once per Mac first. It creates a self-signed `SuYanShot Local Signing` identity in the login keychain; without it the app is ad-hoc signed and macOS asks for Screen Recording again after every rebuild.

## Install on another Mac

1. Download `Glean-<version>.zip` from [Releases](https://github.com/xiah6415/shotscreen/releases/latest), unzip it, and drag `Glean.app` (shown as 拾光) into 應用程式 (Applications).
2. The first launch is blocked by Gatekeeper, because the app is signed with a local self-signed certificate rather than an Apple Developer ID and is not notarized. Right-click the app → 打開 → 打開, or try to open it once and then go to System Settings → Privacy & Security → 仍要打開 (Open Anyway). This is only needed once.
3. Grant **Screen Recording** (and **Accessibility** for global shortcuts, auto scroll and pasting) when asked.

After that the app checks GitHub for a newer release when it starts and once a day (設定 → 更新, or 檢查更新… in the menu). 更新 downloads the zip, checks that the new app has the same bundle id and is signed with the same certificate, replaces `/Applications/Glean.app`, and relaunches. Because the certificate stays the same, the Screen Recording and Accessibility grants carry over. If anything fails it offers to open the release page for a manual download.

Removing the Gatekeeper warning entirely would require notarization, which needs a paid Apple Developer Program membership (Developer ID certificate + `notarytool`).

## Publishing a release

```sh
echo 0.2.0 > VERSION            # bump, commit and push to main
scripts/release.sh notes.md     # or no argument: notes from commit subjects since the last tag
```

`scripts/release.sh` refuses a dirty tree or unpushed HEAD, builds with `make-app.sh`, checks the app is signed with `SuYanShot Local Signing`, zips it with `ditto -c -k --keepParent`, shows the notes, asks for confirmation, then runs `gh release create v<version>`. `--dry-run` stops after the zip.

**Back up the signing certificate.** Installed copies only accept updates signed by the same certificate. If the `SuYanShot Local Signing` identity is lost (new Mac, keychain reset), a newly generated one will not match and everyone has to reinstall by hand and grant permissions again. Export it once from Keychain Access (login → My Certificates → right-click → Export, `.p12` with a password) and keep it somewhere safe; import that `.p12` on any Mac you release from instead of running `make-signing-cert.sh` there.

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
- Screen recording (⌘⇧R or All-in-One 錄影 mode; menu 錄影): adjustable region, a single window, or full screen to MP4; sound from all apps or only one chosen app on the Desktop, system audio (on by default) and microphone toggles right before it starts, optional cursor, 30 or 60 fps, pause/resume, countdown; preview card with Show in Finder, copy file, and GIF export
- Annotation editor: arrows, boxes, ellipses, lines, pen, highlighter, text, numbered steps, pixelate, undo/redo
- Global hotkeys for region, window, full screen, and delayed capture, customizable in 設定… (⌘,)
- Update check against GitHub Releases (on launch and daily, or 檢查更新…) with in-place install and relaunch

## Known limitations

- Interactive verification needs a logged-in desktop session plus Screen Recording permission.
- This checkout currently uses macOS Command Line Tools rather than full Xcode; `swift build` passes, but XCTest needs a full Xcode toolchain to run here.
- Scroll Capture is an explicit stub: it does not record frames, detect overlap, or stitch images.
- Annotation and settings UI are intentionally minimal extension points.

## Future roadmap

1. **Scroll Capture technical spike**: manually scrolled frame acquisition, filtering, overlap detection, and stitching.
2. Annotation editor.
3. Recording/GIF only after the capture core is stable.
