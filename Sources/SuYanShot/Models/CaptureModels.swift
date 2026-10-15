import AppKit

enum CaptureMode: String, CaseIterable {
    case region, window, fullScreen, delayed
}

enum DelayOption: Int, CaseIterable {
    case three = 3, five = 5, ten = 10

    var title: String { "\(rawValue) 秒" }
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
        case .permissionDenied: "需要「螢幕錄製」權限才能截圖。"
        case .noDisplay: "找不到可以截圖的螢幕。"
        case .noShareableWindows: "目前沒有可以截圖的視窗。"
        case .invalidSelection: "選取的範圍太小，無法截圖。"
        }
    }
}
