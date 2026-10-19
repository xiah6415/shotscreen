import AppKit
import SwiftUI

/// While recording: a red click-through border just outside the region (not drawn for full screen)
/// and a control panel with elapsed time, Pause / Resume, Cancel and Stop. Our own windows are excluded from the video.
@MainActor
final class RecordingHUD {
    private var border: NSPanel?
    private var controls: NSPanel?
    private var timer: Timer?
    private let model = RecordingHUDModel()

    /// `region` is in global Cocoa coordinates; nil records the whole `screen`.
    func show(region: CGRect?, on screen: NSScreen, onPause: @escaping (Bool) -> Void, onStop: @escaping () -> Void, onCancel: @escaping () -> Void) {
        hide()
        model.reset()

        if let region {
            let inset: CGFloat = 4
            let border = NSPanel(contentRect: region.insetBy(dx: -inset, dy: -inset), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            border.isOpaque = false
            border.backgroundColor = .clear
            border.ignoresMouseEvents = true
            border.level = .statusBar
            border.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            border.contentView = RecordingBorderView()
            border.orderFrontRegardless()
            self.border = border
        }

        let size = NSSize(width: RecordingHUDView.width, height: 48)
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

        let controls = NSPanel(contentRect: CGRect(origin: origin, size: size), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        controls.isOpaque = false
        controls.backgroundColor = .clear
        controls.hasShadow = true
        controls.level = .statusBar
        controls.isMovableByWindowBackground = true
        controls.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        controls.contentView = NSHostingView(rootView: RecordingHUDView(
            model: model,
            onPause: { [weak self] in
                guard let self else { return }
                self.model.togglePause()
                onPause(self.model.isPaused)
            },
            onStop: onStop,
            onCancel: onCancel))
        controls.orderFrontRegardless()
        self.controls = controls

        timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.model.tick() }
        }
    }

    func hide() {
        timer?.invalidate()
        timer = nil
        border?.orderOut(nil)
        controls?.orderOut(nil)
        border = nil
        controls = nil
    }
}

private final class RecordingHUDModel: ObservableObject {
    @Published private(set) var elapsed: TimeInterval = 0
    @Published private(set) var isPaused = false
    private var startedAt = Date()
    private var pausedAt: Date?
    private var pausedTotal: TimeInterval = 0

    func reset() {
        startedAt = .now
        pausedAt = nil
        pausedTotal = 0
        isPaused = false
        elapsed = 0
    }

    func togglePause() {
        if let pausedAt {
            pausedTotal += Date.now.timeIntervalSince(pausedAt)
            self.pausedAt = nil
            isPaused = false
        } else {
            pausedAt = .now
            isPaused = true
        }
        tick()
    }

    func tick() {
        let end = pausedAt ?? .now
        elapsed = max(0, end.timeIntervalSince(startedAt) - pausedTotal)
    }
}

private struct RecordingHUDView: View {
    static let width: CGFloat = 330
    @ObservedObject var model: RecordingHUDModel
    let onPause: () -> Void
    let onStop: () -> Void
    let onCancel: () -> Void

    private var time: String {
        let total = Int(model.elapsed)
        return String(format: "%02d:%02d", total / 60, total % 60)
    }

    var body: some View {
        HStack(spacing: 10) {
            Circle()
                .fill(model.isPaused ? Color.orange : Color.red)
                .frame(width: 10, height: 10)
                .opacity(model.isPaused || Int(model.elapsed * 2) % 2 == 0 ? 1 : 0.35)
            Text(model.isPaused ? "已暫停 \(time)" : time)
                .font(.system(size: 14, weight: .semibold).monospacedDigit())
            Spacer(minLength: 4)
            Button(action: onPause) {
                Image(systemName: model.isPaused ? "play.fill" : "pause.fill")
            }
            .help(model.isPaused ? "繼續錄影" : "暫停錄影")
            Button(action: onCancel) { Image(systemName: "trash") }
                .help("取消並丟掉這段錄影")
            Button(action: onStop) { Label("停止", systemImage: "stop.fill") }
                .buttonStyle(.borderedProminent)
                .tint(.red)
                .help("停止並存檔")
        }
        .padding(.horizontal, 14)
        .frame(width: Self.width, height: 48)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}

private final class RecordingBorderView: NSView {
    override func draw(_ dirtyRect: NSRect) {
        let path = NSBezierPath(roundedRect: bounds.insetBy(dx: 1.5, dy: 1.5), xRadius: 4, yRadius: 4)
        path.lineWidth = 2
        path.setLineDash([8, 5], count: 2, phase: 0)
        NSColor.systemRed.setStroke()
        path.stroke()
    }
}
