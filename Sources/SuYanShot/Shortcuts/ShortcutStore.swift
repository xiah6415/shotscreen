import AppKit
import Carbon.HIToolbox

/// User-assigned global shortcuts, persisted in UserDefaults. Also owns the "press a new shortcut" recording state,
/// during which global hotkeys are suspended so the current combination can be re-recorded.
@MainActor
final class ShortcutStore: ObservableObject {
    static let defaults: [ShortcutAction: KeyShortcut] = [
        .region: KeyShortcut(keyCode: UInt32(kVK_ANSI_1), modifiers: [.command, .shift], key: "1"),
        .window: KeyShortcut(keyCode: UInt32(kVK_ANSI_2), modifiers: [.command, .shift], key: "2"),
        .fullScreen: KeyShortcut(keyCode: UInt32(kVK_ANSI_3), modifiers: [.command, .shift], key: "3"),
        .delayed: KeyShortcut(keyCode: UInt32(kVK_ANSI_4), modifiers: [.command, .shift], key: "4")
    ]

    @Published private(set) var shortcuts: [ShortcutAction: KeyShortcut]
    @Published private(set) var recordingAction: ShortcutAction?
    /// Actions whose shortcut could not be registered (usually taken by another app).
    @Published var unavailable: Set<ShortcutAction> = []

    /// Called whenever shortcuts or recording state change.
    var onChange: (() -> Void)?

    private let userDefaults: UserDefaults
    private let storageKey = "shortcuts"
    private var keyMonitor: Any?

    init(userDefaults: UserDefaults = .standard) {
        self.userDefaults = userDefaults
        if let data = userDefaults.data(forKey: storageKey),
           let saved = try? JSONDecoder().decode([String: KeyShortcut].self, from: data) {
            // Once saved, a missing action means the user cleared it.
            shortcuts = Dictionary(uniqueKeysWithValues: saved.compactMap { key, value in
                Int(key).flatMap(ShortcutAction.init(rawValue:)).map { ($0, value) }
            })
        } else {
            shortcuts = Self.defaults
        }
    }

    /// Assigns a shortcut; the same combination is removed from any other action.
    func set(_ shortcut: KeyShortcut?, for action: ShortcutAction) {
        if let shortcut {
            for (other, existing) in shortcuts where other != action && existing == shortcut { shortcuts[other] = nil }
        }
        shortcuts[action] = shortcut
        save()
    }

    func resetToDefaults() {
        endRecording()
        shortcuts = Self.defaults
        save()
    }

    func beginRecording(_ action: ShortcutAction) {
        endRecording()
        recordingAction = action
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            self?.record(event)
            return nil
        }
        onChange?()
    }

    func endRecording() {
        guard recordingAction != nil else { return }
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
        keyMonitor = nil
        recordingAction = nil
        onChange?()
    }

    private func record(_ event: NSEvent) {
        guard let action = recordingAction else { return }
        let bareKey = event.modifierFlags.intersection(KeyShortcut.relevantModifiers).isEmpty
        switch Int(event.keyCode) {
        case kVK_Escape:
            endRecording()
        case kVK_Delete where bareKey, kVK_ForwardDelete where bareKey:
            set(nil, for: action)
            endRecording()
        default:
            guard let shortcut = KeyShortcut(event: event) else { NSSound.beep(); return }
            set(shortcut, for: action)
            endRecording()
        }
    }

    private func save() {
        let encoded = Dictionary(uniqueKeysWithValues: shortcuts.map { (String($0.key.rawValue), $0.value) })
        if let data = try? JSONEncoder().encode(encoded) { userDefaults.set(data, forKey: storageKey) }
        onChange?()
    }
}
