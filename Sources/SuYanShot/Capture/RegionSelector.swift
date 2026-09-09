import AppKit
import ScreenCaptureKit

@MainActor
final class RegionSelector: NSObject {
    private var panels: [SelectionPanel] = []
    private var completion: ((Result<RegionSelection, Error>) -> Void)?

    func begin(completion: @escaping (Result<RegionSelection, Error>) -> Void) {
        cancel()
        self.completion = completion
        panels = NSScreen.screens.map { screen in
            let panel = SelectionPanel(screen: screen) { [weak self] result in self?.finish(result) }
            panel.orderFrontRegardless()
            return panel
        }
    }

    func cancel() { finish(.failure(CancellationError())) }

    private func finish(_ result: Result<RegionSelection, Error>) {
        guard let completion else { return }
        panels.forEach { $0.orderOut(nil) }
        panels = []
        self.completion = nil
        completion(result)
    }
}

struct RegionSelection {
    let screen: NSScreen
    let rectInScreenPoints: CGRect

    /// ScreenCaptureKit source rectangles use top-left-origin backing pixels.
    var sourceRectPixels: CGRect {
        let scale = screen.backingScaleFactor
        let screenFrame = screen.frame
        let x = rectInScreenPoints.minX * scale
        let y = (screenFrame.height - rectInScreenPoints.maxY) * scale
        return CGRect(x: x, y: y, width: rectInScreenPoints.width * scale, height: rectInScreenPoints.height * scale).integral
    }
}

private final class SelectionPanel: NSPanel {
    init(screen: NSScreen, complete: @escaping (Result<RegionSelection, Error>) -> Void) {
        super.init(contentRect: screen.frame, styleMask: .borderless, backing: .buffered, defer: false)
        isOpaque = false
        backgroundColor = .clear
        level = .screenSaver
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        hidesOnDeactivate = false
        ignoresMouseEvents = false
        contentView = SelectionView(screen: screen, complete: complete)
        makeKeyAndOrderFront(nil)
    }

    override var canBecomeKey: Bool { true }
}

private final class SelectionView: NSView {
    private let screen: NSScreen
    private let complete: (Result<RegionSelection, Error>) -> Void
    private var startPoint: CGPoint?
    private var selection: CGRect = .zero

    init(screen: NSScreen, complete: @escaping (Result<RegionSelection, Error>) -> Void) {
        self.screen = screen
        self.complete = complete
        super.init(frame: CGRect(origin: .zero, size: screen.frame.size))
    }
    required init?(coder: NSCoder) { nil }

    override var acceptsFirstResponder: Bool { true }
    override func viewDidMoveToWindow() { window?.makeFirstResponder(self) }
    override func draw(_ dirtyRect: NSRect) {
        NSColor.black.withAlphaComponent(0.28).setFill()
        bounds.fill()
        guard !selection.isEmpty else { return }
        NSColor.clear.setFill()
        selection.fill(using: .copy)
        NSColor.systemBlue.setStroke()
        let path = NSBezierPath(rect: selection)
        path.lineWidth = 2
        path.stroke()
        let text = "\(Int(selection.width)) × \(Int(selection.height))"
        let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.monospacedDigitSystemFont(ofSize: 13, weight: .medium), .foregroundColor: NSColor.white]
        let size = text.size(withAttributes: attributes)
        let label = CGRect(x: selection.minX, y: max(4, selection.maxY + 6), width: size.width + 12, height: size.height + 6)
        NSColor.black.withAlphaComponent(0.75).setFill(); NSBezierPath(roundedRect: label, xRadius: 4, yRadius: 4).fill()
        text.draw(at: CGPoint(x: label.minX + 6, y: label.minY + 3), withAttributes: attributes)
    }
    override func mouseDown(with event: NSEvent) { startPoint = convert(event.locationInWindow, from: nil); selection = .zero }
    override func mouseDragged(with event: NSEvent) {
        guard let startPoint else { return }
        selection = CGRect(x: min(startPoint.x, convert(event.locationInWindow, from: nil).x), y: min(startPoint.y, convert(event.locationInWindow, from: nil).y), width: abs(startPoint.x - convert(event.locationInWindow, from: nil).x), height: abs(startPoint.y - convert(event.locationInWindow, from: nil).y))
        needsDisplay = true
    }
    override func mouseUp(with event: NSEvent) {
        guard selection.width >= 2, selection.height >= 2 else { complete(.failure(CaptureError.invalidSelection)); return }
        complete(.success(RegionSelection(screen: screen, rectInScreenPoints: selection)))
    }
    override func keyDown(with event: NSEvent) { if event.keyCode == 53 { complete(.failure(CancellationError())) } }
}
