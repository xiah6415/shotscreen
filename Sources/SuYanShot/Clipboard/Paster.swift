import AppKit
import Carbon.HIToolbox

/// Puts a history entry back on the pasteboard and, when allowed, presses ⌘V in the frontmost app.
@MainActor
enum Paster {
    /// Returns the pasteboard change count of the write, so the monitor can ignore it.
    @discardableResult
    static func write(_ item: ClipboardItem, from store: ClipboardStore, plainText: Bool = false) -> Int {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        switch item.kind {
        case .text:
            pasteboard.setString(item.text ?? "", forType: .string)
        case .richText:
            pasteboard.setString(item.text ?? "", forType: .string)
            if !plainText {
                if item.hasRTF, let rtf = store.payload(item.id, ext: "rtf") { pasteboard.setData(rtf, forType: .rtf) }
                if item.hasHTML, let html = store.payload(item.id, ext: "html") { pasteboard.setData(html, forType: .html) }
            }
        case .image:
            if let png = store.payload(item.id, ext: "png") {
                pasteboard.setData(png, forType: .png)
                // Some apps only read TIFF.
                if let tiff = NSBitmapImageRep(data: png)?.tiffRepresentation { pasteboard.setData(tiff, forType: .tiff) }
            }
        case .file:
            pasteboard.writeObjects((item.fileURLs ?? []) as [NSURL])
        }
        return pasteboard.changeCount
    }

    /// Synthesizes ⌘V. Needs Accessibility permission.
    static func pressPaste() {
        let source = CGEventSource(stateID: .combinedSessionState)
        let keyCode = CGKeyCode(kVK_ANSI_V)
        for keyDown in [true, false] {
            guard let event = CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: keyDown) else { continue }
            event.flags = .maskCommand
            event.post(tap: .cghidEventTap)
        }
    }
}
