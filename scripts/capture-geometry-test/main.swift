import CoreGraphics

// A selection local to a display stays independent of its global desktop origin.
let selection = CGRect(x: 120, y: 200, width: 300, height: 150)
let rect = CaptureGeometry.sourceRect(selection: selection, screenHeight: 982)
precondition(rect == CGRect(x: 120, y: 632, width: 300, height: 150))
precondition(CaptureGeometry.pixelSize(for: rect, scale: 2) == CGSize(width: 600, height: 300))
precondition(CaptureGeometry.pixelSize(for: rect, scale: 1) == CGSize(width: 300, height: 150))
let full = CaptureGeometry.sourceRect(selection: CGRect(x: 0, y: 0, width: 1512, height: 982), screenHeight: 982)
precondition(full.origin == .zero)
precondition(CaptureGeometry.pixelSize(for: full, scale: 2) == CGSize(width: 3024, height: 1964))
let edge = CaptureGeometry.sourceRect(selection: CGRect(x: 1211.5, y: 0, width: 300.5, height: 100.5), screenHeight: 982)
precondition(edge.maxX == 1512 && edge.maxY == 982)
precondition(CaptureGeometry.pixelSize(for: edge, scale: 2) == CGSize(width: 601, height: 201))
print("Capture geometry checks passed (1×, Retina 2×, full display, fractional edge).")
