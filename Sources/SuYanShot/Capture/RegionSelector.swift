import AppKit
import ScreenCaptureKit

@MainActor
final class RegionSelector: NSObject {
    private var panels: [OverlayPanel] = []
    private var completion: ((Result<RegionSelection, Error>) -> Void)?

    func begin(completion: @escaping (Result<RegionSelection, Error>) -> Void) {
        cancel()
        self.completion = completion
        NSApp.activate(ignoringOtherApps: true)
        panels = NSScreen.screens.map { screen in
            let panel = OverlayPanel(screen: screen)
            panel.contentView = SelectionView(screen: screen) { [weak self] result in self?.finish(result) }
            panel.makeKeyAndOrderFront(nil)
            return panel
        }
        (panels.first { $0.screen == NSScreen.underMouse } ?? panels.first)?.makeKey()
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

private final class SelectionView: NSView {
    private let screen: NSScreen
    private let complete: (Result<RegionSelection, Error>) -> Void
    private var startPoint: CGPoint?
    private var pointer: CGPoint?
    private var selection: CGRect = .zero

    init(screen: NSScreen, complete: @escaping (Result<RegionSelection, Error>) -> Void) {
        self.screen = screen
        self.complete = complete
        super.init(frame: CGRect(origin: .zero, size: screen.frame.size))
    }
    required init?(coder: NSCoder) { nil }

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func viewDidMoveToWindow() {
        window?.makeFirstResponder(self)
        let local = NSEvent.mouseLocation
        if screen.frame.contains(local) { pointer = CGPoint(x: local.x - screen.frame.minX, y: local.y - screen.frame.minY) }
    }
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .cursorUpdate, .activeAlways, .inVisibleRect], owner: self))
    }
    override func cursorUpdate(with event: NSEvent) { NSCursor.crosshair.set() }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.black.withAlphaComponent(0.3).setFill()
        bounds.fill()
        if selection.isEmpty {
            drawCrosshair()
            return
        }
        NSColor.clear.setFill()
        selection.fill(using: .copy)
        NSColor.white.setStroke()
        let path = NSBezierPath(rect: selection.insetBy(dx: -0.5, dy: -0.5))
        path.lineWidth = 1
        path.stroke()
        OverlayLabel.draw("\(Int(selection.width)) × \(Int(selection.height))", near: CGPoint(x: selection.minX, y: selection.maxY), in: bounds)
    }

    /// Full-height and full-width guide lines through the pointer before a drag starts.
    private func drawCrosshair() {
        guard let pointer else { return }
        NSColor.white.withAlphaComponent(0.55).setStroke()
        let lines = NSBezierPath()
        lines.lineWidth = 1
        lines.move(to: CGPoint(x: pointer.x.rounded() + 0.5, y: 0)); lines.line(to: CGPoint(x: pointer.x.rounded() + 0.5, y: bounds.height))
        lines.move(to: CGPoint(x: 0, y: pointer.y.rounded() + 0.5)); lines.line(to: CGPoint(x: bounds.width, y: pointer.y.rounded() + 0.5))
        lines.stroke()
        OverlayLabel.draw("拖曳框選 · Esc 取消", near: CGPoint(x: pointer.x + 12, y: pointer.y), in: bounds, above: false)
    }

    override func mouseMoved(with event: NSEvent) {
        NSCursor.crosshair.set()
        pointer = convert(event.locationInWindow, from: nil)
        needsDisplay = true
    }
    override func mouseExited(with event: NSEvent) { pointer = nil; needsDisplay = true }
    override func mouseDown(with event: NSEvent) { startPoint = convert(event.locationInWindow, from: nil); selection = .zero }
    override func mouseDragged(with event: NSEvent) {
        guard let startPoint else { return }
        let point = convert(event.locationInWindow, from: nil)
        selection = CGRect(x: min(startPoint.x, point.x), y: min(startPoint.y, point.y), width: abs(startPoint.x - point.x), height: abs(startPoint.y - point.y))
        needsDisplay = true
    }
    override func mouseUp(with event: NSEvent) {
        guard selection.width >= 2, selection.height >= 2 else { complete(.failure(CaptureError.invalidSelection)); return }
        complete(.success(RegionSelection(screen: screen, rectInScreenPoints: selection)))
    }
    override func rightMouseDown(with event: NSEvent) { complete(.failure(CancellationError())) }
    override func keyDown(with event: NSEvent) { if event.keyCode == 53 { complete(.failure(CancellationError())) } }
}
