import AppKit
import ServiceManagement

/// 開機時自動啟動, via the system's login items (System Settings → General → Login Items).
@MainActor
enum LaunchAtLogin {
    static var isEnabled: Bool { SMAppService.mainApp.status == .enabled }
    /// The user must still allow it in System Settings.
    static var needsApproval: Bool { SMAppService.mainApp.status == .requiresApproval }

    static func set(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            NSAlert(error: error).runModal()
        }
        if needsApproval { SMAppService.openSystemSettingsLoginItems() }
    }
}

/// macOS's own screenshot shortcuts, which take ⌘⇧3 / ⌘⇧4 / ⌘⇧5 before any app sees them.
enum SystemScreenshotShortcuts {
    /// Symbolic hotkey IDs → the key code they use (with ⌘⇧).
    private static let hotkeys: [Int: Int] = [28: 20, 30: 21, 184: 23] // ⌘⇧3, ⌘⇧4, ⌘⇧5

    /// Our actions whose shortcut macOS currently keeps for itself.
    static func conflicts(with shortcuts: [ShortcutAction: KeyShortcut]) -> [ShortcutAction] {
        let enabledKeyCodes = Set(hotkeys.filter { isEnabled(id: $0.key) }.map(\.value))
        return ShortcutAction.allCases.filter { action in
            guard let shortcut = shortcuts[action] else { return false }
            return shortcut.modifierFlags == [.command, .shift] && enabledKeyCodes.contains(Int(shortcut.keyCode))
        }
    }

    /// Absent entries mean the system default, which is on.
    private static func isEnabled(id: Int) -> Bool {
        let all = CFPreferencesCopyAppValue("AppleSymbolicHotKeys" as CFString, "com.apple.symbolichotkeys" as CFString) as? [String: Any]
        return ((all?[String(id)] as? [String: Any])?["enabled"] as? Bool) ?? true
    }

    /// System Settings → Keyboard, where 鍵盤快速鍵 → 截圖 lives.
    static func openSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.Keyboard-Settings.extension") { NSWorkspace.shared.open(url) }
    }
}
