import AppKit
import ScreenCaptureKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let coordinator = CaptureCoordinator()
    private let shortcutStore = ShortcutStore()
    private let shortcuts = ShortcutManager()
    private let clipboard = ClipboardFeature()
    private var menuBarController: MenuBarController?
    private lazy var settings = SettingsWindowController(store: shortcutStore, clipboard: clipboard.store)

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        menuBarController = MenuBarController(
            coordinator: coordinator,
            openSettings: { [weak self] in self?.settings.show() },
            openClipboard: { [weak self] in self?.clipboard.togglePanel() },
            openPhoneShare: { [weak self] in self?.clipboard.showPhoneShare() })
        shortcuts.onAction = { [weak self] action in
            switch action {
            case .region: self?.coordinator.captureRegion()
            case .window: self?.coordinator.captureWindow()
            case .fullScreen: self?.coordinator.captureFullScreen()
            case .delayed: self?.coordinator.captureDelayed(.three)
            case .scroll: self?.coordinator.captureScrolling()
            case .allInOne: self?.coordinator.captureAllInOne()
            case .clipboard: self?.clipboard.togglePanel()
            }
        }
        shortcutStore.onChange = { [weak self] in self?.applyShortcuts() }
        applyShortcuts()
    }

    /// Global hotkeys are paused while the settings window records a new shortcut.
    private func applyShortcuts() {
        if shortcutStore.recordingAction != nil {
            shortcuts.unregisterAll()
        } else {
            shortcutStore.unavailable = shortcuts.apply(shortcutStore.shortcuts)
        }
        menuBarController?.update(shortcuts: shortcutStore.shortcuts)
    }
}

@MainActor
final class CaptureCoordinator {
    private let captureService = ScreenCaptureService()
    private lazy var fullScreenService = FullScreenCaptureService(captureService: captureService)
    private lazy var windowService = WindowCaptureService(captureService: captureService)
    private let regionSelector = RegionSelector()
    private let allInOneSelector = AllInOneSelector()
    private let windowPicker = WindowPicker()
    private let countdown = CountdownHUD()
    private lazy var scrollService: ScrollCaptureService = ManualScrollCaptureService(captureService: captureService)
    private let scrollHUD = ScrollCaptureHUD()
    private var isScrollCapturing = false
    private let exporter = ImageExporter()
    private lazy var thumbnail = FloatingThumbnailController(exporter: exporter) { [weak self] in self?.annotate($0) }
    private var editors: [AnnotationEditorController] = []

    func captureRegion() {
        guard ensurePermission() else { return }
        regionSelector.begin { [weak self] result in
            guard let self, case let .success(selection) = result else { return }
            self.capture(selection)
        }
    }

    /// Region, scroll, window and full-screen capture from one overlay.
    func captureAllInOne() {
        if isScrollCapturing { finishScrolling(); return }
        guard ensurePermission() else { return }
        allInOneSelector.begin { [weak self] action in
            guard let self, let action else { return }
            switch action {
            case let .capture(selection): self.capture(selection)
            case let .scroll(selection): self.startScrolling(selection)
            case .window: self.captureWindow()
            case let .fullScreen(screen): self.captureFullScreen(on: screen)
            }
        }
    }

    private func display(for screen: NSScreen) async throws -> SCDisplay {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first(where: { $0.displayID == screen.displayID }) else { throw CaptureError.noDisplay }
        return display
    }

    private func capture(_ selection: RegionSelection) {
        Task { @MainActor in
            do {
                let display = try await display(for: selection.screen)
                let image = try await captureService.capture(display: display, sourceRect: selection.sourceRectPixels)
                finish(CapturedImage(image: image, mode: .region, capturedAt: .now))
            } catch { present(error) }
        }
    }

    func captureWindow() {
        guard ensurePermission() else { return }
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

    /// Captures the display under the pointer (or `screen`) without asking.
    func captureFullScreen(on screen: NSScreen? = nil) {
        guard ensurePermission() else { return }
        Task { @MainActor in
            do {
                let displays = try await fullScreenService.availableDisplays()
                let target = (screen ?? NSScreen.underMouse)?.displayID
                guard let display = displays.first(where: { $0.displayID == target }) ?? displays.first else { throw CaptureError.noDisplay }
                finish(try await fullScreenService.capture(display))
            } catch { present(error) }
        }
    }

    /// Select a region, then the user scrolls while frames are stitched. Running it again finishes the capture.
    func captureScrolling() {
        if isScrollCapturing { finishScrolling(); return }
        guard ensurePermission() else { return }
        regionSelector.begin { [weak self] result in
            guard let self, case let .success(selection) = result else { return }
            self.startScrolling(selection)
        }
    }

    private func startScrolling(_ selection: RegionSelection) {
        Task { @MainActor in
            do {
                let display = try await display(for: selection.screen)
                isScrollCapturing = true
                let globalRegion = selection.rectInScreenPoints.offsetBy(dx: selection.screen.frame.minX, dy: selection.screen.frame.minY)
                scrollHUD.show(around: globalRegion,
                               onDone: { [weak self] in self?.finishScrolling() },
                               onCancel: { [weak self] in self?.cancelScrolling() },
                               onAutoScroll: { [weak self] enabled in self?.setAutoScroll(enabled) })
                // CoreGraphics global coordinates are top-left origin of the primary display.
                let primaryHeight = NSScreen.screens.first?.frame.height ?? 0
                let configuration = ScrollCaptureConfiguration(
                    display: display,
                    sourceRect: selection.sourceRectPixels,
                    scrollPoint: CGPoint(x: globalRegion.midX, y: primaryHeight - globalRegion.midY),
                    regionHeightPoints: globalRegion.height)
                scrollService.start(configuration: configuration) { [weak self] progress in
                    self?.scrollHUD.update(progress)
                }
            } catch { present(error) }
        }
    }

    /// Auto scroll posts scroll-wheel events, which needs Accessibility permission.
    private func setAutoScroll(_ enabled: Bool) {
        guard !enabled || PermissionsManager.isAccessibilityTrusted else {
            PermissionsManager.requestAccessibility()
            let alert = NSAlert()
            alert.messageText = "自動捲動需要「輔助使用」權限"
            alert.informativeText = "到「系統設定 → 隱私權與安全性 → 輔助使用」打開素顏 Shot，再按一次「自動捲動」。\n不開也可以繼續手動捲動。"
            alert.addButton(withTitle: "打開系統設定")
            alert.addButton(withTitle: "先手動捲")
            NSApp.activate(ignoringOtherApps: true)
            if alert.runModal() == .alertFirstButtonReturn { PermissionsManager.openAccessibilitySettings() }
            return
        }
        scrollService.setAutoScroll(enabled)
    }

    private func finishScrolling() {
        guard isScrollCapturing else { return }
        isScrollCapturing = false
        scrollHUD.hide()
        Task { @MainActor in
            if let image = await scrollService.finish() { finish(CapturedImage(image: image, mode: .scroll, capturedAt: .now)) }
        }
    }

    private func cancelScrolling() {
        isScrollCapturing = false
        scrollHUD.hide()
        scrollService.cancel()
    }

    func captureDelayed(_ delay: DelayOption) {
        guard ensurePermission() else { return }
        countdown.start(seconds: delay.rawValue) { [weak self] in self?.captureFullScreen() }
    }

    func annotate(_ captured: CapturedImage) {
        let editor = AnnotationEditorController(captured: captured, exporter: exporter)
        editor.onClose = { [weak self] closed in self?.editors.removeAll { $0 === closed } }
        editors.append(editor)
        editor.show()
    }

    func openCaptureFolder() { NSWorkspace.shared.open(exporter.defaultDirectory) }

    private func finish(_ captured: CapturedImage) {
        ClipboardManager.copy(captured.image)
        thumbnail.show(captured)
    }

    /// Checked before any ScreenCaptureKit call, since those calls trigger the system prompt themselves.
    private func ensurePermission() -> Bool {
        if PermissionsManager.canCaptureScreen { return true }
        if PermissionsManager.requestOncePerLaunch() { return false } // macOS shows its own prompt this time
        showPermissionHelp()
        return false
    }

    private func showPermissionHelp() {
        let alert = NSAlert()
        alert.messageText = "素顏 Shot 需要「螢幕錄製」權限"
        alert.informativeText = "到「系統設定 → 隱私權與安全性 → 螢幕與系統錄音」打開素顏 Shot。\n打開之後要重新啟動 App 才會生效。"
        alert.addButton(withTitle: "打開系統設定")
        alert.addButton(withTitle: "重新啟動 App")
        alert.addButton(withTitle: "取消")
        NSApp.activate(ignoringOtherApps: true)
        switch alert.runModal() {
        case .alertFirstButtonReturn: PermissionsManager.openScreenCaptureSettings()
        case .alertSecondButtonReturn: PermissionsManager.relaunch()
        default: break
        }
    }

    private func present(_ error: Error) {
        if case CaptureError.permissionDenied = error { showPermissionHelp(); return }
        NSAlert(error: error).runModal()
    }
}
