import CoreGraphics

enum PermissionsManager {
    static var canCaptureScreen: Bool { CGPreflightScreenCaptureAccess() }

    @discardableResult
    static func requestScreenCaptureAccess() -> Bool {
        CGRequestScreenCaptureAccess()
    }
}
