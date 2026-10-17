import AppKit
import CryptoKit

/// Serves the clipboard history to a phone browser on the same network, and accepts text/images sent back.
/// Runs only after the user starts it, and stops by itself after 30 idle minutes.
@MainActor
final class PhoneShare: ObservableObject {
    @Published private(set) var isRunning = false
    @Published private(set) var url: URL?
    @Published private(set) var problem: String?
    @Published private(set) var lastActivity: Date?

    static let idleLimit: TimeInterval = 30 * 60

    private let store: ClipboardStore
    /// Puts an entry on the Mac's pasteboard (without recording it again).
    private let putOnMacClipboard: (ClipboardItem) -> Void
    private lazy var server = PhoneShareServer { [weak self] request in
        self?.handle(request) ?? .text("Not Found", status: 404)
    }
    private var idleTimer: Timer?

    init(store: ClipboardStore, putOnMacClipboard: @escaping (ClipboardItem) -> Void) {
        self.store = store
        self.putOnMacClipboard = putOnMacClipboard
        server.onStateChange = { [weak self] in self?.serverStateChanged() }
        server.onActivity = { [weak self] in
            self?.lastActivity = .now
            self?.scheduleIdleStop()
        }
    }

    func start() {
        guard !isRunning else { return }
        problem = nil
        server.start()
        scheduleIdleStop()
    }

    func stop() {
        idleTimer?.invalidate()
        idleTimer = nil
        server.stop()
    }

    func regenerateLink() {
        server.regenerateToken()
    }

    private func serverStateChanged() {
        switch server.state {
        case let .running(port):
            isRunning = true
            if let address = PhoneShareServer.localIPv4Address() {
                url = URL(string: "http://\(address):\(port)/?t=\(server.token)")
                problem = nil
            } else {
                url = nil
                problem = "找不到區域網路位址，請確認這台 Mac 已連上 Wi-Fi 或有線網路。"
            }
        case let .failed(message):
            isRunning = false
            url = nil
            problem = "無法開始分享：\(message)"
        case .stopped:
            isRunning = false
            url = nil
        case .starting:
            break
        }
    }

    private func scheduleIdleStop() {
        idleTimer?.invalidate()
        idleTimer = Timer.scheduledTimer(withTimeInterval: Self.idleLimit, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.stop() }
        }
    }

    // MARK: - Routes

    private func handle(_ request: PhoneShareServer.Request) -> PhoneShareServer.Response {
        let parts = request.path.split(separator: "/").map(String.init)
        let method = request.method
        if method == "GET", parts.isEmpty {
            return .text(PhoneSharePage.html, type: "text/html; charset=utf-8")
        }
        if method == "GET", parts == ["api", "items"] {
            return itemsResponse(knownVersion: request.query["v"])
        }
        if method == "POST", parts == ["api", "send"] {
            return receive(request)
        }
        // /api/items/<id>/image and /api/items/<id>/to-mac
        guard parts.count == 4, parts[0] == "api", parts[1] == "items", let item = item(parts[2]) else {
            return .text("Not Found", status: 404)
        }
        switch (method, parts[3]) {
        case ("GET", "image"):
            guard item.kind == .image else { return .text("Not Found", status: 404) }
            if request.query["thumb"] != nil, let thumbnail = store.thumbnail(for: item),
               let tiff = thumbnail.tiffRepresentation, let jpeg = NSBitmapImageRep(data: tiff)?.representation(using: .jpeg, properties: [.compressionFactor: 0.8]) {
                return PhoneShareServer.Response(status: 200, contentType: "image/jpeg", body: jpeg)
            }
            guard let png = store.payload(item.id, ext: "png") else { return .text("Not Found", status: 404) }
            return PhoneShareServer.Response(status: 200, contentType: "image/png", body: png)
        case ("POST", "to-mac"):
            putOnMacClipboard(item)
            store.bump(item.id)
            return .text("{\"ok\":true}", type: "application/json")
        default:
            return .text("Not Found", status: 404)
        }
    }

    private func item(_ id: String) -> ClipboardItem? {
        UUID(uuidString: id).flatMap { uuid in store.items.first { $0.id == uuid } }
    }

    /// Text or an image from the phone: recorded in history (source 手機) and put on the Mac pasteboard.
    private func receive(_ request: PhoneShareServer.Request) -> PhoneShareServer.Response {
        let type = request.headers["content-type"] ?? ""
        let snapshot: ClipboardSnapshot
        if type.hasPrefix("image/") {
            // ImageIO also reads HEIC, which iPhones may upload as-is.
            guard let source = CGImageSourceCreateWithData(request.body as CFData, nil),
                  let image = CGImageSourceCreateImageAtIndex(source, 0, [kCGImageSourceCreateThumbnailWithTransform: true] as CFDictionary),
                  let png = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else {
                return .text("{\"ok\":false,\"error\":\"看不懂這張圖片\"}", status: 400, type: "application/json")
            }
            snapshot = ClipboardSnapshot(kind: .image, png: png, imagePixelSize: CGSize(width: image.width, height: image.height), sourceAppName: "手機")
        } else {
            guard let text = String(data: request.body, encoding: .utf8), !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                return .text("{\"ok\":false,\"error\":\"沒有內容\"}", status: 400, type: "application/json")
            }
            snapshot = ClipboardSnapshot(kind: .text, text: text, sourceAppName: "手機")
        }
        store.add(snapshot)
        if let newest = store.items.first { putOnMacClipboard(newest) }
        return .text("{\"ok\":true}", type: "application/json")
    }

    // MARK: - JSON

    private struct WebItem: Encodable {
        let id: UUID
        let kind: String
        let text: String?
        let truncated: Bool
        let createdAt: Double
        let pinned: Bool
        let tags: [UUID]
        let source: String?
        let width: Int?
        let height: Int?
        let files: [String]?
    }

    private struct WebTag: Encodable {
        let id: UUID
        let name: String
        let color: String
    }

    private struct Payload: Encodable {
        let version: String
        let unchanged: Bool
        let items: [WebItem]?
        let tags: [WebTag]?
    }

    private static let maxTextLength = 100_000

    /// The phone polls with the last version it saw; an unchanged history answers with no items.
    private func itemsResponse(knownVersion: String?) -> PhoneShareServer.Response {
        let version = historyVersion()
        let payload: Payload
        if knownVersion == version {
            payload = Payload(version: version, unchanged: true, items: nil, tags: nil)
        } else {
            let items = store.items.map { item -> WebItem in
                let text = item.text.map { $0.count > Self.maxTextLength ? String($0.prefix(Self.maxTextLength)) : $0 }
                return WebItem(id: item.id, kind: item.kind.rawValue, text: text, truncated: (item.text?.count ?? 0) > Self.maxTextLength,
                               createdAt: item.createdAt.timeIntervalSince1970 * 1000, pinned: item.pinned, tags: item.tags,
                               source: item.sourceAppName, width: item.imagePixelSize.map { Int($0.width) },
                               height: item.imagePixelSize.map { Int($0.height) }, files: item.fileURLs?.map(\.lastPathComponent))
            }
            let tags = store.tags.map { WebTag(id: $0.id, name: $0.name, color: Self.hex($0.color)) }
            payload = Payload(version: version, unchanged: false, items: items, tags: tags)
        }
        let data = (try? JSONEncoder().encode(payload)) ?? Data("{}".utf8)
        return PhoneShareServer.Response(status: 200, contentType: "application/json", body: data)
    }

    private func historyVersion() -> String {
        var hasher = SHA256()
        for item in store.items {
            hasher.update(data: Data("\(item.id)\(item.createdAt.timeIntervalSince1970)\(item.pinned)\(item.tags)".utf8))
        }
        for tag in store.tags { hasher.update(data: Data("\(tag.id)\(tag.name)\(tag.colorIndex)".utf8)) }
        return hasher.finalize().prefix(8).map { String(format: "%02x", $0) }.joined()
    }

    private static func hex(_ color: NSColor) -> String {
        guard let rgb = color.usingColorSpace(.sRGB) else { return "#888888" }
        return String(format: "#%02x%02x%02x", Int(rgb.redComponent * 255), Int(rgb.greenComponent * 255), Int(rgb.blueComponent * 255))
    }
}
