import AppKit
import ScreenCaptureKit
import SwiftUI

/// Hover-to-highlight window picker: the window under the pointer is highlighted, click captures it, Esc cancels.
/// With `listed` windows it also shows a dropdown, so covered windows or ones on other desktops can be chosen by name.
@MainActor
final class WindowPicker: NSObject {
    struct Candidate {
        let window: SCWindow
        /// Cocoa global coordinates (bottom-left origin of the primary screen).
        let frame: CGRect
    }

    private var panels: [OverlayPanel] = []
    private var candidates: [Candidate] = []
    private var completion: ((SCWindow?) -> Void)?

    func begin(windows: [SCWindow], listed: [SCWindow] = [], completion: @escaping (SCWindow?) -> Void) {
        finish(nil)
        self.completion = completion
        candidates = Self.frontToBack(windows)
        NSApp.activate(ignoringOtherApps: true)
        panels = NSScreen.screens.map { screen in
            let panel = OverlayPanel(screen: screen)
            panel.contentView = WindowHighlightView(screen: screen, picker: self)
            panel.makeKeyAndOrderFront(nil)
            return panel
        }
        let main = panels.first { $0.screen == NSScreen.underMouse } ?? panels.first
        main?.makeKey()
        if !listed.isEmpty, let view = main?.contentView {
            let bar = NSHostingView(rootView: WindowListBar(windows: listed,
                                                            onPick: { [weak self] window in self?.finish(window) },
                                                            onCancel: { [weak self] in self?.cancel() }))
            let size = bar.fittingSize
            bar.frame = CGRect(x: (view.bounds.width - size.width) / 2, y: view.bounds.height - size.height - 60, width: size.width, height: size.height)
            view.addSubview(bar)
        }
        pointerMoved()
    }

    fileprivate func pointerMoved() {
        let hovered = candidate(at: NSEvent.mouseLocation)
        panels.forEach { ($0.contentView as? WindowHighlightView)?.hovered = hovered }
    }

    fileprivate func pick() { finish(candidate(at: NSEvent.mouseLocation)?.window) }
    fileprivate func cancel() { finish(nil) }

    private func candidate(at point: CGPoint) -> Candidate? { candidates.first { $0.frame.contains(point) } }

    private func finish(_ window: SCWindow?) {
        guard let completion else { return }
        panels.forEach { $0.orderOut(nil) }
        panels = []
        candidates = []
        self.completion = nil
        completion(window)
    }

    /// ScreenCaptureKit does not report z-order, so order its windows by the on-screen CGWindowList (front first).
    private static func frontToBack(_ windows: [SCWindow]) -> [Candidate] {
        let byID = Dictionary(windows.map { ($0.windowID, $0) }, uniquingKeysWith: { first, _ in first })
        let info = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] ?? []
        let primaryHeight = NSScreen.screens.first?.frame.height ?? 0
        let ownPID = ProcessInfo.processInfo.processIdentifier
        return info.compactMap { entry in
            guard let id = entry[kCGWindowNumber as String] as? CGWindowID,
                  let window = byID[id],
                  (entry[kCGWindowLayer as String] as? Int) == 0,
                  window.owningApplication?.processID != ownPID else { return nil }
            let cg = window.frame // top-left origin
            return Candidate(window: window, frame: CGRect(x: cg.minX, y: primaryHeight - cg.maxY, width: cg.width, height: cg.height))
        }
    }
}

private final class WindowHighlightView: NSView {
    private let screen: NSScreen
    private weak var picker: WindowPicker?
    var hovered: WindowPicker.Candidate? { didSet { needsDisplay = true } }

    init(screen: NSScreen, picker: WindowPicker) {
        self.screen = screen
        self.picker = picker
        super.init(frame: CGRect(origin: .zero, size: screen.frame.size))
    }
    required init?(coder: NSCoder) { nil }

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func viewDidMoveToWindow() { window?.makeFirstResponder(self) }
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .cursorUpdate, .activeAlways, .inVisibleRect], owner: self))
    }
    override func cursorUpdate(with event: NSEvent) { NSCursor.pointingHand.set() }

    override func draw(_ dirtyRect: NSRect) {
        guard let hovered else {
            NSColor.black.withAlphaComponent(0.15).setFill()
            bounds.fill()
            return
        }
        let rect = hovered.frame.offsetBy(dx: -screen.frame.minX, dy: -screen.frame.minY)
        guard rect.intersects(bounds) else { return }
        let shape = NSBezierPath(roundedRect: rect, xRadius: 10, yRadius: 10)
        NSColor.controlAccentColor.withAlphaComponent(0.22).setFill()
        shape.fill()
        NSColor.controlAccentColor.setStroke()
        shape.lineWidth = 3
        shape.stroke()

        let name = hovered.window.owningApplication?.applicationName ?? "視窗"
        let label = "\(name)  \(Int(rect.width)) × \(Int(rect.height))"
        OverlayLabel.draw(label, near: CGPoint(x: rect.midX - 60, y: rect.midY), in: rect.intersection(bounds))
    }

    override func mouseMoved(with event: NSEvent) {
        NSCursor.pointingHand.set()
        picker?.pointerMoved()
    }
    override func mouseDown(with event: NSEvent) { picker?.pick() }
    override func rightMouseDown(with event: NSEvent) { picker?.cancel() }
    override func keyDown(with event: NSEvent) { if event.keyCode == 53 { picker?.cancel() } }
}

/// 「或從清單選」: every shareable window grouped by app, including covered ones and those on other desktops.
private struct WindowListBar: View {
    let windows: [SCWindow]
    let onPick: (SCWindow) -> Void
    let onCancel: () -> Void

    private var groups: [(app: String, windows: [SCWindow])] {
        Dictionary(grouping: windows) { $0.owningApplication?.applicationName ?? "其他" }
            .map { ($0.key, $0.value) }
            .sorted { $0.app.localizedStandardCompare($1.app) == .orderedAscending }
    }

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: "macwindow").foregroundStyle(.secondary)
            Text("把游標移到視窗上點一下，或從清單選：").font(.system(size: 13))
            Menu("選擇視窗") {
                ForEach(groups, id: \.app) { group in
                    Section(group.app) {
                        ForEach(group.windows, id: \.windowID) { window in
                            Button(title(of: window)) { onPick(window) }
                        }
                    }
                }
            }
            .fixedSize()
            Button("取消", action: onCancel)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
    }

    private func title(of window: SCWindow) -> String {
        let name = window.title?.isEmpty == false ? window.title! : "（沒有標題）"
        return window.isOnScreen ? name : "\(name)（在其他桌面或被縮小）"
    }
}
