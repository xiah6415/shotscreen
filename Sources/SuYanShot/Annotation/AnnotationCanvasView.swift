import AppKit
import Carbon.HIToolbox
import Combine

/// Shows the capture scaled to fit and turns mouse input into annotations.
final class AnnotationCanvasView: NSView, NSTextFieldDelegate {
    let source: CGImage
    let effects: AnnotationEffects
    var onCopy: (() -> Void)?
    var onSave: (() -> Void)?

    private let state: AnnotationEditorState
    private var draft: Annotation?
    private var dragAnchor: CGPoint?
    private var didCheckpointDrag = false
    private var cropAnchor: CGPoint?
    private var cropDraft: CGRect?
    private var textField: NSTextField?
    private var editingTextID: UUID?
    private var observation: AnyCancellable?

    init(state: AnnotationEditorState, source: CGImage) {
        self.state = state
        self.source = source
        self.effects = AnnotationEffects(source: source)
        super.init(frame: .zero)
        observation = state.objectWillChange.sink { [weak self] _ in
            DispatchQueue.main.async { self?.stateDidChange() }
        }
    }
    required init?(coder: NSCoder) { nil }

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func viewDidMoveToWindow() { window?.makeFirstResponder(self) }

    private func stateDidChange() {
        if state.tool != .text, state.tool != .select { commitText() }
        if state.tool != .crop { commitCrop() }
        window?.invalidateCursorRects(for: self)
        needsDisplay = true
        // Toolbar clicks must not leave keyboard focus outside the canvas.
        if textField == nil, window?.firstResponder !== self { window?.makeFirstResponder(self) }
    }

    // MARK: - Geometry

    private var imageSize: CGSize { CGSize(width: source.width, height: source.height) }
    private var fullRect: CGRect { CGRect(origin: .zero, size: imageSize) }

    /// The part of the image on screen: everything while cropping, otherwise the applied crop.
    private var visibleRegion: CGRect { state.tool == .crop ? fullRect : (state.crop ?? fullRect) }

    /// View points per image pixel: fit the view, but never larger than the capture's real size.
    private var scale: CGFloat {
        let region = visibleRegion
        let fit = min(bounds.width / region.width, bounds.height / region.height)
        return min(fit, 1 / state.pixelScale)
    }

    /// Where the visible region is drawn, in view coordinates.
    private var imageRect: CGRect {
        let size = CGSize(width: visibleRegion.width * scale, height: visibleRegion.height * scale)
        return CGRect(x: ((bounds.width - size.width) / 2).rounded(), y: ((bounds.height - size.height) / 2).rounded(), width: size.width, height: size.height)
    }

    private func imagePoint(fromView point: CGPoint) -> CGPoint {
        CGPoint(x: visibleRegion.minX + (point.x - imageRect.minX) / scale, y: visibleRegion.minY + (point.y - imageRect.minY) / scale)
    }

    private func imagePoint(_ event: NSEvent) -> CGPoint {
        let point = convert(event.locationInWindow, from: nil)
        let rect = imageRect
        return imagePoint(fromView: CGPoint(x: min(max(point.x, rect.minX), rect.maxX), y: min(max(point.y, rect.minY), rect.maxY)))
    }

    private func viewPoint(_ point: CGPoint) -> CGPoint {
        CGPoint(x: imageRect.minX + (point.x - visibleRegion.minX) * scale, y: imageRect.minY + (point.y - visibleRegion.minY) * scale)
    }

    private func viewRect(_ rect: CGRect) -> CGRect {
        let origin = viewPoint(rect.origin)
        return CGRect(x: origin.x, y: origin.y, width: rect.width * scale, height: rect.height * scale)
    }

    // MARK: - Drawing

    override func draw(_ dirtyRect: NSRect) {
        NSColor.underPageBackgroundColor.setFill()
        bounds.fill()
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        let rect = imageRect

        context.saveGState()
        context.clip(to: rect)
        context.translateBy(x: rect.minX - visibleRegion.minX * scale, y: rect.minY - visibleRegion.minY * scale)
        context.scaleBy(x: scale, y: scale)
        context.interpolationQuality = .high
        context.draw(source, in: fullRect)
        for annotation in state.annotations where annotation.id != editingTextID {
            AnnotationRenderer.draw(annotation, in: context, imageSize: imageSize, effects: effects)
        }
        if let draft { AnnotationRenderer.draw(draft, in: context, imageSize: imageSize, effects: effects) }
        context.restoreGState()

        if state.tool == .crop { drawCropOverlay() }

        if let selected = state.selected, selected.id != editingTextID {
            let outline = NSBezierPath(rect: viewRect(AnnotationRenderer.bounds(of: selected)).insetBy(dx: -4, dy: -4))
            outline.lineWidth = 1
            outline.setLineDash([4, 3], count: 2, phase: 0)
            NSColor.controlAccentColor.setStroke()
            outline.stroke()
        }
    }

    /// Dims everything outside the pending (or current) crop and labels its size.
    private func drawCropOverlay() {
        let box = viewRect(cropDraft ?? state.crop ?? fullRect)
        let shade = NSBezierPath(rect: imageRect)
        shade.append(NSBezierPath(rect: box))
        shade.windingRule = .evenOdd
        NSColor.black.withAlphaComponent(0.5).setFill()
        shade.fill()
        NSColor.white.setStroke()
        let border = NSBezierPath(rect: box.insetBy(dx: -0.5, dy: -0.5))
        border.lineWidth = 1
        border.stroke()
        let pixels = cropDraft ?? state.crop ?? fullRect
        let hint = cropDraft == nil ? "拖曳框出要保留的範圍 · ⌫ 還原原圖" : "Return 套用 · Esc 取消"
        OverlayLabel.draw("\(Int(pixels.width)) × \(Int(pixels.height))  \(hint)", near: CGPoint(x: box.minX, y: box.maxY), in: bounds)
    }

    override func resetCursorRects() {
        let cursor: NSCursor = switch state.tool {
        case .select: .arrow
        case .text: .iBeam
        default: .crosshair
        }
        addCursorRect(imageRect, cursor: cursor)
    }

    override func setFrameSize(_ newSize: NSSize) {
        commitText()
        super.setFrameSize(newSize)
    }

    // MARK: - Mouse

    override func mouseDown(with event: NSEvent) {
        commitText()
        let point = imagePoint(event)
        let tolerance = 6 / scale
        switch state.tool {
        case .select:
            let hit = state.hit(at: point, tolerance: tolerance)
            if event.clickCount == 2, let hit, case .text = hit.kind {
                beginText(at: point, editing: hit)
                return
            }
            state.selectedID = hit?.id
            dragAnchor = hit == nil ? nil : point
            didCheckpointDrag = false
        case .crop:
            cropAnchor = point
            cropDraft = nil
        case .text:
            if let hit = state.hit(at: point, tolerance: tolerance), case .text = hit.kind {
                beginText(at: point, editing: hit)
            } else {
                beginText(at: point)
            }
        case .counter:
            if let counter = state.newAnnotation(at: point) { state.add(counter) }
        default:
            draft = state.newAnnotation(at: point)
        }
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        let point = imagePoint(event)
        let shift = event.modifierFlags.contains(.shift)
        if state.tool == .select, let anchor = dragAnchor, let id = state.selectedID {
            if !didCheckpointDrag { state.checkpoint(); didCheckpointDrag = true }
            state.move(id, dx: point.x - anchor.x, dy: point.y - anchor.y)
            dragAnchor = point
        } else if state.tool == .crop, let anchor = cropAnchor {
            let end = shift ? constrained(from: anchor, to: point, kind: .rectangle) : point
            cropDraft = CGRect(x: min(anchor.x, end.x), y: min(anchor.y, end.y), width: abs(end.x - anchor.x), height: abs(end.y - anchor.y))
                .intersection(fullRect)
            needsDisplay = true
        } else if var current = draft {
            switch current.kind {
            case .pen, .highlighter:
                current.points.append(point)
            default:
                current.points = [current.start, shift ? constrained(from: current.start, to: point, kind: current.kind) : point]
            }
            draft = current
            needsDisplay = true
        }
    }

    override func mouseUp(with event: NSEvent) {
        dragAnchor = nil
        if state.tool == .crop {
            cropAnchor = nil
            if let cropDraft, cropDraft.width * scale < 4 || cropDraft.height * scale < 4 { self.cropDraft = nil }
            needsDisplay = true
            return
        }
        guard let finished = draft else { return }
        draft = nil
        let isStroke = finished.kind == .pen || finished.kind == .highlighter
        let travelled = hypot(finished.end.x - finished.start.x, finished.end.y - finished.start.y) * scale
        if isStroke || travelled >= 3 { state.add(finished) }
        needsDisplay = true
    }

    /// Shift: squares/circles for boxes, 45° steps for lines and arrows.
    private func constrained(from start: CGPoint, to point: CGPoint, kind: Annotation.Kind) -> CGPoint {
        let dx = point.x - start.x, dy = point.y - start.y
        switch kind {
        case .line, .arrow:
            let step = CGFloat.pi / 4
            let angle = (atan2(dy, dx) / step).rounded() * step
            let length = hypot(dx, dy)
            return CGPoint(x: start.x + cos(angle) * length, y: start.y + sin(angle) * length)
        default:
            let side = max(abs(dx), abs(dy))
            return CGPoint(x: start.x + (dx < 0 ? -side : side), y: start.y + (dy < 0 ? -side : side))
        }
    }

    // MARK: - Crop

    /// Applies a pending crop drag, if any.
    func commitCrop() {
        guard let pending = cropDraft else { return }
        cropDraft = nil
        state.applyCrop(pending, imageSize: imageSize)
    }

    private func leaveCropMode(apply: Bool) {
        if apply { commitCrop() } else { cropDraft = nil }
        state.tool = state.toolBeforeCrop
        needsDisplay = true
    }

    // MARK: - Keyboard

    override func keyDown(with event: NSEvent) {
        let flags = event.modifierFlags.intersection([.command, .shift, .option, .control])
        let key = event.charactersIgnoringModifiers?.lowercased() ?? ""
        switch (flags, key) {
        case ([.command], "z"): cropDraft = nil; state.undo(); return
        case ([.command, .shift], "z"): cropDraft = nil; state.redo(); return
        case ([.command], "c"): onCopy?(); return
        case ([.command], "s"): onSave?(); return
        case ([.command], "w"): window?.performClose(nil); return
        default: break
        }
        guard flags.isEmpty || flags == .shift else { super.keyDown(with: event); return }

        if state.tool == .crop {
            switch Int(event.keyCode) {
            case kVK_Return, kVK_ANSI_KeypadEnter: leaveCropMode(apply: true); return
            case kVK_Escape: leaveCropMode(apply: false); return
            case kVK_Delete, kVK_ForwardDelete:
                cropDraft = nil
                state.applyCrop(nil, imageSize: imageSize)
                return
            default: break
            }
        }

        switch Int(event.keyCode) {
        case kVK_Delete, kVK_ForwardDelete:
            state.removeSelected()
        case kVK_Return where state.selected.map({ if case .text = $0.kind { true } else { false } }) == true:
            if let selected = state.selected { beginText(at: selected.start, editing: selected) }
        case kVK_Escape:
            if draft != nil { draft = nil; needsDisplay = true } else { state.selectedID = nil }
        default:
            if let tool = AnnotationTool.allCases.first(where: { $0.key == key }) { state.tool = tool } else { super.keyDown(with: event) }
        }
    }

    // MARK: - Text

    /// Opens an in-place text field for a new text mark at `point`, or over an existing one to edit it.
    private func beginText(at point: CGPoint, editing existing: Annotation? = nil) {
        guard case let .text(currentText) = existing?.kind ?? .text("") else { return }
        let font = NSFont.systemFont(ofSize: (existing?.fontSize ?? state.fontSize) * scale, weight: .bold)
        let height = ceil(font.ascender - font.descender + font.leading) + 4
        let origin: CGPoint
        if let existing {
            let anchor = viewPoint(existing.start)
            origin = CGPoint(x: anchor.x - 2, y: anchor.y - 2)
        } else {
            let anchor = viewPoint(point)
            origin = CGPoint(x: anchor.x - 2, y: anchor.y - height / 2)
        }

        let field = NSTextField(string: currentText)
        field.font = font
        field.textColor = existing?.color ?? state.color
        field.isBordered = false
        field.drawsBackground = false
        field.focusRingType = .none
        field.placeholderString = "輸入文字，按 Return 完成"
        field.cell?.isScrollable = true
        field.cell?.wraps = false
        field.delegate = self
        field.frame = CGRect(x: origin.x, y: origin.y, width: max(160, imageRect.maxX - origin.x), height: height)
        addSubview(field)
        window?.makeFirstResponder(field)
        field.currentEditor()?.selectAll(nil)
        textField = field
        editingTextID = existing?.id
        needsDisplay = true
    }

    func controlTextDidEndEditing(_ notification: Notification) { commitText() }

    /// Turns the in-place text field into a text mark, updates the mark being edited, or drops an empty new one.
    func commitText() {
        guard let field = textField else { return }
        textField = nil
        field.delegate = nil
        let text = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        let anchor = imagePoint(fromView: CGPoint(x: field.frame.minX + 2, y: field.frame.minY + 2))
        let editedID = editingTextID
        editingTextID = nil
        field.removeFromSuperview()
        window?.makeFirstResponder(self)
        if let editedID {
            state.updateText(editedID, to: text)
        } else if !text.isEmpty {
            state.add(state.newText(text, at: anchor))
        }
        needsDisplay = true
    }
}
