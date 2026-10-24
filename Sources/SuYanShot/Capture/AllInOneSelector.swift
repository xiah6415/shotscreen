import AppKit
import Carbon.HIToolbox
import Combine
import SwiftUI

enum AllInOneAction {
    case capture(RegionSelection)
    case scroll(RegionSelection)
    case record(RegionSelection)
    case window
    case fullScreen(NSScreen)
    case recordWindow
    case recordFullScreen(NSScreen)
}

enum AllInOneMode { case capture, record }

/// CleanShot-style All-in-One overlay: drag out a region, adjust it (move / 8 handles), then pick an action
/// from the toolbar. Tab switches between 截圖 and 錄影 modes; in 錄影 mode the sound controls sit in the toolbar
/// and nothing records until 錄範圍 / 錄視窗 / 錄全螢幕 is chosen. Return runs the main action, Esc cancels.
@MainActor
final class AllInOneSelector: NSObject {
    private var panels: [OverlayPanel] = []
    private var completion: ((AllInOneAction?) -> Void)?

    func begin(mode: AllInOneMode = .capture, recording: RecordingSettings, completion: @escaping (AllInOneAction?) -> Void) {
        finish(nil)
        self.completion = completion
        NSApp.activate(ignoringOtherApps: true)
        panels = NSScreen.screens.map { screen in
            let panel = OverlayPanel(screen: screen)
            let view = AllInOneView(screen: screen, selector: self, mode: mode, recording: recording)
            view.showsIdleToolbar = screen == NSScreen.underMouse
            panel.contentView = view
            panel.makeKeyAndOrderFront(nil)
            return panel
        }
        (panels.first { $0.screen == NSScreen.underMouse } ?? panels.first)?.makeKey()
    }

    /// A selection lives on one screen at a time; only that screen keeps a toolbar.
    fileprivate func didStartSelection(in view: AllInOneView) {
        for case let other as AllInOneView in panels.map(\.contentView) where other !== view {
            other.clearSelection()
            other.showsIdleToolbar = false
        }
        view.showsIdleToolbar = true
        view.window?.makeKey()
    }

    fileprivate func finish(_ action: AllInOneAction?) {
        guard let completion else { return }
        panels.forEach { $0.orderOut(nil) }
        panels = []
        self.completion = nil
        completion(action)
    }
}

private final class AllInOneToolbarModel: ObservableObject {
    @Published var hasSelection = false
    @Published var mode: AllInOneMode = .capture
}

private final class AllInOneView: NSView {
    private enum Handle: CaseIterable { case bottomLeft, bottom, bottomRight, right, topRight, top, topLeft, left }
    private enum Drag {
        case creating(anchor: CGPoint)
        case moving(grab: CGPoint, original: CGRect)
        case resizing(Handle, original: CGRect)
    }

    private let screen: NSScreen
    private weak var selector: AllInOneSelector?
    private var selection: CGRect? { didSet { selectionDidChange() } }
    /// Whether the toolbar shows before anything is selected (only on one screen).
    var showsIdleToolbar = true { didSet { layoutToolbar() } }
    private var drag: Drag?
    private var pointer: CGPoint?
    private let model = AllInOneToolbarModel()
    private var toolbar: NSHostingView<AllInOneToolbar>!
    private var toolbarSize: NSSize { NSSize(width: AllInOneToolbar.width(for: model.mode), height: 56) }
    private let handleSize: CGFloat = 8
    private var modeObservation: AnyCancellable?

    init(screen: NSScreen, selector: AllInOneSelector, mode: AllInOneMode, recording: RecordingSettings) {
        self.screen = screen
        self.selector = selector
        super.init(frame: CGRect(origin: .zero, size: screen.frame.size))
        model.mode = mode
        toolbar = NSHostingView(rootView: AllInOneToolbar(
            model: model,
            recording: recording,
            onCapture: { [weak self] in self?.perform(.capture) },
            onScroll: { [weak self] in self?.perform(.scroll) },
            onRecord: { [weak self] in self?.perform(.record) },
            onWindow: { [weak self] in self?.selector?.finish(.window) },
            onFullScreen: { [weak self] in self.map { $0.selector?.finish(.fullScreen($0.screen)) } },
            onRecordWindow: { [weak self] in self?.selector?.finish(.recordWindow) },
            onRecordFullScreen: { [weak self] in self.map { $0.selector?.finish(.recordFullScreen($0.screen)) } },
            onCancel: { [weak self] in self?.selector?.finish(nil) }
        ))
        addSubview(toolbar)
        layoutToolbar()
        modeObservation = model.$mode.dropFirst().sink { [weak self] _ in
            DispatchQueue.main.async { self?.layoutToolbar() }
        }
    }
    required init?(coder: NSCoder) { nil }

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func viewDidMoveToWindow() {
        window?.makeFirstResponder(self)
        let mouse = NSEvent.mouseLocation
        if screen.frame.contains(mouse) { pointer = CGPoint(x: mouse.x - screen.frame.minX, y: mouse.y - screen.frame.minY) }
    }
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
    }

    func clearSelection() {
        selection = nil
        drag = nil
    }

    private enum Action { case capture, scroll, record }

    private func perform(_ action: Action) {
        guard let selection, selection.width >= 2, selection.height >= 2 else { return }
        let region = RegionSelection(screen: screen, rectInScreenPoints: selection.integral)
        switch action {
        case .capture: selector?.finish(.capture(region))
        case .scroll: selector?.finish(.scroll(region))
        case .record: selector?.finish(.record(region))
        }
    }

    private func selectionDidChange() {
        model.hasSelection = selection != nil
        layoutToolbar()
        window?.invalidateCursorRects(for: self)
        needsDisplay = true
    }

    /// Under the selection if there is room, else above, else inside its bottom edge; bottom-center before any selection.
    private func layoutToolbar() {
        let size = toolbarSize
        var origin: CGPoint
        if let selection, drag == nil || !isCreating {
            origin = CGPoint(x: selection.midX - size.width / 2, y: selection.minY - size.height - 12)
            if origin.y < 8 { origin.y = selection.maxY + 12 }
            if origin.y + size.height > bounds.maxY - 8 { origin.y = selection.minY + 12 }
        } else {
            origin = CGPoint(x: bounds.midX - size.width / 2, y: 80)
        }
        origin.x = min(max(8, origin.x), bounds.maxX - size.width - 8)
        toolbar.frame = CGRect(origin: origin, size: size)
        toolbar.isHidden = isCreating || (selection == nil && !showsIdleToolbar)
    }

    private var isCreating: Bool { if case .creating = drag { true } else { false } }

    // MARK: - Drawing

    override func draw(_ dirtyRect: NSRect) {
        NSColor.black.withAlphaComponent(0.3).setFill()
        bounds.fill()
        guard let selection, !selection.isEmpty else {
            drawCrosshair()
            return
        }
        NSColor.clear.setFill()
        selection.fill(using: .copy)
        NSColor.white.setStroke()
        let border = NSBezierPath(rect: selection.insetBy(dx: -0.5, dy: -0.5))
        border.lineWidth = 1
        border.stroke()
        if !isCreating {
            for handle in Handle.allCases {
                let rect = handleRect(handle, in: selection)
                let shape = NSBezierPath(roundedRect: rect, xRadius: 2, yRadius: 2)
                NSColor.white.setFill()
                shape.fill()
                NSColor.black.withAlphaComponent(0.35).setStroke()
                shape.lineWidth = 0.5
                shape.stroke()
            }
        }
        OverlayLabel.draw("\(Int(selection.width)) × \(Int(selection.height))", near: CGPoint(x: selection.minX, y: selection.maxY), in: bounds)
    }

    private func drawCrosshair() {
        guard let pointer, !toolbar.frame.contains(pointer) else { return }
        NSColor.white.withAlphaComponent(0.55).setStroke()
        let lines = NSBezierPath()
        lines.lineWidth = 1
        lines.move(to: CGPoint(x: pointer.x.rounded() + 0.5, y: 0)); lines.line(to: CGPoint(x: pointer.x.rounded() + 0.5, y: bounds.height))
        lines.move(to: CGPoint(x: 0, y: pointer.y.rounded() + 0.5)); lines.line(to: CGPoint(x: bounds.width, y: pointer.y.rounded() + 0.5))
        lines.stroke()
        OverlayLabel.draw("拖曳選取範圍 · Esc 取消", near: CGPoint(x: pointer.x + 12, y: pointer.y), in: bounds, above: false)
    }

    // MARK: - Hit testing

    private func handlePoint(_ handle: Handle, in rect: CGRect) -> CGPoint {
        switch handle {
        case .bottomLeft: CGPoint(x: rect.minX, y: rect.minY)
        case .bottom: CGPoint(x: rect.midX, y: rect.minY)
        case .bottomRight: CGPoint(x: rect.maxX, y: rect.minY)
        case .right: CGPoint(x: rect.maxX, y: rect.midY)
        case .topRight: CGPoint(x: rect.maxX, y: rect.maxY)
        case .top: CGPoint(x: rect.midX, y: rect.maxY)
        case .topLeft: CGPoint(x: rect.minX, y: rect.maxY)
        case .left: CGPoint(x: rect.minX, y: rect.midY)
        }
    }

    private func handleRect(_ handle: Handle, in rect: CGRect) -> CGRect {
        let p = handlePoint(handle, in: rect)
        return CGRect(x: p.x - handleSize / 2, y: p.y - handleSize / 2, width: handleSize, height: handleSize)
    }

    private func handle(at point: CGPoint) -> Handle? {
        guard let selection else { return nil }
        return Handle.allCases.first { handleRect($0, in: selection).insetBy(dx: -5, dy: -5).contains(point) }
    }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: .crosshair)
        guard let selection, !isCreating else { return }
        addCursorRect(selection.insetBy(dx: 6, dy: 6), cursor: .openHand)
        for handle in Handle.allCases {
            let cursor: NSCursor = switch handle {
            case .left, .right: .resizeLeftRight
            case .top, .bottom: .resizeUpDown
            default: .crosshair
            }
            addCursorRect(handleRect(handle, in: selection).insetBy(dx: -5, dy: -5), cursor: cursor)
        }
        addCursorRect(toolbar.frame, cursor: .arrow)
    }

    // MARK: - Mouse

    private func clamp(_ point: CGPoint) -> CGPoint {
        CGPoint(x: min(max(point.x, 0), bounds.width), y: min(max(point.y, 0), bounds.height))
    }

    override func mouseMoved(with event: NSEvent) {
        pointer = convert(event.locationInWindow, from: nil)
        if selection == nil { needsDisplay = true }
    }
    override func mouseExited(with event: NSEvent) { pointer = nil; needsDisplay = true }

    override func mouseDown(with event: NSEvent) {
        let point = clamp(convert(event.locationInWindow, from: nil))
        if let selection, event.clickCount == 2, selection.contains(point) { perform(model.mode == .capture ? .capture : .record); return }
        if let selection, let handle = handle(at: point) {
            drag = .resizing(handle, original: selection)
        } else if let selection, selection.contains(point) {
            drag = .moving(grab: point, original: selection)
            NSCursor.closedHand.set()
        } else {
            selector?.didStartSelection(in: self)
            drag = .creating(anchor: point)
            selection = nil
        }
    }

    override func mouseDragged(with event: NSEvent) {
        let point = clamp(convert(event.locationInWindow, from: nil))
        switch drag {
        case let .creating(anchor):
            selection = CGRect(x: min(anchor.x, point.x), y: min(anchor.y, point.y), width: abs(point.x - anchor.x), height: abs(point.y - anchor.y))
        case let .moving(grab, original):
            var moved = original.offsetBy(dx: point.x - grab.x, dy: point.y - grab.y)
            moved.origin.x = min(max(0, moved.minX), bounds.width - moved.width)
            moved.origin.y = min(max(0, moved.minY), bounds.height - moved.height)
            selection = moved
        case let .resizing(handle, original):
            selection = resized(original, handle: handle, to: point)
        case nil:
            break
        }
    }

    override func mouseUp(with event: NSEvent) {
        let wasCreating = isCreating
        drag = nil
        if wasCreating, let selection, selection.width < 4 || selection.height < 4 { self.selection = nil }
        selectionDidChange()
    }

    /// Moves the dragged edges to `point`; dragging past the opposite edge flips the box.
    private func resized(_ rect: CGRect, handle: Handle, to point: CGPoint) -> CGRect {
        var minX = rect.minX, maxX = rect.maxX, minY = rect.minY, maxY = rect.maxY
        switch handle {
        case .left, .topLeft, .bottomLeft: minX = point.x
        case .right, .topRight, .bottomRight: maxX = point.x
        default: break
        }
        switch handle {
        case .bottom, .bottomLeft, .bottomRight: minY = point.y
        case .top, .topLeft, .topRight: maxY = point.y
        default: break
        }
        return CGRect(x: min(minX, maxX), y: min(minY, maxY), width: abs(maxX - minX), height: abs(maxY - minY))
    }

    override func rightMouseDown(with event: NSEvent) { selector?.finish(nil) }

    override func keyDown(with event: NSEvent) {
        switch Int(event.keyCode) {
        case kVK_Escape: selector?.finish(nil)
        case kVK_Tab: model.mode = model.mode == .capture ? .record : .capture
        case kVK_Return, kVK_ANSI_KeypadEnter: perform(model.mode == .capture ? .capture : .record)
        case kVK_ANSI_S where model.mode == .capture: perform(.scroll)
        case kVK_ANSI_R:
            // From 截圖 mode, R first shows the recording options instead of starting straight away.
            if model.mode == .capture { model.mode = .record } else { perform(.record) }
        default: super.keyDown(with: event)
        }
    }
}

private struct AllInOneToolbar: View {
    @ObservedObject var model: AllInOneToolbarModel
    @ObservedObject var recording: RecordingSettings
    let onCapture: () -> Void
    let onScroll: () -> Void
    let onRecord: () -> Void
    let onWindow: () -> Void
    let onFullScreen: () -> Void
    let onRecordWindow: () -> Void
    let onRecordFullScreen: () -> Void
    let onCancel: () -> Void

    static func width(for mode: AllInOneMode) -> CGFloat { mode == .capture ? 520 : 800 }

    var body: some View {
        HStack(spacing: 2) {
            Picker("", selection: $model.mode) {
                Text("截圖").tag(AllInOneMode.capture)
                Text("錄影").tag(AllInOneMode.record)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(width: 110)
            .padding(.trailing, 6)
            .help("Tab 切換截圖 / 錄影")

            if model.mode == .capture {
                ToolbarAction(title: "截圖", symbol: "camera.viewfinder", help: "截取選取範圍（Return）", action: onCapture)
                    .disabled(!model.hasSelection)
                ToolbarAction(title: "捲動截圖", symbol: "scroll", help: "開始捲動截圖（S）", action: onScroll)
                    .disabled(!model.hasSelection)
                Divider().frame(height: 30).padding(.horizontal, 4)
                ToolbarAction(title: "視窗", symbol: "macwindow", help: "改選一個視窗", action: onWindow)
                ToolbarAction(title: "全螢幕", symbol: "display", help: "截取這個螢幕", action: onFullScreen)
            } else {
                ToolbarAction(title: "錄範圍", symbol: "record.circle", help: "開始錄選取範圍（Return）", action: onRecord)
                    .disabled(!model.hasSelection)
                ToolbarAction(title: "錄視窗", symbol: "macwindow", help: "選一個視窗來錄", action: onRecordWindow)
                ToolbarAction(title: "錄全螢幕", symbol: "display", help: "錄這個螢幕", action: onRecordFullScreen)
                Divider().frame(height: 30).padding(.horizontal, 4)
                RecordingSoundControls(settings: recording)
                    .padding(.horizontal, 4)
            }
            Divider().frame(height: 30).padding(.horizontal, 4)
            ToolbarAction(title: "取消", symbol: "xmark", help: "取消（Esc）", action: onCancel)
        }
        .padding(.horizontal, 8)
        .frame(width: Self.width(for: model.mode), height: 56)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
    }
}

private struct ToolbarAction: View {
    let title: String
    let symbol: String
    let help: String
    let action: () -> Void
    @Environment(\.isEnabled) private var isEnabled

    var body: some View {
        Button(action: action) {
            VStack(spacing: 3) {
                Image(systemName: symbol).font(.system(size: 16, weight: .medium))
                Text(title).font(.system(size: 10, weight: .medium))
            }
            .frame(width: 66, height: 46)
            .contentShape(RoundedRectangle(cornerRadius: 8))
            .opacity(isEnabled ? 1 : 0.35)
        }
        .buttonStyle(.plain)
        .help(help)
    }
}
