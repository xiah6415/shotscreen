import Foundation
import Network
import SystemConfiguration

/// Identifies the network the Mac is on by its router (gateway), so phone sharing can run only on networks
/// the user allowed. Uses the router's MAC address when the ARP table has it (IP addresses like 192.168.1.1
/// repeat across many networks); this needs no Location permission, unlike reading the Wi-Fi name.
struct NetworkIdentity: Codable, Equatable, Identifiable {
    /// Router MAC address, or "ip:<router>" when the MAC is unknown.
    let id: String
    let routerIP: String
    var label: String

    static func current() -> NetworkIdentity? {
        guard let store = SCDynamicStoreCreate(nil, "Glean" as CFString, nil, nil),
              let global = SCDynamicStoreCopyValue(store, "State:/Network/Global/IPv4" as CFString) as? [String: Any],
              let router = global["Router"] as? String else { return nil }
        let mac = routerMAC(router)
        return NetworkIdentity(id: mac ?? "ip:\(router)", routerIP: router, label: "路由器 \(router)")
    }

    private static func routerMAC(_ ip: String) -> String? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/sbin/arp")
        process.arguments = ["-n", ip]
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = Pipe()
        guard (try? process.run()) != nil else { return nil }
        process.waitUntilExit()
        let output = String(data: pipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        // "? (192.168.1.1) at a4:2b:b0:12:34:56 on en7 ifscope [ethernet]"
        guard let range = output.range(of: #" at ([0-9a-f]{1,2}(:[0-9a-f]{1,2}){5})"#, options: .regularExpression) else { return nil }
        return String(output[range].dropFirst(4)).lowercased()
    }
}

/// Calls `onChange` whenever the Mac's network path changes (Wi-Fi switch, cable plugged in, sleep/wake).
@MainActor
final class NetworkChangeMonitor {
    var onChange: (() -> Void)?
    private let monitor = NWPathMonitor()

    func start() {
        monitor.pathUpdateHandler = { [weak self] _ in
            // Give DHCP/ARP a moment to settle before reading the router.
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) { self?.onChange?() }
        }
        monitor.start(queue: .main)
    }

    deinit { monitor.cancel() }
}
