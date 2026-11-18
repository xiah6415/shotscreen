import Foundation

/// Reasons are observations, not guesses that the document has reached its end.
enum ScrollPauseReason: Equatable {
    case noMovement
    case cannotStitch
    case noProgress
    case captureFailed
}

/// Bounded retries and monotonic no-progress timing, independent of UI and screen capture.
struct ScrollProgressWatchdog {
    enum Frame { case first, appended, unchanged, rejected }
    private var lastProgress: ContinuousClock.Instant
    private var unchangedFrames = 0
    private var rejectedFrames = 0
    private var captureFailures = 0

    init(now: ContinuousClock.Instant = .now) { lastProgress = now }

    mutating func reset(now: ContinuousClock.Instant = .now) {
        self = Self(now: now)
    }

    mutating func captureSucceeded() { captureFailures = 0 }

    mutating func captureFailed() -> ScrollPauseReason? {
        captureFailures += 1
        return captureFailures >= 3 ? .captureFailed : nil
    }

    mutating func observe(_ frame: Frame, now: ContinuousClock.Instant = .now) -> ScrollPauseReason? {
        switch frame {
        case .first, .appended:
            lastProgress = now
            unchangedFrames = 0
            rejectedFrames = 0
            return nil
        case .unchanged:
            unchangedFrames += 1
            rejectedFrames = 0
        case .rejected:
            rejectedFrames += 1
            unchangedFrames = 0
        }
        if unchangedFrames >= 4 { return .noMovement }
        if rejectedFrames >= 8 { return .cannotStitch }
        if now - lastProgress >= .seconds(10) { return .noProgress }
        return nil
    }
}
