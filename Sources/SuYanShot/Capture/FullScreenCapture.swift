import AppKit
import ScreenCaptureKit

@MainActor
final class FullScreenCaptureService {
    private let captureService: ScreenCaptureService

    init(captureService: ScreenCaptureService) { self.captureService = captureService }

    func availableDisplays() async throws -> [SCDisplay] {
        try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true).displays
    }

    func capture(_ display: SCDisplay) async throws -> CapturedImage {
        CapturedImage(image: try await captureService.capture(display: display), mode: .fullScreen, capturedAt: .now)
    }
}
