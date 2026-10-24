import Foundation

/// Update check preferences, persisted in UserDefaults and edited in the settings window.
@MainActor
final class UpdateSettings: ObservableObject {
    @Published var automaticallyChecks: Bool { didSet { defaults.set(automaticallyChecks, forKey: "updateAutomaticallyChecks") } }
    /// The version the user chose 略過這個版本 for; automatic checks stay quiet about it.
    var skippedVersion: String? {
        get { defaults.string(forKey: "updateSkippedVersion") }
        set { defaults.set(newValue, forKey: "updateSkippedVersion") }
    }
    var lastCheck: Date? {
        get { defaults.object(forKey: "updateLastCheck") as? Date }
        set { defaults.set(newValue, forKey: "updateLastCheck") }
    }

    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        automaticallyChecks = defaults.object(forKey: "updateAutomaticallyChecks") as? Bool ?? true
    }
}
