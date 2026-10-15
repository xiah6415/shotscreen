import AppKit

enum AnnotationTool: String, CaseIterable, Identifiable {
    case select, crop, arrow, rectangle, ellipse, line, pen, highlighter, text, counter, pixelate, blur

    var id: String { rawValue }

    var title: String {
        switch self {
        case .select: "選取"
        case .crop: "裁切"
        case .arrow: "箭頭"
        case .rectangle: "矩形"
        case .ellipse: "橢圓"
        case .line: "直線"
        case .pen: "畫筆"
        case .highlighter: "螢光筆"
        case .text: "文字"
        case .counter: "數字標記"
        case .pixelate: "馬賽克"
        case .blur: "模糊"
        }
    }

    var symbol: String {
        switch self {
        case .select: "cursorarrow"
        case .crop: "crop"
        case .arrow: "arrow.up.right"
        case .rectangle: "rectangle"
        case .ellipse: "circle"
        case .line: "line.diagonal"
        case .pen: "scribble"
        case .highlighter: "highlighter"
        case .text: "textformat"
        case .counter: "1.circle"
        case .pixelate: "checkerboard.rectangle"
        case .blur: "drop.halffull"
        }
    }

    /// Single-key shortcut inside the editor.
    var key: String {
        switch self {
        case .select: "v"
        case .crop: "c"
        case .arrow: "a"
        case .rectangle: "r"
        case .ellipse: "o"
        case .line: "l"
        case .pen: "p"
        case .highlighter: "h"
        case .text: "t"
        case .counter: "n"
        case .pixelate: "b"
        case .blur: "u"
        }
    }
}

/// One mark on a capture. Geometry is in image pixels with a bottom-left origin, so the same value
/// renders on screen (scaled) and into the exported image (1:1).
struct Annotation: Identifiable {
    enum Kind: Equatable {
        case arrow, rectangle, ellipse, line, pen, highlighter, pixelate, blur
        case text(String)
        case counter(Int)
    }

    let id = UUID()
    var kind: Kind
    /// Shapes: [start, end]. Pen/highlighter: the stroke path. Text/counter: [anchor].
    var points: [CGPoint]
    var color: NSColor
    var lineWidth: CGFloat
    var fontSize: CGFloat

    var start: CGPoint { points.first ?? .zero }
    var end: CGPoint { points.last ?? start }
    var rect: CGRect {
        CGRect(x: min(start.x, end.x), y: min(start.y, end.y), width: abs(end.x - start.x), height: abs(end.y - start.y))
    }

    mutating func translate(dx: CGFloat, dy: CGFloat) {
        points = points.map { CGPoint(x: $0.x + dx, y: $0.y + dy) }
    }
}
