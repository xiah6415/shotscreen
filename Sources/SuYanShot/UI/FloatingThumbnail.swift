import AppKit
import SwiftUI

/// CleanShot-style preview card in the bottom-right corner: hover for Copy / Save / Annotate, double-click to annotate, drag out as a PNG file,
/// and it slides away on its own after a few seconds unless the pointer is over it.
@MainActor
final class FloatingThumbnailController: NSObject {
    private var panel: NSPanel?
    private var captured: CapturedImage?
    private var dragFileURL: URL?
    private var dismissTimer: Timer?
    private let model = ThumbnailModel()
    private let exporter: ImageExporter
    private let onAnnotate: (CapturedImage) -> Void
    private let cardWidth: CGFloat = 260
    private let margin: CGFloat = 16
    private let autoDismissDelay: TimeInterval = 8

    init(exporter: ImageExporter, onAnnotate: @escaping (CapturedImage) -> Void) {
        self.exporter = exporter
        self.onAnnotate = onAnnotate
    }

    func show(_ captured: CapturedImage) {
        dismiss(animated: false)
        self.captured = captured
        model.isHovering = false
        model.feedback = nil

        let image = NSImage(cgImage: captured.image, size: NSSize(width: captured.image.width, height: captured.image.height))
        let aspect = CGFloat(captured.image.height) / CGFloat(max(1, captured.image.width))
        let size = NSSize(width: cardWidth, height: min(220, max(140, cardWidth * aspect)))
        let view = FloatingThumbnailView(
            image: image,
            size: size,
            model: model,
            actions: ThumbnailActions(
                copy: { [weak self] in self?.copy() },
                save: { [weak self] in self?.save() },
                annotate: { [weak self] in self?.annotate() },
                close: { [weak self] in self?.dismiss(animated: true) },
                hover: { [weak self] in self?.hoverChanged($0) },
                dragFile: { [weak self] in self?.temporaryPNG() },
                dragEnded: { [weak self] in self?.dismiss(animated: true) }
            )
        )

        let visible = NSScreen.underMouse?.visibleFrame ?? CGRect(x: 0, y: 0, width: 800, height: 600)
        let target = CGRect(x: visible.maxX - size.width - margin, y: visible.minY + margin, width: size.width, height: size.height)
        let panel = NSPanel(contentRect: target.offsetBy(dx: 40, dy: 0), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.level = .floating
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.contentView = NSHostingView(rootView: view)
        panel.alphaValue = 0
        panel.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.22
            context.timingFunction = CAMediaTimingFunction(name: .easeOut)
            panel.animator().setFrame(target, display: true)
            panel.animator().alphaValue = 1
        }
        self.panel = panel
        scheduleDismiss()
    }

    private func copy() {
        guard let image = captured?.image else { return }
        ClipboardManager.copy(image)
        flash("已複製")
    }

    private func save() {
        guard let captured else { return }
        do {
            _ = try exporter.exportPNG(captured.image, date: captured.capturedAt)
            flash("已存到桌面") { [weak self] in self?.dismiss(animated: true) }
        } catch { NSAlert(error: error).runModal() }
    }

    private func annotate() {
        guard let captured else { return }
        dismiss(animated: true)
        onAnnotate(captured)
    }

    /// Writes the capture to a temporary PNG once, so it can be dragged into Finder, chats, or editors as a file.
    private func temporaryPNG() -> URL? {
        if let dragFileURL { return dragFileURL }
        guard let captured else { return nil }
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("SuYanShot", isDirectory: true)
        dragFileURL = try? exporter.exportPNG(captured.image, to: directory, date: captured.capturedAt)
        return dragFileURL
    }

    private func flash(_ message: String, then: (() -> Void)? = nil) {
        dismissTimer?.invalidate()
        withAnimation(.easeOut(duration: 0.15)) { model.feedback = message }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.9) { [weak self] in
            withAnimation(.easeOut(duration: 0.15)) { self?.model.feedback = nil }
            if let then { then() } else if self?.model.isHovering == false { self?.scheduleDismiss() }
        }
    }

    private func hoverChanged(_ hovering: Bool) {
        withAnimation(.easeOut(duration: 0.12)) { model.isHovering = hovering }
        if hovering { dismissTimer?.invalidate() } else { scheduleDismiss() }
    }

    private func scheduleDismiss() {
        guard panel != nil else { return }
        dismissTimer?.invalidate()
        dismissTimer = Timer.scheduledTimer(withTimeInterval: autoDismissDelay, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.dismiss(animated: true) }
        }
    }

    private func dismiss(animated: Bool) {
        dismissTimer?.invalidate()
        dismissTimer = nil
        captured = nil
        dragFileURL = nil
        guard let panel else { return }
        self.panel = nil
        guard animated else { panel.orderOut(nil); return }
        NSAnimationContext.runAnimationGroup({ context in
            context.duration = 0.18
            panel.animator().setFrame(panel.frame.offsetBy(dx: 40, dy: 0), display: true)
            panel.animator().alphaValue = 0
        }, completionHandler: { panel.orderOut(nil) })
    }
}

private final class ThumbnailModel: ObservableObject {
    @Published var isHovering = false
    @Published var feedback: String?
}

private struct ThumbnailActions {
    let copy: () -> Void
    let save: () -> Void
    let annotate: () -> Void
    let close: () -> Void
    let hover: (Bool) -> Void
    let dragFile: () -> URL?
    let dragEnded: () -> Void
}

private struct FloatingThumbnailView: View {
    let image: NSImage
    let size: NSSize
    @ObservedObject var model: ThumbnailModel
    let actions: ThumbnailActions

    private var shape: RoundedRectangle { RoundedRectangle(cornerRadius: 12, style: .continuous) }

    var body: some View {
        ZStack {
            Image(nsImage: image)
                .resizable()
                .interpolation(.high)
                .aspectRatio(contentMode: .fit)
                .frame(width: size.width, height: size.height)

            DragHoverArea(preview: image, actions: actions)

            if model.isHovering || model.feedback != nil {
                Color.black.opacity(0.4).allowsHitTesting(false)
            }

            if let feedback = model.feedback {
                Label(feedback, systemImage: "checkmark.circle.fill")
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(.white)
                    .transition(.scale(scale: 0.9).combined(with: .opacity))
            } else if model.isHovering {
                VStack(spacing: 8) {
                    PillButton(title: "複製", systemImage: "doc.on.doc", action: actions.copy)
                    PillButton(title: "儲存", systemImage: "square.and.arrow.down", action: actions.save)
                    PillButton(title: "標註", systemImage: "pencil.tip.crop.circle", action: actions.annotate)
                }
                .transition(.opacity)

                Button(action: actions.close) {
                    Image(systemName: "xmark")
                        .font(.system(size: 10, weight: .bold))
                        .foregroundStyle(.white)
                        .frame(width: 22, height: 22)
                        .background(.black.opacity(0.55), in: Circle())
                }
                .buttonStyle(.plain)
                .help("關閉")
                .padding(8)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                .transition(.opacity)
            }
        }
        .frame(width: size.width, height: size.height)
        .background(.regularMaterial)
        .clipShape(shape)
        .overlay(shape.strokeBorder(.white.opacity(0.18), lineWidth: 1))
    }
}

private struct PillButton: View {
    let title: String
    let systemImage: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Label(title, systemImage: systemImage)
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(.black)
                .frame(width: 104, height: 30)
                .background(.white.opacity(0.92), in: Capsule())
        }
        .buttonStyle(.plain)
    }
}

/// Transparent AppKit layer behind the hover controls: tracks hover (even when the panel is not key) and starts file drags.
private struct DragHoverArea: NSViewRepresentable {
    let preview: NSImage
    let actions: ThumbnailActions

    func makeNSView(context: Context) -> DragHoverView { DragHoverView() }
    func updateNSView(_ view: DragHoverView, context: Context) {
        view.preview = preview
        view.actions = actions
    }
}

private final class DragHoverView: NSView, NSDraggingSource {
    var preview: NSImage?
    var actions: ThumbnailActions?

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
    }
    override func mouseEntered(with event: NSEvent) { actions?.hover(true) }
    override func mouseExited(with event: NSEvent) { actions?.hover(false) }
    override func mouseDown(with event: NSEvent) { if event.clickCount == 2 { actions?.annotate() } }
    override func mouseDragged(with event: NSEvent) {
        guard let url = actions?.dragFile() else { return }
        let item = NSDraggingItem(pasteboardWriter: url as NSURL)
        item.setDraggingFrame(bounds, contents: preview)
        beginDraggingSession(with: [item], event: event, source: self)
    }

    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation { .copy }
    func draggingSession(_ session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
        if !operation.isEmpty { actions?.dragEnded() }
    }
}
