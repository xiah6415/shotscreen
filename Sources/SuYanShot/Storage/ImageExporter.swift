import AppKit
import UniformTypeIdentifiers

struct ImageExporter {
    var defaultDirectory: URL

    init(defaultDirectory: URL = FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask).first ?? FileManager.default.temporaryDirectory) {
        self.defaultDirectory = defaultDirectory
    }

    func defaultFilename(at date: Date = .now) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd 'at' HH.mm.ss"
        return "Screenshot \(formatter.string(from: date)).png"
    }

    func exportPNG(_ image: CGImage, to directory: URL? = nil, filename: String? = nil, date: Date = .now) throws -> URL {
        let targetDirectory = directory ?? defaultDirectory
        try FileManager.default.createDirectory(at: targetDirectory, withIntermediateDirectories: true)
        let url = targetDirectory.appendingPathComponent(filename ?? defaultFilename(at: date))
        let representation = NSBitmapImageRep(cgImage: image)
        guard let data = representation.representation(using: .png, properties: [:]) else {
            throw CocoaError(.fileWriteUnknown)
        }
        try data.write(to: url, options: .atomic)
        return url
    }
}
