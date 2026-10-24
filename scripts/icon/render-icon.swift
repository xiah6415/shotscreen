import AppKit

// Renders the 拾光 / Glean app icon (1024×1024 PNG): warm sunrise tile, white viewfinder, white sparkle.
// Usage: swiftc -O -o /tmp/render-icon scripts/icon/render-icon.swift && /tmp/render-icon Resources/AppIcon.png
let out = URL(fileURLWithPath: CommandLine.arguments[1])
let size: CGFloat = 1024

func color(_ hex: UInt32, _ a: CGFloat = 1) -> CGColor {
  CGColor(srgbRed: CGFloat((hex >> 16) & 0xff) / 255, green: CGFloat((hex >> 8) & 0xff) / 255, blue: CGFloat(hex & 0xff) / 255, alpha: a)
}

/// macOS icon grid: 824 pt rounded square centred on a 1024 canvas, with a soft drop shadow.
func tile(_ ctx: CGContext, gradient: [UInt32]) {
  let rect = CGRect(x: 100, y: 100, width: 824, height: 824)
  let path = CGPath(roundedRect: rect, cornerWidth: 185, cornerHeight: 185, transform: nil)
  ctx.saveGState()
  ctx.setShadow(offset: CGSize(width: 0, height: -12), blur: 28, color: color(0x000000, 0.28))
  ctx.addPath(path); ctx.setFillColor(color(gradient[0])); ctx.fillPath()
  ctx.restoreGState()
  ctx.saveGState()
  ctx.addPath(path); ctx.clip()
  let g = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB), colors: gradient.map { color($0) } as CFArray, locations: nil)!
  ctx.drawLinearGradient(g, start: CGPoint(x: 512, y: 924), end: CGPoint(x: 512, y: 100), options: [])
  // gentle top highlight
  let hl = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB), colors: [color(0xffffff, 0.22), color(0xffffff, 0)] as CFArray, locations: nil)!
  ctx.drawLinearGradient(hl, start: CGPoint(x: 512, y: 924), end: CGPoint(x: 512, y: 560), options: [])
  ctx.restoreGState()
}

/// Four rounded viewfinder corners.
func viewfinder(_ ctx: CGContext, inset: CGFloat, arm: CGFloat, width: CGFloat, stroke: CGColor? = nil, gradient: [UInt32]? = nil) {
  let r = CGRect(x: 100 + inset, y: 100 + inset, width: 824 - inset * 2, height: 824 - inset * 2)
  let path = CGMutablePath()
  let c: CGFloat = 60
  // bottom-left
  path.move(to: CGPoint(x: r.minX, y: r.minY + arm)); path.addLine(to: CGPoint(x: r.minX, y: r.minY + c))
  path.addQuadCurve(to: CGPoint(x: r.minX + c, y: r.minY), control: CGPoint(x: r.minX, y: r.minY)); path.addLine(to: CGPoint(x: r.minX + arm, y: r.minY))
  // bottom-right
  path.move(to: CGPoint(x: r.maxX - arm, y: r.minY)); path.addLine(to: CGPoint(x: r.maxX - c, y: r.minY))
  path.addQuadCurve(to: CGPoint(x: r.maxX, y: r.minY + c), control: CGPoint(x: r.maxX, y: r.minY)); path.addLine(to: CGPoint(x: r.maxX, y: r.minY + arm))
  // top-right
  path.move(to: CGPoint(x: r.maxX, y: r.maxY - arm)); path.addLine(to: CGPoint(x: r.maxX, y: r.maxY - c))
  path.addQuadCurve(to: CGPoint(x: r.maxX - c, y: r.maxY), control: CGPoint(x: r.maxX, y: r.maxY)); path.addLine(to: CGPoint(x: r.maxX - arm, y: r.maxY))
  // top-left
  path.move(to: CGPoint(x: r.minX + arm, y: r.maxY)); path.addLine(to: CGPoint(x: r.minX + c, y: r.maxY))
  path.addQuadCurve(to: CGPoint(x: r.minX, y: r.maxY - c), control: CGPoint(x: r.minX, y: r.maxY)); path.addLine(to: CGPoint(x: r.minX, y: r.maxY - arm))
  ctx.saveGState()
  ctx.setLineWidth(width); ctx.setLineCap(.round); ctx.setLineJoin(.round)
  if let gradient {
    ctx.addPath(path); ctx.replacePathWithStrokedPath(); ctx.clip()
    let g = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB), colors: gradient.map { color($0) } as CFArray, locations: nil)!
    ctx.drawLinearGradient(g, start: CGPoint(x: 200, y: 824), end: CGPoint(x: 824, y: 200), options: [])
  } else if let stroke {
    ctx.setShadow(offset: CGSize(width: 0, height: -4), blur: 10, color: color(0x000000, 0.18))
    ctx.addPath(path); ctx.setStrokeColor(stroke); ctx.strokePath()
  }
  ctx.restoreGState()
}

/// Four-point sparkle ("light") centred in the frame.
func sparkle(_ ctx: CGContext, center: CGPoint, radius: CGFloat, fill: CGColor, glow: CGColor) {
  let p = CGMutablePath()
  let w = radius * 0.22
  p.move(to: CGPoint(x: center.x, y: center.y + radius))
  p.addQuadCurve(to: CGPoint(x: center.x + radius, y: center.y), control: CGPoint(x: center.x + w, y: center.y + w))
  p.addQuadCurve(to: CGPoint(x: center.x, y: center.y - radius), control: CGPoint(x: center.x + w, y: center.y - w))
  p.addQuadCurve(to: CGPoint(x: center.x - radius, y: center.y), control: CGPoint(x: center.x - w, y: center.y - w))
  p.addQuadCurve(to: CGPoint(x: center.x, y: center.y + radius), control: CGPoint(x: center.x - w, y: center.y + w))
  ctx.saveGState()
  ctx.setShadow(offset: .zero, blur: radius * 0.6, color: glow)
  ctx.addPath(p); ctx.setFillColor(fill); ctx.fillPath()
  ctx.restoreGState()
}

func render(_ name: String, draw: (CGContext) -> Void) {
  let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1024, pixelsHigh: 1024, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
  NSGraphicsContext.saveGraphicsState()
  NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
  draw(NSGraphicsContext.current!.cgContext)
  NSGraphicsContext.restoreGraphicsState()
  try! rep.representation(using: .png, properties: [:])!.write(to: name.isEmpty ? out : out.appendingPathComponent(name))
}

render("") { ctx in
  tile(ctx, gradient: [0xFFC15A, 0xFF7A59, 0xF2547D])
  viewfinder(ctx, inset: 150, arm: 170, width: 46, stroke: color(0xffffff))
  sparkle(ctx, center: CGPoint(x: 512, y: 512), radius: 150, fill: color(0xffffff), glow: color(0xffffff, 0.9))
}
