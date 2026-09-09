import Carbon.HIToolbox

enum ShortcutAction: Int {
    case region = 1, window, fullScreen, delayed
}

final class ShortcutManager {
    var onAction: ((ShortcutAction) -> Void)?
    private var hotKeys: [EventHotKeyRef?] = []
    private var handler: EventHandlerRef?

    func registerDefaults() {
        let eventType = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, userData in
            guard let event, let userData else { return noErr }
            var hotKeyID = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil, MemoryLayout<EventHotKeyID>.size, nil, &hotKeyID)
            let manager = Unmanaged<ShortcutManager>.fromOpaque(userData).takeUnretainedValue()
            if let action = ShortcutAction(rawValue: Int(hotKeyID.id)) { manager.onAction?(action) }
            return noErr
        }, 1, [eventType], Unmanaged.passUnretained(self).toOpaque(), &handler)

        register(.region, keyCode: 18)     // ⌘⇧1
        register(.window, keyCode: 19)     // ⌘⇧2
        register(.fullScreen, keyCode: 20) // ⌘⇧3
        register(.delayed, keyCode: 21)    // ⌘⇧4
    }

    private func register(_ action: ShortcutAction, keyCode: UInt32) {
        var ref: EventHotKeyRef?
        let id = EventHotKeyID(signature: OSType(0x53595348), id: UInt32(action.rawValue)) // SYSH
        let status = RegisterEventHotKey(keyCode, UInt32(cmdKey | shiftKey), id, GetApplicationEventTarget(), 0, &ref)
        if status == noErr { hotKeys.append(ref) }
    }

    deinit {
        hotKeys.forEach { hotKey in if let hotKey { UnregisterEventHotKey(hotKey) } }
        if let handler { RemoveEventHandler(handler) }
    }
}
