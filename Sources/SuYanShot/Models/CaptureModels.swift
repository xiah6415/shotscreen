import AppKit

enum CaptureMode: String, CaseIterable {
    case region, window, fullScreen, delayed
}

enum DelayOption: Int, CaseIterable {
    case three = 3, five = 5, ten = 10

    var title: String { "\(rawValue) seconds" }
}

struct CapturedImage {
    let image: CGImage
    let mode: CaptureMode
    let capturedAt: Date
}

enum CaptureError: LocalizedError {
    case permissionDenied
    case noDisplay
    case noShareableWindows
    case invalidSelection

    var errorDescription: String? {
        switch self {
        case .permissionDenied: "Screen Recording permission is required."
        case .noDisplay: "No display is available for capture."
        case .noShareableWindows: "No shareable windows are available."
        case .invalidSelection: "The selected area is too small to capture."
        }
    }
}
