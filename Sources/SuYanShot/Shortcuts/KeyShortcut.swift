import AppKit
import Carbon.HIToolbox

/// A user-assignable key combination, stored by hardware key code so it survives keyboard-layout changes.
struct KeyShortcut: Codable, Equatable {
    var keyCode: UInt32
    /// `NSEvent.ModifierFlags` raw value limited to ⌘ ⌥ ⌃ ⇧.
    var modifiers: UInt
    /// Key name for display, e.g. "1", "A", "F5", "Space".
    var key: String

    static let relevantModifiers: NSEvent.ModifierFlags = [.command, .option, .control, .shift]

    init(keyCode: UInt32, modifiers: NSEvent.ModifierFlags, key: String) {
        self.keyCode = keyCode
        self.modifiers = modifiers.intersection(Self.relevantModifiers).rawValue
        self.key = key
    }

    /// Builds a shortcut from a key press. Returns nil unless it uses ⌘, ⌥ or ⌃ (function keys may stand alone).
    init?(event: NSEvent) {
        let code = UInt32(event.keyCode)
        let flags = event.modifierFlags.intersection(Self.relevantModifiers)
        let isFunctionKey = Self.functionKeys[code] != nil
        guard isFunctionKey || !flags.intersection([.command, .option, .control]).isEmpty else { return nil }
        // Ignore Shift when naming the key so ⌘⇧1 shows "1", not "!".
        guard let name = Self.specialKeyNames[code] ?? event.characters(byApplyingModifiers: [])?.uppercased(),
              !name.isEmpty, name.unicodeScalars.allSatisfy({ !CharacterSet.controlCharacters.contains($0) }) else { return nil }
        self.init(keyCode: code, modifiers: flags, key: name)
    }

    var modifierFlags: NSEvent.ModifierFlags { NSEvent.ModifierFlags(rawValue: modifiers) }

    var carbonModifiers: UInt32 {
        var result: UInt32 = 0
        if modifierFlags.contains(.command) { result |= UInt32(cmdKey) }
        if modifierFlags.contains(.option) { result |= UInt32(optionKey) }
        if modifierFlags.contains(.control) { result |= UInt32(controlKey) }
        if modifierFlags.contains(.shift) { result |= UInt32(shiftKey) }
        return result
    }

    var displayString: String {
        var symbols = ""
        if modifierFlags.contains(.control) { symbols += "⌃" }
        if modifierFlags.contains(.option) { symbols += "⌥" }
        if modifierFlags.contains(.shift) { symbols += "⇧" }
        if modifierFlags.contains(.command) { symbols += "⌘" }
        return symbols + key
    }

    /// Character for `NSMenuItem.keyEquivalent`.
    var menuKeyEquivalent: String {
        if let scalar = Self.functionKeys[keyCode].flatMap(UnicodeScalar.init) { return String(Character(scalar)) }
        switch Int(keyCode) {
        case kVK_Space: return " "
        case kVK_Return: return "\r"
        case kVK_Tab: return "\t"
        case kVK_LeftArrow: return String(Character(UnicodeScalar(NSLeftArrowFunctionKey)!))
        case kVK_RightArrow: return String(Character(UnicodeScalar(NSRightArrowFunctionKey)!))
        case kVK_UpArrow: return String(Character(UnicodeScalar(NSUpArrowFunctionKey)!))
        case kVK_DownArrow: return String(Character(UnicodeScalar(NSDownArrowFunctionKey)!))
        default: return key.lowercased()
        }
    }

    private static let functionKeys: [UInt32: Int] = [
        UInt32(kVK_F1): NSF1FunctionKey, UInt32(kVK_F2): NSF2FunctionKey, UInt32(kVK_F3): NSF3FunctionKey,
        UInt32(kVK_F4): NSF4FunctionKey, UInt32(kVK_F5): NSF5FunctionKey, UInt32(kVK_F6): NSF6FunctionKey,
        UInt32(kVK_F7): NSF7FunctionKey, UInt32(kVK_F8): NSF8FunctionKey, UInt32(kVK_F9): NSF9FunctionKey,
        UInt32(kVK_F10): NSF10FunctionKey, UInt32(kVK_F11): NSF11FunctionKey, UInt32(kVK_F12): NSF12FunctionKey
    ]

    private static let specialKeyNames: [UInt32: String] = {
        var names: [UInt32: String] = [
            UInt32(kVK_Space): "Space", UInt32(kVK_Return): "↩", UInt32(kVK_Tab): "⇥",
            UInt32(kVK_LeftArrow): "←", UInt32(kVK_RightArrow): "→", UInt32(kVK_UpArrow): "↑", UInt32(kVK_DownArrow): "↓"
        ]
        for (index, code) in [kVK_F1, kVK_F2, kVK_F3, kVK_F4, kVK_F5, kVK_F6, kVK_F7, kVK_F8, kVK_F9, kVK_F10, kVK_F11, kVK_F12].enumerated() {
            names[UInt32(code)] = "F\(index + 1)"
        }
        return names
    }()
}
