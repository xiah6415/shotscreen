import CoreGraphics
import Foundation

/// Stitches frames of a fixed screen region, captured while the user scrolls down, into one tall image.
///
/// Each frame is compared with the last accepted frame to find how many pixel rows the content moved.
/// Rows that do not move (sticky headers/footers, toolbars) are detected and left out of the match, so
/// they appear once instead of repeating. Frames that don't line up are skipped, not guessed.
final class ScrollStitcher: @unchecked Sendable { // used serially by one capture session
    enum FrameResult: Equatable {
        case first
        case appended(rows: Int)
        case unchanged
        case rejected
        case limitReached
    }

    /// Stitched height so far, in pixels.
    private(set) var height = 0
    let maxHeight: Int

    private struct Frame {
        let image: CGImage
        /// Grayscale, `columns` samples per row, top row first.
        let rows: [UInt8]
        /// Same, averaged over blocks of `coarseFactor` rows, for a fast first pass.
        let coarse: [UInt8]
        /// Small copy for the live preview, made once a frame is accepted.
        var thumbnail: CGImage?
    }

    private let columns = 96
    private let coarseFactor = 4
    private var first: Frame?
    private var last: Frame?
    /// Frames appended after the first, with how many new rows each contributed.
    private var strips: [(image: CGImage, thumbnail: CGImage?, rows: Int)] = []
    /// Width of the live preview produced by `makePreview()`.
    let previewWidth: Int
    /// Height of a static bottom bar, fixed once detected so the composition stays consistent.
    private var footer: Int?

    // Mean absolute difference per sample (0–255).
    private let identicalThreshold = 0.6
    private let staticRowThreshold = 1.5
    private let matchThreshold = 3.0
    private let candidateCount = 6

    init(maxHeight: Int = 40_000, previewWidth: Int = 200) {
        self.maxHeight = maxHeight
        self.previewWidth = previewWidth
    }

    func add(_ image: CGImage) -> FrameResult {
        let rows = Self.signature(of: image, columns: columns)
        var current = Frame(image: image, rows: rows, coarse: downsample(rows, height: image.height))
        guard let last else {
            current.thumbnail = thumbnail(of: image)
            first = current
            self.last = current
            height = image.height
            return .first
        }
        guard image.width == last.image.width, image.height == last.image.height else { return .rejected }
        let h = image.height

        if score(last.rows, current.rows, offset: 0, rows: 0..<h, step: 2) < identicalThreshold { return .unchanged }

        // Static bars at the top and bottom don't scroll; keep them out of the match.
        let limit = Int(Double(h) * 0.35)
        let header = min(limit, staticRun(last.rows, current.rows, height: h, fromTop: true))
        let bottom = min(limit, staticRun(last.rows, current.rows, height: h, fromTop: false))
        let minOverlap = max(24, h / 8)
        let maxOffset = h - header - bottom - minOverlap
        guard maxOffset >= 1 else { return .rejected }

        // Coarse pass on 1/4-height signatures. A true offset that isn't a multiple of 4 blurs in the coarse
        // signatures and can lose to a merely similar region, so refine several distinct candidates at full resolution.
        let coarseHeight = h / coarseFactor
        var coarseScores: [(offset: Int, score: Double)] = []
        for offset in 1...max(1, maxOffset / coarseFactor) {
            let range = (header / coarseFactor + 1)..<max(header / coarseFactor + 1, (h - bottom) / coarseFactor - offset - 1)
            guard range.count >= minOverlap / coarseFactor / 2 else { continue }
            coarseScores.append((offset, score(last.coarse, current.coarse, offset: offset, rows: range, step: 1, height: coarseHeight)))
        }
        var candidates: [Int] = []
        for entry in coarseScores.sorted(by: { $0.score < $1.score }) where candidates.count < candidateCount {
            if candidates.allSatisfy({ abs($0 - entry.offset) > 2 }) { candidates.append(entry.offset) }
        }
        var best = 0
        var bestScore = Double.infinity
        var refined = Set<Int>()
        for center in candidates.map({ $0 * coarseFactor }) {
            for offset in max(1, center - coarseFactor - 2)...max(1, min(maxOffset, center + coarseFactor + 2)) where refined.insert(offset).inserted {
                let value = score(last.rows, current.rows, offset: offset, rows: header..<(h - bottom - offset), step: 1)
                if value < bestScore { bestScore = value; best = offset }
            }
        }
        let stillScore = score(last.rows, current.rows, offset: 0, rows: header..<(h - bottom), step: 2)
        guard best > 0, bestScore < matchThreshold, bestScore < stillScore * 0.6 else { return .rejected }
        guard height + best <= maxHeight else { return .limitReached }

        if footer == nil, bottom > 0 { footer = bottom }
        current.thumbnail = thumbnail(of: image)
        strips.append((image, current.thumbnail, best))
        height += best
        self.last = current
        return .appended(rows: best)
    }

    /// The full-resolution stitched image.
    func makeImage() -> CGImage? { compose(scale: 1, thumbnails: false) }

    /// A `previewWidth`-wide version built from thumbnails, cheap enough to refresh after every frame.
    func makePreview() -> CGImage? {
        guard let first else { return nil }
        return compose(scale: CGFloat(previewWidth) / CGFloat(first.image.width), thumbnails: true)
    }

    /// First frame down to the footer, each new strip, then the footer once from the last frame.
    private func compose(scale: CGFloat, thumbnails: Bool) -> CGImage? {
        guard let first, let last else { return nil }
        let h = first.image.height
        let footer = footer ?? 0
        var pieces: [(image: CGImage?, rows: Range<Int>)] = [(thumbnails ? first.thumbnail : first.image, 0..<(h - footer))]
        for strip in strips { pieces.append((thumbnails ? strip.thumbnail : strip.image, max(0, h - footer - strip.rows)..<(h - footer))) }
        pieces.append((thumbnails ? last.thumbnail : last.image, (h - footer)..<h))

        let outWidth = max(1, Int((CGFloat(first.image.width) * scale).rounded()))
        let outHeight = max(1, Int((CGFloat(height) * scale).rounded()))
        let space = first.image.colorSpace.flatMap { $0.supportsOutput ? $0 : nil } ?? CGColorSpace(name: CGColorSpace.sRGB)!
        guard let context = CGContext(data: nil, width: outWidth, height: outHeight, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        context.interpolationQuality = thumbnails ? .medium : .none
        let frameHeight = CGFloat(h) * scale
        var y: CGFloat = 0 // top-left-origin cursor in the output
        for piece in pieces {
            guard let image = piece.image, !piece.rows.isEmpty else { continue }
            let pieceHeight = CGFloat(piece.rows.count) * scale
            // Place the whole frame so its first wanted row lands at the cursor, and clip to just those rows.
            let frameTop = y - CGFloat(piece.rows.lowerBound) * scale
            context.saveGState()
            context.clip(to: CGRect(x: 0, y: CGFloat(outHeight) - y - pieceHeight, width: CGFloat(outWidth), height: pieceHeight))
            context.draw(image, in: CGRect(x: 0, y: CGFloat(outHeight) - frameTop - frameHeight, width: CGFloat(outWidth), height: frameHeight))
            context.restoreGState()
            y += pieceHeight
        }
        return context.makeImage()
    }

    private func thumbnail(of image: CGImage) -> CGImage? {
        let scale = CGFloat(previewWidth) / CGFloat(image.width)
        let size = CGSize(width: previewWidth, height: max(1, Int((CGFloat(image.height) * scale).rounded())))
        guard let context = CGContext(data: nil, width: Int(size.width), height: Int(size.height), bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        context.interpolationQuality = .medium
        context.draw(image, in: CGRect(origin: .zero, size: size))
        return context.makeImage()
    }

    // MARK: - Signatures

    /// Grayscale thumbnail `columns` wide and full height. The rightmost 4% is dropped because overlay scrollbars change between frames.
    static func signature(of image: CGImage, columns: Int) -> [UInt8] {
        let usable = image.cropping(to: CGRect(x: 0, y: 0, width: max(1, Int(Double(image.width) * 0.96)), height: image.height)) ?? image
        var buffer = [UInt8](repeating: 0, count: columns * image.height)
        buffer.withUnsafeMutableBytes { raw in
            guard let context = CGContext(data: raw.baseAddress, width: columns, height: image.height, bitsPerComponent: 8, bytesPerRow: columns,
                                          space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.none.rawValue) else { return }
            context.interpolationQuality = .medium
            context.draw(usable, in: CGRect(x: 0, y: 0, width: columns, height: image.height))
        }
        return buffer
    }

    private func downsample(_ rows: [UInt8], height: Int) -> [UInt8] {
        let blocks = height / coarseFactor
        var result = [UInt8](repeating: 0, count: blocks * columns)
        rows.withUnsafeBufferPointer { source in
            for block in 0..<blocks {
                for column in 0..<columns {
                    var sum = 0
                    for r in 0..<coarseFactor { sum += Int(source[(block * coarseFactor + r) * columns + column]) }
                    result[block * columns + column] = UInt8(sum / coarseFactor)
                }
            }
        }
        return result
    }

    /// Mean absolute difference between `new` rows `rows` and `old` rows shifted down by `offset`.
    private func score(_ old: [UInt8], _ new: [UInt8], offset: Int, rows: Range<Int>, step: Int, height: Int? = nil) -> Double {
        let h = height ?? (old.count / columns)
        let lower = max(0, rows.lowerBound), upper = min(rows.upperBound, h - offset)
        guard upper > lower else { return .infinity }
        var total = 0, count = 0
        old.withUnsafeBufferPointer { a in
            new.withUnsafeBufferPointer { b in
                var y = lower
                while y < upper {
                    let ra = (y + offset) * columns, rb = y * columns
                    for column in 0..<columns { total += abs(Int(a[ra + column]) - Int(b[rb + column])) }
                    count += columns
                    y += step
                }
            }
        }
        return count == 0 ? .infinity : Double(total) / Double(count)
    }

    /// Number of consecutive identical rows from the top (or bottom) edge.
    private func staticRun(_ old: [UInt8], _ new: [UInt8], height: Int, fromTop: Bool) -> Int {
        var run = 0
        while run < height {
            let y = fromTop ? run : height - 1 - run
            if score(old, new, offset: 0, rows: y..<(y + 1), step: 1) > staticRowThreshold { break }
            run += 1
        }
        return run
    }
}
