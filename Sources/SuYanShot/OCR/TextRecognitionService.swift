import CoreGraphics
import Foundation
import Vision

/// Vision runs on this actor's executor so recognition never blocks the selection UI.
actor TextRecognitionService {
    func recognize(_ image: CGImage) throws -> String {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true
        let supported = try request.supportedRecognitionLanguages()
        request.recognitionLanguages = ["zh-Hant", "en-US"].filter { supported.contains($0) }
        let handler = VNImageRequestHandler(cgImage: image, orientation: .up)
        try handler.perform([request])
        // Vision supplies reading order; preserve line breaks instead of joining words arbitrarily.
        return (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }
            .joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
