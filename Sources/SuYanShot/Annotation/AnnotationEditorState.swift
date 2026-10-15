import AppKit
import SwiftUI

/// Editor document: annotations plus tool settings, with snapshot-based undo.
@MainActor
final class AnnotationEditorState: ObservableObject {
    static let palette: [NSColor] = [.systemRed, .systemOrange, .systemYellow, .systemGreen, .systemBlue, .black, .white]
    static let sizeLevels = 3

    @Published var tool: AnnotationTool = .arrow {
        didSet {
            if tool != .select { selectedID = nil }
            if tool == .crop, oldValue != .crop { toolBeforeCrop = oldValue }
        }
    }
    /// Tool to return to after a crop is applied or cancelled.
    private(set) var toolBeforeCrop: AnnotationTool = .arrow
    @Published private(set) var color: NSColor = .systemRed
    @Published private(set) var sizeLevel = 1
    @Published private(set) var annotations: [Annotation] = []
    /// Applied crop in image pixels (bottom-left origin); nil shows the whole capture.
    @Published private(set) var crop: CGRect?
    @Published var selectedID: UUID?
    @Published private(set) var feedback: String?
    @Published private(set) var canUndo = false
    @Published private(set) var canRedo = false

    /// Image pixels per screen point at capture time, so default sizes look the same on Retina and non-Retina captures.
    let pixelScale: CGFloat
    private struct Snapshot {
        let annotations: [Annotation]
        let crop: CGRect?
    }
    private var undoStack: [Snapshot] = [] { didSet { canUndo = !undoStack.isEmpty } }
    private var redoStack: [Snapshot] = [] { didSet { canRedo = !redoStack.isEmpty } }
    private var snapshot: Snapshot { Snapshot(annotations: annotations, crop: crop) }

    init(pixelScale: CGFloat) { self.pixelScale = pixelScale }

    var lineWidth: CGFloat { [3, 5, 9][sizeLevel] * pixelScale }
    var fontSize: CGFloat { [16, 22, 32][sizeLevel] * pixelScale }
    var selected: Annotation? { annotations.first { $0.id == selectedID } }
    private var selectedIndex: Int? { annotations.firstIndex { $0.id == selectedID } }

    var nextCounter: Int {
        let numbers = annotations.compactMap { annotation -> Int? in
            if case let .counter(number) = annotation.kind { return number }
            return nil
        }
        return (numbers.max() ?? 0) + 1
    }

    func newAnnotation(at point: CGPoint) -> Annotation? {
        let kind: Annotation.Kind
        switch tool {
        case .arrow: kind = .arrow
        case .rectangle: kind = .rectangle
        case .ellipse: kind = .ellipse
        case .line: kind = .line
        case .pen: kind = .pen
        case .highlighter: kind = .highlighter
        case .pixelate: kind = .pixelate
        case .blur: kind = .blur
        case .counter: kind = .counter(nextCounter)
        case .select, .crop, .text: return nil
        }
        let points = (kind == .pen || kind == .highlighter || tool == .counter) ? [point] : [point, point]
        return Annotation(kind: kind, points: points, color: color, lineWidth: lineWidth, fontSize: fontSize)
    }

    func newText(_ text: String, at point: CGPoint) -> Annotation {
        Annotation(kind: .text(text), points: [point], color: color, lineWidth: lineWidth, fontSize: fontSize)
    }

    /// Changing color or size also restyles the selected mark.
    func setColor(_ newColor: NSColor) {
        color = newColor
        guard let index = selectedIndex else { return }
        checkpoint()
        annotations[index].color = newColor
    }

    func setSizeLevel(_ level: Int) {
        sizeLevel = max(0, min(Self.sizeLevels - 1, level))
        guard let index = selectedIndex else { return }
        checkpoint()
        annotations[index].lineWidth = lineWidth
        annotations[index].fontSize = fontSize
    }

    func checkpoint() {
        undoStack.append(snapshot)
        redoStack.removeAll()
    }

    /// nil (or a rect covering the whole image) removes the crop.
    func applyCrop(_ rect: CGRect?, imageSize: CGSize) {
        let full = CGRect(origin: .zero, size: imageSize)
        let clipped = rect.map { $0.integral.intersection(full) }
        let newCrop = (clipped == nil || clipped == full || clipped!.width < 2 || clipped!.height < 2) ? nil : clipped
        guard newCrop != crop else { return }
        checkpoint()
        crop = newCrop
    }

    /// Replaces a text mark's string; an empty string deletes it.
    func updateText(_ id: UUID, to text: String) {
        guard let index = annotations.firstIndex(where: { $0.id == id }),
              case let .text(old) = annotations[index].kind, old != text else { return }
        checkpoint()
        if text.isEmpty {
            annotations.remove(at: index)
            if selectedID == id { selectedID = nil }
        } else {
            annotations[index].kind = .text(text)
        }
    }

    func add(_ annotation: Annotation) {
        checkpoint()
        annotations.append(annotation)
    }

    /// Call `checkpoint()` once before a drag, then `move` for each step.
    func move(_ id: UUID, dx: CGFloat, dy: CGFloat) {
        guard let index = annotations.firstIndex(where: { $0.id == id }) else { return }
        annotations[index].translate(dx: dx, dy: dy)
    }

    func removeSelected() {
        guard let index = selectedIndex else { return }
        checkpoint()
        annotations.remove(at: index)
        selectedID = nil
    }

    func undo() {
        guard let previous = undoStack.popLast() else { return }
        redoStack.append(snapshot)
        restore(previous)
    }

    func redo() {
        guard let next = redoStack.popLast() else { return }
        undoStack.append(snapshot)
        restore(next)
    }

    private func restore(_ snapshot: Snapshot) {
        annotations = snapshot.annotations
        crop = snapshot.crop
        selectedID = nil
    }

    func hit(at point: CGPoint, tolerance: CGFloat) -> Annotation? {
        annotations.reversed().first { AnnotationRenderer.hitTest($0, at: point, tolerance: tolerance) }
    }

    func flash(_ message: String) {
        withAnimation(.easeOut(duration: 0.15)) { feedback = message }
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(1.4))
            guard self?.feedback == message else { return }
            withAnimation(.easeOut(duration: 0.2)) { self?.feedback = nil }
        }
    }
}
