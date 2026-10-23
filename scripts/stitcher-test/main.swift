// Synthetic regression test for ScrollStitcher. Run through scripts/test-stitcher.sh.
//
// Builds a 1000×7000 "page" (text-like rows, gradient blocks, a 700 px blank gap), cuts 800 px frames at
// random scroll offsets (pauses, 1–12 px nudges, 30–520 px jumps), adds sticky bars and a moving scrollbar,
// then checks the stitched result against the ground truth.
//   normal mode: 70 px sticky header + 50 px sticky footer must appear once; content rows must match exactly.
//   edge mode:   no sticky bars, but every frame's bottom 2 rows are washed out towards white, like the blended
//                edge of a real capture. Those rows change as the page scrolls, so they can't be treated as a
//                sticky bar; only the last frame's 2 rows may end up in the result.
import AppKit

struct Seeded: RandomNumberGenerator {
    var state: UInt64
    mutating func next() -> UInt64 { state = state &* 6364136223846793005 &+ 1442695040888963407; return state }
}

let seed = UInt64(CommandLine.arguments[1])!
let edgeMode = CommandLine.arguments.count > 2 && CommandLine.arguments[2] == "edge"
var generator = Seeded(state: seed)
let W = 1000, contentH = 7000, H = 800
let header = edgeMode ? 0 : 70, footer = edgeMode ? 0 : 50
let space = CGColorSpace(name: CGColorSpace.sRGB)!

func context(_ w: Int, _ h: Int) -> CGContext {
    CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0, space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
}

// Page content (top-left thinking: y_top → CG y = contentH - y_top - h).
let page = context(W, contentH)
page.setFillColor(.white)
page.fill(CGRect(x: 0, y: 0, width: W, height: contentH))
var y = 20
while y < contentH - 40 {
    if y > 3000 && y < 3700 { y = 3700; continue } // long blank gap
    if Int.random(in: 0..<10, using: &generator) == 0 {
        let blockHeight = Int.random(in: 120...300, using: &generator)
        for i in 0..<blockHeight {
            page.setFillColor(CGColor(red: Double(i) / Double(blockHeight), green: 0.4, blue: Double.random(in: 0...1, using: &generator), alpha: 1))
            page.fill(CGRect(x: 60, y: contentH - y - i - 1, width: 700, height: 1))
        }
        y += blockHeight + 20
        continue
    }
    var x = 40
    while x < 900 {
        let w = Int.random(in: 10...70, using: &generator)
        page.setFillColor(CGColor(gray: Double.random(in: 0...0.5, using: &generator), alpha: 1))
        page.fill(CGRect(x: x, y: contentH - y - 14, width: w, height: 14))
        x += w + Int.random(in: 6...14, using: &generator)
    }
    y += Int.random(in: 22...34, using: &generator)
}
let content = page.makeImage()!

func frame(at offset: Int, scrollbar: Double) -> CGImage {
    let f = context(W, H)
    f.draw(content.cropping(to: CGRect(x: 0, y: offset, width: W, height: H))!, in: CGRect(x: 0, y: 0, width: W, height: H))
    if header > 0 {
        f.setFillColor(CGColor(red: 0.1, green: 0.3, blue: 0.8, alpha: 1)); f.fill(CGRect(x: 0, y: H - header, width: W, height: header))
        f.setFillColor(.white); f.fill(CGRect(x: 30, y: H - 45, width: 200, height: 20))
    }
    if footer > 0 {
        f.setFillColor(CGColor(gray: 0.92, alpha: 1)); f.fill(CGRect(x: 0, y: 0, width: W, height: footer))
        f.setFillColor(CGColor(gray: 0.3, alpha: 1)); f.fill(CGRect(x: 400, y: 15, width: 200, height: 20))
    }
    f.setFillColor(CGColor(gray: scrollbar, alpha: 1)); f.fill(CGRect(x: W - 14, y: Int(scrollbar * 500), width: 10, height: 200))
    if edgeMode { f.setFillColor(CGColor(gray: 1, alpha: 0.5)); f.fill(CGRect(x: 0, y: 0, width: W, height: 2)) }
    return f.makeImage()!
}

var offsets = [0]
var offset = 0
while offset < contentH - H - 10 {
    let roll = Int.random(in: 0..<10, using: &generator)
    let step = roll == 0 ? 0 : (roll < 3 ? Int.random(in: 1...12, using: &generator) : Int.random(in: 30...520, using: &generator))
    offset = min(contentH - H, offset + step)
    offsets.append(offset)
}

let stitcher = ScrollStitcher()
var lastAccepted = 0
var wrongWithContent = 0
let started = Date()
for (index, frameOffset) in offsets.enumerated() {
    if case let .appended(rows) = stitcher.add(frame(at: frameOffset, scrollbar: Double(index % 7) / 7)) {
        // A wrong offset is only acceptable when the whole overlap is blank (nothing to match on).
        let overlapTop = frameOffset + header, overlapBottom = lastAccepted + H - footer
        let blank = overlapTop >= 3000 - 40 && overlapBottom <= 3700 + 40
        if rows != frameOffset - lastAccepted && !blank { wrongWithContent += 1 }
        lastAccepted = frameOffset
    }
}
let msPerFrame = Date().timeIntervalSince(started) / Double(offsets.count) * 1000
let output = stitcher.makeImage()!

func gray(_ image: CGImage) -> [UInt8] {
    var buffer = [UInt8](repeating: 0, count: image.width * image.height)
    let c = CGContext(data: &buffer, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: image.width,
                      space: CGColorSpaceCreateDeviceGray(), bitmapInfo: 0)!
    c.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
    return buffer
}
/// Rows that aren't pure white (ignoring the scrollbar strip), top first.
func contentRows(_ image: CGImage) -> [[UInt8]] {
    let pixels = gray(image)
    return (0..<image.height).compactMap { r in
        let row = Array(pixels[(r * image.width)..<(r * image.width + W - 40)])
        return row.contains { $0 < 250 } ? row : nil
    }
}

var failures: [String] = []
if wrongWithContent > 0 { failures.append("\(wrongWithContent) frame(s) with content in the overlap got a wrong offset") }

do {
    // Ground truth: header + content + footer, as the last accepted frame would show them.
    let truth = context(W, lastAccepted + H)
    truth.draw(content.cropping(to: CGRect(x: 0, y: 0, width: W, height: lastAccepted + H))!, in: CGRect(x: 0, y: 0, width: W, height: lastAccepted + H))
    let first = frame(at: 0, scrollbar: 0)
    if header > 0 {
        truth.draw(first.cropping(to: CGRect(x: 0, y: 0, width: W, height: header))!, in: CGRect(x: 0, y: lastAccepted + H - header, width: W, height: header))
    }
    if footer > 0 {
        truth.draw(first.cropping(to: CGRect(x: 0, y: H - footer, width: W, height: footer))!, in: CGRect(x: 0, y: 0, width: W, height: footer))
    }
    let a = contentRows(output), b = contentRows(truth.makeImage()!)
    var differing = 0
    if a.count == b.count {
        for (x, y) in zip(a, b) where zip(x, y).reduce(0, { $0 + abs(Int($1.0) - Int($1.1)) }) > x.count * 2 { differing += 1 }
    } else {
        failures.append("content row count \(a.count) vs ground truth \(b.count)")
    }
    // In edge mode the very last frame's 2 washed-out rows legitimately differ.
    let allowed = edgeMode ? 2 : 0
    if differing > allowed { failures.append("\(differing) content rows differ from ground truth (allowed \(allowed))") }
    let preview = stitcher.makePreview()
    if preview?.width != stitcher.previewWidth { failures.append("preview width \(preview?.width ?? -1)") }
    print("seed \(seed)\(edgeMode ? " edge" : ""): frames \(offsets.count), content rows \(a.count)/\(b.count), differing \(differing), \(String(format: "%.1f", msPerFrame)) ms/frame")
}

if !failures.isEmpty {
    failures.forEach { print("  FAIL: \($0)") }
    exit(1)
}
