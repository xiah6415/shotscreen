import AppKit

/// Checks GitHub for a newer release on launch, once a day and from the menu, and walks the user through installing it.
@MainActor
final class UpdateController {
    let settings = UpdateSettings()
    /// True while installing would interrupt something (a recording); automatic prompts wait for a later check.
    var isAppBusy: () -> Bool = { false }

    private let checker = UpdateChecker()
    private var timer: Timer?
    private var isBusy = false
    private var progressPanel: NSPanel?

    func start() {
        // Hourly tick; a check only runs when the last one is a day old.
        timer = Timer.scheduledTimer(withTimeInterval: 3600, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.checkAutomatically(force: false) }
        }
        timer?.tolerance = 600
        // Give launch (permission prompts, hotkeys) a moment first.
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in self?.checkAutomatically(force: true) }
    }

    /// 檢查更新…: always answers, including "already up to date" and errors.
    func checkNow() { check(userInitiated: true) }

    private func checkAutomatically(force: Bool) {
        guard settings.automaticallyChecks, !isAppBusy() else { return }
        if !force, let last = settings.lastCheck, Date.now.timeIntervalSince(last) < 24 * 3600 { return }
        check(userInitiated: false)
    }

    private func check(userInitiated: Bool) {
        guard !isBusy else { return }
        isBusy = true
        Task { @MainActor in
            defer { isBusy = false }
            do {
                let release = try await checker.latestRelease()
                settings.lastCheck = .now
                guard let release, AppVersion.isNewer(release.version, than: AppVersion.current) else {
                    if userInitiated { inform("拾光已經是最新版本", "目前版本 \(AppVersion.current)。") }
                    return
                }
                if !userInitiated && (release.version == settings.skippedVersion || isAppBusy()) { return }
                await offer(release)
            } catch {
                if userInitiated { inform("沒辦法檢查更新", "\(error.localizedDescription)\n請確認網路連線後再試一次。") }
            }
        }
    }

    private func offer(_ release: ReleaseInfo) async {
        let alert = NSAlert()
        alert.messageText = "拾光 \(release.version) 推出了"
        alert.informativeText = "你目前用的是 \(AppVersion.current)。更新會下載新版、取代「應用程式」裡的 App，然後重新打開。"
        if !release.notes.isEmpty { alert.accessoryView = notesView(release.notes) }
        alert.addButton(withTitle: "更新")
        alert.addButton(withTitle: "稍後")
        alert.addButton(withTitle: "略過這個版本")
        NSApp.activate(ignoringOtherApps: true)
        switch alert.runModal() {
        case .alertFirstButtonReturn: await install(release)
        case .alertThirdButtonReturn: settings.skippedVersion = release.version
        default: break
        }
    }

    private func install(_ release: ReleaseInfo) async {
        if isAppBusy() {
            inform("錄影中，先不更新", "停止錄影後再從選單的「檢查更新…」更新。")
            return
        }
        showProgress("正在下載拾光 \(release.version)…")
        let installer = UpdateInstaller()
        do {
            let app = try await installer.prepare(release)
            let installed = try installer.install(app)
            try installer.relaunch(installed)
        } catch {
            hideProgress()
            let alert = NSAlert()
            alert.alertStyle = .warning
            alert.messageText = "沒辦法自動更新"
            alert.informativeText = "\(error.localizedDescription)\n\n可以到下載頁面手動下載壓縮檔，解壓縮後把 App 拖進「應用程式」資料夾取代舊的。"
            alert.addButton(withTitle: "打開下載頁面")
            alert.addButton(withTitle: "取消")
            NSApp.activate(ignoringOtherApps: true)
            if alert.runModal() == .alertFirstButtonReturn { NSWorkspace.shared.open(release.pageURL) }
        }
    }

    private func inform(_ title: String, _ message: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }

    private func notesView(_ notes: String) -> NSView {
        let scroll = NSTextView.scrollableTextView()
        scroll.frame = NSRect(x: 0, y: 0, width: 380, height: 160)
        scroll.borderType = .bezelBorder
        let text = scroll.documentView as! NSTextView
        text.isEditable = false
        text.font = .systemFont(ofSize: NSFont.smallSystemFontSize)
        text.textContainerInset = NSSize(width: 4, height: 6)
        text.string = notes
        return scroll
    }

    private func showProgress(_ message: String) {
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 300, height: 76), styleMask: [.titled], backing: .buffered, defer: false)
        panel.title = "更新拾光"
        panel.isReleasedWhenClosed = false
        let label = NSTextField(labelWithString: message)
        let bar = NSProgressIndicator()
        bar.isIndeterminate = true
        bar.startAnimation(nil)
        let stack = NSStackView(views: [label, bar])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.edgeInsets = NSEdgeInsets(top: 14, left: 18, bottom: 14, right: 18)
        bar.widthAnchor.constraint(equalToConstant: 264).isActive = true
        panel.contentView = stack
        panel.center()
        NSApp.activate(ignoringOtherApps: true)
        panel.makeKeyAndOrderFront(nil)
        progressPanel = panel
    }

    private func hideProgress() {
        progressPanel?.close()
        progressPanel = nil
    }
}
