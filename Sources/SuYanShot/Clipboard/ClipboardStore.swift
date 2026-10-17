import AppKit
import CryptoKit

/// Clipboard history on disk: `index.json` plus one payload file per image/RTF/HTML entry,
/// in ~/Library/Application Support/SuYanShot/Clipboard. Nothing leaves the Mac.
@MainActor
final class ClipboardStore: ObservableObject {
    static let limitOptions = [50, 100, 200, 500]

    @Published private(set) var items: [ClipboardItem] = []
    @Published var isEnabled: Bool { didSet { userDefaults.set(isEnabled, forKey: Keys.enabled) } }
    /// Unpinned entries kept; pinned ones never count and are never trimmed.
    @Published var limit: Int {
        didSet {
            userDefaults.set(limit, forKey: Keys.limit)
            trim()
            save()
        }
    }

    let directory: URL
    private let userDefaults: UserDefaults
    private let thumbnails = NSCache<NSUUID, NSImage>()
    private enum Keys {
        static let enabled = "clipboardEnabled"
        static let limit = "clipboardLimit"
    }

    init(directory: URL? = nil, userDefaults: UserDefaults = .standard) {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first ?? FileManager.default.temporaryDirectory
        self.directory = directory ?? base.appendingPathComponent("SuYanShot/Clipboard", isDirectory: true)
        self.userDefaults = userDefaults
        isEnabled = userDefaults.object(forKey: Keys.enabled) as? Bool ?? true
        limit = userDefaults.object(forKey: Keys.limit) as? Int ?? 200
        try? FileManager.default.createDirectory(at: self.directory, withIntermediateDirectories: true)
        load()
    }

    // MARK: - Adding

    func add(_ snapshot: ClipboardSnapshot) {
        let hash = Self.hash(of: snapshot)
        if let index = items.firstIndex(where: { $0.contentHash == hash }) {
            // Same content copied again: move it to the top, keep pin state and payload.
            var existing = items.remove(at: index)
            existing.createdAt = .now
            existing.sourceAppName = snapshot.sourceAppName ?? existing.sourceAppName
            existing.sourceBundleID = snapshot.sourceBundleID ?? existing.sourceBundleID
            items.insert(existing, at: 0)
            save()
            return
        }
        let item = ClipboardItem(
            id: UUID(), kind: snapshot.kind, createdAt: .now, text: snapshot.text, fileURLs: snapshot.fileURLs,
            hasRTF: snapshot.rtf != nil, hasHTML: snapshot.html != nil, imagePixelSize: snapshot.imagePixelSize,
            sourceAppName: snapshot.sourceAppName, sourceBundleID: snapshot.sourceBundleID, contentHash: hash)
        write(snapshot.rtf, for: item.id, ext: "rtf")
        write(snapshot.html, for: item.id, ext: "html")
        write(snapshot.png, for: item.id, ext: "png")
        items.insert(item, at: 0)
        trim()
        save()
    }

    /// Pasting an entry makes it the most recent one.
    func bump(_ id: UUID) {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return }
        var item = items.remove(at: index)
        item.createdAt = .now
        items.insert(item, at: 0)
        save()
    }

    func togglePin(_ id: UUID) {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return }
        items[index].pinned.toggle()
        trim()
        save()
    }

    func delete(_ id: UUID) {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return }
        removePayloads(for: items.remove(at: index))
        save()
    }

    /// Keeps pinned entries.
    func clearUnpinned() {
        items.filter { !$0.pinned }.forEach(removePayloads)
        items.removeAll { !$0.pinned }
        save()
    }

    // MARK: - Payloads

    func payload(_ id: UUID, ext: String) -> Data? { try? Data(contentsOf: payloadURL(id, ext: ext)) }

    func thumbnail(for item: ClipboardItem) -> NSImage? {
        if let cached = thumbnails.object(forKey: item.id as NSUUID) { return cached }
        guard item.kind == .image, let data = payload(item.id, ext: "png"),
              let source = CGImageSourceCreateWithData(data as CFData, nil),
              let thumb = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                  kCGImageSourceCreateThumbnailFromImageAlways: true,
                  kCGImageSourceThumbnailMaxPixelSize: 480
              ] as CFDictionary) else { return nil }
        let image = NSImage(cgImage: thumb, size: NSSize(width: thumb.width, height: thumb.height))
        thumbnails.setObject(image, forKey: item.id as NSUUID)
        return image
    }

    private func payloadURL(_ id: UUID, ext: String) -> URL { directory.appendingPathComponent("\(id.uuidString).\(ext)") }

    private func write(_ data: Data?, for id: UUID, ext: String) {
        guard let data else { return }
        try? data.write(to: payloadURL(id, ext: ext), options: .atomic)
    }

    private func removePayloads(for item: ClipboardItem) {
        for ext in ["rtf", "html", "png"] { try? FileManager.default.removeItem(at: payloadURL(item.id, ext: ext)) }
        thumbnails.removeObject(forKey: item.id as NSUUID)
    }

    // MARK: - Persistence

    private var indexURL: URL { directory.appendingPathComponent("index.json") }

    private func load() {
        guard let data = try? Data(contentsOf: indexURL),
              let decoded = try? JSONDecoder().decode([ClipboardItem].self, from: data) else { return }
        items = decoded
    }

    private func save() {
        guard let data = try? JSONEncoder().encode(items) else { return }
        try? data.write(to: indexURL, options: .atomic)
    }

    private func trim() {
        var unpinned = 0
        var kept: [ClipboardItem] = []
        for item in items {
            if item.pinned { kept.append(item); continue }
            unpinned += 1
            if unpinned <= limit { kept.append(item) } else { removePayloads(for: item) }
        }
        items = kept
    }

    static func hash(of snapshot: ClipboardSnapshot) -> String {
        var hasher = SHA256()
        hasher.update(data: Data(snapshot.kind.rawValue.utf8))
        switch snapshot.kind {
        case .text, .richText: hasher.update(data: Data((snapshot.text ?? "").utf8))
        case .image: hasher.update(data: snapshot.png ?? Data())
        case .file: hasher.update(data: Data((snapshot.fileURLs ?? []).map(\.path).joined(separator: "\n").utf8))
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }
}
