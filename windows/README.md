# 素顏 Shot for Windows

The Windows port of 素顏 Shot. It lives in the same repository as the macOS app (repository root) and follows the same product
rules: private, native, no subscription, cloud, account or third-party runtime dependency.

## Requirements

- Windows 10 2004 (build 19041) or later; Windows 11 recommended
- .NET 8 SDK to build (`winget install Microsoft.DotNet.SDK.8`)

## Build, test, run

From this `windows/` folder:

```powershell
dotnet build
dotnet test
dotnet run --project src/SuYanShot
```

The app lives in the notification area (system tray) and has no main window. Quit it from its menu.

To produce a standalone exe:

```powershell
dotnet publish src/SuYanShot -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## Current features (phase one)

| Feature | Default shortcut | macOS equivalent |
| --- | --- | --- |
| 框選截圖: drag a region on a frozen copy of the screen; Esc / right-click cancels; size label and crosshair guides | Ctrl+Shift+1 | ⌘⇧1 |
| 視窗截圖: hover highlights the front-most window, click captures it (even where other windows cover it) | Ctrl+Shift+2 | ⌘⇧2 |
| 全螢幕截圖: the monitor under the pointer | Ctrl+Shift+3 | ⌘⇧3 |
| 延遲截圖: 3 / 5 / 10 s countdown (click the countdown to cancel), then full screen | Ctrl+Shift+4 (3 s) | ⌘⇧4 |

Every capture is copied to the clipboard (bitmap + PNG) and shown on a preview card in the bottom-right corner:
hover for 複製 / 儲存 / 關閉, drag it into another app to drop a `.png`, double-click to save and open it.
The card closes after 8 s unless hovered. Saved files go to the Desktop as `Screenshot 2026-10-21 at 14.03.07.png`, the same
naming as macOS.

Tray menu also has 打開截圖資料夾, 編輯快捷鍵… and 登入時啟動 (adds/removes `HKCU\...\Run\SuYanShot`).

### Shortcuts

`%APPDATA%\SuYanShot\shortcuts.json`, opened by 編輯快捷鍵…, is reloaded automatically when saved:

```json
{ "Region": "Ctrl+Shift+1", "Window": "Ctrl+Shift+2", "FullScreen": "Ctrl+Shift+3", "Delayed": "Ctrl+Shift+4" }
```

Modifiers: `Ctrl`, `Alt`, `Shift`, `Win`. Keys: `A`–`Z`, `0`–`9`, `F1`–`F24`, `PrintScreen`. A shortcut needs Ctrl, Alt or Win
unless it is a function key; `""` disables an action. If another program already owns a combination, a tray notification lists it.
Using bare `PrintScreen` needs Windows' own "Use the Print screen key to open screen capture" setting turned off.

## Architecture

WinForms on .NET 8, no NuGet packages in the app. The structure mirrors `Sources/SuYanShot`:

- `App/TrayApplication.cs` — tray icon and menu (≈ `MenuBarController` + `AppDelegate`).
- `Capture/CaptureCoordinator.cs` — workflow boundary: capture → clipboard → preview card. Capture code never touches UI or files.
- `Capture/ScreenCapture.cs` — `FrozenDesktop`, GDI screen capture, `PrintWindow` window capture with a fallback.
- `Capture/SelectionOverlay.cs` — one overlay per monitor for region and window selection.
- `Capture/WindowList.cs` — visible top-level windows in z-order.
- `Shortcuts/` — `RegisterHotKey` wrapper, shortcut parsing and the JSON store.
- `Storage/` — `ImageExporter` (naming / PNG policy) and clipboard.
- `UI/` — preview card and countdown HUD.
- `Native/NativeMethods.cs` — all Win32 interop.

### Decisions

- **WinForms rather than WPF.** The process is per-monitor DPI aware (v2) and every WinForms coordinate is a physical pixel, so
  overlays, selections and captures share one coordinate space on mixed-DPI multi-monitor setups with no point↔pixel conversion.
  UI sizes are scaled by each window's `DeviceDpi`.
- **Freeze, then select.** Region and window selection take one copy of the whole virtual desktop first and draw the overlays on
  top of it; the result is cropped from that copy. What you see is exactly what you get and the overlays can never appear in it.
- **Own windows stay out of captures** via `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` on the preview card, countdown and
  overlays (the macOS app does the same with `SCContentFilter`).
- **Window capture uses `PrintWindow(PW_RENDERFULLCONTENT)`** cropped to the DWM extended frame (no invisible resize borders).
  Apps that return black fall back to the frozen desktop.
- Like macOS, a region stays within the monitor where the drag started.

## Status and testing

- `dotnet build`: passes with no warnings. `dotnet test`: 19 tests pass (naming, no-overwrite export, shortcut parsing/store,
  frozen-desktop cropping with negative virtual-screen origins, blank detection).
- The app was launched and stayed running, and it wrote the default `shortcuts.json`.
- Not yet checked interactively: the region / window / full-screen / delayed flows, preview card, drag-out, and mixed-DPI
  multi-monitor behaviour.

## Not yet ported

All-in-One, scroll capture (the stitching algorithm in `Capture/ScrollStitcher.swift` should be ported as-is and tested against
the same synthetic cases described in `memory.md`), annotation editor, screen recording / GIF, clipboard history, phone sharing,
and a settings window (shortcuts are a JSON file for now).
