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

## In Progress
- Manual interactive capture validation on a desktop with Screen Recording permission.

## TODO
- Implement and manually validate capture workflows.
- Implement the Scroll Capture technical spike (next agent priority).

## Known Issues
- Screen Recording and Accessibility permissions must be granted by the user at runtime.
- Interactive GUI and multi-display validation require a logged-in macOS desktop session.
- This machine has Command Line Tools selected instead of full Xcode, so `swift test` cannot import XCTest. The XCTest source is present and should run from full Xcode / its selected toolchain.

## Testing
- `swift build`: passed.
- App launch: passed (process running as the SwiftPM debug executable).
- `swift test`: blocked by the local Command Line Tools SDK lacking XCTest; not counted as passed.
- Region/window/full-screen/clipboard/save/delay/hotkey/multi-display interaction: not programmatically verifiable in this session; requires user permission and a visual desktop check.

## Next Agent Instructions
Read `AGENTS.md`, this file, and `HANDOFF TO CLAUDE CODE.md` before modifying capture code. Do not expand annotation, recording, OCR, cloud, or scroll-stitching scope before the Scroll Capture technical spike is planned.
