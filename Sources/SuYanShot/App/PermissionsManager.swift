import AppKit

@MainActor
enum PermissionsManager {
    private static var hasRequestedThisLaunch = false

    static var canCaptureScreen: Bool { CGPreflightScreenCaptureAccess() }

    /// Triggers the system Screen Recording prompt at most once per launch, so repeated captures
    /// don't keep re-asking. Returns true if this call is the one that asked.
    static func requestOncePerLaunch() -> Bool {
        guard !hasRequestedThisLaunch else { return false }
        hasRequestedThisLaunch = true
        CGRequestScreenCaptureAccess()
        return true
    }

    static func openScreenCaptureSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(url)
        }
    }

    /// A newly granted Screen Recording permission only applies after the app restarts.
    static func relaunch() {
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.createsNewApplicationInstance = true
        NSWorkspace.shared.openApplication(at: Bundle.main.bundleURL, configuration: configuration) { _, _ in
            DispatchQueue.main.async { NSApp.terminate(nil) }
        }
    }
}
