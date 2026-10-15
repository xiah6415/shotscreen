import AppKit
import SwiftUI

/// While a scroll capture runs: a click-through dashed border just outside the region (so it is never captured),
/// a small control panel next to it with progress, auto scroll and Done / Cancel, and a live preview of what is stitched so far.
@MainActor
final class ScrollCaptureHUD {
    private var border: NSPanel?
    private var controls: NSPanel?
    private var preview: NSPanel?
    private let previewWidth: CGFloat = 150
    private let model = ScrollHUDModel()

    /// `region` is in global Cocoa coordinates.
    func show(around region: CGRect, onDone: @escaping () -> Void, onCancel: @escaping () -> Void, onAutoScroll: @escaping (Bool) -> Void) {
        hide()
        model.height = 0
        model.message = nil
        model.preview = nil
        model.auto = .off

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

        let size = NSSize(width: ScrollHUDView.width, height: 52)
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
        controls.contentView = NSHostingView(rootView: ScrollHUDView(model: model, onDone: onDone, onCancel: onCancel, onAutoScroll: onAutoScroll))
        controls.orderFrontRegardless()
        self.controls = controls

        // Preview beside the region: right if it fits, else left, else inside the right edge.
        let previewSize = NSSize(width: previewWidth + 16, height: min(max(region.height, 160), screen.height - 16))
        var previewX = region.maxX + inset + 10
        if previewX + previewSize.width > screen.maxX - 8 { previewX = region.minX - inset - 10 - previewSize.width }
        if previewX < screen.minX + 8 { previewX = region.maxX - previewSize.width - 12 }
        let previewY = min(max(region.maxY - previewSize.height, screen.minY + 8), screen.maxY - previewSize.height - 8)
        let preview = NSPanel(contentRect: CGRect(x: previewX, y: previewY, width: previewSize.width, height: previewSize.height),
                              styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        preview.isOpaque = false
        preview.backgroundColor = .clear
        preview.hasShadow = true
        preview.ignoresMouseEvents = true
        preview.level = .statusBar
        preview.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        preview.contentView = NSHostingView(rootView: ScrollPreviewView(model: model, width: previewWidth))
        preview.orderFrontRegardless()
        self.preview = preview
    }

    func update(_ progress: ScrollCaptureProgress) {
        model.height = progress.height
        if let image = progress.preview {
            model.preview = NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height))
        }
        model.auto = progress.auto
        switch (progress.auto, progress.lastResult) {
        case (_, .limitReached): model.message = "已達長度上限，請按完成"
        case (.checkpoint, _): model.message = "已自動往下捲了一段，要繼續嗎？"
        case (.reachedEnd, _): model.message = "已經到底了，按完成產生長圖"
        case (.running, _): model.message = nil
        case (.off, .rejected): model.message = "沒對上，請慢一點往下捲"
        case (.off, .appended): model.message = nil
        case (.off, _): break
        }
    }

    func hide() {
        border?.orderOut(nil)
        controls?.orderOut(nil)
        preview?.orderOut(nil)
        border = nil
        controls = nil
        preview = nil
    }
}

private final class ScrollHUDModel: ObservableObject {
    @Published var height = 0
    @Published var message: String?
    @Published var preview: NSImage?
    @Published var auto: AutoScrollStatus = .off
}

/// Stitched-so-far thumbnail, pinned to its newest (bottom) part once it is taller than the panel.
private struct ScrollPreviewView: View {
    @ObservedObject var model: ScrollHUDModel
    let width: CGFloat

    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .bottom) {
                if let image = model.preview {
                    Image(nsImage: image)
                        .resizable()
                        .interpolation(.medium)
                        .frame(width: width, height: width * image.size.height / max(1, image.size.width))
                        .frame(width: width, height: geometry.size.height - 16, alignment: .bottom)
                        .clipped()
                        .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                } else {
                    ProgressView().controlSize(.small).frame(maxHeight: .infinity)
                }
            }
            .padding(8)
        }
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}

private struct ScrollHUDView: View {
    static let width: CGFloat = 470
    @ObservedObject var model: ScrollHUDModel
    let onDone: () -> Void
    let onCancel: () -> Void
    let onAutoScroll: (Bool) -> Void

    private var placeholder: String { model.auto == .running ? "自動捲動中…" : "往下捲動要截的內容" }

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "scroll")
                .font(.system(size: 16, weight: .semibold))
                .foregroundStyle(Color.accentColor)
            VStack(alignment: .leading, spacing: 1) {
                Text(model.message ?? placeholder)
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(model.message == nil ? .primary : Color.orange)
                Text(model.height > 0 ? "已拼接 \(model.height) px" : "準備中…")
                    .font(.system(size: 11).monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 4)
            autoScrollButton
            Button("取消", action: onCancel)
            Button("完成", action: onDone).buttonStyle(.borderedProminent)
        }
        .padding(.horizontal, 12)
        .frame(width: Self.width, height: 52)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}

extension ScrollHUDView {
    @ViewBuilder private var autoScrollButton: some View {
        switch model.auto {
        case .off:
            Button { onAutoScroll(true) } label: { Label("自動捲動", systemImage: "play.fill") }
                .help("自動往下捲，每捲一段會停下來問你要不要繼續")
        case .running:
            Button { onAutoScroll(false) } label: { Label("暫停", systemImage: "pause.fill") }
        case .checkpoint:
            Button { onAutoScroll(true) } label: { Label("繼續往下", systemImage: "arrow.down") }
                .buttonStyle(.borderedProminent)
                .tint(.orange)
        case .reachedEnd:
            EmptyView()
        }
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
