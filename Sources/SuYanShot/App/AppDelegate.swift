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
    private let thumbnail = FloatingThumbnailController(exporter: ImageExporter())

    func captureRegion() {
        regionSelector.begin { [weak self] result in
            guard let self, case let .success(selection) = result else { return }
            Task { @MainActor in
                do {
                    let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                    let screenNumber = selection.screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber
                    guard let display = content.displays.first(where: { $0.displayID == screenNumber?.uint32Value }) else { throw CaptureError.noDisplay }
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
                guard let window = choose(windows: windows) else { return }
                finish(try await windowService.capture(window))
            } catch { present(error) }
        }
    }

    func captureFullScreen() {
        Task { @MainActor in
            do {
                let displays = try await fullScreenService.availableDisplays()
                guard let display = choose(displays: displays) else { throw CaptureError.noDisplay }
                finish(try await fullScreenService.capture(display))
            } catch { present(error) }
        }
    }

    func captureDelayed(_ delay: DelayOption) {
        let alert = NSAlert()
        alert.messageText = "Capture in \(delay.title)"
        alert.informativeText = "Move the desired window into position."
        alert.addButton(withTitle: "Start")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + .seconds(delay.rawValue)) { [weak self] in self?.captureFullScreen() }
    }

    private func choose(windows: [SCWindow]) -> SCWindow? {
        let popup = NSPopUpButton(frame: .zero, pullsDown: false)
        windows.forEach { popup.addItem(withTitle: "\($0.owningApplication?.applicationName ?? "Unknown") — \($0.title ?? "Untitled")") }
        let alert = NSAlert(); alert.messageText = "Choose a window"; alert.accessoryView = popup; alert.addButton(withTitle: "Capture"); alert.addButton(withTitle: "Cancel")
        return alert.runModal() == .alertFirstButtonReturn ? windows[popup.indexOfSelectedItem] : nil
    }

    private func choose(displays: [SCDisplay]) -> SCDisplay? {
        guard !displays.isEmpty else { return nil }
        let popup = NSPopUpButton(frame: .zero, pullsDown: false)
        displays.enumerated().forEach { popup.addItem(withTitle: "Display \($0.offset + 1) (\($0.element.width) × \($0.element.height))") }
        let alert = NSAlert(); alert.messageText = "Choose a display"; alert.accessoryView = popup; alert.addButton(withTitle: "Capture"); alert.addButton(withTitle: "Cancel")
        return alert.runModal() == .alertFirstButtonReturn ? displays[popup.indexOfSelectedItem] : nil
    }

    private func finish(_ captured: CapturedImage) {
        ClipboardManager.copy(captured.image)
        thumbnail.show(captured)
    }

    private func present(_ error: Error) { NSAlert(error: error).runModal() }
}
