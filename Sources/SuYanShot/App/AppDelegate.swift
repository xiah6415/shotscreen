import AppKit
import ScreenCaptureKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let coordinator = CaptureCoordinator()
    private let shortcuts = ShortcutManager()
    private var menuBarController: MenuBarController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        menuBarController = MenuBarController(coordinator: coordinator)
        shortcuts.onAction = { [weak self] action in
            switch action {
            case .region: self?.coordinator.captureRegion()
            case .window: self?.coordinator.captureWindow()
            case .fullScreen: self?.coordinator.captureFullScreen()
            case .delayed: self?.coordinator.captureDelayed(.three)
            }
        }
        shortcuts.registerDefaults()
    }
}

@MainActor
final class CaptureCoordinator {
    private let captureService = ScreenCaptureService()
    private lazy var fullScreenService = FullScreenCaptureService(captureService: captureService)
    private lazy var windowService = WindowCaptureService(captureService: captureService)
    private let regionSelector = RegionSelector()
    private let windowPicker = WindowPicker()
    private let countdown = CountdownHUD()
    private let exporter = ImageExporter()
    private lazy var thumbnail = FloatingThumbnailController(exporter: exporter)

    func captureRegion() {
        regionSelector.begin { [weak self] result in
            guard let self, case let .success(selection) = result else { return }
            Task { @MainActor in
                do {
                    let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                    guard let display = content.displays.first(where: { $0.displayID == selection.screen.displayID }) else { throw CaptureError.noDisplay }
                    let image = try await self.captureService.capture(display: display, sourceRect: selection.sourceRectPixels)
                    self.finish(CapturedImage(image: image, mode: .region, capturedAt: .now))
                } catch { self.present(error) }
            }
        }
    }

    func captureWindow() {
        Task { @MainActor in
            do {
                let windows = try await windowService.shareableWindows()
                guard !windows.isEmpty else { throw CaptureError.noShareableWindows }
                windowPicker.begin(windows: windows) { [weak self] window in
                    guard let self, let window else { return }
                    Task { @MainActor in
                        do { self.finish(try await self.windowService.capture(window)) } catch { self.present(error) }
                    }
                }
            } catch { present(error) }
        }
    }

    /// Captures the display under the pointer without asking.
    func captureFullScreen() {
        Task { @MainActor in
            do {
                let displays = try await fullScreenService.availableDisplays()
                let target = NSScreen.underMouse?.displayID
                guard let display = displays.first(where: { $0.displayID == target }) ?? displays.first else { throw CaptureError.noDisplay }
                finish(try await fullScreenService.capture(display))
            } catch { present(error) }
        }
    }

    func captureDelayed(_ delay: DelayOption) {
        countdown.start(seconds: delay.rawValue) { [weak self] in self?.captureFullScreen() }
    }

    func openCaptureFolder() { NSWorkspace.shared.open(exporter.defaultDirectory) }

    private func finish(_ captured: CapturedImage) {
        ClipboardManager.copy(captured.image)
        thumbnail.show(captured)
    }

    private func present(_ error: Error) {
        let alert = NSAlert(error: error)
        if case CaptureError.permissionDenied = error {
            alert.addButton(withTitle: "打開系統設定")
            alert.addButton(withTitle: "取消")
            if alert.runModal() == .alertFirstButtonReturn { PermissionsManager.openScreenCaptureSettings() }
        } else {
            alert.runModal()
        }
    }
}
