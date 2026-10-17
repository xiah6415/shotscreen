import AppKit

@MainActor
final class MenuBarController: NSObject {
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let coordinator: CaptureCoordinator
    private let openSettings: () -> Void
    private let openClipboard: () -> Void
    private let openPhoneShare: () -> Void
    private var actionItems: [ShortcutAction: NSMenuItem] = [:]

    init(coordinator: CaptureCoordinator, openSettings: @escaping () -> Void, openClipboard: @escaping () -> Void,
         openPhoneShare: @escaping () -> Void) {
        self.coordinator = coordinator
        self.openSettings = openSettings
        self.openClipboard = openClipboard
        self.openPhoneShare = openPhoneShare
        super.init()
        statusItem.button?.image = NSImage(systemSymbolName: "camera.viewfinder", accessibilityDescription: "素顏 Shot")
        let menu = NSMenu()
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
        menu.addItem(.separator())
        actionItems[.clipboard] = item("剪貼簿紀錄", symbol: "doc.on.clipboard", action: #selector(showClipboard))
        menu.addItem(actionItems[.clipboard]!)
        menu.addItem(item("在手機上開啟…", symbol: "iphone", action: #selector(showPhoneShare)))
        menu.addItem(.separator())
        menu.addItem(item("打開截圖資料夾", symbol: "folder", action: #selector(openFolder)))
        menu.addItem(item("權限設定…", symbol: "lock.shield", action: #selector(openPermissions)))
        let settings = item("設定…", symbol: "gearshape", action: #selector(showSettings), key: ",")
        settings.keyEquivalentModifierMask = .command
        menu.addItem(settings)
        menu.addItem(.separator())
        let quit = item("結束 素顏 Shot", action: #selector(quit), key: "q")
        quit.keyEquivalentModifierMask = .command
        menu.addItem(quit)
        statusItem.menu = menu
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
    @objc private func delayCapture(_ sender: NSMenuItem) { if let option = sender.representedObject as? DelayOption { coordinator.captureDelayed(option) } }
    @objc private func openFolder() { coordinator.openCaptureFolder() }
    @objc private func showSettings() { openSettings() }
    @objc private func showClipboard() { openClipboard() }
    @objc private func showPhoneShare() { openPhoneShare() }
    @objc private func openPermissions() { PermissionsManager.openScreenCaptureSettings() }
    @objc private func quit() { NSApp.terminate(nil) }
}
