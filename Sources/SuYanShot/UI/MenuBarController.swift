import AppKit

@MainActor
final class MenuBarController: NSObject {
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let coordinator: CaptureCoordinator
    private let openSettings: () -> Void
    private let openClipboard: () -> Void
    private let openPhoneShare: () -> Void
    private let togglePhoneShare: () -> Void
    private let checkForUpdates: () -> Void
    private let openWelcome: () -> Void
    private var phoneShareItem: NSMenuItem?
    private var isRecording = false
    private var isSharing = false
    private var actionItems: [ShortcutAction: NSMenuItem] = [:]
    private var stopRecordingItem: NSMenuItem?

    init(coordinator: CaptureCoordinator, openSettings: @escaping () -> Void, openClipboard: @escaping () -> Void,
         openPhoneShare: @escaping () -> Void, togglePhoneShare: @escaping () -> Void, checkForUpdates: @escaping () -> Void,
         openWelcome: @escaping () -> Void) {
        self.coordinator = coordinator
        self.openSettings = openSettings
        self.openClipboard = openClipboard
        self.openPhoneShare = openPhoneShare
        self.togglePhoneShare = togglePhoneShare
        self.checkForUpdates = checkForUpdates
        self.openWelcome = openWelcome
        super.init()
        statusItem.button?.image = MenuBarIcon.image
        let menu = NSMenu()
        let stop = item("停止錄影", symbol: "stop.circle.fill", action: #selector(stopRecording))
        stop.isHidden = true
        menu.addItem(stop)
        stopRecordingItem = stop
        actionItems[.allInOne] = item("All-in-One", symbol: "viewfinder", action: #selector(allInOneCapture))
        menu.addItem(actionItems[.allInOne]!)
        menu.addItem(.separator())
        actionItems[.region] = item("框選截圖", symbol: "rectangle.dashed", action: #selector(regionCapture))
        actionItems[.window] = item("視窗截圖", symbol: "macwindow", action: #selector(windowCapture))
        actionItems[.fullScreen] = item("全螢幕截圖", symbol: "display", action: #selector(fullScreenCapture))
        actionItems[.scroll] = item("捲動截圖", symbol: "scroll", action: #selector(scrollCapture))
        [ShortcutAction.region, .window, .fullScreen, .scroll].forEach { menu.addItem(actionItems[$0]!) }
        let delayed = item("延遲截圖", symbol: "timer", action: nil)
        let delayedMenu = NSMenu(title: "延遲截圖")
        for option in DelayOption.allCases {
            let menuItem = item(option.title, action: #selector(delayCapture))
            menuItem.representedObject = option
            if option == .three { actionItems[.delayed] = menuItem }
            delayedMenu.addItem(menuItem)
        }
        delayed.submenu = delayedMenu
        menu.addItem(delayed)
        let record = item("錄影", symbol: "record.circle", action: nil)
        let recordMenu = NSMenu(title: "錄影")
        actionItems[.record] = item("框選範圍錄影", symbol: "rectangle.dashed", action: #selector(recordRegion))
        recordMenu.addItem(actionItems[.record]!)
        recordMenu.addItem(item("視窗錄影", symbol: "macwindow", action: #selector(recordWindow)))
        recordMenu.addItem(item("全螢幕錄影", symbol: "display", action: #selector(recordFullScreen)))
        record.submenu = recordMenu
        menu.addItem(record)
        menu.addItem(.separator())
        actionItems[.clipboard] = item("剪貼簿紀錄", symbol: "doc.on.clipboard", action: #selector(showClipboard))
        menu.addItem(actionItems[.clipboard]!)
        let phoneShare = item("手機分享", symbol: "iphone", action: #selector(togglePhone))
        menu.addItem(phoneShare)
        phoneShareItem = phoneShare
        menu.addItem(item("手機分享設定…", symbol: "qrcode", action: #selector(showPhoneShare)))
        menu.addItem(.separator())
        menu.addItem(item("打開截圖資料夾", symbol: "folder", action: #selector(openFolder)))
        menu.addItem(item("權限設定…", symbol: "lock.shield", action: #selector(openPermissions)))
        let settings = item("設定…", symbol: "gearshape", action: #selector(showSettings), key: ",")
        settings.keyEquivalentModifierMask = .command
        menu.addItem(settings)
        menu.addItem(item("檢查更新…", symbol: "arrow.down.circle", action: #selector(checkUpdates)))
        menu.addItem(item("初次設定…", symbol: "sparkles", action: #selector(showWelcome)))
        menu.addItem(.separator())
        let quit = item("結束拾光", action: #selector(quit), key: "q")
        quit.keyEquivalentModifierMask = .command
        menu.addItem(quit)
        statusItem.menu = menu
    }

    /// Red menu-bar icon and a 停止錄影 item while recording.
    func setRecording(_ recording: Bool) {
        isRecording = recording
        refreshIcon()
        statusItem.button?.contentTintColor = recording ? .systemRed : nil
        stopRecordingItem?.isHidden = !recording
    }

    /// Check mark on 手機分享, its status in the title, and a dot on the menu-bar icon while phones can connect.
    func setPhoneShare(enabled: Bool, serving: Bool, note: String?) {
        phoneShareItem?.state = enabled ? .on : .off
        phoneShareItem?.title = note.map { "手機分享（\($0)）" } ?? "手機分享"
        isSharing = serving
        refreshIcon()
    }

    private func refreshIcon() {
        statusItem.button?.image = isRecording
            ? NSImage(systemSymbolName: "record.circle.fill", accessibilityDescription: "拾光 錄影中")
            : (isSharing ? MenuBarIcon.sharingImage : MenuBarIcon.image)
    }

    /// Shows the user's global shortcuts next to the matching menu items.
    func update(shortcuts: [ShortcutAction: KeyShortcut]) {
        for (action, item) in actionItems {
            item.keyEquivalent = shortcuts[action]?.menuKeyEquivalent ?? ""
            item.keyEquivalentModifierMask = shortcuts[action]?.modifierFlags ?? []
        }
    }

    private func item(_ title: String, symbol: String? = nil, action: Selector?, key: String = "") -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = self
        if let symbol { item.image = NSImage(systemSymbolName: symbol, accessibilityDescription: title) }
        return item
    }

    @objc private func allInOneCapture() { coordinator.captureAllInOne() }
    @objc private func regionCapture() { coordinator.captureRegion() }
    @objc private func windowCapture() { coordinator.captureWindow() }
    @objc private func fullScreenCapture() { coordinator.captureFullScreen() }
    @objc private func scrollCapture() { coordinator.captureScrolling() }
    @objc private func recordRegion() { coordinator.recordRegion() }
    @objc private func recordFullScreen() { coordinator.recordFullScreen() }
    @objc private func recordWindow() { coordinator.recordWindow() }
    @objc private func stopRecording() { coordinator.stopRecording() }
    @objc private func delayCapture(_ sender: NSMenuItem) { if let option = sender.representedObject as? DelayOption { coordinator.captureDelayed(option) } }
    @objc private func openFolder() { coordinator.openCaptureFolder() }
    @objc private func showSettings() { openSettings() }
    @objc private func showClipboard() { openClipboard() }
    @objc private func showPhoneShare() { openPhoneShare() }
    @objc private func togglePhone() { togglePhoneShare() }
    @objc private func checkUpdates() { checkForUpdates() }
    @objc private func showWelcome() { openWelcome() }
    @objc private func openPermissions() { PermissionsManager.openScreenCaptureSettings() }
    @objc private func quit() { NSApp.terminate(nil) }
}
