import AppKit

/// Distinct native sounds, played only after a successful capture or OCR copy.
@MainActor
enum CompletionSound {
    private static let screenshotSound = NSSound(named: "Pop")
    private static let ocrSound = NSSound(named: "Tink")

    static func screenshot() {
        screenshotSound?.stop()
        screenshotSound?.play()
    }

    static func ocr() {
        ocrSound?.stop()
        ocrSound?.play()
    }
}
