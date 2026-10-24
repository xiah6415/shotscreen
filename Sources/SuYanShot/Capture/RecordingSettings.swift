import Foundation

/// Screen recording preferences, persisted in UserDefaults and edited in the settings window.
@MainActor
final class RecordingSettings: ObservableObject {
    static let frameRates = [30, 60]
    static var microphoneSupported: Bool { if #available(macOS 15.0, *) { true } else { false } }

    @Published var frameRate: Int { didSet { defaults.set(frameRate, forKey: "recordingFrameRate") } }
    @Published var showsCursor: Bool { didSet { defaults.set(showsCursor, forKey: "recordingShowsCursor") } }
    @Published var systemAudio: Bool { didSet { defaults.set(systemAudio, forKey: "recordingSystemAudio") } }
    @Published var microphone: Bool { didSet { defaults.set(microphone, forKey: "recordingMicrophone") } }
    @Published var countdown: Bool { didSet { defaults.set(countdown, forKey: "recordingCountdown") } }
    /// The only app whose sound is recorded for region / full-screen recordings; nil records all sound.
    @Published var audioAppBundleID: String? { didSet { defaults.set(audioAppBundleID, forKey: "recordingAudioApp") } }
    /// Window recordings: record only the window's own app (default) instead of all sound.
    @Published var windowAudioFromAppOnly: Bool { didSet { defaults.set(windowAudioFromAppOnly, forKey: "recordingWindowAudioAppOnly") } }

    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        frameRate = defaults.object(forKey: "recordingFrameRate") as? Int ?? 30
        showsCursor = defaults.object(forKey: "recordingShowsCursor") as? Bool ?? true
        systemAudio = defaults.object(forKey: "recordingSystemAudio") as? Bool ?? true
        microphone = defaults.object(forKey: "recordingMicrophone") as? Bool ?? false
        countdown = defaults.object(forKey: "recordingCountdown") as? Bool ?? true
        audioAppBundleID = defaults.string(forKey: "recordingAudioApp")
        windowAudioFromAppOnly = defaults.object(forKey: "recordingWindowAudioAppOnly") as? Bool ?? true
    }

    var options: RecordingOptions {
        RecordingOptions(frameRate: frameRate, showsCursor: showsCursor, systemAudio: systemAudio,
                         microphone: microphone && Self.microphoneSupported)
    }
}

struct RecordingOptions {
    var frameRate: Int
    var showsCursor: Bool
    var systemAudio: Bool
    var microphone: Bool
}

enum RecordingSoundError: LocalizedError {
    case appNotRunning(String)

    var errorDescription: String? {
        switch self {
        case let .appNotRunning(name): "設定成只錄「\(name)」的聲音，但它沒有在執行。"
        }
    }

    var recoverySuggestion: String? { "請先打開它，或在錄影工具列把聲音來源改成「所有聲音」。為了不錄到其他聲音，這次沒有開始錄影。" }
}
