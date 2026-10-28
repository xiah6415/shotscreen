import Carbon.HIToolbox

enum ShortcutAction: Int, CaseIterable {
    case region = 1, window, fullScreen, delayed, scroll, allInOne, clipboard, record, recognizeText

    var title: String {
        switch self {
        case .recognizeText: "OCR"
        case .region: "框選截圖"
        case .window: "視窗截圖"
        case .fullScreen: "全螢幕截圖"
        case .delayed: "延遲截圖（3 秒）"
        case .scroll: "捲動截圖"
        case .allInOne: "All-in-One"
        case .clipboard: "剪貼簿紀錄"
        case .record: "錄影（錄影中按一下停止）"
        }
    }
}

/// Registers Carbon global hotkeys. `apply` can be called repeatedly; it replaces the previous registrations.
final class ShortcutManager {
    var onAction: ((ShortcutAction) -> Void)?
    private var hotKeys: [EventHotKeyRef] = []
    private var handler: EventHandlerRef?

    /// Returns the actions that could not be registered.
    @discardableResult
    func apply(_ shortcuts: [ShortcutAction: KeyShortcut]) -> Set<ShortcutAction> {
        installHandlerIfNeeded()
        unregisterAll()
        var failed: Set<ShortcutAction> = []
        for (action, shortcut) in shortcuts {
            var ref: EventHotKeyRef?
            let id = EventHotKeyID(signature: OSType(0x53595348), id: UInt32(action.rawValue)) // SYSH
            let status = RegisterEventHotKey(shortcut.keyCode, shortcut.carbonModifiers, id, GetApplicationEventTarget(), 0, &ref)
            if status == noErr, let ref { hotKeys.append(ref) } else { failed.insert(action) }
        }
        return failed
    }

    func unregisterAll() {
        hotKeys.forEach { UnregisterEventHotKey($0) }
        hotKeys = []
    }

    private func installHandlerIfNeeded() {
        guard handler == nil else { return }
        let eventType = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, userData in
            guard let event, let userData else { return noErr }
            var hotKeyID = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil, MemoryLayout<EventHotKeyID>.size, nil, &hotKeyID)
            let manager = Unmanaged<ShortcutManager>.fromOpaque(userData).takeUnretainedValue()
            if let action = ShortcutAction(rawValue: Int(hotKeyID.id)) { manager.onAction?(action) }
            return noErr
        }, 1, [eventType], Unmanaged.passUnretained(self).toOpaque(), &handler)
    }

    deinit {
        unregisterAll()
        if let handler { RemoveEventHandler(handler) }
    }
}
