import AVFoundation
import ImageIO
import UniformTypeIdentifiers

/// Converts a recording into a looping GIF next to it (same name, .gif).
enum GIFExporter {
    /// Long clips get a lower frame rate so the GIF stays a reasonable size.
    static let maxFrames = 900

    static func export(_ video: URL, frameRate: Double = 12, maxWidth: CGFloat = 800) async throws -> URL {
        let asset = AVURLAsset(url: video)
        let duration = try await asset.load(.duration).seconds
        guard duration.isFinite, duration > 0 else { throw RecordingError.noFrames }
        let fps = min(frameRate, Double(maxFrames) / duration)
        let count = max(1, Int((duration * fps).rounded(.down)))
        let times = (0..<count).map { CMTime(seconds: Double($0) / fps, preferredTimescale: 600) }

        let generator = AVAssetImageGenerator(asset: asset)
        generator.appliesPreferredTrackTransform = true
        generator.maximumSize = CGSize(width: maxWidth, height: maxWidth * 4)
        generator.requestedTimeToleranceBefore = CMTime(seconds: 0.5 / fps, preferredTimescale: 600)
        generator.requestedTimeToleranceAfter = CMTime(seconds: 0.5 / fps, preferredTimescale: 600)

        let output = video.deletingPathExtension().appendingPathExtension("gif")
        try? FileManager.default.removeItem(at: output)
        guard let destination = CGImageDestinationCreateWithURL(output as CFURL, UTType.gif.identifier as CFString, count, nil) else {
            throw RecordingError.writerFailed("無法建立 GIF")
        }
        CGImageDestinationSetProperties(destination, [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFLoopCount: 0]] as CFDictionary)
        let delay = 1 / fps
        let frameProperties = [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFDelayTime: delay, kCGImagePropertyGIFUnclampedDelayTime: delay]] as CFDictionary

        // Recordings of a still screen have no frames for a while; repeat the last one so timing is kept.
        var added = 0
        var previous: CGImage?
        for await result in generator.images(for: times) {
            guard let image = (try? result.image) ?? previous else { continue }
            CGImageDestinationAddImage(destination, image, frameProperties)
            previous = image
            added += 1
        }
        guard added > 0, CGImageDestinationFinalize(destination) else {
            try? FileManager.default.removeItem(at: output)
            throw RecordingError.writerFailed("GIF 轉檔失敗")
        }
        return output
    }
}
