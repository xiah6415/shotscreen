import Foundation
import dnssd

/// Publishes `<name>.local` → this Mac's LAN address over Bonjour (multicast DNS), so phones can always open
/// `http://glean.local:8765` whichever Mac is running 拾光. If another device already owns the name, it retries as
/// `<name>-2.local`, `<name>-3.local`, … and reports the name actually in use.
@MainActor
final class LocalNameAdvertiser {
    /// Host name currently published (e.g. "glean.local"), or nil.
    private(set) var publishedName: String?
    var onChange: ((String?) -> Void)?

    private var connection: DNSServiceRef?
    private var record: DNSRecordRef?
    private var baseName = "glean"
    private var attempt = 1
    private var address: String?

    /// (Re)publishes `name` for `address` (dotted IPv4).
    func publish(name: String, address: String) {
        if name != baseName { attempt = 1 }
        baseName = name
        self.address = address
        register()
    }

    func stop() {
        if let connection { DNSServiceRefDeallocate(connection) }
        connection = nil
        record = nil
        setPublished(nil)
    }

    private var candidate: String { attempt == 1 ? baseName : "\(baseName)-\(attempt)" }

    private func register() {
        stop()
        guard let address, var ip = ipv4Bytes(address) else { return }
        var connection: DNSServiceRef?
        guard DNSServiceCreateConnection(&connection) == kDNSServiceErr_NoError, let connection else { return }
        self.connection = connection
        DNSServiceSetDispatchQueue(connection, .main)

        let fullName = candidate + ".local."
        var record: DNSRecordRef?
        let context = Unmanaged.passUnretained(self).toOpaque()
        let status = DNSServiceRegisterRecord(
            connection, &record, DNSServiceFlags(kDNSServiceFlagsUnique), 0, fullName,
            UInt16(kDNSServiceType_A), UInt16(kDNSServiceClass_IN), 4, &ip, 120,
            { _, _, _, error, context in
                guard let context else { return }
                let advertiser = Unmanaged<LocalNameAdvertiser>.fromOpaque(context).takeUnretainedValue()
                MainActor.assumeIsolated { advertiser.registrationFinished(error) }
            },
            context)
        guard status == kDNSServiceErr_NoError else { stop(); return }
        self.record = record
    }

    private func registrationFinished(_ error: DNSServiceErrorType) {
        switch error {
        case DNSServiceErrorType(kDNSServiceErr_NoError):
            setPublished(candidate + ".local")
        case DNSServiceErrorType(kDNSServiceErr_NameConflict) where attempt < 9:
            attempt += 1
            register()
        default:
            stop()
        }
    }

    private func setPublished(_ name: String?) {
        guard publishedName != name else { return }
        publishedName = name
        onChange?(name)
    }

    private func ipv4Bytes(_ address: String) -> [UInt8]? {
        let parts = address.split(separator: ".").compactMap { UInt8($0) }
        return parts.count == 4 ? parts : nil
    }

    /// Lowercase letters, digits and hyphens, as DNS labels allow.
    static func sanitize(_ name: String) -> String {
        let allowed = name.lowercased().map { $0.isLetter && $0.isASCII || $0.isNumber || $0 == "-" ? $0 : "-" }
        let trimmed = String(allowed).trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        return trimmed.isEmpty ? "glean" : String(trimmed.prefix(40))
    }
}
