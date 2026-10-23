import AppKit
import Combine
import SwiftUI

/// 初次設定: shown on first launch (and from the menu). Walks through permissions, shortcut conflicts,
/// whether to keep clipboard history, and launching at login.
@MainActor
final class WelcomeWindowController: NSObject, NSWindowDelegate {
    static let completedKey = "onboardingCompleted"

    private var window: NSWindow?
    private let model: WelcomeModel

    init(shortcuts: ShortcutStore, clipboard: ClipboardStore) {
        model = WelcomeModel(shortcuts: shortcuts, clipboard: clipboard)
    }

    static var isCompleted: Bool { UserDefaults.standard.bool(forKey: completedKey) }

    func show() {
        if window == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 540, height: 660), styleMask: [.titled, .closable], backing: .buffered, defer: false)
            window.title = "歡迎使用拾光"
            window.isReleasedWhenClosed = false
            window.delegate = self
            // Show up on whichever desktop the user is on, even if the app launched while another was active.
            window.collectionBehavior = [.moveToActiveSpace]
            window.contentView = NSHostingView(rootView: WelcomeView(model: model) { [weak self] in self?.finish() })
            window.center()
            self.window = window
        }
        model.startRefreshing()
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    private func finish() {
        UserDefaults.standard.set(true, forKey: Self.completedKey)
        window?.close()
    }

    func windowWillClose(_ notification: Notification) {
        model.stopRefreshing()
        // Closing counts as done too; the menu's 初次設定… brings it back.
        UserDefaults.standard.set(true, forKey: Self.completedKey)
    }
}

@MainActor
private final class WelcomeModel: ObservableObject {
    let shortcuts: ShortcutStore
    let clipboard: ClipboardStore
    @Published var screenRecording = PermissionsManager.canCaptureScreen
    @Published var accessibility = PermissionsManager.isAccessibilityTrusted
    @Published var launchAtLogin = LaunchAtLogin.isEnabled
    @Published var conflicts: [ShortcutAction] = []
    /// Set after asking for Screen Recording: macOS applies it only after a relaunch.
    @Published var askedForScreenRecording = false
    private var timer: Timer?
    private var observation: AnyCancellable?

    init(shortcuts: ShortcutStore, clipboard: ClipboardStore) {
        self.shortcuts = shortcuts
        self.clipboard = clipboard
        refresh()
        observation = shortcuts.objectWillChange.sink { [weak self] _ in DispatchQueue.main.async { self?.refresh() } }
    }

    func startRefreshing() {
        refresh()
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.refresh() }
        }
    }

    func stopRefreshing() {
        timer?.invalidate()
        timer = nil
    }

    func refresh() {
        screenRecording = PermissionsManager.canCaptureScreen
        accessibility = PermissionsManager.isAccessibilityTrusted
        launchAtLogin = LaunchAtLogin.isEnabled
        conflicts = SystemScreenshotShortcuts.conflicts(with: shortcuts.shortcuts)
    }
}

private struct WelcomeView: View {
    @ObservedObject var model: WelcomeModel
    @ObservedObject var clipboard: ClipboardStore
    let onDone: () -> Void

    init(model: WelcomeModel, onDone: @escaping () -> Void) {
        self.model = model
        self.clipboard = model.clipboard
        self.onDone = onDone
    }

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    HStack(spacing: 14) {
                        Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 60, height: 60)
                        VStack(alignment: .leading, spacing: 4) {
                            Text("歡迎使用拾光").font(.title2.weight(.bold))
                            Text("截圖、捲動截圖、錄影、標註和剪貼簿紀錄，都在選單列的圖示裡。花一分鐘把下面幾項設好。")
                                .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        }
                    }

                    StepRow(done: model.screenRecording, title: "螢幕錄製權限（必要）",
                            detail: model.screenRecording ? "已允許。" : "截圖和錄影都需要。按「允許」後在系統設定打開「拾光」，再按「重新啟動」。") {
                        if !model.screenRecording {
                            HStack {
                                Button("允許") {
                                    CGRequestScreenCaptureAccess()
                                    PermissionsManager.openScreenCaptureSettings()
                                    model.askedForScreenRecording = true
                                }
                                if model.askedForScreenRecording { Button("重新啟動") { PermissionsManager.relaunch() } }
                            }
                        }
                    }

                    StepRow(done: model.accessibility, title: "輔助使用權限（選用）",
                            detail: model.accessibility ? "已允許。" : "用在自動捲動，以及從剪貼簿紀錄直接貼上。不開的話，這兩項會改成你自己捲、自己按 ⌘V。") {
                        if !model.accessibility {
                            Button("允許") {
                                PermissionsManager.requestAccessibility()
                                PermissionsManager.openAccessibilitySettings()
                            }
                        }
                    }

                    StepRow(done: model.conflicts.isEmpty, title: "快捷鍵",
                            detail: shortcutDetail) {
                        if !model.conflicts.isEmpty {
                            VStack(alignment: .trailing, spacing: 6) {
                                Button("改用 ⌃⇧ 組合") { model.shortcuts.useControlShiftDefaults() }
                                Button("關掉系統截圖鍵…") { SystemScreenshotShortcuts.openSettings() }
                            }
                        }
                    }
                    shortcutList

                    StepRow(done: true, title: "剪貼簿紀錄", detail: "記錄你複製過的文字、圖片和檔案，按 \(display(.clipboard)) 叫出來。只存在這台 Mac，密碼管理工具的內容不會被記錄。") {
                        Toggle("", isOn: $clipboard.isEnabled).toggleStyle(.switch).labelsHidden()
                    }

                    StepRow(done: true, title: "開機時自動啟動", detail: "讓快捷鍵一開機就能用。") {
                        Toggle("", isOn: Binding(get: { model.launchAtLogin }, set: { LaunchAtLogin.set($0); model.refresh() }))
                            .toggleStyle(.switch).labelsHidden()
                    }
                }
                .padding(24)
            }

            Divider()
            HStack {
                Text("之後可以從選單列圖示 → 初次設定… 再打開這裡。").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("開始使用", action: onDone).buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
            }
            .padding(.horizontal, 24)
            .padding(.vertical, 14)
        }
        .frame(width: 540, height: 660)
    }

    private var shortcutDetail: String {
        if model.conflicts.isEmpty {
            return "主要的快捷鍵如下，都可以在 設定… 裡改。"
        }
        let names = model.conflicts.map(\.title).joined(separator: "、")
        return "「\(names)」跟 macOS 內建的截圖鍵一樣，按了會跑出系統截圖。可以一鍵改用 ⌃⇧ 開頭的組合，或到系統設定把系統的截圖鍵關掉。"
    }

    private var shortcutList: some View {
        let main: [ShortcutAction] = [.allInOne, .region, .scroll, .record, .clipboard]
        return VStack(alignment: .leading, spacing: 4) {
            ForEach(main, id: \.self) { action in
                HStack {
                    Text(action.title).foregroundStyle(.secondary)
                    Spacer()
                    Text(display(action)).font(.system(.body, design: .rounded).weight(.medium))
                        .foregroundStyle(model.conflicts.contains(action) ? Color.orange : .primary)
                }
                .font(.callout)
            }
            Text("⌘⇧V、⌘⇧R 在部分 App 裡另有用途（貼上並符合樣式、重新整理），被拾光拿來用時那些 App 的功能會失效；不習慣的話也可以改用 ⌃⇧ 組合。")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true).padding(.top, 4)
        }
        .padding(.leading, 38)
    }

    private func display(_ action: ShortcutAction) -> String {
        model.shortcuts.shortcuts[action]?.displayString ?? "未設定"
    }
}

private struct StepRow<Accessory: View>: View {
    let done: Bool
    let title: String
    let detail: String
    @ViewBuilder let accessory: () -> Accessory

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: done ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                .font(.system(size: 22))
                .foregroundStyle(done ? Color.green : Color.orange)
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(.headline)
                Text(detail).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 8)
            accessory()
        }
    }
}
