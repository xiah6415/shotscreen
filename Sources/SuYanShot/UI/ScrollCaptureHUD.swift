import AppKit
import SwiftUI

/// While a scroll capture runs: a click-through dashed border just outside the region (so it is never captured)
/// and a small control panel next to it with progress and Done / Cancel.
@MainActor
final class ScrollCaptureHUD {
    private var border: NSPanel?
    private var controls: NSPanel?
    private let model = ScrollHUDModel()

    /// `region` is in global Cocoa coordinates.
    func show(around region: CGRect, onDone: @escaping () -> Void, onCancel: @escaping () -> Void) {
        hide()
        model.height = 0
        model.message = nil

        let inset: CGFloat = 4
        let border = NSPanel(contentRect: region.insetBy(dx: -inset, dy: -inset), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        border.isOpaque = false
        border.backgroundColor = .clear
        border.ignoresMouseEvents = true // scrolling must reach the app underneath
        border.level = .statusBar
        border.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        border.contentView = DashedBorderView()
        border.orderFrontRegardless()
        self.border = border

        let size = NSSize(width: 360, height: 52)
        let screen = NSScreen.screens.first { $0.frame.intersects(region) }?.visibleFrame ?? region
        let x = min(max(region.midX - size.width / 2, screen.minX + 8), screen.maxX - size.width - 8)
        // Below the region if there is room, otherwise above, otherwise inside along the bottom edge.
        let y: CGFloat = if region.minY - inset - size.height - 10 >= screen.minY {
            region.minY - inset - size.height - 10
        } else if region.maxY + inset + size.height + 10 <= screen.maxY {
            region.maxY + inset + 10
        } else {
            region.minY + 12
        }
        let controls = NSPanel(contentRect: CGRect(x: x, y: y, width: size.width, height: size.height), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        controls.isOpaque = false
        controls.backgroundColor = .clear
        controls.hasShadow = true
        controls.level = .statusBar
        controls.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        controls.contentView = NSHostingView(rootView: ScrollHUDView(model: model, onDone: onDone, onCancel: onCancel))
        controls.orderFrontRegardless()
        self.controls = controls
    }

    func update(_ progress: ScrollCaptureProgress) {
        model.height = progress.height
        switch progress.lastResult {
        case .rejected: model.message = "沒對上，請慢一點往下捲"
        case .limitReached: model.message = "已達長度上限，請按完成"
        case .appended: model.message = nil
        case .first, .unchanged: break
        }
    }

    func hide() {
        border?.orderOut(nil)
        controls?.orderOut(nil)
        border = nil
        controls = nil
    }
}

private final class ScrollHUDModel: ObservableObject {
    @Published var height = 0
    @Published var message: String?
}

private struct ScrollHUDView: View {
    @ObservedObject var model: ScrollHUDModel
    let onDone: () -> Void
    let onCancel: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "scroll")
                .font(.system(size: 16, weight: .semibold))
                .foregroundStyle(Color.accentColor)
            VStack(alignment: .leading, spacing: 1) {
                Text(model.message ?? "往下捲動要截的內容")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(model.message == nil ? .primary : Color.orange)
                Text(model.height > 0 ? "已拼接 \(model.height) px" : "準備中…")
                    .font(.system(size: 11).monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 4)
            Button("取消", action: onCancel)
            Button("完成", action: onDone).buttonStyle(.borderedProminent)
        }
        .padding(.horizontal, 12)
        .frame(width: 360, height: 52)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}

private final class DashedBorderView: NSView {
    override func draw(_ dirtyRect: NSRect) {
        let path = NSBezierPath(roundedRect: bounds.insetBy(dx: 1.5, dy: 1.5), xRadius: 4, yRadius: 4)
        path.lineWidth = 2
        path.setLineDash([8, 5], count: 2, phase: 0)
        NSColor.controlAccentColor.setStroke()
        path.stroke()
    }
}
