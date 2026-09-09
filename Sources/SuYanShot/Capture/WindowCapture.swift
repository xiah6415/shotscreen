import AppKit
import ScreenCaptureKit

@MainActor
final class WindowCaptureService {
    private let captureService: ScreenCaptureService

    init(captureService: ScreenCaptureService) { self.captureService = captureService }

    func shareableWindows() async throws -> [SCWindow] {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        return content.windows.filter { $0.isOnScreen && $0.frame.width > 1 && $0.frame.height > 1 && !($0.title ?? "").isEmpty }
    }

    func capture(_ window: SCWindow) async throws -> CapturedImage {
        CapturedImage(image: try await captureService.capture(window: window), mode: .window, capturedAt: .now)
    }
}
