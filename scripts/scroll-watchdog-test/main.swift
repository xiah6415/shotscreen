import Foundation

let start = ContinuousClock.now
var guardState = ScrollProgressWatchdog(now: start)
for i in 1...7 {
    precondition(guardState.observe(.rejected, now: start.advanced(by: .milliseconds(i * 200))) == nil)
}
precondition(guardState.observe(.rejected, now: start.advanced(by: .seconds(2))) == .cannotStitch)

guardState.reset(now: start)
for _ in 0..<3 { precondition(guardState.observe(.unchanged, now: start) == nil) }
precondition(guardState.observe(.unchanged, now: start) == .noMovement)
// Reversing direction or pressing Retry starts a fresh bounded attempt.
guardState.reset(now: start)
precondition(guardState.observe(.unchanged, now: start) == nil)

// Alternating outcomes must still stop even though neither consecutive count reaches its limit.
guardState.reset(now: start)
for second in 1...9 {
    precondition(guardState.observe(second % 2 == 0 ? .rejected : .unchanged,
                                   now: start.advanced(by: .seconds(second))) == nil)
}
precondition(guardState.observe(.rejected, now: start.advanced(by: .seconds(10))) == .noProgress)

// Real progress resets the timer and consecutive failure counters.
guardState.reset(now: start)
for _ in 0..<7 { _ = guardState.observe(.rejected, now: start) }
precondition(guardState.observe(.appended, now: start.advanced(by: .seconds(9))) == nil)
precondition(guardState.observe(.rejected, now: start.advanced(by: .seconds(11))) == nil)
precondition(guardState.observe(.unchanged, now: start.advanced(by: .seconds(19))) == .noProgress)

guardState.reset(now: start)
precondition(guardState.captureFailed() == nil)
precondition(guardState.captureFailed() == nil)
guardState.captureSucceeded()
precondition(guardState.captureFailed() == nil)
precondition(guardState.captureFailed() == nil)
precondition(guardState.captureFailed() == .captureFailed)
guardState.reset(now: start)
precondition(guardState.captureFailed() == nil)
print("Scroll watchdog checks passed: unchanged/rejection limits, mixed no-progress timeout, progress recovery, capture failures and retry reset.")
