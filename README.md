# 拾光 / Glean

Formerly 素顏 Shot.

macOS 和 Windows 的截圖小工具：截圖、捲動截圖、OCR、錄影、標註和剪貼簿紀錄。不用訂閱、不用帳號、不上雲端，也不依賴任何第三方套件。兩個平台放在同一個 repo，功能一起維護：macOS 版在根目錄（Swift），Windows 版在 [`windows/`](windows/README.md)（C# / .NET 8）。

A native screenshot tool for macOS (menu bar) and Windows (notification area): screenshots, scrolling captures, OCR, screen recording, annotation and clipboard history — no subscription, account, cloud or third-party frameworks.

## 下載

到 [Releases](https://github.com/xiah6415/shotscreen/releases/latest) 下載：

| 平台 | 檔案 | 需求 |
|---|---|---|
| macOS | `Glean-<版本>.zip` | macOS 14 Sonoma 以上，Apple 晶片和 Intel Mac 都可以 |
| Windows | `Glean-<版本>-win-x64.exe` | Windows 10 2004 以上（建議 Windows 11），不用另外裝 .NET |

不是每個版本都同時有兩個平台的檔案；找不到 Windows 版時，往前一個版本找。

## 安裝 macOS 版

1. 解壓縮 `Glean-<版本>.zip`，把 **拾光**（Glean.app）拖進「應用程式」。
2. **第一次打開會被 macOS 擋下來**：拾光沒有經過 Apple 公證（那需要付費的開發者帳號），所以會出現「無法打開」或「無法驗證開發者」。
   - 先照常打開一次，看到警告就按「完成」。
   - 到 **系統設定 → 隱私權與安全性**，往下捲到「安全性」，按 **「仍要打開」**，輸入 Mac 的密碼。
   - macOS 15 之後「右鍵 → 打開」已經不能繞過這個警告，一定要走系統設定。這一步只需要做一次。
3. 打開後會出現 **歡迎使用拾光** 視窗，照著設定：
   - **螢幕錄製**（必要）：按「允許」→ 在系統設定打開「拾光」→ 回來按「重新啟動」。
   - **輔助使用**（選用）：自動捲動、從剪貼簿紀錄直接貼上會用到。
   - **快捷鍵**：如果跟 macOS 內建的截圖鍵（⌘⇧3 / ⌘⇧4 / ⌘⇧5）衝突，可以一鍵改成 ⌃⇧ 開頭，或到系統設定關掉系統的。
   - **剪貼簿紀錄**：要不要記錄複製過的東西（預設不記錄）。
   - **開機時自動啟動**。
   之後隨時可以從選單列圖示 → **初次設定…** 再打開。
4. 有新版時拾光會自己提醒，按「更新」就會裝好並重新開啟，權限不用重開。

## 安裝 Windows 版

1. 打開下載的 `Glean-<版本>-win-x64.exe`。
2. **Windows 可能會跳出「Windows 已保護您的電腦」**：拾光是用專案自己的憑證簽名，不是付費憑證，所以 SmartScreen 不認得。按 **「其他資訊」→「仍要執行」**，只需要做一次。
3. 問「要把拾光安裝到這台電腦嗎？」時按 **是**：會裝到 `%LOCALAPPDATA%\Programs\Glean`，並加到開始功能表，不需要系統管理員權限。之後下載新版用同樣方式打開，就會取代舊的。
4. 拾光住在右下角的系統匣（沒看到的話點 `^` 展開）。第一次打開會出現 **歡迎使用拾光**，可以設定快捷鍵、剪貼簿紀錄和開機自動啟動。
5. 有新版時會自己提醒，按「更新」就會下載、確認是同一張憑證簽的，然後替換並重新開啟。

移除：從系統匣「結束拾光」，刪掉 `%LOCALAPPDATA%\Programs\Glean` 和開始功能表的捷徑；設定和紀錄在 `%APPDATA%\SuYanShot`、`%LOCALAPPDATA%\SuYanShot`。

## 主要功能與預設快捷鍵

| 功能 | macOS | Windows |
|---|---|---|
| All-in-One（框選後選截圖、捲動截圖等；macOS 另有 OCR、錄影，Tab 切換截圖／錄影） | ⌘⇧5 | Ctrl+Shift+5 |
| 框選截圖 | ⌘⇧1 | Ctrl+Shift+1 |
| 視窗截圖（游標點選，或按空白鍵用縮圖一次看所有視窗） | ⌘⇧2 | Ctrl+Shift+2 |
| 全螢幕截圖 | ⌘⇧3 | Ctrl+Shift+3 |
| 延遲截圖（3 秒） | ⌘⇧4 | Ctrl+Shift+4 |
| 捲動截圖（可自動捲動，每段會停下來問要不要繼續，卡住時會暫停） | ⌘⇧6 | Ctrl+Shift+6 |
| OCR（離線辨識繁體中文、英文，直接複製） | ⌘⇧7 | 尚未支援 |
| 標註剪貼簿裡的圖片（複製的圖片或圖片檔；剪貼簿紀錄的圖片右鍵也有「標註」） | ⌘⇧8 | Ctrl+Shift+7 |
| 錄影（範圍、視窗或全螢幕；可只錄某個 App 的聲音），可轉 GIF | ⌘⇧R | 尚未支援 |
| 剪貼簿紀錄（搜尋、標籤、直接貼上） | ⌘⇧V | Ctrl+Alt+V |

快捷鍵都能在 **設定…** 裡改。macOS 的 ⌘⇧V、⌘⇧R 在部分 App 另有用途（貼上並符合樣式、重新整理），也有「改用 ⌃⇧ 組合」一鍵避開衝突。Windows 的剪貼簿紀錄用 Ctrl+Alt+V，是因為 Ctrl+Shift+V 在瀏覽器和 Office 是「貼上純文字」。

兩個平台都有：截圖後的預覽卡（複製、標註、儲存、拖到其他 App）、標註編輯器（箭頭、框、文字、數字、螢光筆、馬賽克、模糊、裁切）、完成提示音，以及 **手機分享**：同一個 Wi-Fi 下用手機打開 `http://glean.local:8765`，用密碼登入後可以看剪貼簿紀錄、把文字或照片傳回電腦（輸入框可直接貼上圖片、預覽後按傳送，也可用「照片」選取）；只在你允許的網路開放。Mac 和 Windows 用同一個網址，所以同一個主畫面捷徑兩邊都能用。

All-in-One 的截圖模式框選後可點 **OCR**（或按 **O**，目前只有 macOS），辨識完成直接複製文字。

### 隱私

- 截圖、錄影都存在你的電腦（預設是桌面），不會上傳。
- 剪貼簿紀錄存在 macOS 的 `~/Library/Application Support/SuYanShot/Clipboard`、Windows 的 `%LOCALAPPDATA%\SuYanShot\Clipboard`；密碼管理工具標為機密的內容不會被記錄。
- 手機分享預設關閉；打開後只在你允許的網路運作，連線沒有加密，請只在信任的網路使用。
- 拾光唯一會主動連上網路的地方是檢查更新（GitHub Releases）。

## Build from source

### macOS

Requirements: macOS 14+, Swift 5.10+ (Command Line Tools are enough; full Xcode is not needed).

```sh
scripts/make-signing-cert.sh      # once per Mac: self-signed "SuYanShot Local Signing" identity
scripts/make-app.sh --install     # universal (arm64 + x86_64) Glean.app → /Applications, then launch
scripts/test-stitcher.sh          # regression test for scroll-capture stitching
```

`make-app.sh` builds each architecture with `swift build --arch` and joins them with `lipo`, adds the icon (`Resources/AppIcon.png`, reproducible with `scripts/icon/render-icon.swift`) and the zh-Hant display name 拾光, and signs with the stable identity so Screen Recording / Accessibility grants survive rebuilds. Without that identity it falls back to ad-hoc signing, which macOS treats as a new app on every build.

`swift test` needs a full Xcode toolchain (XCTest); with Command Line Tools only, use the scripts above.

### Windows

Requirements: Windows 10 2004+ and the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`). From `windows/`:

```powershell
dotnet build
dotnet test
dotnet run --project src/SuYanShot
```

See [`windows/README.md`](windows/README.md) for the architecture, decisions and what each platform still lacks.

## Publishing a release

Both apps share the `VERSION` file and the GitHub Release `v<VERSION>`.

```sh
echo 0.3.0 > VERSION            # bump, commit and push to main
scripts/release.sh notes.md     # macOS: or no argument: notes from commit subjects since the last tag
```

`scripts/release.sh` refuses a dirty tree or unpushed HEAD, builds with `make-app.sh`, checks the app is signed with `SuYanShot Local Signing`, zips it with `ditto -c -k --keepParent`, shows the notes, asks for confirmation, then runs `gh release create v<version>`. `--dry-run` stops after the zip.

```powershell
powershell -ExecutionPolicy Bypass -File windows/scripts/release.ps1   # Windows: adds the signed exe + .sha256 to v<VERSION>
```

The Windows script builds a self-contained `Glean-<VERSION>-win-x64.exe` signed with `Glean Local Signing`, then adds it (and its `.sha256`) to the existing release, or creates the release. It is deliberately not a .zip, so the macOS updater never picks it.

**Back up the signing certificates.** Installed copies only accept updates signed by the same certificate. If one is lost (new computer, keychain or certificate store reset), a newly generated one will not match and everyone has to reinstall by hand (and, on macOS, grant permissions again).

```sh
scripts/backup-signing-cert.sh                     # macOS: export to a password-protected .p12 (asks for the password)
scripts/backup-signing-cert.sh --restore file.p12  # import on another Mac you release from
```

On Windows, export `Glean Local Signing` with its private key from `certmgr.msc` → Personal → Certificates (`.pfx`). Keep the backups in a password manager or on an encrypted disk, never in this repository.

## License

[MIT](LICENSE)
