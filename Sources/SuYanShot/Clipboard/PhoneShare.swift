import AppKit
import CryptoKit

/// Serves the clipboard history to phones on the same network at a fixed address (`http://glean.local:8765`,
/// published over Bonjour), and accepts text/images sent back. A switch in the menu turns it on and it then
/// starts with the app. Devices sign in once with a password; it only runs on networks the user allowed.
@MainActor
final class PhoneShare: ObservableObject {
    enum Status: Equatable {
        case off
        case needsPassword
        case noNetwork
        case otherNetwork
        case starting
        case running
        case failed(String)
    }

    static let port: UInt16 = 8765

    @Published private(set) var status: Status = .off
    @Published private(set) var isEnabled: Bool
    @Published private(set) var localName: String
    @Published private(set) var restrictToHomeNetworks: Bool
    @Published private(set) var homeNetworks: [NetworkIdentity]
    @Published private(set) var currentNetwork: NetworkIdentity?
    @Published private(set) var publishedName: String?
    @Published private(set) var address: String?
    @Published private(set) var lastActivity: Date?
    let auth = PhoneShareAuth()
    /// Tells the menu bar whether sharing is live.
    var onServingChange: ((Bool) -> Void)?

    var isServing: Bool { status == .running }
    /// The address to bookmark: same on every Mac running 拾光 with the default name.
    var url: URL? { publishedName.flatMap { URL(string: "http://\($0):\(Self.port)/") } }
    /// Fallback if the phone can't resolve .local names.
    var ipURL: URL? { address.flatMap { URL(string: "http://\($0):\(Self.port)/") } }

    private let store: ClipboardStore
    /// Puts an entry on the Mac's pasteboard (without recording it again).
    private let putOnMacClipboard: (ClipboardItem) -> Void
    private let defaults: UserDefaults
    private lazy var server = PhoneShareServer { [weak self] request in
        self?.handle(request) ?? .text("Not Found", status: 404)
    }
    private let advertiser = LocalNameAdvertiser()
    private let networkMonitor = NetworkChangeMonitor()
    private var touchIcon: Data?

    init(store: ClipboardStore, defaults: UserDefaults = .standard, putOnMacClipboard: @escaping (ClipboardItem) -> Void) {
        self.store = store
        self.defaults = defaults
        self.putOnMacClipboard = putOnMacClipboard
        isEnabled = defaults.bool(forKey: "phoneShareEnabled")
        localName = LocalNameAdvertiser.sanitize(defaults.string(forKey: "phoneShareName") ?? "glean")
        restrictToHomeNetworks = defaults.object(forKey: "phoneShareHomeOnly") as? Bool ?? true
        homeNetworks = (defaults.data(forKey: "phoneShareHomeNetworks")).flatMap { try? JSONDecoder().decode([NetworkIdentity].self, from: $0) } ?? []
        server.onStateChange = { [weak self] in self?.serverStateChanged() }
        advertiser.onChange = { [weak self] name in self?.publishedName = name }
        networkMonitor.onChange = { [weak self] in self?.refresh() }
        networkMonitor.start()
        refresh()
    }

    // MARK: - Settings

    func setEnabled(_ enabled: Bool) {
        isEnabled = enabled
        defaults.set(enabled, forKey: "phoneShareEnabled")
        // First time on: trust the network the Mac is on now.
        if enabled, restrictToHomeNetworks, homeNetworks.isEmpty, let network = NetworkIdentity.current() {
            allow(network)
        }
        refresh()
    }

    func setLocalName(_ name: String) {
        localName = LocalNameAdvertiser.sanitize(name)
        defaults.set(localName, forKey: "phoneShareName")
        refresh()
    }

    func setRestrictToHomeNetworks(_ restrict: Bool) {
        restrictToHomeNetworks = restrict
        defaults.set(restrict, forKey: "phoneShareHomeOnly")
        refresh()
    }

    func allowCurrentNetwork() {
        if let network = NetworkIdentity.current() { allow(network) }
        refresh()
    }

    func removeNetwork(_ id: String) {
        homeNetworks.removeAll { $0.id == id }
        saveNetworks()
        refresh()
    }

    func setPassword(_ password: String) {
        auth.setPassword(password)
        refresh()
    }

    private func allow(_ network: NetworkIdentity) {
        guard !homeNetworks.contains(where: { $0.id == network.id }) else { return }
        homeNetworks.append(network)
        saveNetworks()
    }

    private func saveNetworks() {
        if let data = try? JSONEncoder().encode(homeNetworks) { defaults.set(data, forKey: "phoneShareHomeNetworks") }
    }

    // MARK: - Running

    /// Starts or stops serving to match the switch, the password and the current network.
    private func refresh() {
        currentNetwork = NetworkIdentity.current()
        guard isEnabled else { stopServing(.off); return }
        guard auth.hasPassword else { stopServing(.needsPassword); return }
        guard let currentNetwork, let address = PhoneShareServer.localIPv4Address() else { stopServing(.noNetwork); return }
        if restrictToHomeNetworks, !homeNetworks.contains(where: { $0.id == currentNetwork.id }) { stopServing(.otherNetwork); return }
        self.address = address
        switch server.state {
        case .stopped, .failed:
            setStatus(.starting)
            server.start(port: Self.port)
        case .starting, .running:
            break
        }
        advertiser.publish(name: localName, address: address)
    }

    private func stopServing(_ reason: Status) {
        server.stop()
        advertiser.stop()
        address = nil
        setStatus(reason)
    }

    private func setStatus(_ new: Status) {
        let wasServing = isServing
        status = new
        if wasServing != isServing { onServingChange?(isServing) }
    }

    private func serverStateChanged() {
        switch server.state {
        case .running:
            setStatus(.running)
        case let .failed(message):
            let inUse = message.localizedCaseInsensitiveContains("in use")
            setStatus(.failed(inUse ? "\(Self.port) 埠被其他程式佔用了。" : message))
        case .stopped, .starting:
            break
        }
    }

    // MARK: - Routes

    private static let cookieMaxAge = 365 * 24 * 60 * 60

    private func handle(_ request: PhoneShareServer.Request) -> PhoneShareServer.Response {
        let parts = request.path.split(separator: "/").map(String.init)
        let method = request.method

        // Public: what the login page and Home Screen need.
        if method == "GET", parts == ["apple-touch-icon.png"] { return touchIconResponse() }
        if method == "GET", parts == ["manifest.webmanifest"] {
            return .text(##"{"name":"拾光剪貼簿","short_name":"拾光","start_url":"/","display":"standalone","background_color":"#000000","icons":[{"src":"/apple-touch-icon.png","sizes":"180x180","type":"image/png"}]}"##,
                         type: "application/manifest+json")
        }
        if method == "POST" {
            // Cross-site forms can't set custom headers without a CORS preflight, which this server never answers.
            guard request.headers["x-requested-with"] == "glean" else { return .text("Forbidden", status: 403) }
        }
        if method == "POST", parts == ["api", "login"] { return login(request) }

        let device = request.cookies[PhoneShareAuth.cookieName].flatMap { auth.device(forToken: $0) }
        if method == "GET", parts.isEmpty {
            return .text(device == nil ? PhoneSharePage.loginHTML : PhoneSharePage.html, type: "text/html; charset=utf-8")
        }
        guard let device else { return .text(#"{"ok":false,"error":"login"}"#, status: 401, type: "application/json") }
        lastActivity = .now

        if method == "POST", parts == ["api", "logout"] {
            auth.revoke(device.id)
            var response = PhoneShareServer.Response.text(#"{"ok":true}"#, type: "application/json")
            response.headers["Set-Cookie"] = "\(PhoneShareAuth.cookieName)=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict"
            return response
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
            return .text(#"{"ok":true}"#, type: "application/json")
        default:
            return .text("Not Found", status: 404)
        }
    }

    private struct LoginBody: Decodable {
        let password: String
        let device: String?
    }

    private func login(_ request: PhoneShareServer.Request) -> PhoneShareServer.Response {
        guard let body = try? JSONDecoder().decode(LoginBody.self, from: request.body) else { return .text("Bad Request", status: 400) }
        switch auth.login(password: body.password, deviceName: body.device ?? "", from: request.remoteAddress) {
        case let .success(token):
            var response = PhoneShareServer.Response.text(#"{"ok":true}"#, type: "application/json")
            response.headers["Set-Cookie"] = "\(PhoneShareAuth.cookieName)=\(token); Path=/; Max-Age=\(Self.cookieMaxAge); HttpOnly; SameSite=Strict"
            return response
        case let .wrongPassword(remaining):
            return .text(#"{"ok":false,"error":"wrong","remaining":\#(remaining)}"#, status: 401, type: "application/json")
        case let .lockedOut(seconds):
            return .text(#"{"ok":false,"error":"locked","seconds":\#(seconds)}"#, status: 429, type: "application/json")
        }
    }

    /// 180 px PNG of the app icon for "Add to Home Screen".
    private func touchIconResponse() -> PhoneShareServer.Response {
        let bundled = Bundle.main.url(forResource: "AppIcon", withExtension: "icns").flatMap(NSImage.init(contentsOf:))
        if touchIcon == nil, let icon = bundled ?? NSApp.applicationIconImage {
            let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 180, pixelsHigh: 180, bitsPerSample: 8, samplesPerPixel: 4,
                                       hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
            if let rep {
                NSGraphicsContext.saveGraphicsState()
                NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
                icon.draw(in: NSRect(x: 0, y: 0, width: 180, height: 180))
                NSGraphicsContext.restoreGraphicsState()
                touchIcon = rep.representation(using: .png, properties: [:])
            }
        }
        guard let touchIcon else { return .text("Not Found", status: 404) }
        return PhoneShareServer.Response(status: 200, contentType: "image/png", body: touchIcon)
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
