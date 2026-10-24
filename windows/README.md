# 拾光 / Glean for Windows

The Windows port of 拾光 / Glean (formerly 素顏 Shot). It lives in the same repository as the macOS app (repository root) and
follows the same product rules: private, native, no subscription, cloud, account or third-party runtime dependency.

The exe is `Glean.exe` (product name 拾光, the shared icon from `Resources/AppIcon.png`, version from the repository's
`VERSION` file). The C# project, namespaces and data folders keep the old `SuYanShot` name, the way the macOS app kept its bundle
id, so history and settings carry over: `%APPDATA%\SuYanShot` (settings, shortcuts, phone-share login) and
`%LOCALAPPDATA%\SuYanShot\Clipboard`. An existing 登入時啟動 entry is moved to the new exe on launch.
`scripts/make-icon.ps1` rebuilds `Resources/Glean.ico` from the shared PNG.

## Requirements

- Windows 10 2004 (build 19041) or later; Windows 11 recommended
- .NET 8 SDK to build (`winget install Microsoft.DotNet.SDK.8`)

## Install (for users)

1. From [Releases](https://github.com/xiah6415/shotscreen/releases/latest) download `Glean-<version>-win-x64.exe` (one
   self-contained file; no .NET install needed).
2. Open it. Windows SmartScreen may say it is from an unknown publisher (the app is signed with the project's own
   certificate, not a paid one): choose **More info → Run anyway**.
3. Answer **是** to 「要把拾光安裝到這台電腦嗎？」: it copies itself to `%LOCALAPPDATA%\Programs\Glean\Glean.exe` and adds 拾光 to
   the Start menu. No administrator rights. Opening a newer download the same way replaces the installed copy.
4. 拾光 lives in the notification area. 設定… → 登入 Windows 時自動啟動 starts it with Windows.

To uninstall: quit 拾光, delete `%LOCALAPPDATA%\Programs\Glean` and the Start menu shortcut; settings and history are in
`%APPDATA%\SuYanShot` and `%LOCALAPPDATA%\SuYanShot`.

### Updates

Same flow as macOS: shortly after launch and then daily (設定 → 自動檢查更新), and from 檢查更新… in the tray menu, the app reads
the latest GitHub Release. If it carries a newer Windows build it offers 更新 / 稍後 / 略過這個版本. 更新 downloads the exe next
to the installed one and installs it only if all of these hold: its SHA-256 matches the published `.sha256`, its product
name and version match the release, its Authenticode signature is intact, and it is signed with **the same certificate as
the running app**. Then the running exe is renamed to `Glean.exe.old`, the new one takes its place and starts, waits for the
old process to quit and deletes `.old`. Anything else falls back to opening the release page. A development build
(`bin\Debug`) never checks or installs updates. `settings.json` → `updateFeedUrl` points the check at a test feed.

The Windows build is attached to the same release as the macOS zip, as `Glean-<version>-win-x64.exe` plus
`Glean-<version>-win-x64.exe.sha256`. It is deliberately not a .zip: the macOS updater takes the first .zip asset.

## Releasing (for the maintainer)

1. Once per PC: `powershell -ExecutionPolicy Bypass -File windows/scripts/make-signing-cert.ps1` creates the
   `Glean Local Signing` code-signing certificate in the user's certificate store. **Back it up** with its private key
   (certmgr.msc → Personal → Certificates → Export, .pfx): installed copies only accept updates signed with it.
2. Bump `VERSION` (shared with macOS), commit and push.
3. `powershell -ExecutionPolicy Bypass -File windows/scripts/release.ps1` builds the signed exe
   (`windows/scripts/publish.ps1`, output in `windows/dist`), then, after asking, adds it to the existing GitHub Release
   `v<VERSION>` or creates that release. `-DryRun` builds only; `-Notes notes.md` sets the notes of a new release.

## Build, test, run

From this `windows/` folder:

```powershell
dotnet build
dotnet test
dotnet run --project src/SuYanShot
```

The built app is `src/SuYanShot/bin/Debug/net8.0-windows10.0.19041.0/Glean.exe`.

The app lives in the notification area (system tray) and has no main window. Quit it from its menu.

To produce a standalone exe:

```powershell
dotnet publish src/SuYanShot -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## Current features (phase one)

| Feature | Default shortcut | macOS equivalent |
| --- | --- | --- |
| All-in-One: drag a region, adjust it (move, 8 handles, flips past the opposite edge), then 截圖 (Enter / double-click), 捲動截圖 (S), 視窗 or 全螢幕 from the toolbar; Esc / right-click cancels | Ctrl+Shift+5 | ⌘⇧5 |
| 框選截圖: drag a region on a frozen copy of the screen; Esc / right-click cancels; size label and crosshair guides | Ctrl+Shift+1 | ⌘⇧1 |
| 視窗截圖: hover highlights the front-most window, click captures it (even where other windows cover it) | Ctrl+Shift+2 | ⌘⇧2 |
| 全螢幕截圖: the monitor under the pointer | Ctrl+Shift+3 | ⌘⇧3 |
| 延遲截圖: 3 / 5 / 10 s countdown (click the countdown to cancel), then full screen | Ctrl+Shift+4 (3 s) | ⌘⇧4 |
| 捲動截圖: select a region, scroll down (or turn on 自動捲動); frames are stitched into one tall image. Pressing the shortcut again finishes | Ctrl+Shift+6 | ⌘⇧6 |

Every capture is copied to the clipboard (bitmap + PNG) and shown on a preview card in the bottom-right corner:
hover for 複製 / 標註 / 儲存 / 關閉, drag it into another app to drop a `.png`, double-click (or 標註) to open the
annotation editor.
The card closes after 8 s unless hovered. Saved files go to the Desktop as `Screenshot 2026-10-21 at 14.03.07.png`, the same
naming as macOS.

Tray menu also has 剪貼簿紀錄, 手機分享 (switch), 手機分享設定…, 打開截圖資料夾 and 設定….

### 捲動截圖 (scroll capture)

Same flow as macOS. After selecting a region, a dashed border appears just outside it, a control bar below it (自動捲動 /
暫停 / 繼續往下, 取消, 完成) and a live preview of the stitched-so-far image beside it; none of them take focus or appear in the
frames. The region is captured every 100 ms (200 ms while auto scrolling) and `Capture/ScrollStitcher.cs` — a port of the
macOS stitcher with the same thresholds — matches each frame against the last one: 96-column grayscale row signatures
(rightmost 4% ignored for overlay scrollbars), sticky top/bottom bars detected and shown once, a coarse 1/4-height search
refined at full resolution, unmatched frames skipped rather than guessed, scrolling down only, 40,000 px cap.

One Windows-only addition (`Disambiguate`): when the compared overlap is entirely blank, blank rows just above the footer
are compared too before picking an offset, so a long empty stretch can only lose or repeat blank rows, never content.
The ported synthetic test found the macOS stitcher dropping a 14 px text line next to a blank stretch (seed 7, normal mode,
with this test's random generator); the macOS stitcher may want the same change.

自動捲動 sends mouse-wheel notches to the window under the region (the pointer is moved to its centre), learns how many pixels
a notch moves in that app from the stitched offsets, aims for ~35% of the region per frame, shrinks the step after a
rejected frame, tries the other direction once if nothing ever moved, pauses every ~3 region heights to ask 要繼續嗎？, and
stops with 已經到底了 after 4 frames without movement. No permission is needed; apps running as administrator ignore it.

### 標註 (annotation editor)

Same tools, keys and behaviour as macOS: 選取 (V), 裁切 (C), 箭頭 (A), 矩形 (R), 橢圓 (O), 直線 (L), 畫筆 (P), 螢光筆 (H),
文字 (T), 數字標記 (N), 馬賽克 (B), 模糊 (U); 7 colours and 3 sizes (changing them restyles the selected mark); Shift
constrains shapes to squares/circles and lines to 45°; Delete/Backspace removes the selection; double-click or Enter on a
text mark edits it. Crop: drag a box, Enter applies, Esc cancels, Backspace restores the full image. Ctrl+Z / Ctrl+Shift+Z
(or Ctrl+Y) undo and redo (crop included), Ctrl+C copies, Ctrl+S saves to the Desktop with the capture's time, Ctrl+W closes.
Geometry is stored in image pixels, so the canvas and the exported image use the same renderer.

Differences from macOS, from GDI+: the soft shadow under marks is a translucent offset stroke, the highlighter is a
translucent stroke rather than a multiply blend, and the text box shows on a plain backdrop while typing.

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

### 手機分享 (phone access)

Same design as macOS (v0.3.x): a switch in the tray menu (手機分享) that is remembered and starts with the app, a fixed address
`http://glean.local:8765/`, password login, and allowed networks. The tray icon gets a green dot while phones can connect, and
the menu item shows why it is paused (要先設定密碼 / 暫停：不在允許的網路 / 等待網路 / 無法啟動). Turning it on without a password
opens 手機分享設定….

- **Address.** `glean.local` is published over multicast DNS by a small built-in responder (`LocalNameAdvertiser`; Windows has
  no public API for a bare host name). It probes first; if another device answers for the name it uses `glean-2`, `glean-3` …
  The name can be changed in the settings window; keeping `glean` means one Home Screen shortcut works for every computer
  running 拾光, Mac or PC. The IP address is shown as a fallback, and while the name is not published.
- **Login.** The URL carries no secret. A device enters the password once (`POST /api/login`) and gets a one-year
  `glean_session` cookie (HttpOnly, SameSite=Strict). Only hashes are stored, in `%APPDATA%\SuYanShot\phone-share-auth.json`:
  PBKDF2-SHA256 (200,000 rounds) for the password, SHA-256 for session tokens. Five wrong passwords lock that address for a
  minute; devices can be signed out one by one or all at once; changing the password signs everyone out. Every POST needs an
  `X-Requested-With: glean` header, which cross-site forms cannot send.
- **Networks.** With 只在允許的網路開放 (on by default), sharing runs only on networks in the list, identified by the router's MAC
  address (via ARP), and pauses elsewhere; the network it was first turned on in is added automatically.
- **Routes and pages** match macOS: `GET /` (login page until signed in), `/apple-touch-icon.png`, `/manifest.webmanifest`,
  `POST /api/login`, `POST /api/logout`, `GET /api/items?v=`, `GET /api/items/<id>/image[?thumb=1]`,
  `POST /api/items/<id>/to-mac`, `POST /api/send`. The pages are copies of the macOS ones with "Mac" worded as 電腦.
  The earlier Windows-only token links and `/api/latest` are gone; add the page to the Home Screen instead.

**The first time sharing starts, Windows Defender Firewall asks whether 拾光 may accept connections. Allow it on private
networks**, otherwise the phone cannot reach the page or resolve `glean.local`. Photos from the phone are decoded by GDI+
(JPEG/PNG with EXIF rotation) or the Windows imaging codecs (HEIC needs the free HEIF extension from the Microsoft Store).

### 設定

The settings window (tray menu → 設定…) has a recorder per action: click it, press the new combination. Esc cancels,
Backspace clears. A shortcut needs Ctrl, Alt or Win plus a letter, digit or F-key; F1–F24 and `PrintScreen` can stand alone.
A combination already used by another action moves to the new one. Changes apply immediately; combinations another program
already owns are marked 已被其他程式使用. 還原預設 restores the defaults. 登入 Windows 時自動啟動 adds/removes
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Glean`.

Shortcuts are stored in `%APPDATA%\SuYanShot\shortcuts.json` (`{ "Region": "Ctrl+Shift+1", ... }`, `""` = cleared).
Using bare `PrintScreen` needs Windows' own "Use the Print screen key to open screen capture" setting turned off.

## Architecture

WinForms on .NET 8, no NuGet packages in the app. The structure mirrors `Sources/SuYanShot`:

- `App/TrayApplication.cs` — tray icon and menu (≈ `MenuBarController` + `AppDelegate`).
- `Capture/CaptureCoordinator.cs` — workflow boundary: capture → clipboard → preview card. Capture code never touches UI or files.
- `Capture/ScreenCapture.cs` — `FrozenDesktop`, GDI screen capture, `PrintWindow` window capture with a fallback.
- `Capture/SelectionOverlay.cs` — one overlay per monitor for region and window selection.
- `Capture/WindowList.cs` — visible top-level windows in z-order.
- `Capture/AllInOneSelector.cs` — the All-in-One overlay (adjustable region and its toolbar).
- `Capture/ScrollStitcher.cs`, `Capture/ScrollCapture.cs`, `UI/ScrollCaptureHud.cs` — scroll capture: stitcher, capture loop
  with auto scroll, and the border / control bar / preview windows.
- `Annotation/` (namespace `SuYanShot.Annotations`) — model, renderer + pixelate/blur effects, editor state with undo,
  canvas, and the editor window with its code-drawn toolbar.
- `Shortcuts/` — `RegisterHotKey` wrapper, shortcut parsing and the JSON store.
- `Clipboard/` (namespace `SuYanShot.ClipboardHistory`, to avoid clashing with WinForms' `Clipboard`) — model, store,
  monitor, paster, feature, and phone sharing (`PhoneShareServer` = TcpListener HTTP server, `PhoneShare` = state and
  routes, `PhoneShareAuth` = password and devices, `LocalNameAdvertiser` = mDNS responder, `HomeNetwork` = network
  identity and change monitor, `PhoneSharePage.html` / `PhoneShareLogin.html` = embedded copies of the macOS pages).
- `App/AppSettings.cs` — non-shortcut preferences.
- `App/StartupRegistration.cs` — 登入時啟動 (per-user Run key).
- `Storage/` — `ImageExporter` (naming / PNG policy) and clipboard.
- `UI/AppIcon.cs` — the shared icon for the tray, windows and the phone's Home Screen.
- `UI/` — preview card, countdown HUD, settings window and its shortcut recorder, clipboard panel, phone share window,
  and `QrCode.cs` (a small QR encoder, byte mode / level M, so no package is needed).
- `Update/` — `UpdateChecker` (GitHub Releases feed, version compare), `UpdateInstaller` (download, checksum, signature
  checks via `WinVerifyTrust` and the signer thumbprint, swap), `UpdateController` (schedule and prompts),
  `AppInstallation` (first-run install, Start menu shortcut, post-update clean-up).
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
- **Own mDNS responder** for `glean.local`: it shares UDP 5353 with Windows' own mDNS service (`SO_REUSEADDR`), answers only A
  queries for its name, and probes before claiming it.

## Status and testing

- `dotnet build`: passes with no warnings. `dotnet test`: 80 tests pass — naming, no-overwrite export, shortcut
  parsing/store, frozen-desktop cropping, blank detection; clipboard store (ordering, de-duplication, payloads, thumbnails,
  persistence with macOS field values, trimming and clearing with favourites/tags, tag create/rename/toggle/delete, payload
  cleanup); HTTP parsing and private-address checks; phone sharing over loopback (status for off / no password / other network /
  no network / allowed, settings persisted; login page and public routes, X-Requested-With guard, wrong password count, cookie
  attributes, hashed-only storage, every route signed in, forged cookie, logout, five-failure lockout, password change signing
  everyone out); mDNS name sanitising, query/answer round trip and compressed names; All-in-One resizing; annotation state and
  renderer; the macOS synthetic stitcher test ported (8 seeds × normal / edge mode: every frame with content in the overlap gets
  the exact offset, sticky bars appear once, content rows match the ground truth row for row; ~20 ms per 1000×800 frame);
  update version comparison and release parsing (Windows assets picked, the macOS zip never, macOS-only releases ignored).
- Checked by hand on this PC (2026-10-24): Windows' own resolver (`Resolve-DnsName`) found the name published by the responder
  at this PC's address, and with a simulated device already owning the name the responder backed off to `-2`.
- QR codes: a generated sharing link was decoded with jsQR in a browser and matched exactly.
- Updates, checked end to end on this PC against a local feed (2026-10-24) with a signed 0.3.1 "installed" copy and a
  0.3.2 build: the right asset was chosen, downloaded, verified and swapped in, and the new version started, waited for the
  old process and deleted `.old`; a tampered exe (with a matching checksum), an exe signed by another certificate, a wrong
  checksum and a wrong version were all refused, leaving 0.3.1 in place.
- Scroll capture, checked by hand on this PC (2026-10-24): auto scroll on a 6,400 px test window stitched 6,396 px (the
  region is a few pixels shorter than the window) through three checkpoints and stopped at the end, three runs in a row; the
  seams were checked by eye. One earlier run stopped after two frames, probably because the test window was not yet in front.
- Tried by the user: capture, settings window, clipboard history and the earlier token-based phone page (2026-10-21).
- Not yet tried by the user: the rename, `glean.local` with password login from a real phone, All-in-One, annotation editor,
  scroll capture.

## Not yet ported

Screen recording / GIF (and its 錄影 button in All-in-One and section in the settings window), the window picker's
thumbnail grid, and the first-run 初次設定 window.
