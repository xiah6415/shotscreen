import CoreGraphics

/// Keep logical capture coordinates separate from Retina output dimensions.
enum CaptureGeometry {
    static func sourceRect(selection: CGRect, screenHeight: CGFloat) -> CGRect {
        CGRect(x: selection.minX, y: screenHeight - selection.maxY,
               width: selection.width, height: selection.height)
    }

    static func pixelSize(for rect: CGRect, scale: CGFloat) -> CGSize {
        CGSize(width: (rect.width * scale).rounded(.up),
               height: (rect.height * scale).rounded(.up))
    }
}
