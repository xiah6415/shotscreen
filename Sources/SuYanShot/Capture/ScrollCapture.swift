import CoreGraphics
import Foundation

/// Reserved for the next-phase manual-scroll technical spike. This implementation intentionally does not capture, analyze, or stitch frames.
protocol ScrollCaptureService: AnyObject {
    func start(configuration: ScrollCaptureConfiguration) throws
    func stop() -> ScrollCaptureResult
}

struct ScrollCaptureConfiguration: Sendable {
    let region: CGRect
}

struct ScrollCaptureFrame: Sendable {
    let image: CGImage
    let timestamp: Date
}

enum ScrollCaptureResult: Sendable {
    case notImplemented
    case frames([ScrollCaptureFrame])
    case stitched(CGImage)
}

final class PlaceholderScrollCaptureService: ScrollCaptureService {
    func start(configuration: ScrollCaptureConfiguration) throws { }
    func stop() -> ScrollCaptureResult { .notImplemented }
}
