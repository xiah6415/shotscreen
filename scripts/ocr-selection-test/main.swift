import AppKit

// Renders three known lines, recognizes them with the real Vision path, then checks the editor's selection rules.
// Run: swiftc Sources/SuYanShot/OCR/TextRecognitionService.swift scripts/ocr-selection-test/main.swift -o /tmp/ocr-test && /tmp/ocr-test

let lines = ["Glean screenshot tool", "拾光截圖工具", "Copy 42 lines"]
let size = CGSize(width: 900, height: 360)
let context = CGContext(data: nil, width: Int(size.width), height: Int(size.height), bitsPerComponent: 8, bytesPerRow: 0,
                        space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
context.setFillColor(.white)
context.fill(CGRect(origin: .zero, size: size))
NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
for (i, text) in lines.enumerated() {
    // Top line first: y counts from the bottom.
    (text as NSString).draw(at: CGPoint(x: 40, y: size.height - 90 - CGFloat(i) * 110),
                            withAttributes: [.font: NSFont.systemFont(ofSize: 48), .foregroundColor: NSColor.black])
}
let image = context.makeImage()!

let done = DispatchSemaphore(value: 0)
Task {
    let found = try await TextRecognitionService().recognizeLines(image)
    print("recognized:", found.map(\.text))
    precondition(found.count == 3, "expected 3 lines")
    precondition(found[0].text == lines[0] && found[2].text.contains("42"), "reading order / text")
    precondition(found[1].text.contains("拾光"), "Traditional Chinese")
    precondition(found[0].box.minY > found[1].box.maxY && found[1].box.minY > found[2].box.maxY, "boxes top to bottom, bottom-left origin")
    for line in found { precondition(CGRect(origin: .zero, size: size).contains(line.box), "box inside the image") }

    var selection = OCRTextSelection(lines: found)
    precondition(selection.textToCopy == OCRTextSelection.join(found.map(\.text)), "nothing selected copies everything")
    let mid = { (i: Int) in CGPoint(x: found[i].box.midX, y: found[i].box.midY) }
    selection.click(at: mid(1), toggle: false)
    precondition(selection.selected == [1] && selection.textToCopy == found[1].text, "click selects one line")
    selection.click(at: mid(2), toggle: true)
    precondition(selection.selected == [1, 2], "shift-click adds")
    selection.click(at: mid(1), toggle: true)
    precondition(selection.selected == [2], "shift-click again removes")
    selection.click(at: CGPoint(x: 880, y: 5), toggle: false)
    precondition(selection.selected.isEmpty, "click on empty space clears")
    selection.beginDrag(adding: false)
    selection.drag(to: CGRect(x: 60, y: found[1].box.midY, width: 50, height: found[0].box.midY - found[1].box.midY))
    precondition(selection.selected == [0, 1], "drag selects the lines it touches")
    precondition(selection.textToCopy == found[0].text + "\n" + found[1].text, "copied in reading order")
    selection.beginDrag(adding: true)
    selection.drag(to: CGRect(x: 60, y: found[2].box.midY, width: 10, height: 1))
    precondition(selection.selected == [0, 1, 2], "shift-drag adds")
    selection.clear(); selection.selectAll()
    precondition(selection.selected.count == 3, "select all")
    print("OCR selection checks passed: 3 lines (incl. Traditional Chinese) with boxes, click, shift-click, drag, shift-drag, copy order.")
    done.signal()
}
done.wait()
