import AppKit

@MainActor
final class MenuBarController: NSObject {
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let coordinator: CaptureCoordinator

    init(coordinator: CaptureCoordinator) {
        self.coordinator = coordinator
        super.init()
        statusItem.button?.image = NSImage(systemSymbolName: "camera.viewfinder", accessibilityDescription: "素顏 Shot")
        let menu = NSMenu()
        menu.addItem(item("框選截圖", symbol: "rectangle.dashed", action: #selector(regionCapture), key: "1"))
        menu.addItem(item("視窗截圖", symbol: "macwindow", action: #selector(windowCapture), key: "2"))
        menu.addItem(item("全螢幕截圖", symbol: "display", action: #selector(fullScreenCapture), key: "3"))
        let delayed = item("延遲截圖", symbol: "timer", action: nil)
        let delayedMenu = NSMenu(title: "延遲截圖")
        for option in DelayOption.allCases {
            let menuItem = item(option.title, action: #selector(delayCapture), key: option == .three ? "4" : "")
            menuItem.representedObject = option
            delayedMenu.addItem(menuItem)
        }
        delayed.submenu = delayedMenu
        menu.addItem(delayed)
        menu.addItem(.separator())
        menu.addItem(item("打開截圖資料夾", symbol: "folder", action: #selector(openFolder)))
        menu.addItem(item("權限設定…", symbol: "lock.shield", action: #selector(openPermissions)))
        menu.addItem(.separator())
        let quit = item("結束 素顏 Shot", action: #selector(quit), key: "q")
        quit.keyEquivalentModifierMask = .command
        menu.addItem(quit)
        statusItem.menu = menu
    }

    /// Number keys are shown as ⌘⇧N to match the global shortcuts.
    private func item(_ title: String, symbol: String? = nil, action: Selector?, key: String = "") -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = self
        item.keyEquivalentModifierMask = [.command, .shift]
        if let symbol { item.image = NSImage(systemSymbolName: symbol, accessibilityDescription: title) }
        return item
    }

    @objc private func regionCapture() { coordinator.captureRegion() }
    @objc private func windowCapture() { coordinator.captureWindow() }
    @objc private func fullScreenCapture() { coordinator.captureFullScreen() }
    @objc private func delayCapture(_ sender: NSMenuItem) { if let option = sender.representedObject as? DelayOption { coordinator.captureDelayed(option) } }
    @objc private func openFolder() { coordinator.openCaptureFolder() }
    @objc private func openPermissions() { PermissionsManager.openScreenCaptureSettings() }
    @objc private func quit() { NSApp.terminate(nil) }
}
