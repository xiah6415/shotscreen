# Agent Guide — 拾光 / Glean (formerly 素顏 Shot)

## Project purpose
Personal, native macOS screenshot tool: a durable replacement for the most-used CleanShot X workflows, not a commercial clone.

## Technology stack
Swift 5.10+, AppKit, SwiftUI, ScreenCaptureKit, Core Graphics, Core Image, Vision (reserved), and AVFoundation (reserved). No third-party dependencies.

## Architecture and directory structure
`Sources/SuYanShot` is divided by responsibility:

- `App`: lifecycle and permissions.
- `Capture`: selection UI and ScreenCaptureKit capture services.
- `Storage`: clipboard and filesystem export.
- `UI`: menu bar and floating thumbnail UI.
- `Shortcuts`: Carbon-based global hotkey registration.
- `Models`: value types shared between modules.
- `Clipboard`: clipboard history (Paste-style) — model, on-disk store, pasteboard monitor, paster, and phone sharing over the local network (`PhoneShareServer` = minimal Network.framework HTTP server, `PhoneShare` = routes, `PhoneSharePage` = the mobile web page); panel UI lives in `UI/ClipboardPanel.swift`, QR window in `UI/PhoneShareWindow.swift`.
- `Update`: GitHub Releases check (`UpdateChecker`), preferences, and the verify-then-replace installer; the alerts live in `UI/UpdatePrompt.swift`. Releases are made with `scripts/release.sh` from the `VERSION` file.
- `Annotation`: annotation editor — model (`AnnotationModel`), document/undo state, renderer shared by canvas and export, AppKit canvas, editor window.

`Tests/SuYanShotTests` contains non-interactive unit tests. With Command Line Tools only, `scripts/test-stitcher.sh` is the runnable regression test; `scripts/make-app.sh` builds a universal (arm64 + x86_64) app. Keep UI orchestration out of capture and storage services.

## Coding rules
- Prefer current native APIs and macOS 14 availability.
- Keep each service focused; inject callbacks/dependencies rather than making UI own storage or capture details.
- Avoid third-party packages and premature abstractions.
- Build after each cohesive change and add unit tests for deterministic logic.
- Record material architecture decisions and handoff state in `memory.md`.
- Do not modify unrelated files or implement OCR, cloud, or accounts in this phase. (The annotation editor, screen recording and GIF export were added at the user's explicit request.)

## Build and test
From repository root:

```sh
swift build
swift test
swift run SuYanShot
```

For an app bundle / signed distribution, create an Xcode project from the package or use `xcodebuild` after a bundle target is introduced. Development runs require a graphical macOS session.

## Permissions
- **Screen Recording**: required by ScreenCaptureKit captures; prompted/opened through System Settings.
- **Accessibility**: required for global hotkeys using Carbon event handlers. Grant in Privacy & Security if macOS requests it.

## Windows port
`windows/` holds the Windows app (C# / .NET 8 WinForms, `dotnet build` / `dotnet test` from that folder). It mirrors this structure; see `windows/README.md` for its decisions, status and what is not yet ported. Changes to one platform do not require touching the other.

## Agent handoff rules
1. Read `memory.md` and `HANDOFF TO CLAUDE CODE.md` first.
2. Preserve the `ScrollCaptureService` boundary. Scrolling is manual by default; auto scroll (added at the user's request on 2026-10-15) only runs when the user turns it on and must keep pausing at checkpoints to ask before continuing.
3. Update `memory.md` after each completed milestone.
4. State precisely which interactive checks were actually performed.

## Modules not to change casually
- `Capture/ScrollStitcher.swift`: offset matching and stitching; run `scripts/test-stitcher.sh` after changing it.
- `Models/CaptureModels.swift`: coordinates and scale contracts shared by capture services.
- `Storage/ImageExporter.swift`: owns naming/export policy; keep that policy out of UI.
