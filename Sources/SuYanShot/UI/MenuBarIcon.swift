import AppKit

/// The menu-bar version of the 拾光 icon: viewfinder corners around a four-point sparkle.
/// Drawn as a template image so macOS tints it for light, dark and highlighted menu bars.
enum MenuBarIcon {
    static let image: NSImage = {
        let size = NSSize(width: 18, height: 18)
        let image = NSImage(size: size, flipped: false) { rect in
            draw(in: rect)
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "拾光"
        return image
    }()

    /// The icon with a small dot beside it: phone sharing is on and reachable.
    static let sharingImage: NSImage = {
        let image = NSImage(size: NSSize(width: 23, height: 18), flipped: false) { rect in
            draw(in: CGRect(x: 0, y: 0, width: 18, height: 18))
            NSColor.black.setFill()
            NSBezierPath(ovalIn: CGRect(x: 19, y: 12.5, width: 4, height: 4)).fill()
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "拾光（手機分享中）"
        return image
    }()

    /// Same geometry as Resources/AppIcon.png, scaled to an 18 pt square.
    private static func draw(in rect: CGRect) {
        let frame = rect.insetBy(dx: 1.5, dy: 1.5)
        let arm: CGFloat = 4.6
        let corner: CGFloat = 2.2
        let corners = NSBezierPath()
        corners.lineWidth = 1.7
        corners.lineCapStyle = .round
        corners.lineJoinStyle = .round
        func bracket(_ start: CGPoint, _ bend: CGPoint, _ end: CGPoint) {
            corners.move(to: start)
            corners.line(to: CGPoint(x: bend.x + (start.x - bend.x).sign(corner), y: bend.y + (start.y - bend.y).sign(corner)))
            corners.curve(to: CGPoint(x: bend.x + (end.x - bend.x).sign(corner), y: bend.y + (end.y - bend.y).sign(corner)),
                          controlPoint1: bend, controlPoint2: bend)
            corners.line(to: end)
        }
        bracket(CGPoint(x: frame.minX, y: frame.minY + arm), CGPoint(x: frame.minX, y: frame.minY), CGPoint(x: frame.minX + arm, y: frame.minY))
        bracket(CGPoint(x: frame.maxX - arm, y: frame.minY), CGPoint(x: frame.maxX, y: frame.minY), CGPoint(x: frame.maxX, y: frame.minY + arm))
        bracket(CGPoint(x: frame.maxX, y: frame.maxY - arm), CGPoint(x: frame.maxX, y: frame.maxY), CGPoint(x: frame.maxX - arm, y: frame.maxY))
        bracket(CGPoint(x: frame.minX + arm, y: frame.maxY), CGPoint(x: frame.minX, y: frame.maxY), CGPoint(x: frame.minX, y: frame.maxY - arm))
        NSColor.black.setStroke()
        corners.stroke()

        let center = CGPoint(x: rect.midX, y: rect.midY)
        let radius: CGFloat = 4.4
        let pinch = radius * 0.22
        let sparkle = NSBezierPath()
        sparkle.move(to: CGPoint(x: center.x, y: center.y + radius))
        sparkle.curve(to: CGPoint(x: center.x + radius, y: center.y), controlPoint1: CGPoint(x: center.x + pinch, y: center.y + pinch), controlPoint2: CGPoint(x: center.x + pinch, y: center.y + pinch))
        sparkle.curve(to: CGPoint(x: center.x, y: center.y - radius), controlPoint1: CGPoint(x: center.x + pinch, y: center.y - pinch), controlPoint2: CGPoint(x: center.x + pinch, y: center.y - pinch))
        sparkle.curve(to: CGPoint(x: center.x - radius, y: center.y), controlPoint1: CGPoint(x: center.x - pinch, y: center.y - pinch), controlPoint2: CGPoint(x: center.x - pinch, y: center.y - pinch))
        sparkle.curve(to: CGPoint(x: center.x, y: center.y + radius), controlPoint1: CGPoint(x: center.x - pinch, y: center.y + pinch), controlPoint2: CGPoint(x: center.x - pinch, y: center.y + pinch))
        sparkle.close()
        NSColor.black.setFill()
        sparkle.fill()
    }
}

private extension CGFloat {
    /// `magnitude` with the sign of `self` (0 stays 0).
    func sign(_ magnitude: CGFloat) -> CGFloat { self > 0 ? magnitude : (self < 0 ? -magnitude : 0) }
}
