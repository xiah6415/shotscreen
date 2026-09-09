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
        menu.addItem(item("Region Capture", action: #selector(regionCapture), key: "1"))
        menu.addItem(item("Window Capture", action: #selector(windowCapture), key: "2"))
        menu.addItem(item("Full Screen Capture", action: #selector(fullScreenCapture), key: "3"))
        let delayed = NSMenuItem(title: "Delay Capture", action: nil, keyEquivalent: "")
        let delayedMenu = NSMenu(title: "Delay Capture")
        for option in DelayOption.allCases { let menuItem = item(option.title, action: #selector(delayCapture)); menuItem.representedObject = option; delayedMenu.addItem(menuItem) }
        delayed.submenu = delayedMenu; menu.addItem(delayed)
        menu.addItem(.separator())
        menu.addItem(item("Settings…", action: #selector(settings)))
        menu.addItem(.separator())
        menu.addItem(item("Quit 素顏 Shot", action: #selector(quit), key: "q"))
        statusItem.menu = menu
    }
    private func item(_ title: String, action: Selector, key: String = "") -> NSMenuItem { let item = NSMenuItem(title: title, action: action, keyEquivalent: key); item.target = self; return item }
    @objc private func regionCapture() { coordinator.captureRegion() }
    @objc private func windowCapture() { coordinator.captureWindow() }
    @objc private func fullScreenCapture() { coordinator.captureFullScreen() }
    @objc private func delayCapture(_ sender: NSMenuItem) { if let option = sender.representedObject as? DelayOption { coordinator.captureDelayed(option) } }
    @objc private func settings() { let alert = NSAlert(); alert.messageText = "Settings"; alert.informativeText = "Settings UI is intentionally deferred. Screenshots save to Desktop by default."; alert.runModal() }
    @objc private func quit() { NSApp.terminate(nil) }
}
