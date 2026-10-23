import AppKit
import SwiftUI

/// "Whose sound is recorded": all sound, or one running app. For window recordings: the window's app or all sound.
struct AudioSourcePicker: View {
    @ObservedObject var settings: RecordingSettings
    /// Set for window recordings: the name of the window's app.
    var windowAppName: String?
    @State private var apps: [RunningApp] = []

    var body: some View {
        Menu {
            if let windowAppName {
                Toggle("只錄 \(windowAppName)", isOn: Binding(get: { settings.windowAudioFromAppOnly }, set: { if $0 { settings.windowAudioFromAppOnly = true } }))
                Toggle("所有聲音", isOn: Binding(get: { !settings.windowAudioFromAppOnly }, set: { if $0 { settings.windowAudioFromAppOnly = false } }))
            } else {
                Toggle("所有聲音", isOn: Binding(get: { settings.audioAppBundleID == nil }, set: { if $0 { settings.audioAppBundleID = nil } }))
                Divider()
                Section("只錄這個 App 的聲音") {
                    ForEach(apps) { app in
                        Toggle(app.name, isOn: Binding(get: { settings.audioAppBundleID == app.id }, set: { if $0 { settings.audioAppBundleID = app.id } }))
                    }
                }
            }
        } label: {
            HStack(spacing: 4) {
                Image(systemName: "waveform")
                Text(title).lineLimit(1).truncationMode(.tail)
            }
            .font(.system(size: 12, weight: .medium))
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .frame(maxWidth: 170)
        .disabled(!settings.systemAudio)
        .opacity(settings.systemAudio ? 1 : 0.4)
        .help("選擇要錄誰的聲音")
        .onAppear { apps = RunningApp.current() }
    }

    private var title: String {
        if let windowAppName { return settings.windowAudioFromAppOnly ? "只錄 \(windowAppName)" : "所有聲音" }
        guard let id = settings.audioAppBundleID else { return "所有聲音" }
        return "只錄 \(RunningApp.name(for: id))"
    }
}

struct RunningApp: Identifiable {
    /// Bundle identifier.
    let id: String
    let name: String

    /// Regular (Dock) apps that are running now, by name.
    static func current() -> [RunningApp] {
        let own = ProcessInfo.processInfo.processIdentifier
        let apps = NSWorkspace.shared.runningApplications.compactMap { app -> RunningApp? in
            guard app.activationPolicy == .regular, app.processIdentifier != own, let id = app.bundleIdentifier else { return nil }
            return RunningApp(id: id, name: app.localizedName ?? id)
        }
        return Dictionary(apps.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first }).values
            .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    /// Display name for a bundle ID, even if the app is not running.
    static func name(for bundleID: String) -> String {
        if let running = NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).first?.localizedName { return running }
        if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) {
            return FileManager.default.displayName(atPath: url.path).replacingOccurrences(of: ".app", with: "")
        }
        return bundleID
    }
}

/// Capsule on/off button used for 系統聲音 / 麥克風.
struct SoundToggle: View {
    let title: String
    let on: String
    let off: String
    @Binding var isOn: Bool
    @Environment(\.isEnabled) private var isEnabled

    var body: some View {
        Button { isOn.toggle() } label: {
            HStack(spacing: 5) {
                Image(systemName: isOn ? on : off).frame(width: 16)
                Text(title).font(.system(size: 12, weight: .medium))
            }
            .padding(.horizontal, 10)
            .frame(height: 28)
            .background(isOn ? Color.accentColor.opacity(0.2) : Color.primary.opacity(0.06), in: Capsule())
            .foregroundStyle(isOn ? Color.accentColor : .secondary)
            .opacity(isEnabled ? 1 : 0.4)
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
    }
}

/// 系統聲音 · source · 麥克風, as shown before recording starts.
struct RecordingSoundControls: View {
    @ObservedObject var settings: RecordingSettings
    var windowAppName: String?

    var body: some View {
        HStack(spacing: 6) {
            SoundToggle(title: "系統聲音", on: "speaker.wave.2.fill", off: "speaker.slash", isOn: $settings.systemAudio)
                .help("錄下電腦播放的聲音")
            AudioSourcePicker(settings: settings, windowAppName: windowAppName)
            SoundToggle(title: "麥克風", on: "mic.fill", off: "mic.slash", isOn: $settings.microphone)
                .disabled(!RecordingSettings.microphoneSupported)
                .help(RecordingSettings.microphoneSupported ? "錄下你的聲音（第一次會請你允許使用麥克風）" : "需要 macOS 15 以上")
        }
    }
}
