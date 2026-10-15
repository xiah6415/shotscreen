# Project Memory

## Project Goal
Build a private, native macOS 14+ screenshot utility named **素顏 Shot**. The first phase targets the common capture workflow without subscriptions: region, window, full-screen, delayed captures, clipboard/export, and a lightweight floating preview.

## Current Status
Phase-one foundation complete and buildable. The app is currently launchable from the SwiftPM debug product.

## Architecture
Swift Package Manager executable using AppKit for application lifecycle and overlays, with SwiftUI used inside the floating preview. Capture, storage, UI, and shortcut responsibilities remain separate.

## Decisions
- Use only Apple frameworks; no third-party dependencies.
- Target macOS 14+, Apple Silicon and Intel through SwiftPM's native universal build support.
- Use ScreenCaptureKit for capture. Region selection is an AppKit overlay and selected rectangles are converted into per-display pixel coordinates before capture.
- Scroll capture is deliberately an interface-only technical-spike boundary in this phase.

## Completed
- Project documentation created before source implementation.
- Native menu-bar application lifecycle and default global shortcuts (⌘⇧1–4).
- AppKit multi-display region overlay: drag selection, Escape cancellation, dimension label, backing-scale conversion, and per-display ScreenCaptureKit capture.
- ScreenCaptureKit window and display capture selectors.
- 3/5/10 second delayed capture architecture.
- Clipboard copy, Desktop PNG export with deterministic naming, and floating preview controls including drag-out.
- Annotation model extension point and explicitly non-functional Scroll Capture interface/model stubs.
- `swift build` succeeds on 2026-09-09; built executable was launched and observed running.
- Git repository initialized on branch `main`; initial commit is `6a41e5c` (`Initial native screenshot app foundation`).
- 2026-10-15: `scripts/make-app.sh` packages the release build as an ad-hoc-signed menu-bar `素顏 Shot.app` (bundle id `local.suyanshot.app`, `LSUIElement`); `--install` copies it to `/Applications` and relaunches. Installed and observed running.
- 2026-10-15 UI pass (Chinese UI): menu items with SF Symbols and ⌘⇧N hints, 打開截圖資料夾 / 權限設定 replace the placeholder Settings alert; region overlay has crosshair guides, crosshair cursor, right-click cancel; `Capture/WindowPicker.swift` replaces the window popup alert with hover-highlight + click (z-order from CGWindowList, own windows excluded); full screen captures the display under the pointer with no picker; delayed capture shows `UI/CountdownHUD.swift` (click to cancel) instead of a modal alert; floating thumbnail redesigned (hover Copy/Save/close, auto-dismiss after 8s unless hovered, drags out as a temp PNG file and closes after a successful drop); window captures now request full Retina resolution. Shared overlay panel and `NSScreen.displayID` / `underMouse` helpers live in `Capture/ScreenOverlay.swift`. `swift build` passes with no warnings; app reinstalled and observed running. Interactive behaviour checked by the user on 2026-10-15: works as expected.

## In Progress
- Manual interactive capture validation on a desktop with Screen Recording permission.

## TODO
- Implement and manually validate capture workflows.
- Implement the Scroll Capture technical spike (next agent priority).

## Known Issues
- Screen Recording and Accessibility permissions must be granted by the user at runtime.
- The `.app` is ad-hoc signed and built for the host architecture only (universal builds need full Xcode). Reinstalling a rebuilt app may make macOS ask for Screen Recording permission again.
- Interactive GUI and multi-display validation require a logged-in macOS desktop session.
- Default ⌘⇧3 / ⌘⇧4 collide with macOS's own screenshot shortcuts; if the system shortcuts stay enabled, those two hotkeys may not reach the app.
- This machine has Command Line Tools selected instead of full Xcode, so `swift test` cannot import XCTest. The XCTest source is present and should run from full Xcode / its selected toolchain.

## Testing
- `swift build`: passed.
- App launch: passed (process running as the SwiftPM debug executable).
- `swift test`: blocked by the local Command Line Tools SDK lacking XCTest; not counted as passed.
- Region/window/full-screen/clipboard/save/delay/hotkey/multi-display interaction: not programmatically verifiable in this session; requires user permission and a visual desktop check.

## Next Agent Instructions
Read `AGENTS.md`, this file, and `HANDOFF TO CLAUDE CODE.md` before modifying capture code. Do not expand annotation, recording, OCR, cloud, or scroll-stitching scope before the Scroll Capture technical spike is planned.
