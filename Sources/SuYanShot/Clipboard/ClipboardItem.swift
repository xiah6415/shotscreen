import AppKit

/// One clipboard history entry. Small fields live in the index; image/RTF/HTML payloads are files next to it.
struct ClipboardItem: Codable, Identifiable, Equatable {
    enum Kind: String, Codable {
        case text, richText, image, file

        var title: String {
            switch self {
            case .text: "文字"
            case .richText: "格式文字"
            case .image: "圖片"
            case .file: "檔案"
            }
        }
    }

    let id: UUID
    var kind: Kind
    var createdAt: Date
    var pinned = false
    /// Plain text for text kinds.
    var text: String?
    var fileURLs: [URL]?
    var hasRTF = false
    var hasHTML = false
    var imagePixelSize: CGSize?
    var sourceAppName: String?
    var sourceBundleID: String?
    /// Identifies identical content so re-copying moves the old entry to the top instead of duplicating it.
    var contentHash: String

    var searchText: String {
        switch kind {
        case .text, .richText: text ?? ""
        case .file: (fileURLs ?? []).map(\.lastPathComponent).joined(separator: " ")
        case .image: "圖片 image"
        }
    }
}

/// What the monitor read from the pasteboard, before it is stored.
struct ClipboardSnapshot {
    var kind: ClipboardItem.Kind
    var text: String?
    var rtf: Data?
    var html: Data?
    var png: Data?
    var imagePixelSize: CGSize?
    var fileURLs: [URL]?
    var sourceAppName: String?
    var sourceBundleID: String?
}
