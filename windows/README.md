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

Tray menu also has 剪貼簿紀錄, 在手機上開啟…, 打開截圖資料夾 and 設定….

### 剪貼簿紀錄 (clipboard history)

Same features as macOS. Every copy of text, rich text (RTF/HTML kept), images (stored as PNG) and files is recorded in
`%LOCALAPPDATA%\SuYanShot\Clipboard` (`index.json`, `tags.json`, one payload file per image/RTF/HTML entry), de-duplicated
by content hash. Content marked `ExcludeClipboardContentFromMonitorProcessing`, `Clipboard Viewer Ignore` or
`CanIncludeInClipboardHistory = 0` (what password managers set) is skipped, as is anything copied from KeePass, KeePassXC,
1Password, Bitwarden and similar. Windows notifies clipboard changes (`AddClipboardFormatListener`), so there is no polling.

**Ctrl+Alt+V** opens the panel along the bottom of the screen (macOS uses ⌘⇧V; Ctrl+Shift+V is left alone because it is
"paste as plain text" in browsers and Office). Search, filters 全部 · 常用 · 文字 · 圖片 · 檔案 · tags · ＋ (Tab / Shift+Tab),
← → select, Enter pastes into the app you were in, Shift+Enter pastes plain text, Ctrl+1–9 quick paste, Ctrl+P 常用,
Ctrl+Delete deletes, Esc clears the search then closes, clicking elsewhere closes. Right-click a card for 加入常用 / 標籤 /
刪除, right-click a tag chip to rename or delete it. Pasting needs no permission on Windows; apps running as administrator
ignore the simulated Ctrl+V (the entry is still on the clipboard). 常用 and tagged entries are never trimmed or cleared.

Settings → 剪貼簿: on/off, keep 50/100/200/500, 清除一般紀錄…. Stored in `%APPDATA%\SuYanShot\settings.json`.

### 在手機上開啟 (phone access)

Same page and routes as macOS (`GET /`, `GET /api/items?v=`, `GET /api/items/<id>/image[?thumb=1]`,
`POST /api/items/<id>/to-mac`, `POST /api/send`), with "Mac" worded as 電腦, plus `GET /api/latest` (newest text entry as
plain text). The phone must be on the same network. A 128-bit token is required on every request, connections from
non-private addresses are refused, and a request is capped at 40 MB.

**Unlike macOS, sharing is a switch (tray → 手機連線) with a fixed link**, at the user's request, so it can be bookmarked or
used from iOS Shortcuts: the port and token are created once and kept in `settings.json`, the switch is remembered across
restarts, and there is no 30-minute idle stop. 換一組網址 replaces the token (old links and Shortcuts stop working). If the saved
port is taken by another program a free one is used and saved. The link also changes when the PC's IP address changes, so a
DHCP reservation on the router is recommended. 在手機上開啟… shows the QR code and the Shortcut addresses:

| iOS Shortcut ("Get Contents of URL") | Method | URL |
| --- | --- | --- |
| Send text to the PC | POST, body = text | `http://<ip>:<port>/api/send?t=<token>` |
| Send a photo to the PC | POST, body = image file | `http://<ip>:<port>/api/send?t=<token>` |
| Get the PC's newest text | GET | `http://<ip>:<port>/api/latest?t=<token>` |

**The first time, Windows Defender Firewall asks whether SuYanShot may accept connections. Allow it on private networks**,
otherwise the phone cannot reach the page. Photos from the phone are decoded by GDI+ (JPEG/PNG with EXIF rotation) or the
Windows imaging codecs (HEIC needs the free HEIF extension from the Microsoft Store).

### 設定

The settings window (tray menu → 設定…) has a recorder per action: click it, press the new combination. Esc cancels,
Backspace clears. A shortcut needs Ctrl, Alt or Win plus a letter, digit or F-key; F1–F24 and `PrintScreen` can stand alone.
A combination already used by another action moves to the new one. Changes apply immediately; combinations another program
already owns are marked 已被其他程式使用. 還原預設 restores Ctrl+Shift+1–4. 登入 Windows 時自動啟動 adds/removes
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SuYanShot`.

Shortcuts are stored in `%APPDATA%\SuYanShot\shortcuts.json` (`{ "Region": "Ctrl+Shift+1", ... }`, `""` = cleared).
Using bare `PrintScreen` needs Windows' own "Use the Print screen key to open screen capture" setting turned off.

## Architecture

WinForms on .NET 8, no NuGet packages in the app. The structure mirrors `Sources/SuYanShot`:

- `App/TrayApplication.cs` — tray icon and menu (≈ `MenuBarController` + `AppDelegate`).
- `Capture/CaptureCoordinator.cs` — workflow boundary: capture → clipboard → preview card. Capture code never touches UI or files.
- `Capture/ScreenCapture.cs` — `FrozenDesktop`, GDI screen capture, `PrintWindow` window capture with a fallback.
- `Capture/SelectionOverlay.cs` — one overlay per monitor for region and window selection.
- `Capture/WindowList.cs` — visible top-level windows in z-order.
- `Shortcuts/` — `RegisterHotKey` wrapper, shortcut parsing and the JSON store.
- `Clipboard/` (namespace `SuYanShot.ClipboardHistory`, to avoid clashing with WinForms' `Clipboard`) — model, store,
  monitor, paster, feature, and phone sharing (`PhoneShareServer` = TcpListener HTTP server, `PhoneShare` = routes,
  `PhoneSharePage.html` = embedded copy of the macOS page).
- `App/AppSettings.cs` — non-shortcut preferences.
- `App/StartupRegistration.cs` — 登入時啟動 (per-user Run key).
- `Storage/` — `ImageExporter` (naming / PNG policy) and clipboard.
- `UI/` — preview card, countdown HUD, settings window and its shortcut recorder, clipboard panel, phone share window,
  and `QrCode.cs` (a small QR encoder, byte mode / level M, so no package is needed).
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
- **Phone server on TcpListener**, because `HttpListener` needs administrator rights (a URL ACL) to listen beyond localhost.
- **Own QR encoder** instead of a NuGet package, keeping the app dependency-free. Verified by decoding its output with jsQR.

## Status and testing

- `dotnet build`: passes with no warnings. `dotnet test`: 40 tests pass — naming, no-overwrite export, shortcut
  parsing/store, frozen-desktop cropping, blank detection; clipboard store (ordering, de-duplication, payloads, thumbnails,
  persistence with macOS field values, trimming and clearing with favourites/tags, tag create/rename/toggle/delete, payload
  cleanup); HTTP parsing and private-address checks; and every phone route driven over loopback (403 without / with a wrong /
  with a regenerated token, page, unchanged poll, text and JPEG upload, thumbnail and full image, to-mac, latest, 404, 400),
  and the fixed link surviving a restart (same port and token, switch remembered, 換一組網址 saved).
- QR codes: a generated sharing link was decoded with jsQR in a browser and matched exactly.
- The app was launched with the clipboard feature and stayed running.
- Tried by the user: region / window / full-screen / delayed capture and the settings window (2026-10-21).
- Not yet tried by the user: clipboard panel, paste-back, phone page on a real phone, firewall prompt.

## Not yet ported

All-in-One, scroll capture (the stitching algorithm in `Capture/ScrollStitcher.swift` should be ported as-is and tested against
the same synthetic cases described in `memory.md`), annotation editor, screen recording / GIF, and the 錄影 section of the settings window.
