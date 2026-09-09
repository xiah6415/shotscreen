# HANDOFF TO CLAUDE CODE

## Current State

Phase one is a small, native macOS 14+ screenshot utility foundation. `swift build` passes and the debug executable was launched successfully. It is intentionally a Swift Package executable during this phase, with no external dependencies and no app-bundle/signing setup yet.

## What Changed

- Created required collaboration records: `memory.md`, `AGENTS.md`, and `README.md`.
- Implemented a menu-bar lifecycle with Region / Window / Full Screen / Delay / Settings / Quit actions.
- Added `ScreenCaptureService`, capture services for windows/displays, and an AppKit region overlay.
- Added Clipboard, PNG exporter, and a floating SwiftUI thumbnail hosted in an AppKit panel. The image can be dragged out as an `NSImage` pasteboard item.
- Added Carbon global hotkey registrations: ⌘⇧1 region, ⌘⇧2 window, ⌘⇧3 full-screen, ⌘⇧4 delayed.
- Added intentionally inert scroll-capture protocol/models and an annotation model extension point.
- Added XCTest source for naming/delay configuration.

## Architecture

`CaptureCoordinator` is the workflow boundary: it asks capture services for a `CGImage`, copies it to the clipboard, then hands it to `FloatingThumbnailController`. Capture services do not know about UI, exports, or pasteboards. `ImageExporter` owns PNG naming and writing policy.

For region capture, each display gets its own overlay. The selected AppKit point rectangle is converted to top-left-origin backing pixels (`RegionSelection.sourceRectPixels`) for `SCStreamConfiguration.sourceRect`; selections intentionally do not span displays in phase one.

`ScrollCaptureService` must remain a boundary until its technical spike replaces `PlaceholderScrollCaptureService`.

## How To Build

```sh
cd /Users/suyan/SuYanShot
swift build
swift run SuYanShot
```

Use Xcode 15.4+ (or switch `xcode-select` to a full Xcode installation) for XCTest:

```sh
swift test
```

## How To Run

Run from a logged-in desktop session. Grant Screen Recording permission in System Settings → Privacy & Security when prompted, then relaunch. If a shortcut is not delivered by the system, check Accessibility permission and conflicting shortcuts.

## What Was Tested

- `swift build` passed after the final changes.
- The compiled debug executable was opened and confirmed running.
- `swift test` was attempted but cannot compile here: active developer directory is Command Line Tools and its SDK has no XCTest module.

## Known Issues

- The application is currently a SwiftPM executable, not a signed `.app` bundle. A later distribution pass should add an Xcode app target, Info.plist usage messaging as appropriate, signing, and universal archive validation.
- GUI capture workflows require manual validation with Screen Recording permission. No automated visual/multi-display test was possible here.
- The delay action currently asks for a delay then routes to the display picker; it does not remember a capture target.
- Window choice is a basic alert popup; no live hover/highlight picker yet.
- Drag-out supplies an image pasteboard item, not a promised `.png` file.

## Incomplete Features

- **Scroll Capture is not implemented.** No continuous frames, manual-scroll session, filtering, overlap detection, registration, or stitching exists.
- No annotation editor, OCR, cloud, account, recording, GIF, or advanced settings UI.

## Important Decisions

- macOS 14+ `SCScreenshotManager.captureImage` is used for one-shot screenshots rather than creating a stream for every capture.
- No third-party dependency was introduced.
- Region capture is per-display to keep point/pixel and multi-monitor coordinate contracts clear for phase one.
- Screenshot output is copied immediately and only written to Desktop if Save is chosen in the floating preview.

## Recommended Next Step

**First priority: build the Scroll Capture Technical Spike, not additional ordinary screenshot UI.**

Use a manual-scroll workflow only: selected region → continuous capture → user scrolls → frame filtering → overlap detection → stitch result. Define measurable spike success criteria and preserve the `ScrollCaptureService` seam. Do not automate scrolling.
