import AppKit
import ScreenCaptureKit

@MainActor
final class ScreenCaptureService {
    func capture(display: SCDisplay, sourceRect: CGRect? = nil) async throws -> CGImage {
        guard PermissionsManager.canCaptureScreen || PermissionsManager.requestScreenCaptureAccess() else {
            throw CaptureError.permissionDenied
        }

        let filter = SCContentFilter(display: display, excludingWindows: [])
        let configuration = SCStreamConfiguration()
        configuration.showsCursor = false
        if let sourceRect {
            configuration.sourceRect = sourceRect.integral
            configuration.width = max(1, Int(sourceRect.width))
            configuration.height = max(1, Int(sourceRect.height))
        }
        return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration)
    }

    func capture(window: SCWindow) async throws -> CGImage {
        guard PermissionsManager.canCaptureScreen || PermissionsManager.requestScreenCaptureAccess() else {
            throw CaptureError.permissionDenied
        }
        let filter = SCContentFilter(desktopIndependentWindow: window)
        // Default configuration size is not Retina-aware; request the window at full backing resolution.
        let scale = CGFloat(filter.pointPixelScale)
        let configuration = SCStreamConfiguration()
        configuration.width = max(1, Int(window.frame.width * scale))
        configuration.height = max(1, Int(window.frame.height * scale))
        configuration.showsCursor = false
        return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration)
    }
}
