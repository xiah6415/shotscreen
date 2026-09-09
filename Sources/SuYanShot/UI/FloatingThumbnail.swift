import AppKit
import SwiftUI

@MainActor
final class FloatingThumbnailController: NSObject {
    private var panel: NSPanel?
    private var captured: CapturedImage?
    private let exporter: ImageExporter

    init(exporter: ImageExporter) { self.exporter = exporter }

    func show(_ captured: CapturedImage) {
        panel?.orderOut(nil)
        self.captured = captured
        let view = FloatingThumbnailView(image: NSImage(cgImage: captured.image, size: .zero), onCopy: { [weak self] in self?.copy() }, onSave: { [weak self] in self?.save() }, onDelete: { [weak self] in self?.dismiss() })
        let hostingView = NSHostingView(rootView: view)
        let size = NSSize(width: 250, height: 205)
        let frame = CGRect(x: (NSScreen.main?.visibleFrame.maxX ?? 300) - size.width - 20, y: (NSScreen.main?.visibleFrame.minY ?? 0) + 20, width: size.width, height: size.height)
        let panel = NSPanel(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.level = .floating; panel.isOpaque = false; panel.backgroundColor = .clear; panel.hasShadow = true; panel.contentView = hostingView
        panel.orderFrontRegardless()
        self.panel = panel
    }

    private func copy() { if let image = captured?.image { ClipboardManager.copy(image) } }
    private func save() {
        guard let captured else { return }
        do { _ = try exporter.exportPNG(captured.image, date: captured.capturedAt); dismiss() }
        catch { NSAlert(error: error).runModal() }
    }
    private func dismiss() { panel?.orderOut(nil); panel = nil; captured = nil }
}

private struct FloatingThumbnailView: View {
    let image: NSImage
    let onCopy: () -> Void
    let onSave: () -> Void
    let onDelete: () -> Void
    var body: some View {
        VStack(spacing: 8) {
            DraggableThumbnail(image: image).frame(height: 150).clipShape(RoundedRectangle(cornerRadius: 8))
            HStack { Button("Copy", action: onCopy); Button("Save", action: onSave); Spacer(); Button("Delete", action: onDelete) }
        }.padding(10).background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12)).frame(width: 250, height: 205)
    }
}

private struct DraggableThumbnail: NSViewRepresentable {
    let image: NSImage

    func makeNSView(context: Context) -> DraggableImageView { DraggableImageView(image: image) }
    func updateNSView(_ view: DraggableImageView, context: Context) { view.image = image }
}

private final class DraggableImageView: NSImageView, NSDraggingSource {
    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        imageScaling = .scaleProportionallyUpOrDown
    }
    convenience init(image: NSImage) { self.init(frame: .zero); self.image = image }
    required init?(coder: NSCoder) { nil }

    override func mouseDragged(with event: NSEvent) {
        guard let image else { return }
        let item = NSDraggingItem(pasteboardWriter: image)
        item.setDraggingFrame(bounds, contents: image)
        beginDraggingSession(with: [item], event: event, source: self)
    }

    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation { .copy }
}
