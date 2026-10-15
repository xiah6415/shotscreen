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
- 2026-10-15 permission prompts: the user saw the Screen Recording prompt on every capture. Cause: ad-hoc signing makes the TCC designated requirement a cdhash, so each rebuild is a new app. Fix: `scripts/make-signing-cert.sh` creates a self-signed `SuYanShot Local Signing` identity (login keychain, untrusted is fine for codesign) and `make-app.sh` signs with it when present, giving a requirement of identifier + certificate leaf. Code now checks permission before any ScreenCaptureKit call, triggers the system prompt at most once per launch, and otherwise shows its own alert (打開系統設定 / 重新啟動 App). The user re-granted once after the first stably signed install and confirmed the prompt no longer repeats.
- 2026-10-15 custom shortcuts: 設定… (⌘,) opens `UI/SettingsWindow.swift` with a recorder per action. `Shortcuts/KeyShortcut.swift` stores key code + ⌘⌥⌃⇧ (requires ⌘/⌥/⌃ unless F1–F12); `Shortcuts/ShortcutStore.swift` persists to UserDefaults key `shortcuts` (a saved-but-missing action means cleared), removes duplicates from other actions, and suspends global hotkeys while recording. `ShortcutManager.apply` re-registers and reports combos it could not register (warning icon in settings). Menu items show the current shortcuts. Defaults unchanged (⌘⇧1–4). Built, installed, and tried by the user: works.
- 2026-10-15 annotation editor (user request, overrides the earlier "no editor this phase" rule): open from the preview card's 標註 button or by double-clicking it. Tools: select/move (V), arrow (A), rectangle (R), ellipse (O), line (L), pen (P), highlighter (H), text (T), numbered counter (N), pixelate (B); 7-color palette, 3 sizes, snapshot undo/redo (⌘Z / ⌘⇧Z), ⌫ deletes the selection, Shift constrains, ⌘C copies and ⌘S saves the flattened image to the Desktop. Geometry is stored in image pixels (bottom-left origin) so `AnnotationRenderer` draws the same marks on the canvas and in the export. Pixelate reveals a pre-pixelated copy through a clip. Renderer verified headlessly by flattening every annotation kind into a test PNG; the interactive editor tried by the user: works.
- 2026-10-15 editor follow-up: Copy/Save in the editor toolbar are icon-only (tooltips). Added crop (C): drag a box, Return applies, Esc cancels, ⌫ restores the full image; switching tools applies a pending crop; crop is part of the undo snapshot and the canvas shows only the cropped region afterwards. Added blur (U), revealed through a clip like pixelate (`AnnotationEffects` precomputes both). Existing text is edited by double-clicking it with Select, clicking it with Text, or Return on a selected text; clearing the text deletes it. Renderer + crop verified headlessly with a test PNG; tried by the user: works. `make-app.sh --install` now waits for the old instance to quit before relaunching (avoids LaunchServices error -600).
- 2026-10-15 scroll capture (replaces the placeholder): 捲動截圖 menu item, default shortcut ⌘⇧6 (shortcuts saved before this action existed still get the default via `shortcutsKnownActions`). Flow: region select → click-through dashed border + control panel outside the region (`UI/ScrollCaptureHUD.swift`) → `ManualScrollCaptureService` captures the region every 100 ms while the user scrolls → `ScrollStitcher` matches each frame against the last accepted one → Done (or the shortcut again) stitches and shows the normal preview card. Never scrolls by itself.
  - Matching: 96-column grayscale row signatures (rightmost 4% dropped for overlay scrollbars), static top/bottom rows detected and excluded (sticky header/footer appear once), coarse 1/4-height search keeping 6 distinct candidates, full-resolution refine, accept only if mean abs diff < 3 and clearly better than no movement. Unmatched frames are skipped, not guessed. Down-scrolling only; 40,000 px cap.
  - Success criteria, verified with a synthetic test (1000×7000 page with sticky header/footer, gradient blocks, scrollbar noise, a 700 px blank gap, random scroll steps incl. pauses and 1–12 px nudges) over 8 seeds: every frame whose overlap contains content gets the exact offset; after removing blank rows the stitched result matches ground truth row-for-row (0 differing rows in all 8); ~5 ms per frame. Known limit: when the overlap is entirely blank the offset is ambiguous, so a long empty stretch may come out shorter or longer, but no content is lost or duplicated. Test harness lives outside the repo (needs XCTest-free swiftc); recreate from this description if needed.
  - Tried by the user on real apps: works. Expected real-world issues: overlay scrollbars can leave slivers at the right edge, animated content/video in the region causes skipped frames, scrolling up is ignored.
  - Live preview (2026-10-15): while scrolling, a click-through panel beside the region (right, else left, else inside) shows the stitched-so-far image pinned to its newest part. `ScrollStitcher` keeps a 200-px-wide thumbnail per accepted frame and `makePreview()` composes from those (~1 ms); `makeImage()` now shares the same compose path at scale 1 (clip-and-draw instead of cropping). Re-ran the 8-seed synthetic test after the change: still 0 differing content rows. Preview panel tried by the user: works.

- 2026-10-15 All-in-One (`Capture/AllInOneSelector.swift`), menu item at the top, default ⌘⇧5: drag a region, adjust it (move, 8 handles, flips past the opposite edge), then the toolbar under the selection offers 截圖 (Return / double-click), 捲動截圖 (S), 視窗, 全螢幕, 取消 (Esc / right-click). Before a selection only the screen under the pointer shows the toolbar. The coordinator's capture paths were split into `capture(_ selection:)`, `startScrolling(_:)` and `captureFullScreen(on:)` so menu, hotkeys and All-in-One share them. Built, installed, and tried by the user: works.

## In Progress
- Manual interactive capture validation on a desktop with Screen Recording permission.

## TODO
- Implement and manually validate capture workflows.

## Known Issues
- Screen Recording and Accessibility permissions must be granted by the user at runtime.
- The `.app` is built for the host architecture only (universal builds need full Xcode).
- macOS 15+ still re-confirms ScreenCaptureKit access periodically (the "bypass the private window picker" prompt); that is system policy, not a bug.
- Interactive GUI and multi-display validation require a logged-in macOS desktop session.
- Default ⌘⇧3 / ⌘⇧4 collide with macOS's own screenshot shortcuts; if the system shortcuts stay enabled, those two hotkeys may not reach the app.
- This machine has Command Line Tools selected instead of full Xcode, so `swift test` cannot import XCTest. The XCTest source is present and should run from full Xcode / its selected toolchain.

## Testing
- `swift build`: passed.
- App launch: passed (process running as the SwiftPM debug executable).
- `swift test`: blocked by the local Command Line Tools SDK lacking XCTest; not counted as passed.
- Region/window/full-screen/clipboard/save/delay/hotkey/multi-display interaction: not programmatically verifiable in this session; requires user permission and a visual desktop check.

## Next Agent Instructions
Read `AGENTS.md`, this file, and `HANDOFF TO CLAUDE CODE.md` before modifying capture code. Scroll capture and the annotation editor exist now; recording, OCR, and cloud remain out of scope unless the user asks.
