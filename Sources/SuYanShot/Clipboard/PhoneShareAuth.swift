import CommonCrypto
import CryptoKit
import Foundation

/// Password login for phone sharing. The URL carries no secret: a device enters the password once and gets a
/// long-lived session cookie. Only hashes are stored — PBKDF2 for the password, SHA-256 for session tokens —
/// so reading the preferences file doesn't let anyone in.
@MainActor
final class PhoneShareAuth: ObservableObject {
    struct Device: Codable, Identifiable, Equatable {
        let id: UUID
        var name: String
        let tokenHash: String
        let createdAt: Date
        var lastSeen: Date
    }

    @Published private(set) var devices: [Device] = []
    @Published private(set) var hasPassword = false

    static let minimumPasswordLength = 6
    static let cookieName = "glean_session"

    private let defaults: UserDefaults
    private let passwordKey = "phoneSharePassword"
    private let devicesKey = "phoneShareDevices"
    private let iterations: UInt32 = 200_000

    /// Failed logins per source address, for a short lockout after repeated wrong passwords.
    private var failures: [String: (count: Int, lockedUntil: Date?)] = [:]
    private let maxFailures = 5
    private let lockout: TimeInterval = 60

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        hasPassword = defaults.dictionary(forKey: passwordKey) != nil
        if let data = defaults.data(forKey: devicesKey), let decoded = try? JSONDecoder().decode([Device].self, from: data) {
            devices = decoded
        }
    }

    // MARK: - Password

    /// Sets or changes the password. Changing it signs every device out.
    func setPassword(_ password: String) {
        let salt = Self.randomBytes(16)
        defaults.set(["salt": salt.base64EncodedString(), "hash": derive(password, salt: salt).base64EncodedString(),
                      "iterations": Int(iterations)], forKey: passwordKey)
        hasPassword = true
        devices = []
        saveDevices()
    }

    func checkPassword(_ password: String) -> Bool {
        guard let stored = defaults.dictionary(forKey: passwordKey),
              let salt = (stored["salt"] as? String).flatMap({ Data(base64Encoded: $0) }),
              let hash = (stored["hash"] as? String).flatMap({ Data(base64Encoded: $0) }) else { return false }
        let rounds = UInt32(stored["iterations"] as? Int ?? Int(iterations))
        return Self.constantTimeEqual(derive(password, salt: salt, rounds: rounds), hash)
    }

    // MARK: - Login

    enum LoginResult {
        case success(token: String)
        case wrongPassword(remaining: Int)
        case lockedOut(seconds: Int)
    }

    func login(password: String, deviceName: String, from address: String) -> LoginResult {
        if let lockedUntil = failures[address]?.lockedUntil, lockedUntil > .now {
            return .lockedOut(seconds: Int(lockedUntil.timeIntervalSinceNow.rounded(.up)))
        }
        guard checkPassword(password) else {
            var entry = failures[address] ?? (0, nil)
            entry.count += 1
            if entry.count >= maxFailures { entry = (0, Date.now.addingTimeInterval(lockout)) }
            failures[address] = entry
            if let lockedUntil = entry.lockedUntil { return .lockedOut(seconds: Int(lockedUntil.timeIntervalSinceNow.rounded(.up))) }
            return .wrongPassword(remaining: maxFailures - entry.count)
        }
        failures[address] = nil
        let token = Self.randomBytes(32).map { String(format: "%02x", $0) }.joined()
        let name = deviceName.trimmingCharacters(in: .whitespacesAndNewlines)
        devices.append(Device(id: UUID(), name: name.isEmpty ? "未命名裝置" : String(name.prefix(40)),
                              tokenHash: Self.sha256(token), createdAt: .now, lastSeen: .now))
        saveDevices()
        return .success(token: token)
    }

    /// The signed-in device for a session token, refreshing its last-seen time (saved at most once a minute).
    func device(forToken token: String) -> Device? {
        let hash = Self.sha256(token)
        guard let index = devices.firstIndex(where: { Self.constantTimeEqual(Data($0.tokenHash.utf8), Data(hash.utf8)) }) else { return nil }
        if Date.now.timeIntervalSince(devices[index].lastSeen) > 60 {
            devices[index].lastSeen = .now
            saveDevices()
        }
        return devices[index]
    }

    func revoke(_ id: UUID) {
        devices.removeAll { $0.id == id }
        saveDevices()
    }

    func revokeAll() {
        devices = []
        saveDevices()
    }

    // MARK: - Helpers

    private func saveDevices() {
        if let data = try? JSONEncoder().encode(devices) { defaults.set(data, forKey: devicesKey) }
    }

    private func derive(_ password: String, salt: Data, rounds: UInt32? = nil) -> Data {
        var output = Data(count: 32)
        let passwordData = Data(password.utf8)
        _ = output.withUnsafeMutableBytes { out in
            salt.withUnsafeBytes { saltBytes in
                passwordData.withUnsafeBytes { passwordBytes in
                    CCKeyDerivationPBKDF(CCPBKDFAlgorithm(kCCPBKDF2),
                                         passwordBytes.bindMemory(to: CChar.self).baseAddress, passwordData.count,
                                         saltBytes.bindMemory(to: UInt8.self).baseAddress, salt.count,
                                         CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), rounds ?? iterations,
                                         out.bindMemory(to: UInt8.self).baseAddress, 32)
                }
            }
        }
        return output
    }

    private static func randomBytes(_ count: Int) -> Data {
        Data((0..<count).map { _ in UInt8.random(in: 0...255) })
    }

    static func sha256(_ string: String) -> String {
        SHA256.hash(data: Data(string.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    private static func constantTimeEqual(_ a: Data, _ b: Data) -> Bool {
        guard a.count == b.count else { return false }
        return zip(a, b).reduce(0) { $0 | ($1.0 ^ $1.1) } == 0
    }
}
