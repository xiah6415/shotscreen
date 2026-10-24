import Foundation
import Network

/// Tiny HTTP/1.1 server (one request per connection) used to show clipboard history on a phone on the same network.
/// Connections from outside private address ranges are refused; authentication is up to the handler.
@MainActor
final class PhoneShareServer {
    struct Request {
        let method: String
        let path: String
        let query: [String: String]
        let headers: [String: String]
        let body: Data
        /// Client IP, for login rate limiting.
        let remoteAddress: String

        var cookies: [String: String] {
            var result: [String: String] = [:]
            for part in (headers["cookie"] ?? "").split(separator: ";") {
                let pair = part.split(separator: "=", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
                if pair.count == 2 { result[pair[0]] = pair[1] }
            }
            return result
        }
    }

    struct Response {
        var status: Int
        var contentType: String
        var body: Data
        var headers: [String: String] = [:]

        static func text(_ string: String, status: Int = 200, type: String = "text/plain; charset=utf-8") -> Response {
            Response(status: status, contentType: type, body: Data(string.utf8))
        }
    }

    enum State: Equatable {
        case stopped
        case starting
        case running(port: UInt16)
        case failed(String)
    }

    private(set) var state: State = .stopped { didSet { onStateChange?() } }
    var onStateChange: (() -> Void)?

    private let handler: (Request) -> Response
    private var listener: NWListener?
    private let maxRequestSize = 40 * 1024 * 1024

    init(handler: @escaping (Request) -> Response) { self.handler = handler }

    /// Listens on a fixed port so bookmarked / Home Screen links keep working.
    func start(port: UInt16) {
        stop()
        state = .starting
        do {
            let parameters = NWParameters.tcp
            parameters.allowLocalEndpointReuse = true
            let listener = try NWListener(using: parameters, on: NWEndpoint.Port(rawValue: port) ?? .any)
            listener.newConnectionHandler = { [weak self] connection in
                MainActor.assumeIsolated { self?.accept(connection) }
            }
            listener.stateUpdateHandler = { [weak self] newState in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    switch newState {
                    case .ready: self.state = .running(port: listener.port?.rawValue ?? 0)
                    case let .failed(error): self.state = .failed(error.localizedDescription)
                    default: break
                    }
                }
            }
            listener.start(queue: .main)
            self.listener = listener
        } catch {
            state = .failed(error.localizedDescription)
        }
    }

    func stop() {
        listener?.cancel()
        listener = nil
        if state != .stopped { state = .stopped }
    }

    // MARK: - Connections

    private func accept(_ connection: NWConnection) {
        guard Self.isLocal(connection.endpoint) else { connection.cancel(); return }
        connection.start(queue: .main)
        receive(on: connection, buffer: Data(), remote: Self.address(of: connection.endpoint))
    }

    private func receive(on connection: NWConnection, buffer: Data, remote: String) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 256 * 1024) { [weak self] data, _, isComplete, error in
            MainActor.assumeIsolated {
                guard let self else { connection.cancel(); return }
                var buffer = buffer
                if let data { buffer.append(data) }
                guard buffer.count <= self.maxRequestSize else {
                    self.send(.text("太大了", status: 413), on: connection)
                    return
                }
                switch Self.parse(buffer, remote: remote) {
                case .incomplete:
                    if isComplete || error != nil { connection.cancel() } else { self.receive(on: connection, buffer: buffer, remote: remote) }
                case .invalid:
                    self.send(.text("Bad Request", status: 400), on: connection)
                case let .request(request):
                    self.send(self.handler(request), on: connection)
                }
            }
        }
    }

    private func send(_ response: Response, on connection: NWConnection) {
        let reason = [200: "OK", 303: "See Other", 400: "Bad Request", 401: "Unauthorized", 403: "Forbidden", 404: "Not Found",
                      413: "Payload Too Large", 429: "Too Many Requests", 500: "Internal Server Error"][response.status] ?? "OK"
        var head = "HTTP/1.1 \(response.status) \(reason)\r\n"
        head += "Content-Type: \(response.contentType)\r\n"
        head += "Content-Length: \(response.body.count)\r\n"
        head += "Cache-Control: no-store\r\n"
        head += "X-Content-Type-Options: nosniff\r\n"
        head += "X-Frame-Options: DENY\r\n"
        for (name, value) in response.headers { head += "\(name): \(value)\r\n" }
        head += "Connection: close\r\n\r\n"
        connection.send(content: Data(head.utf8) + response.body, completion: .contentProcessed { _ in connection.cancel() })
    }

    // MARK: - Parsing

    private enum Parsed {
        case incomplete
        case invalid
        case request(Request)
    }

    private static func parse(_ buffer: Data, remote: String) -> Parsed {
        guard let headerEnd = buffer.range(of: Data("\r\n\r\n".utf8)) else { return .incomplete }
        guard let head = String(data: buffer[..<headerEnd.lowerBound], encoding: .utf8) else { return .invalid }
        var lines = head.components(separatedBy: "\r\n")
        let requestLine = lines.removeFirst().split(separator: " ")
        guard requestLine.count >= 2 else { return .invalid }
        var headers: [String: String] = [:]
        for line in lines {
            guard let colon = line.firstIndex(of: ":") else { continue }
            headers[line[..<colon].trimmingCharacters(in: .whitespaces).lowercased()] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
        }
        let length = Int(headers["content-length"] ?? "0") ?? 0
        let bodyStart = headerEnd.upperBound
        guard buffer.count - bodyStart >= length else { return .incomplete }
        guard let components = URLComponents(string: "http://local" + requestLine[1]) else { return .invalid }
        var query: [String: String] = [:]
        for item in components.queryItems ?? [] { query[item.name] = item.value ?? "" }
        return .request(Request(method: String(requestLine[0]), path: components.path, query: query, headers: headers,
                                body: buffer.subdata(in: bodyStart..<(bodyStart + length)), remoteAddress: remote))
    }

    // MARK: - Network helpers

    private static func address(of endpoint: NWEndpoint) -> String {
        guard case let .hostPort(host, _) = endpoint else { return "?" }
        return "\(host)"
    }

    /// Loopback, link-local and private (RFC 1918 / ULA) addresses only.
    private static func isLocal(_ endpoint: NWEndpoint) -> Bool {
        guard case let .hostPort(host, _) = endpoint else { return false }
        switch host {
        case let .ipv4(address):
            return isPrivate(ipv4: [UInt8](address.rawValue))
        case let .ipv6(address):
            let bytes = [UInt8](address.rawValue)
            guard bytes.count == 16 else { return false }
            if bytes[0..<10].allSatisfy({ $0 == 0 }) && bytes[10] == 0xff && bytes[11] == 0xff { return isPrivate(ipv4: Array(bytes[12..<16])) }
            return address == .loopback || (bytes[0] == 0xfe && bytes[1] & 0xc0 == 0x80) || (bytes[0] & 0xfe == 0xfc)
        default:
            return false
        }
    }

    private static func isPrivate(ipv4 bytes: [UInt8]) -> Bool {
        guard bytes.count == 4 else { return false }
        return bytes[0] == 10 || bytes[0] == 127
            || (bytes[0] == 172 && (16...31).contains(bytes[1]))
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254)
    }

    /// The Mac's IPv4 address on Wi-Fi/Ethernet that a phone on the same network can reach.
    static func localIPv4Address() -> String? {
        var pointer: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&pointer) == 0, let first = pointer else { return nil }
        defer { freeifaddrs(pointer) }
        var candidates: [(name: String, address: String)] = []
        for entry in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let flags = Int32(entry.pointee.ifa_flags)
            guard let socketAddress = entry.pointee.ifa_addr, socketAddress.pointee.sa_family == UInt8(AF_INET),
                  flags & IFF_UP != 0, flags & IFF_RUNNING != 0, flags & IFF_LOOPBACK == 0 else { continue }
            let name = String(cString: entry.pointee.ifa_name)
            guard name.hasPrefix("en") || name.hasPrefix("bridge") else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            guard getnameinfo(socketAddress, socklen_t(socketAddress.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 else { continue }
            candidates.append((name, String(cString: host)))
        }
        // A phone can reach RFC 1918 addresses; 169.254 link-local means that interface has no real network.
        let isHomeNetwork: (String) -> Bool = { address in
            let bytes = address.split(separator: ".").compactMap { UInt8($0) }
            return Self.isPrivate(ipv4: bytes) && bytes.first != 169 && bytes.first != 127
        }
        return (candidates.first { isHomeNetwork($0.address) && $0.name == "en0" }
            ?? candidates.first { isHomeNetwork($0.address) }
            ?? candidates.first)?.address
    }
}
