import AppKit
import SwiftUI

@MainActor
final class SettingsWindowController: NSObject, NSWindowDelegate {
    private var window: NSWindow?
    private let store: ShortcutStore

    init(store: ShortcutStore) { self.store = store }

    func show() {
        if window == nil {
            let window = NSWindow(contentRect: .zero, styleMask: [.titled, .closable], backing: .buffered, defer: false)
            window.title = "素顏 Shot 設定"
            window.isReleasedWhenClosed = false
            window.delegate = self
            window.contentView = NSHostingView(rootView: SettingsView(store: store))
            window.setContentSize(window.contentView?.fittingSize ?? NSSize(width: 460, height: 320))
            window.center()
            self.window = window
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    func windowWillClose(_ notification: Notification) { store.endRecording() }
}

private struct SettingsView: View {
    @ObservedObject var store: ShortcutStore

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("快捷鍵").font(.headline)

            Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 10) {
                ForEach(ShortcutAction.allCases, id: \.self) { action in
                    GridRow {
                        Text(action.title)
                        ShortcutRecorder(action: action, store: store)
                        if store.unavailable.contains(action) {
                            Image(systemName: "exclamationmark.triangle.fill")
                                .foregroundStyle(.yellow)
                                .help("這組快捷鍵已被其他程式使用，請換一組。")
                        }
                    }
                }
            }

            VStack(alignment: .leading, spacing: 6) {
                Text("點一下按鈕，再按下新的組合鍵。要包含 ⌘、⌥ 或 ⌃（F1–F12 可以單獨用）。按 Esc 取消，按 ⌫ 清除。")
                Text("⌘⇧3、⌘⇧4、⌘⇧5 是 macOS 內建的截圖快捷鍵。要用的話，先到「系統設定 → 鍵盤 → 鍵盤快速鍵 → 截圖」把系統的關掉。")
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)

            HStack {
                Spacer()
                Button("還原預設") { store.resetToDefaults() }
            }
        }
        .padding(20)
        .frame(width: 460)
    }
}

private struct ShortcutRecorder: View {
    let action: ShortcutAction
    @ObservedObject var store: ShortcutStore

    private var isRecording: Bool { store.recordingAction == action }

    var body: some View {
        HStack(spacing: 6) {
            Button {
                if isRecording { store.endRecording() } else { store.beginRecording(action) }
            } label: {
                Text(isRecording ? "請按下組合鍵…" : (store.shortcuts[action]?.displayString ?? "未設定"))
                    .font(.system(size: 13, weight: .medium, design: .rounded))
                    .foregroundStyle(isRecording ? Color.accentColor : (store.shortcuts[action] == nil ? .secondary : .primary))
                    .frame(width: 150, height: 22)
            }
            .buttonStyle(.bordered)

            Button {
                store.set(nil, for: action)
            } label: {
                Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .help("清除")
            .opacity(store.shortcuts[action] == nil || isRecording ? 0 : 1)
            .disabled(store.shortcuts[action] == nil || isRecording)
        }
    }
}
