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

    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        frameRate = defaults.object(forKey: "recordingFrameRate") as? Int ?? 30
        showsCursor = defaults.object(forKey: "recordingShowsCursor") as? Bool ?? true
        systemAudio = defaults.object(forKey: "recordingSystemAudio") as? Bool ?? true
        microphone = defaults.object(forKey: "recordingMicrophone") as? Bool ?? false
        countdown = defaults.object(forKey: "recordingCountdown") as? Bool ?? true
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
