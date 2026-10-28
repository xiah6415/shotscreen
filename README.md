# 拾光 / Glean

Formerly 素顏 Shot.

macOS 選單列小工具：截圖、捲動截圖、OCR、錄影、標註和剪貼簿紀錄。不用訂閱、不用帳號、不上雲端，也不依賴任何第三方套件。

A native macOS menu-bar tool for screenshots, scrolling captures, screen recording, annotation and clipboard history — no subscription, account, cloud or third-party frameworks.

## 安裝（給使用者）

需要 macOS 14 Sonoma 以上，Apple 晶片和 Intel Mac 都可以。

1. 到 [Releases](https://github.com/xiah6415/shotscreen/releases/latest) 下載 `Glean-<版本>.zip`，解壓縮後把 **拾光**（Glean.app）拖進「應用程式」。
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

### 主要功能與預設快捷鍵

| 功能 | 預設快捷鍵 |
|---|---|
| All-in-One（框選後選截圖、OCR、捲動截圖、錄影；Tab 切換截圖／錄影） | ⌘⇧5 |
| 框選截圖 | ⌘⇧1 |
| 視窗截圖（游標點選，或按空白鍵用縮圖一次看所有視窗） | ⌘⇧2 |
| 全螢幕截圖 | ⌘⇧3 |
| 延遲截圖（3 秒） | ⌘⇧4 |
| 捲動截圖（可自動捲動，每段會停下來問要不要繼續） | ⌘⇧6 |
| OCR（離線辨識繁體中文、英文，直接複製） | ⌘⇧7 |
| 錄影（範圍、視窗或全螢幕；可只錄某個 App 的聲音） | ⌘⇧R |
| 剪貼簿紀錄（搜尋、標籤、直接貼上） | ⌘⇧V |

快捷鍵都能在 **設定…（⌘,）** 裡改，也有「改用 ⌃⇧ 組合」一鍵避開衝突。⌘⇧V、⌘⇧R 在部分 App 另有用途（貼上並符合樣式、重新整理），不習慣的話建議改用 ⌃⇧。

All-in-One 的截圖模式框選後可點 **OCR**（或按 **O**），辨識完成直接複製文字。截圖與 OCR 成功時會播放不同提示音。

其他：截圖後的標註編輯器（箭頭、框、文字、數字、馬賽克、模糊、裁切）、錄影轉 GIF、**手機分享**（同一個 Wi-Fi 下用手機打開 `http://glean.local:8765`，用密碼登入後可以看剪貼簿紀錄、把文字或照片傳回 Mac；只在你允許的網路開放）。

### 隱私

- 截圖、錄影都存在你的 Mac（預設是桌面），不會上傳。
- 剪貼簿紀錄存在 `~/Library/Application Support/SuYanShot/Clipboard`，密碼管理工具標為機密的內容不會被記錄。
- 手機分享預設關閉；打開後只在你允許的網路運作，連線沒有加密，請只在信任的網路使用。
- 拾光唯一會主動連上網路的地方是檢查更新（GitHub Releases）。

## Build from source

Requirements: macOS 14+, Swift 5.10+ (Command Line Tools are enough; full Xcode is not needed).

```sh
scripts/make-signing-cert.sh      # once per Mac: self-signed "SuYanShot Local Signing" identity
scripts/make-app.sh --install     # universal (arm64 + x86_64) Glean.app → /Applications, then launch
scripts/test-stitcher.sh          # regression test for scroll-capture stitching
```

`make-app.sh` builds each architecture with `swift build --arch` and joins them with `lipo`, adds the icon (`Resources/AppIcon.png`, reproducible with `scripts/icon/render-icon.swift`) and the zh-Hant display name 拾光, and signs with the stable identity so Screen Recording / Accessibility grants survive rebuilds. Without that identity it falls back to ad-hoc signing, which macOS treats as a new app on every build.

`swift test` needs a full Xcode toolchain (XCTest); with Command Line Tools only, use the scripts above.

## Publishing a release

```sh
echo 0.3.0 > VERSION            # bump, commit and push to main
scripts/release.sh notes.md     # or no argument: notes from commit subjects since the last tag
```

`scripts/release.sh` refuses a dirty tree or unpushed HEAD, builds with `make-app.sh`, checks the app is signed with `SuYanShot Local Signing`, zips it with `ditto -c -k --keepParent`, shows the notes, asks for confirmation, then runs `gh release create v<version>`. `--dry-run` stops after the zip.

**Back up the signing certificate.** Installed copies only accept updates signed by the same certificate. If the `SuYanShot Local Signing` identity is lost (new Mac, keychain reset), a newly generated one will not match and everyone has to reinstall by hand and grant permissions again.

```sh
scripts/backup-signing-cert.sh                     # export to a password-protected .p12 (asks for the password)
scripts/backup-signing-cert.sh --restore file.p12  # import on another Mac you release from
```

Keep the `.p12` in a password manager or on an encrypted disk, never in this repository.

## License

[MIT](LICENSE)
