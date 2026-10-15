import CoreGraphics
import Foundation
import ScreenCaptureKit

/// Scroll capture: repeatedly captures a fixed region and stitches what scrolled into view.
/// The user scrolls by hand, or turns on auto scroll, which pauses at checkpoints to ask before going further.
@MainActor
protocol ScrollCaptureService: AnyObject {
    func start(configuration: ScrollCaptureConfiguration, onProgress: @escaping (ScrollCaptureProgress) -> Void)
    /// Turns auto scroll on or off (also resumes after a checkpoint).
    func setAutoScroll(_ enabled: Bool)
    /// Stops capturing and returns the stitched image (nil if nothing was captured).
    func finish() async -> CGImage?
    func cancel()
}

struct ScrollCaptureConfiguration {
    let display: SCDisplay
    /// Top-left-origin backing pixels on `display`, as used by `SCStreamConfiguration.sourceRect`.
    let sourceRect: CGRect
    /// Centre of the region in global CoreGraphics coordinates, where auto-scroll events are aimed.
    let scrollPoint: CGPoint
    /// Region height in points; auto-scroll step and checkpoint distance derive from it.
    let regionHeightPoints: CGFloat
    var interval: Duration = .milliseconds(100)
    var autoInterval: Duration = .milliseconds(200)
}

enum AutoScrollStatus: Equatable {
    case off
    case running
    /// Stopped after a stretch of auto scrolling; waiting for the user to continue or finish.
    case checkpoint
    /// Nothing moves any more.
    case reachedEnd
}

struct ScrollCaptureProgress {
    let height: Int
    let lastResult: ScrollStitcher.FrameResult
    /// Narrow rendering of everything stitched so far; nil when nothing new was added.
    let preview: CGImage?
    let auto: AutoScrollStatus
}

@MainActor
final class ManualScrollCaptureService: ScrollCaptureService {
    private struct AutoScrollState {
        var step: Int
        var sinceCheckpoint = 0
        var stalledFrames = 0
        var direction = -1 // negative wheel delta scrolls toward the end
        var triedOtherDirection = false
    }

    private let captureService: ScreenCaptureService
    private var stitcher: ScrollStitcher?
    private var loop: Task<Void, Never>?
    private var configuration: ScrollCaptureConfiguration?
    private var auto: AutoScrollState?
    private var autoStatus: AutoScrollStatus = .off

    init(captureService: ScreenCaptureService) { self.captureService = captureService }

    func start(configuration: ScrollCaptureConfiguration, onProgress: @escaping (ScrollCaptureProgress) -> Void) {
        cancel()
        let stitcher = ScrollStitcher()
        self.stitcher = stitcher
        self.configuration = configuration
        loop = Task { @MainActor [captureService, weak self] in
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
                    guard let self, !Task.isCancelled else { break }
                    if result == .limitReached { self.stopAuto(.off) }
                    self.advanceAutoScroll(after: result)
                    onProgress(ScrollCaptureProgress(height: stitcher.height, lastResult: result, preview: preview, auto: self.autoStatus))
                    if result == .limitReached { break }
                }
                let interval = self?.autoStatus == .running ? configuration.autoInterval : configuration.interval
                let remaining = interval - (ContinuousClock.now - began)
                if remaining > .zero { try? await Task.sleep(for: remaining) }
            }
        }
    }

    func setAutoScroll(_ enabled: Bool) {
        guard let configuration else { return }
        if enabled {
            let step = max(60, Int(configuration.regionHeightPoints * 0.35))
            auto = auto ?? AutoScrollState(step: step)
            auto?.sinceCheckpoint = 0
            auto?.stalledFrames = 0
            autoStatus = .running
            AutoScroller.moveCursor(to: configuration.scrollPoint)
        } else {
            stopAuto(.off)
        }
    }

    /// One auto-scroll decision per captured frame: scroll on, back off, pause at a checkpoint, or stop at the end.
    private func advanceAutoScroll(after result: ScrollStitcher.FrameResult) {
        guard autoStatus == .running, var state = auto, let configuration, let stitcher else { return }
        switch result {
        case let .appended(rows):
            state.sinceCheckpoint += rows
            state.stalledFrames = 0
        case .unchanged:
            state.stalledFrames += 1
        case .rejected:
            // Scrolled further than the stitcher can follow; take smaller steps.
            state.step = max(30, state.step * 2 / 3)
        case .first, .limitReached:
            break
        }

        if state.stalledFrames >= 4 {
            // Nothing moved. If nothing was ever stitched, the wheel direction may be inverted; try the other way once.
            if !state.triedOtherDirection, stitcher.height == Int(configuration.sourceRect.height) {
                state.triedOtherDirection = true
                state.direction = -state.direction
                state.stalledFrames = 0
            } else {
                auto = state
                stopAuto(.reachedEnd)
                return
            }
        }

        let regionPixels = Int(configuration.sourceRect.height)
        if state.sinceCheckpoint >= regionPixels * 3 {
            state.sinceCheckpoint = 0
            auto = state
            autoStatus = .checkpoint
            return
        }
        auto = state
        AutoScroller.scroll(by: state.direction * state.step, at: configuration.scrollPoint)
    }

    private func stopAuto(_ status: AutoScrollStatus) {
        autoStatus = status
        if status == .off { auto = nil }
    }

    func finish() async -> CGImage? {
        loop?.cancel()
        await loop?.value
        loop = nil
        auto = nil
        autoStatus = .off
        defer { stitcher = nil }
        guard let stitcher else { return nil }
        return await Task.detached(priority: .userInitiated) { stitcher.makeImage() }.value
    }

    func cancel() {
        loop?.cancel()
        loop = nil
        stitcher = nil
        auto = nil
        autoStatus = .off
    }
}
