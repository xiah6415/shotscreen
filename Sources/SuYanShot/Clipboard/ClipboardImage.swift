import AppKit
import UniformTypeIdentifiers

/// Reads an image off the pasteboard for 標註剪貼簿裡的圖片: copied image data (PNG first, which keeps transparency,
/// then TIFF and the other image types), or an image file copied in Finder.
enum ClipboardImage {
    static func read(from pasteboard: NSPasteboard = .general) -> CGImage? {
        if let urls = pasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] {
            for url in urls where UTType(filenameExtension: url.pathExtension)?.conforms(to: .image) == true {
                if let image = decode(CGImageSourceCreateWithURL(url as CFURL, nil)) { return image }
            }
        }
        let preferred: [NSPasteboard.PasteboardType] = [.png, .tiff]
        let others = (pasteboard.types ?? []).filter { !preferred.contains($0) && UTType($0.rawValue)?.conforms(to: .image) == true }
        for type in preferred + others {
            if let data = pasteboard.data(forType: type), let image = decode(data) { return image }
        }
        return nil
    }

    static func decode(_ data: Data) -> CGImage? { decode(CGImageSourceCreateWithData(data as CFData, nil)) }

    private static func decode(_ source: CGImageSource?) -> CGImage? {
        guard let source, CGImageSourceGetCount(source) > 0 else { return nil }
        // Apply the EXIF orientation (photos from a phone) so the editor shows the image upright.
        return CGImageSourceCreateThumbnailAtIndex(source, 0, [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: 16384
        ] as CFDictionary)
    }
}
