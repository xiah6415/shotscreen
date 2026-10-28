import AppKit

@main
struct OCRSmoke {
    @MainActor
    static func main() async throws {
        let context = CGContext(data: nil, width: 1200, height: 360, bitsPerComponent: 8,
                                bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
                                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        context.setFillColor(NSColor.white.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: 1200, height: 360))
        let blank = context.makeImage()!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
        let style: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 48), .foregroundColor: NSColor.black]
        ("Glean OCR 2026" as NSString).draw(at: CGPoint(x: 50, y: 240), withAttributes: style)
        ("繁體中文文字辨識" as NSString).draw(at: CGPoint(x: 50, y: 130), withAttributes: style)
        NSGraphicsContext.restoreGraphicsState()
        let recognizer = TextRecognitionService()
        let result = try await recognizer.recognize(context.makeImage()!)
        precondition(result.contains("Glean OCR 2026"), "English recognition failed: \(result)")
        precondition(result.contains("繁體中文文字辨識"), "Traditional Chinese recognition failed: \(result)")
        precondition(result.contains("\n"), "Line breaks were lost")
        let empty = try await recognizer.recognize(blank)
        precondition(empty.isEmpty, "Blank image should produce no text")
        print("OCR checks passed: English, Traditional Chinese, line breaks, blank image.")
    }
}
