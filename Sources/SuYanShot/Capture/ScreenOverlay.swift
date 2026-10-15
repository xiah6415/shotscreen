import AppKit

/// Borderless, key-capable panel covering one screen above everything else. Shared by the selection overlays.
final class OverlayPanel: NSPanel {
    init(screen: NSScreen) {
        super.init(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isOpaque = false
        backgroundColor = .clear
        level = .screenSaver
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        hidesOnDeactivate = false
        acceptsMouseMovedEvents = true
    }

    override var canBecomeKey: Bool { true }
}

extension NSScreen {
    var displayID: CGDirectDisplayID? {
        (deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value
    }

    /// The screen the pointer is on, falling back to the main screen.
    static var underMouse: NSScreen? {
        screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? main
    }
}

/// Rounded pill label drawn on top of overlays (dimensions, window names).
enum OverlayLabel {
    static func draw(_ text: String, near anchor: CGPoint, in bounds: CGRect, above: Bool = true) {
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .semibold),
            .foregroundColor: NSColor.white
        ]
        let size = text.size(withAttributes: attributes)
        var rect = CGRect(x: anchor.x, y: above ? anchor.y + 8 : anchor.y - size.height - 14, width: size.width + 16, height: size.height + 6)
        rect.origin.x = min(max(bounds.minX + 4, rect.minX), bounds.maxX - rect.width - 4)
        rect.origin.y = min(max(bounds.minY + 4, rect.minY), bounds.maxY - rect.height - 4)
        NSColor.black.withAlphaComponent(0.72).setFill()
        NSBezierPath(roundedRect: rect, xRadius: rect.height / 2, yRadius: rect.height / 2).fill()
        text.draw(at: CGPoint(x: rect.minX + 8, y: rect.minY + 3), withAttributes: attributes)
    }
}
