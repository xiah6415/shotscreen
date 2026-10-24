import AppKit
import Carbon.HIToolbox
import SwiftUI

/// Shown after choosing what to record (full screen, a window) and before recording starts: sound controls
/// (shared with Settings) plus 開始錄影 / 取消. Return starts, Esc cancels.
@MainActor
final class RecordingStartBar {
    private var border: NSPanel?
    private var bar: KeyablePanel?
    private var keyMonitor: Any?
    private var onStart: (() -> Void)?
    private var onCancel: (() -> Void)?

    /// `region` is in global Cocoa coordinates; nil means the whole `screen`.
    /// `windowAppName` switches the sound source menu to "this window's app / all sound".
    func show(region: CGRect?, on screen: NSScreen, settings: RecordingSettings, windowAppName: String? = nil,
              onStart: @escaping () -> Void, onCancel: @escaping () -> Void) {
        close()
        self.onStart = onStart
        self.onCancel = onCancel

        if let region {
            let inset: CGFloat = 4
            let border = NSPanel(contentRect: region.insetBy(dx: -inset, dy: -inset), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            border.isOpaque = false
            border.backgroundColor = .clear
            border.ignoresMouseEvents = true
            border.level = .statusBar
            border.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            border.contentView = PendingBorderView()
            border.orderFrontRegardless()
            self.border = border
        }

        let size = NSSize(width: RecordingStartView.width, height: 52)
        let visible = screen.visibleFrame
        var origin: CGPoint
        if let region {
            origin = CGPoint(x: region.midX - size.width / 2, y: region.minY - size.height - 14)
            if origin.y < visible.minY + 8 { origin.y = region.maxY + 14 }
            if origin.y + size.height > visible.maxY - 8 { origin.y = region.minY + 14 }
        } else {
            origin = CGPoint(x: visible.midX - size.width / 2, y: visible.minY + 24)
        }
        origin.x = min(max(visible.minX + 8, origin.x), visible.maxX - size.width - 8)

        let bar = KeyablePanel(contentRect: CGRect(origin: origin, size: size), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        bar.isOpaque = false
        bar.backgroundColor = .clear
        bar.hasShadow = true
        bar.level = .statusBar
        bar.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        bar.contentView = NSHostingView(rootView: RecordingStartView(
            settings: settings,
            windowAppName: windowAppName,
            onStart: { [weak self] in self?.finish(start: true) },
            onCancel: { [weak self] in self?.finish(start: false) }))
        bar.makeKeyAndOrderFront(nil)
        self.bar = bar

        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self, event.window === self.bar else { return event }
            switch Int(event.keyCode) {
            case kVK_Return, kVK_ANSI_KeypadEnter: self.finish(start: true); return nil
            case kVK_Escape: self.finish(start: false); return nil
            default: return event
            }
        }
    }

    func close() {
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
        keyMonitor = nil
        border?.orderOut(nil)
        bar?.orderOut(nil)
        border = nil
        bar = nil
    }

    private func finish(start: Bool) {
        let action = start ? onStart : onCancel
        onStart = nil
        onCancel = nil
        close()
        action?()
    }
}

private struct RecordingStartView: View {
    static let width: CGFloat = 560
    @ObservedObject var settings: RecordingSettings
    let windowAppName: String?
    let onStart: () -> Void
    let onCancel: () -> Void

    var body: some View {
        HStack(spacing: 8) {
            RecordingSoundControls(settings: settings, windowAppName: windowAppName)
            Spacer(minLength: 4)
            Button("取消", action: onCancel)
                .help("Esc")
            Button(action: onStart) { Label("開始錄影", systemImage: "record.circle") }
                .buttonStyle(.borderedProminent)
                .tint(.red)
                .help("Return")
        }
        .padding(.horizontal, 12)
        .frame(width: Self.width, height: 52)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}

/// Dimmer than the recording border so it reads as "not recording yet".
private final class PendingBorderView: NSView {
    override func draw(_ dirtyRect: NSRect) {
        let path = NSBezierPath(roundedRect: bounds.insetBy(dx: 1.5, dy: 1.5), xRadius: 4, yRadius: 4)
        path.lineWidth = 2
        path.setLineDash([8, 5], count: 2, phase: 0)
        NSColor.systemRed.withAlphaComponent(0.5).setStroke()
        path.stroke()
    }
}
