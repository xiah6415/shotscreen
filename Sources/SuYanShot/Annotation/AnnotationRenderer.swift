import AppKit
import CoreImage

/// Whole-image pixelated and blurred copies of a capture; pixelate/blur annotations reveal them through a clip.
struct AnnotationEffects {
    let pixelated: CGImage?
    let blurred: CGImage?

    init(source: CGImage) {
        let input = CIImage(cgImage: source)
        let longSide = CGFloat(max(source.width, source.height))
        let context = CIContext()
        pixelated = context.createCGImage(
            input.applyingFilter("CIPixellate", parameters: [kCIInputScaleKey: max(10, longSide / 90), kCIInputCenterKey: CIVector(x: 0, y: 0)])
                .cropped(to: input.extent),
            from: input.extent)
        // Clamp first so edges don't fade to transparent.
        blurred = context.createCGImage(
            input.clampedToExtent().applyingGaussianBlur(sigma: max(8, longSide / 80)).cropped(to: input.extent),
            from: input.extent)
    }

    func image(for kind: Annotation.Kind) -> CGImage? { kind == .blur ? blurred : pixelated }
}

/// Draws annotations into a CGContext whose user space is image pixels. Used by both the canvas and export.
enum AnnotationRenderer {
    /// The capture with all annotations burned in, at full resolution, optionally cropped (image pixels, bottom-left origin).
    static func flatten(_ source: CGImage, effects: AnnotationEffects, annotations: [Annotation], crop: CGRect?) -> CGImage? {
        let space = source.colorSpace.flatMap { $0.supportsOutput ? $0 : nil } ?? CGColorSpace(name: CGColorSpace.sRGB)!
        guard let context = CGContext(data: nil, width: source.width, height: source.height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        let size = CGSize(width: source.width, height: source.height)
        context.draw(source, in: CGRect(origin: .zero, size: size))
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
        annotations.forEach { draw($0, in: context, imageSize: size, effects: effects) }
        NSGraphicsContext.restoreGraphicsState()
        guard let image = context.makeImage() else { return nil }
        guard let crop else { return image }
        // CGImage cropping uses a top-left origin.
        let topLeft = CGRect(x: crop.minX, y: size.height - crop.maxY, width: crop.width, height: crop.height).integral
        return image.cropping(to: topLeft) ?? image
    }

    static func draw(_ annotation: Annotation, in context: CGContext, imageSize: CGSize, effects: AnnotationEffects) {
        context.saveGState()
        defer { context.restoreGState() }
        context.setStrokeColor(annotation.color.cgColor)
        context.setFillColor(annotation.color.cgColor)
        context.setLineWidth(annotation.lineWidth)
        context.setLineCap(.round)
        context.setLineJoin(.round)

        switch annotation.kind {
        case .pixelate, .blur:
            guard let revealed = effects.image(for: annotation.kind) else { return }
            context.clip(to: annotation.rect)
            context.draw(revealed, in: CGRect(origin: .zero, size: imageSize))
            return
        case .highlighter:
            context.setBlendMode(.multiply)
            context.setStrokeColor(annotation.color.withAlphaComponent(0.4).cgColor)
            context.setLineWidth(annotation.lineWidth * 3.5)
            context.setLineCap(.square)
            stroke(annotation.points, in: context)
            return
        default:
            break
        }

        // Soft shadow keeps marks readable on both light and dark content.
        context.setShadow(offset: CGSize(width: 0, height: -annotation.lineWidth * 0.3), blur: annotation.lineWidth * 1.2,
                          color: NSColor.black.withAlphaComponent(0.35).cgColor)

        switch annotation.kind {
        case .rectangle:
            let rect = annotation.rect
            let radius = min(annotation.lineWidth, rect.width / 2, rect.height / 2)
            context.addPath(CGPath(roundedRect: rect, cornerWidth: radius, cornerHeight: radius, transform: nil))
            context.strokePath()
        case .ellipse:
            context.strokeEllipse(in: annotation.rect)
        case .line:
            stroke([annotation.start, annotation.end], in: context)
        case .pen:
            stroke(annotation.points, in: context)
        case .arrow:
            drawArrow(annotation, in: context)
        case let .text(string):
            attributedText(string, for: annotation).draw(at: annotation.start)
        case let .counter(number):
            drawCounter(number, annotation, in: context)
        case .pixelate, .blur, .highlighter:
            break
        }
    }

    static func bounds(of annotation: Annotation) -> CGRect {
        switch annotation.kind {
        case let .text(string):
            return CGRect(origin: annotation.start, size: attributedText(string, for: annotation).size())
        case .counter:
            let diameter = counterDiameter(annotation)
            return CGRect(x: annotation.start.x - diameter / 2, y: annotation.start.y - diameter / 2, width: diameter, height: diameter)
        case .pen, .highlighter:
            let xs = annotation.points.map(\.x), ys = annotation.points.map(\.y)
            let box = CGRect(x: xs.min() ?? 0, y: ys.min() ?? 0, width: (xs.max() ?? 0) - (xs.min() ?? 0), height: (ys.max() ?? 0) - (ys.min() ?? 0))
            return box.insetBy(dx: -annotation.lineWidth * 2, dy: -annotation.lineWidth * 2)
        case .pixelate, .blur:
            return annotation.rect
        default:
            return annotation.rect.insetBy(dx: -annotation.lineWidth, dy: -annotation.lineWidth)
        }
    }

    static func hitTest(_ annotation: Annotation, at point: CGPoint, tolerance: CGFloat) -> Bool {
        let reach = annotation.lineWidth / 2 + tolerance
        switch annotation.kind {
        case .line, .arrow:
            return distance(from: point, toSegment: annotation.start, annotation.end) <= reach
        case .pen, .highlighter:
            return zip(annotation.points, annotation.points.dropFirst()).contains { distance(from: point, toSegment: $0, $1) <= reach * 2 }
                || annotation.points.contains { hypot($0.x - point.x, $0.y - point.y) <= reach * 2 }
        default:
            return bounds(of: annotation).insetBy(dx: -tolerance, dy: -tolerance).contains(point)
        }
    }

    // MARK: - Shapes

    private static func stroke(_ points: [CGPoint], in context: CGContext) {
        guard let first = points.first else { return }
        if points.count == 1 {
            context.addLines(between: [first, CGPoint(x: first.x + 0.01, y: first.y)])
        } else {
            context.addLines(between: points)
        }
        context.strokePath()
    }

    private static func drawArrow(_ annotation: Annotation, in context: CGContext) {
        let start = annotation.start, end = annotation.end
        let length = hypot(end.x - start.x, end.y - start.y)
        guard length > 0.5 else { return }
        let angle = atan2(end.y - start.y, end.x - start.x)
        let head = min(length * 0.6, max(annotation.lineWidth * 4, 18))
        let spread = CGFloat.pi / 7
        let shaftEnd = CGPoint(x: end.x - cos(angle) * head * 0.75, y: end.y - sin(angle) * head * 0.75)
        stroke([start, shaftEnd], in: context)
        context.move(to: end)
        context.addLine(to: CGPoint(x: end.x - head * cos(angle - spread), y: end.y - head * sin(angle - spread)))
        context.addLine(to: CGPoint(x: end.x - head * cos(angle + spread), y: end.y - head * sin(angle + spread)))
        context.closePath()
        context.fillPath()
    }

    private static func counterDiameter(_ annotation: Annotation) -> CGFloat { annotation.fontSize * 1.5 }

    private static func drawCounter(_ number: Int, _ annotation: Annotation, in context: CGContext) {
        let diameter = counterDiameter(annotation)
        let circle = CGRect(x: annotation.start.x - diameter / 2, y: annotation.start.y - diameter / 2, width: diameter, height: diameter)
        context.fillEllipse(in: circle)
        context.setShadow(offset: .zero, blur: 0, color: nil)
        context.setStrokeColor(NSColor.white.cgColor)
        context.setLineWidth(max(2, diameter * 0.07))
        context.strokeEllipse(in: circle.insetBy(dx: diameter * 0.035, dy: diameter * 0.035))
        let label = NSAttributedString(string: "\(number)", attributes: [
            .font: NSFont.monospacedDigitSystemFont(ofSize: annotation.fontSize * 0.8, weight: .bold),
            .foregroundColor: contrasting(annotation.color)
        ])
        let size = label.size()
        label.draw(at: CGPoint(x: annotation.start.x - size.width / 2, y: annotation.start.y - size.height / 2))
    }

    static func attributedText(_ string: String, for annotation: Annotation) -> NSAttributedString {
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
        shadow.shadowBlurRadius = annotation.fontSize * 0.12
        shadow.shadowOffset = NSSize(width: 0, height: -annotation.fontSize * 0.04)
        return NSAttributedString(string: string, attributes: [
            .font: NSFont.systemFont(ofSize: annotation.fontSize, weight: .bold),
            .foregroundColor: annotation.color,
            .shadow: shadow
        ])
    }

    /// Black on light fills (yellow, white), white otherwise.
    private static func contrasting(_ color: NSColor) -> NSColor {
        guard let rgb = color.usingColorSpace(.sRGB) else { return .white }
        let luminance = 0.299 * rgb.redComponent + 0.587 * rgb.greenComponent + 0.114 * rgb.blueComponent
        return luminance > 0.7 ? .black : .white
    }

    private static func distance(from p: CGPoint, toSegment a: CGPoint, _ b: CGPoint) -> CGFloat {
        let dx = b.x - a.x, dy = b.y - a.y
        let lengthSquared = dx * dx + dy * dy
        guard lengthSquared > 0 else { return hypot(p.x - a.x, p.y - a.y) }
        let t = max(0, min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / lengthSquared))
        return hypot(p.x - (a.x + t * dx), p.y - (a.y + t * dy))
    }
}
