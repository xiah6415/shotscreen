import AppKit
import ApplicationServices

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

    /// Needed to post synthetic events (auto scroll, paste from clipboard history).
    static var isAccessibilityTrusted: Bool { AXIsProcessTrusted() }

    /// Shows the system Accessibility prompt if the app is not trusted yet.
    @discardableResult
    static func requestAccessibility() -> Bool {
        AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary)
    }

    static func openAccessibilitySettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
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
