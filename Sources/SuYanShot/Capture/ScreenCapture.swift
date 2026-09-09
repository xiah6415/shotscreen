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
        return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: SCStreamConfiguration())
    }
}
