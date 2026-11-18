import CoreGraphics
import Foundation
import Vision

/// One recognized line: its text and where it is, in image pixels with a bottom-left origin
/// (the annotation editor's coordinates).
struct RecognizedLine: Equatable {
    let text: String
    let box: CGRect
}

/// Vision runs on this actor's executor so recognition never blocks the selection UI.
actor TextRecognitionService {
    func recognize(_ image: CGImage) throws -> String {
        OCRTextSelection.join(try recognizeLines(image).map(\.text))
    }

    /// Lines in Vision's reading order.
    func recognizeLines(_ image: CGImage) throws -> [RecognizedLine] {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true
        let supported = try request.supportedRecognitionLanguages()
        request.recognitionLanguages = ["zh-Hant", "en-US"].filter { supported.contains($0) }
        let handler = VNImageRequestHandler(cgImage: image, orientation: .up)
        try handler.perform([request])
        let width = CGFloat(image.width), height = CGFloat(image.height)
        return (request.results ?? []).compactMap { observation in
            guard let text = observation.topCandidates(1).first?.string.trimmingCharacters(in: .whitespaces), !text.isEmpty else { return nil }
            let box = observation.boundingBox // normalized, bottom-left origin
            return RecognizedLine(text: text, box: CGRect(x: box.minX * width, y: box.minY * height,
                                                          width: box.width * width, height: box.height * height))
        }
    }
}

/// Which recognized lines are selected in the annotation editor's 辨識文字 tool. Pure logic, shared rules with Windows:
/// a click picks the line under it (shift toggles), a drag selects every line the drag rectangle touches
/// (shift adds to what was selected), copying joins the selected lines in reading order.
struct OCRTextSelection: Equatable {
    private(set) var lines: [RecognizedLine]
    private(set) var selected: Set<Int> = []
    private var base: Set<Int> = []

    init(lines: [RecognizedLine]) { self.lines = lines }

    static func join(_ texts: [String]) -> String {
        texts.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// The line under a point, allowing a little slack around short lines.
    func line(at point: CGPoint, tolerance: CGFloat = 0) -> Int? {
        lines.indices.first { lines[$0].box.insetBy(dx: -tolerance, dy: -tolerance).contains(point) }
    }

    /// Mouse down: remembers what stays selected during a drag.
    mutating func beginDrag(adding: Bool) { base = adding ? selected : [] }

    /// Every line the rectangle touches, plus what was selected before a shift-drag.
    mutating func drag(to rect: CGRect) {
        selected = base.union(lines.indices.filter { lines[$0].box.intersects(rect) })
    }

    /// A click without a drag.
    mutating func click(at point: CGPoint, toggle: Bool, tolerance: CGFloat = 0) {
        guard let index = line(at: point, tolerance: tolerance) else {
            if !toggle { selected = [] }
            return
        }
        if toggle {
            if selected.contains(index) { selected.remove(index) } else { selected.insert(index) }
        } else {
            selected = [index]
        }
    }

    mutating func selectAll() { selected = Set(lines.indices) }
    mutating func clear() { selected = [] }

    /// The selected lines, or every line when nothing is selected.
    var textToCopy: String {
        let indices = selected.isEmpty ? Array(lines.indices) : selected.sorted()
        return Self.join(indices.map { lines[$0].text })
    }
}
