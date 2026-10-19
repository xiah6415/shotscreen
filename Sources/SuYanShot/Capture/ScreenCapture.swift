import AppKit
import ScreenCaptureKit

@MainActor
final class ScreenCaptureService {
    /// This app as ScreenCaptureKit sees it; looked up once and excluded from display captures.
    private var ownApplication: SCRunningApplication?

    func capture(display: SCDisplay, sourceRect: CGRect? = nil) async throws -> CGImage {
        guard PermissionsManager.canCaptureScreen else {
            throw CaptureError.permissionDenied
        }

        let filter = try await displayFilter(for: display)
        let configuration = SCStreamConfiguration()
        configuration.showsCursor = false
        if let sourceRect {
            configuration.sourceRect = sourceRect.integral
            configuration.width = max(1, Int(sourceRect.width))
            configuration.height = max(1, Int(sourceRect.height))
        }
        return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration)
    }

    /// The whole display minus this app's own overlays, handles, HUDs and preview cards,
    /// even while they are still being removed from the screen. Shared by screenshots and recordings.
    func displayFilter(for display: SCDisplay) async throws -> SCContentFilter {
        if let own = try await ownApp() {
            return SCContentFilter(display: display, excludingApplications: [own], exceptingWindows: [])
        }
        return SCContentFilter(display: display, excludingWindows: [])
    }

    private func ownApp() async throws -> SCRunningApplication? {
        if let ownApplication { return ownApplication }
        let pid = ProcessInfo.processInfo.processIdentifier
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        ownApplication = content.applications.first { $0.processID == pid }
        return ownApplication
    }

    func capture(window: SCWindow) async throws -> CGImage {
        guard PermissionsManager.canCaptureScreen else {
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
