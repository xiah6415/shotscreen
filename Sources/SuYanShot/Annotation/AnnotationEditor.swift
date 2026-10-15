import AppKit
import SwiftUI

/// One editor window per capture: toolbar on top, canvas below; Copy / Save export the flattened image.
@MainActor
final class AnnotationEditorController: NSObject, NSWindowDelegate {
    var onClose: ((AnnotationEditorController) -> Void)?

    private let window: NSWindow
    private let state: AnnotationEditorState
    private let canvas: AnnotationCanvasView
    private let capturedAt: Date
    private let exporter: ImageExporter
    private static let toolbarHeight: CGFloat = 48

    init(captured: CapturedImage, exporter: ImageExporter) {
        let pixelScale = NSScreen.underMouse?.backingScaleFactor ?? 2
        state = AnnotationEditorState(pixelScale: pixelScale)
        canvas = AnnotationCanvasView(state: state, source: captured.image)
        capturedAt = captured.capturedAt
        self.exporter = exporter
        window = NSWindow(contentRect: .zero, styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        super.init()

        canvas.onCopy = { [weak self] in self?.copy() }
        canvas.onSave = { [weak self] in self?.save() }
        let hostingView = NSHostingView(rootView: AnnotationEditorView(
            state: state, canvas: canvas,
            onCopy: { [weak self] in self?.copy() },
            onSave: { [weak self] in self?.save() }
        ))
        hostingView.sizingOptions = [] // the window, not SwiftUI, decides the size

        window.title = "標註"
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.contentView = hostingView
        window.minSize = NSSize(width: 860, height: 420)

        // Open at the capture's real size when it fits, otherwise scaled into most of the screen.
        let visible = NSScreen.underMouse?.visibleFrame.size ?? NSSize(width: 1280, height: 800)
        let imageSize = NSSize(width: CGFloat(captured.image.width) / pixelScale, height: CGFloat(captured.image.height) / pixelScale)
        let fit = min(1, visible.width * 0.85 / imageSize.width, (visible.height * 0.85 - Self.toolbarHeight) / imageSize.height)
        window.setContentSize(NSSize(width: max(860, imageSize.width * fit), height: max(420, imageSize.height * fit + Self.toolbarHeight)))
        window.center()
    }

    func show() {
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    func windowWillClose(_ notification: Notification) { onClose?(self) }

    private func rendered() -> CGImage? {
        canvas.commitText()
        canvas.commitCrop()
        return AnnotationRenderer.flatten(canvas.source, effects: canvas.effects, annotations: state.annotations, crop: state.crop)
    }

    private func copy() {
        guard let image = rendered() else { return }
        ClipboardManager.copy(image)
        state.flash("已複製")
    }

    private func save() {
        guard let image = rendered() else { return }
        do {
            _ = try exporter.exportPNG(image, date: capturedAt)
            state.flash("已存到桌面")
        } catch { NSAlert(error: error).runModal() }
    }
}

private struct AnnotationEditorView: View {
    @ObservedObject var state: AnnotationEditorState
    let canvas: AnnotationCanvasView
    let onCopy: () -> Void
    let onSave: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                HStack(spacing: 2) {
                    ForEach(AnnotationTool.allCases) { tool in
                        ToolbarIcon(symbol: tool.symbol, isOn: state.tool == tool, help: "\(tool.title)（\(tool.key.uppercased())）") { state.tool = tool }
                    }
                }
                Divider().frame(height: 22)
                HStack(spacing: 6) {
                    ForEach(AnnotationEditorState.palette.indices, id: \.self) { index in
                        let color = AnnotationEditorState.palette[index]
                        ColorSwatch(color: color, isOn: state.color == color) { state.setColor(color) }
                    }
                }
                Divider().frame(height: 22)
                HStack(spacing: 2) {
                    ForEach(0..<AnnotationEditorState.sizeLevels, id: \.self) { level in
                        Button { state.setSizeLevel(level) } label: {
                            Circle()
                                .frame(width: [5, 8, 12][level], height: [5, 8, 12][level])
                                .frame(width: 26, height: 26)
                                .background(state.sizeLevel == level ? Color.accentColor.opacity(0.18) : .clear, in: RoundedRectangle(cornerRadius: 6))
                                .foregroundStyle(state.sizeLevel == level ? Color.accentColor : .primary)
                                .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .help(["細", "中", "粗"][level])
                    }
                }
                Divider().frame(height: 22)
                ToolbarIcon(symbol: "arrow.uturn.backward", isOn: false, help: "復原（⌘Z）", action: state.undo).disabled(!state.canUndo)
                ToolbarIcon(symbol: "arrow.uturn.forward", isOn: false, help: "重做（⌘⇧Z）", action: state.redo).disabled(!state.canRedo)

                Spacer(minLength: 8)

                if let feedback = state.feedback {
                    Label(feedback, systemImage: "checkmark.circle.fill")
                        .font(.callout.weight(.medium))
                        .foregroundStyle(.green)
                        .transition(.opacity)
                }
                ToolbarIcon(symbol: "doc.on.doc", isOn: false, help: "複製到剪貼簿（⌘C）", action: onCopy)
                ToolbarIcon(symbol: "square.and.arrow.down", isOn: false, help: "存到桌面（⌘S）", action: onSave)
            }
            .padding(.horizontal, 12)
            .frame(height: 48)
            .background(.bar)

            Divider()
            CanvasHost(canvas: canvas)
        }
    }
}

private struct ToolbarIcon: View {
    let symbol: String
    let isOn: Bool
    let help: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 14, weight: .medium))
                .frame(width: 28, height: 28)
                .background(isOn ? Color.accentColor.opacity(0.18) : .clear, in: RoundedRectangle(cornerRadius: 6))
                .foregroundStyle(isOn ? Color.accentColor : .primary)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(help)
    }
}

private struct ColorSwatch: View {
    let color: NSColor
    let isOn: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Circle()
                .fill(Color(nsColor: color))
                .overlay(Circle().strokeBorder(.primary.opacity(0.2), lineWidth: 1))
                .frame(width: 18, height: 18)
                .padding(2)
                .overlay(Circle().strokeBorder(isOn ? Color.accentColor : .clear, lineWidth: 2))
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
    }
}

private struct CanvasHost: NSViewRepresentable {
    let canvas: AnnotationCanvasView
    func makeNSView(context: Context) -> AnnotationCanvasView { canvas }
    func updateNSView(_ view: AnnotationCanvasView, context: Context) {}
}
