import AppKit
import AVFoundation
import SwiftUI

/// Preview card for a finished recording, bottom-right like the screenshot card: first frame and duration;
/// hover for Show in Finder / Copy file / Make GIF; drag it out as the .mp4 file.
@MainActor
final class RecordingPreviewController: NSObject {
    private var panel: NSPanel?
    private var dismissTimer: Timer?
    private let model = RecordingPreviewModel()
    private let cardWidth: CGFloat = 260
    private let margin: CGFloat = 16
    private let autoDismissDelay: TimeInterval = 10

    func show(_ url: URL) {
        close()
        model.reset(url: url)

        let size = NSSize(width: cardWidth, height: 160)
        let visible = NSScreen.underMouse?.visibleFrame ?? CGRect(x: 0, y: 0, width: 800, height: 600)
        let frame = CGRect(x: visible.maxX - size.width - margin, y: visible.minY + margin, width: size.width, height: size.height)
        let panel = NSPanel(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.level = .floating
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.contentView = NSHostingView(rootView: RecordingPreviewView(
            model: model, size: size,
            onReveal: { NSWorkspace.shared.activateFileViewerSelecting([url]) },
            onCopy: { [weak self] in self?.copy(url) },
            onGIF: { [weak self] in self?.makeGIF(url) },
            onClose: { [weak self] in self?.close() },
            onHover: { [weak self] hovering in self?.hoverChanged(hovering) },
            onDragged: { [weak self] in self?.close() }))
        panel.orderFrontRegardless()
        self.panel = panel
        scheduleDismiss()

        Task { @MainActor [model] in
            let asset = AVURLAsset(url: url)
            if let duration = try? await asset.load(.duration).seconds { model.duration = duration }
            let generator = AVAssetImageGenerator(asset: asset)
            generator.appliesPreferredTrackTransform = true
            generator.maximumSize = CGSize(width: 600, height: 600)
            if let image = try? await generator.image(at: .zero).image {
                model.poster = NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height))
            }
        }
    }

    func close() {
        dismissTimer?.invalidate()
        dismissTimer = nil
        panel?.orderOut(nil)
        panel = nil
    }

    /// Puts the file itself on the pasteboard, so it pastes into Finder, Messages, Slack, etc.
    private func copy(_ url: URL) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.writeObjects([url as NSURL])
        model.flash("已複製影片檔")
    }

    private func makeGIF(_ url: URL) {
        guard !model.isConverting else { return }
        model.isConverting = true
        dismissTimer?.invalidate()
        Task { @MainActor [model] in
            do {
                let gif = try await GIFExporter.export(url)
                model.isConverting = false
                model.flash("GIF 已存好")
                NSWorkspace.shared.activateFileViewerSelecting([gif])
            } catch {
                model.isConverting = false
                NSAlert(error: error).runModal()
            }
            scheduleDismiss()
        }
    }

    private func hoverChanged(_ hovering: Bool) {
        withAnimation(.easeOut(duration: 0.12)) { model.isHovering = hovering }
        if hovering { dismissTimer?.invalidate() } else if !model.isConverting { scheduleDismiss() }
    }

    private func scheduleDismiss() {
        guard panel != nil else { return }
        dismissTimer?.invalidate()
        dismissTimer = Timer.scheduledTimer(withTimeInterval: autoDismissDelay, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.close() }
        }
    }
}

private final class RecordingPreviewModel: ObservableObject {
    @Published var url: URL?
    @Published var poster: NSImage?
    @Published var duration: Double = 0
    @Published var isHovering = false
    @Published var isConverting = false
    @Published var feedback: String?

    func reset(url: URL) {
        self.url = url
        poster = nil
        duration = 0
        isHovering = false
        isConverting = false
        feedback = nil
    }

    func flash(_ message: String) {
        withAnimation { feedback = message }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { [weak self] in
            withAnimation { if self?.feedback == message { self?.feedback = nil } }
        }
    }
}

private struct RecordingPreviewView: View {
    @ObservedObject var model: RecordingPreviewModel
    let size: NSSize
    let onReveal: () -> Void
    let onCopy: () -> Void
    let onGIF: () -> Void
    let onClose: () -> Void
    let onHover: (Bool) -> Void
    let onDragged: () -> Void

    private var shape: RoundedRectangle { RoundedRectangle(cornerRadius: 12, style: .continuous) }
    private var durationText: String {
        let total = Int(model.duration.rounded())
        return String(format: "%d:%02d", total / 60, total % 60)
    }

    var body: some View {
        ZStack {
            if let poster = model.poster {
                Image(nsImage: poster).resizable().aspectRatio(contentMode: .fit).frame(width: size.width, height: size.height)
            } else {
                Image(systemName: "film").font(.largeTitle).foregroundStyle(.secondary)
            }

            if let url = model.url {
                FileDragArea(url: url, preview: model.poster, onHover: onHover, onDragged: onDragged)
            }

            VStack {
                Spacer()
                HStack {
                    Label(durationText, systemImage: "video.fill")
                        .font(.system(size: 11, weight: .semibold).monospacedDigit())
                        .padding(.horizontal, 7).padding(.vertical, 3)
                        .background(.black.opacity(0.6), in: Capsule())
                        .foregroundStyle(.white)
                    Spacer()
                }
                .padding(8)
            }
            .allowsHitTesting(false)

            if model.isHovering || model.feedback != nil || model.isConverting {
                Color.black.opacity(0.45).allowsHitTesting(false)
            }

            if model.isConverting {
                VStack(spacing: 8) {
                    ProgressView().controlSize(.small).tint(.white)
                    Text("轉成 GIF 中…").font(.system(size: 13, weight: .semibold)).foregroundStyle(.white)
                }
            } else if let feedback = model.feedback {
                Label(feedback, systemImage: "checkmark.circle.fill").font(.system(size: 14, weight: .semibold)).foregroundStyle(.white)
            } else if model.isHovering {
                HStack(spacing: 8) {
                    CardIconButton(symbol: "folder", help: "在 Finder 顯示", action: onReveal)
                    CardIconButton(symbol: "doc.on.doc", help: "複製影片檔", action: onCopy)
                    CardIconButton(symbol: "photo.stack", help: "轉成 GIF", action: onGIF)
                }
                Button(action: onClose) {
                    Image(systemName: "xmark").font(.system(size: 10, weight: .bold)).foregroundStyle(.white)
                        .frame(width: 22, height: 22).background(.black.opacity(0.55), in: Circle())
                }
                .buttonStyle(.plain)
                .padding(8)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            }
        }
        .frame(width: size.width, height: size.height)
        .background(.regularMaterial)
        .clipShape(shape)
        .overlay(shape.strokeBorder(.white.opacity(0.18), lineWidth: 1))
    }
}

private struct CardIconButton: View {
    let symbol: String
    let help: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(.black)
                .frame(width: 44, height: 36)
                .background(.white.opacity(0.92), in: RoundedRectangle(cornerRadius: 9))
        }
        .buttonStyle(.plain)
        .help(help)
    }
}

/// Hover tracking and file drag for the card (works while the panel is not key).
private struct FileDragArea: NSViewRepresentable {
    let url: URL
    let preview: NSImage?
    let onHover: (Bool) -> Void
    let onDragged: () -> Void

    func makeNSView(context: Context) -> FileDragView { FileDragView() }
    func updateNSView(_ view: FileDragView, context: Context) {
        view.url = url
        view.preview = preview
        view.onHover = onHover
        view.onDragged = onDragged
    }
}

private final class FileDragView: NSView, NSDraggingSource {
    var url: URL?
    var preview: NSImage?
    var onHover: ((Bool) -> Void)?
    var onDragged: (() -> Void)?

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
    }
    override func mouseEntered(with event: NSEvent) { onHover?(true) }
    override func mouseExited(with event: NSEvent) { onHover?(false) }
    override func mouseDown(with event: NSEvent) { if event.clickCount == 2, let url { NSWorkspace.shared.open(url) } }
    override func mouseDragged(with event: NSEvent) {
        guard let url else { return }
        let item = NSDraggingItem(pasteboardWriter: url as NSURL)
        item.setDraggingFrame(bounds, contents: preview ?? NSWorkspace.shared.icon(forFile: url.path))
        beginDraggingSession(with: [item], event: event, source: self)
    }
    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation { .copy }
    func draggingSession(_ session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
        if !operation.isEmpty { onDragged?() }
    }
}
