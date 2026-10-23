import AppKit
import ScreenCaptureKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let coordinator = CaptureCoordinator()
    private let shortcutStore = ShortcutStore()
    private let shortcuts = ShortcutManager()
    private let clipboard = ClipboardFeature()
    private var menuBarController: MenuBarController?
    private lazy var settings = SettingsWindowController(store: shortcutStore, clipboard: clipboard.store, recording: coordinator.recordingSettings)

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        menuBarController = MenuBarController(
            coordinator: coordinator,
            openSettings: { [weak self] in self?.settings.show() },
            openClipboard: { [weak self] in self?.clipboard.togglePanel() },
            openPhoneShare: { [weak self] in self?.clipboard.showPhoneShare() })
        coordinator.onRecordingChange = { [weak self] recording in self?.menuBarController?.setRecording(recording) }
        shortcuts.onAction = { [weak self] action in
            switch action {
            case .region: self?.coordinator.captureRegion()
            case .window: self?.coordinator.captureWindow()
            case .fullScreen: self?.coordinator.captureFullScreen()
            case .delayed: self?.coordinator.captureDelayed(.three)
            case .scroll: self?.coordinator.captureScrolling()
            case .allInOne: self?.coordinator.captureAllInOne()
            case .clipboard: self?.clipboard.togglePanel()
            case .record: self?.coordinator.recordRegion()
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
    let recordingSettings = RecordingSettings()
    private var recorder: ScreenRecorder?
    private let recordingHUD = RecordingHUD()
    private let recordingStartBar = RecordingStartBar()
    private lazy var recordingPreview = RecordingPreviewController()
    /// Called with true when a recording starts and false when it ends.
    var onRecordingChange: ((Bool) -> Void)?
    var isRecording: Bool { recorder != nil }
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

    /// Region, scroll, window and full-screen capture — and recording — from one overlay.
    func captureAllInOne(mode: AllInOneMode = .capture) {
        if isScrollCapturing { finishScrolling(); return }
        if isRecording { stopRecording(); return }
        guard ensurePermission() else { return }
        allInOneSelector.begin(mode: mode, recording: recordingSettings) { [weak self] action in
            guard let self, let action else { return }
            switch action {
            case let .capture(selection): self.capture(selection)
            case let .scroll(selection): self.startScrolling(selection)
            case .window: self.captureWindow()
            case let .fullScreen(screen): self.captureFullScreen(on: screen)
            // The All-in-One toolbar already showed the sound options, so these start right away.
            case let .record(selection): self.confirmRecording(.region(selection))
            case let .recordFullScreen(screen): self.confirmRecording(.screen(screen))
            case .recordWindow: self.pickWindowToRecord()
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
            alert.informativeText = "到「系統設定 → 隱私權與安全性 → 輔助使用」打開拾光，再按一次「自動捲動」。\n不開也可以繼續手動捲動。"
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

    // MARK: - Recording

    private enum RecordingTarget {
        case region(RegionSelection)
        case screen(NSScreen)
        case window(SCWindow)
    }

    /// Opens All-in-One in 錄影 mode: pick and adjust the region, check the sound, then 錄範圍.
    /// While recording, stops instead.
    func recordRegion() {
        captureAllInOne(mode: .record)
    }

    /// Records the screen under the pointer after the start bar; while recording, stops instead.
    func recordFullScreen() {
        if isRecording { stopRecording(); return }
        guard ensurePermission(), let screen = NSScreen.underMouse else { return }
        recordingStartBar.show(region: nil, on: screen, settings: recordingSettings,
                               onStart: { [weak self] in self?.confirmRecording(.screen(screen)) }, onCancel: {})
    }

    /// Hover-pick a window, then the start bar (sound defaults to that window's app).
    func recordWindow() {
        if isRecording { stopRecording(); return }
        guard ensurePermission() else { return }
        pickWindowToRecord()
    }

    private func pickWindowToRecord() {
        Task { @MainActor in
            do {
                let windows = try await windowService.shareableWindows()
                guard !windows.isEmpty else { throw CaptureError.noShareableWindows }
                windowPicker.begin(windows: windows) { [weak self] window in
                    guard let self, let window else { return }
                    let screen = self.screen(containing: window) ?? NSScreen.underMouse ?? NSScreen.main!
                    let appName = window.owningApplication?.applicationName ?? "這個 App"
                    self.recordingStartBar.show(region: self.cocoaFrame(of: window), on: screen, settings: self.recordingSettings,
                                                windowAppName: appName,
                                                onStart: { [weak self] in self?.confirmRecording(.window(window)) }, onCancel: {})
                }
            } catch { present(error) }
        }
    }

    /// Starts after the optional countdown.
    private func confirmRecording(_ target: RecordingTarget) {
        guard !isRecording else { return }
        let begin = { [weak self] in
            guard let self else { return }
            Task { @MainActor in await self.beginRecording(target) }
        }
        if recordingSettings.countdown { countdown.start(seconds: 3, completion: begin) } else { begin() }
    }

    private func beginRecording(_ target: RecordingTarget) async {
        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            let options = recordingSettings.options
            let filter: SCContentFilter
            let sourceRect: CGRect?
            let pixelSize: CGSize
            let hudScreen: NSScreen
            let hudRegion: CGRect?
            switch target {
            case let .region(selection):
                let display = try await display(for: selection.screen)
                filter = try await captureService.displayFilter(for: display)
                sourceRect = selection.sourceRectPixels
                pixelSize = selection.sourceRectPixels.size
                hudScreen = selection.screen
                hudRegion = selection.rectInScreenPoints.offsetBy(dx: selection.screen.frame.minX, dy: selection.screen.frame.minY)
            case let .screen(screen):
                let display = try await display(for: screen)
                filter = try await captureService.displayFilter(for: display)
                sourceRect = nil
                pixelSize = CGSize(width: screen.frame.width * screen.backingScaleFactor, height: screen.frame.height * screen.backingScaleFactor)
                hudScreen = screen
                hudRegion = nil
            case let .window(window):
                // Follows the window even when it is covered or moved.
                filter = SCContentFilter(desktopIndependentWindow: window)
                sourceRect = nil
                let scale = CGFloat(filter.pointPixelScale)
                pixelSize = CGSize(width: window.frame.width * scale, height: window.frame.height * scale)
                hudScreen = screen(containing: window) ?? NSScreen.underMouse ?? NSScreen.main!
                hudRegion = nil
            }
            let audioFilter = try await soundFilter(for: target, content: content, enabled: options.systemAudio)

            let recorder = ScreenRecorder()
            recorder.onFailure = { [weak self] error in
                self?.abortRecording()
                self?.present(error)
            }
            try await recorder.start(filter: filter, sourceRect: sourceRect, pixelSize: pixelSize, options: options,
                                     audioFilter: audioFilter, to: try exporter.recordingURL())
            self.recorder = recorder
            recordingHUD.show(region: hudRegion, on: hudScreen,
                              onPause: { [weak recorder] paused in if paused { recorder?.pause() } else { recorder?.resume() } },
                              onStop: { [weak self] in self?.stopRecording() },
                              onCancel: { [weak self] in self?.cancelRecording() })
            onRecordingChange?(true)
        } catch {
            present(error)
        }
    }

    /// Whose sound to record. nil = whatever the video filter hears (all sound for display recordings).
    /// A chosen app that isn't running stops the recording instead of silently recording everything.
    private func soundFilter(for target: RecordingTarget, content: SCShareableContent, enabled: Bool) async throws -> SCContentFilter? {
        guard enabled, let anyDisplay = content.displays.first else { return nil }
        let own = ProcessInfo.processInfo.processIdentifier
        let allSound = { SCContentFilter(display: anyDisplay, excludingApplications: content.applications.filter { $0.processID == own }, exceptingWindows: []) }
        switch target {
        case let .window(window):
            guard recordingSettings.windowAudioFromAppOnly else { return allSound() }
            guard let app = window.owningApplication else { return allSound() }
            return SCContentFilter(display: anyDisplay, including: [app], exceptingWindows: [])
        case .region, .screen:
            guard let bundleID = recordingSettings.audioAppBundleID else { return nil }
            guard let app = content.applications.first(where: { $0.bundleIdentifier == bundleID }) else {
                throw RecordingSoundError.appNotRunning(RunningApp.name(for: bundleID))
            }
            return SCContentFilter(display: anyDisplay, including: [app], exceptingWindows: [])
        }
    }

    private func screen(containing window: SCWindow) -> NSScreen? {
        let frame = cocoaFrame(of: window)
        return NSScreen.screens.max { $0.frame.intersection(frame).width * $0.frame.intersection(frame).height < $1.frame.intersection(frame).width * $1.frame.intersection(frame).height }
    }

    /// SCWindow frames are top-left-origin global points.
    private func cocoaFrame(of window: SCWindow) -> CGRect {
        let primaryHeight = NSScreen.screens.first?.frame.height ?? 0
        return CGRect(x: window.frame.minX, y: primaryHeight - window.frame.maxY, width: window.frame.width, height: window.frame.height)
    }

    func stopRecording() {
        guard let recorder else { return }
        self.recorder = nil
        recordingHUD.hide()
        onRecordingChange?(false)
        Task { @MainActor in
            do { recordingPreview.show(try await recorder.stop()) } catch { present(error) }
        }
    }

    private func cancelRecording() {
        guard let recorder else { return }
        abortRecording()
        Task { await recorder.cancel() }
    }

    private func abortRecording() {
        recorder = nil
        recordingHUD.hide()
        onRecordingChange?(false)
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
        alert.messageText = "拾光需要「螢幕錄製」權限"
        alert.informativeText = "到「系統設定 → 隱私權與安全性 → 螢幕與系統錄音」打開拾光。\n打開之後要重新啟動 App 才會生效。"
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
