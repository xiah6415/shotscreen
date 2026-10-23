import AppKit
import Combine

/// Clipboard history: records copies, shows the panel, and pastes the chosen entry into the frontmost app.
@MainActor
final class ClipboardFeature {
    let store = ClipboardStore()
    private let monitor = ClipboardMonitor()
    private lazy var panel = ClipboardPanelController(store: store)
    private(set) lazy var phoneShare = PhoneShare(store: store) { [weak self] item in
        guard let self else { return }
        self.monitor.ignore(changeCount: Paster.write(item, from: self.store))
    }
    private lazy var phoneShareWindow = PhoneShareWindowController(share: phoneShare)
    private var enabledObservation: AnyCancellable?
    private var explainedAccessibility = false

    init() {
        monitor.onCapture = { [weak self] snapshot in self?.store.add(snapshot) }
        panel.onPaste = { [weak self] item, plainText in self?.paste(item, plainText: plainText) }
        enabledObservation = store.$isEnabled.sink { [weak self] enabled in
            if enabled { self?.monitor.start() } else { self?.monitor.stop() }
        }
    }

    func togglePanel() { panel.toggle() }

    func showPhoneShare() { phoneShareWindow.show() }

    /// The menu switch. Turning it on without a password opens the settings so one can be set.
    func togglePhoneShare() {
        phoneShare.setEnabled(!phoneShare.isEnabled)
        if phoneShare.isEnabled, !phoneShare.auth.hasPassword { phoneShareWindow.show() }
    }

    private func paste(_ item: ClipboardItem, plainText: Bool) {
        monitor.ignore(changeCount: Paster.write(item, from: store, plainText: plainText))
        store.bump(item.id)
        guard PermissionsManager.isAccessibilityTrusted else {
            // Without Accessibility it still works as "copy back"; explain once per launch.
            guard !explainedAccessibility else { return }
            explainedAccessibility = true
            PermissionsManager.requestAccessibility()
            let alert = NSAlert()
            alert.messageText = "已放回剪貼簿，按 ⌘V 就能貼上"
            alert.informativeText = "想讓拾光直接幫你貼上，到「系統設定 → 隱私權與安全性 → 輔助使用」打開拾光。"
            alert.addButton(withTitle: "打開系統設定")
            alert.addButton(withTitle: "好")
            NSApp.activate(ignoringOtherApps: true)
            if alert.runModal() == .alertFirstButtonReturn { PermissionsManager.openAccessibilitySettings() }
            return
        }
        // Give the previous app's window a moment to become key again after the panel closes.
        Task { @MainActor in
            try? await Task.sleep(for: .milliseconds(120))
            Paster.pressPaste()
        }
    }
}
