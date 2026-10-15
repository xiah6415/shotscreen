import AppKit
import ApplicationServices

/// Posts synthetic scroll-wheel events for auto scroll capture. Requires Accessibility permission.
enum AutoScroller {
    static var isTrusted: Bool { AXIsProcessTrusted() }

    /// Shows the system Accessibility prompt if the app is not trusted yet.
    @discardableResult
    static func requestTrust() -> Bool {
        AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary)
    }

    /// Scroll events go to the window under the pointer, so park the pointer over the captured region first.
    /// `point` is in global CoreGraphics coordinates (top-left origin of the primary display).
    static func moveCursor(to point: CGPoint) {
        CGWarpMouseCursorPosition(point)
        CGAssociateMouseAndMouseCursorPosition(1)
    }

    /// Scrolls by `points` (negative = toward the end of the document), split into small pixel-unit events
    /// so apps that cap a single event's delta still move the full distance.
    static func scroll(by points: Int, at point: CGPoint) {
        var remaining = points
        while remaining != 0 {
            let chunk = remaining > 0 ? min(remaining, 40) : max(remaining, -40)
            remaining -= chunk
            guard let event = CGEvent(scrollWheelEvent2Source: nil, units: .pixel, wheelCount: 1, wheel1: Int32(chunk), wheel2: 0, wheel3: 0) else { return }
            event.location = point
            event.post(tap: .cghidEventTap)
        }
    }
}
