import CoreGraphics
import Foundation
import ScreenCaptureKit

/// Manual scroll capture: the user scrolls; this repeatedly captures a fixed region and stitches what scrolled into view.
/// It never scrolls anything itself.
@MainActor
protocol ScrollCaptureService: AnyObject {
    func start(configuration: ScrollCaptureConfiguration, onProgress: @escaping (ScrollCaptureProgress) -> Void)
    /// Stops capturing and returns the stitched image (nil if nothing was captured).
    func finish() async -> CGImage?
    func cancel()
}

struct ScrollCaptureConfiguration {
    let display: SCDisplay
    /// Top-left-origin backing pixels on `display`, as used by `SCStreamConfiguration.sourceRect`.
    let sourceRect: CGRect
    var interval: Duration = .milliseconds(100)
}

struct ScrollCaptureProgress {
    let height: Int
    let lastResult: ScrollStitcher.FrameResult
    /// Narrow rendering of everything stitched so far; nil when nothing new was added.
    let preview: CGImage?
}

@MainActor
final class ManualScrollCaptureService: ScrollCaptureService {
    private let captureService: ScreenCaptureService
    private var stitcher: ScrollStitcher?
    private var loop: Task<Void, Never>?

    init(captureService: ScreenCaptureService) { self.captureService = captureService }

    func start(configuration: ScrollCaptureConfiguration, onProgress: @escaping (ScrollCaptureProgress) -> Void) {
        cancel()
        let stitcher = ScrollStitcher()
        self.stitcher = stitcher
        loop = Task { @MainActor [captureService] in
            while !Task.isCancelled {
                let began = ContinuousClock.now
                if let frame = try? await captureService.capture(display: configuration.display, sourceRect: configuration.sourceRect) {
                    // Matching is CPU work; keep it off the main thread.
                    let (result, preview) = await Task.detached(priority: .userInitiated) { () -> (ScrollStitcher.FrameResult, CGImage?) in
                        let result = stitcher.add(frame)
                        switch result {
                        case .first, .appended: return (result, stitcher.makePreview())
                        default: return (result, nil)
                        }
                    }.value
                    onProgress(ScrollCaptureProgress(height: stitcher.height, lastResult: result, preview: preview))
                    if result == .limitReached { break }
                }
                let remaining = configuration.interval - (ContinuousClock.now - began)
                if remaining > .zero { try? await Task.sleep(for: remaining) }
            }
        }
    }

    func finish() async -> CGImage? {
        loop?.cancel()
        await loop?.value
        loop = nil
        defer { stitcher = nil }
        guard let stitcher else { return nil }
        return await Task.detached(priority: .userInitiated) { stitcher.makeImage() }.value
    }

    func cancel() {
        loop?.cancel()
        loop = nil
        stitcher = nil
    }
}
