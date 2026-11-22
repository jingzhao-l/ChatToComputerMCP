import AppKit
import Foundation

// Original ThreadQuay-era FileMCP artwork remains in the upstream archive.
// This is a separate, native vector identity for ChatToComputerMCP.
// Motif: multiple AI chats -> one local computer -> isolated workspaces.
private func rgb(_ hex: UInt32, _ opacity: CGFloat = 1) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 255) / 255,
            green: CGFloat((hex >> 8) & 255) / 255,
            blue: CGFloat(hex & 255) / 255, alpha: opacity)
}
private func rounded(_ r: NSRect, radius: CGFloat) -> NSBezierPath {
    NSBezierPath(roundedRect: r, xRadius: radius, yRadius: radius)
}
private func render(_ frame: NSRect) {
    NSGraphicsContext.saveGraphicsState()
    let m = NSAffineTransform()
    m.translateX(by: frame.minX, yBy: frame.minY)
    m.scaleX(by: frame.width / 512, yBy: frame.height / 512)
    m.concat()

    let bg = rounded(NSRect(x: 10, y: 10, width: 492, height: 492), radius: 112)
    NSGradient(colors: [rgb(0x17346A), rgb(0x07162E), rgb(0x051329)])!.draw(in: bg, angle: -68)
    rgb(0x55AFFF, 0.55).setStroke()
    bg.lineWidth = 3
    bg.stroke()

    let origin = NSPoint(x: 256, y: 243)
    for (target, width) in [(NSPoint(x: 120, y: 78), CGFloat(14)),
                            (NSPoint(x: 256, y: 69), CGFloat(16)),
                            (NSPoint(x: 392, y: 78), CGFloat(14))] {
        let p = NSBezierPath()
        p.move(to: NSPoint(x: origin.x, y: origin.y - 12))
        p.curve(to: target,
                controlPoint1: NSPoint(x: (origin.x + target.x) * 0.5, y: 205),
                controlPoint2: NSPoint(x: target.x, y: 155))
        rgb(0x073C90, 0.8).setStroke(); p.lineWidth = width + 9; p.stroke()
        rgb(0x29E7F5, 0.9).setStroke(); p.lineWidth = width * 0.44; p.stroke()
    }

    func bubble(_ x: CGFloat, _ y: CGFloat, _ tint: UInt32) {
        let body = rounded(NSRect(x: x, y: y, width: 155, height: 102), radius: 38)
        NSGradient(colors: [rgb(tint), rgb(0x2463DA)])!.draw(in: body, angle: -50)
        rgb(0xDDFEFF, 0.75).setStroke(); body.lineWidth = 3; body.stroke()
        let tail = NSBezierPath()
        tail.move(to: NSPoint(x: x + 54, y: y + 9))
        tail.line(to: NSPoint(x: x + 71, y: y - 19))
        tail.line(to: NSPoint(x: x + 97, y: y + 8))
        tail.close()
        rgb(tint).setFill(); tail.fill()
        for d in 0..<3 {
            rgb(0xECFFFF).setFill()
            NSBezierPath(ovalIn: NSRect(x: x + 38 + CGFloat(d) * 38,
                                      y: y + 45, width: 14, height: 14)).fill()
        }
    }
    bubble(70, 343, 0x13B6D4)
    bubble(287, 343, 0x7465DE)

    // Flat, well-defined laptop: readable down to 32px.
    let display = rounded(NSRect(x: 148, y: 192, width: 216, height: 142), radius: 16)
    rgb(0xB2F9FE).setFill(); display.fill()
    rgb(0x082A64).setFill()
    rounded(NSRect(x: 159, y: 204, width: 194, height: 118), radius: 10).fill()
    NSGradient(colors: [rgb(0x207EDF), rgb(0x0C2E9A)])!.draw(
        in: rounded(NSRect(x: 165, y: 210, width: 182, height: 107), radius: 6), angle: -45)
    let code = "</>" as NSString
    let font = NSFont.monospacedSystemFont(ofSize: 60, weight: .bold)
    let size = code.size(withAttributes: [.font: font])
    code.draw(at: NSPoint(x: 256 - size.width / 2, y: 241),
              withAttributes: [.font: font, .foregroundColor: rgb(0xD8FDFF)])
    NSGradient(colors: [rgb(0xE9FAFF), rgb(0x6FB8EF)])!.draw(
        in: rounded(NSRect(x: 121, y: 178, width: 270, height: 23), radius: 10), angle: 88)
    rgb(0x175EBA).setFill()
    rounded(NSRect(x: 223, y: 185, width: 66, height: 5), radius: 3).fill()

    for x in [74, 211, 348] as [CGFloat] {
        let folder = rounded(NSRect(x: x, y: 60, width: 90, height: 62), radius: 14)
        NSGradient(colors: [rgb(0x62F2FA), rgb(0x156AD3)])!.draw(in: folder, angle: -60)
        rgb(0xF1FFFF, 0.8).setStroke(); folder.lineWidth = 2; folder.stroke()
        rgb(0x60DDF4).setFill()
        rounded(NSRect(x: x + 10, y: 114, width: 43, height: 15), radius: 6).fill()
        let sym = "</>" as NSString
        let small = NSFont.monospacedSystemFont(ofSize: 20, weight: .bold)
        let size = sym.size(withAttributes: [.font: small])
        sym.draw(at: NSPoint(x: x + 45 - size.width / 2, y: 80),
                 withAttributes: [.font: small, .foregroundColor: rgb(0xF4FFFF)])
    }
    NSGraphicsContext.restoreGraphicsState()
}

private func image(_ pixels: Int) throws -> Data {
    guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil,
        pixelsWide: pixels, pixelsHigh: pixels, bitsPerSample: 8,
        samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
        let context = NSGraphicsContext(bitmapImageRep: bitmap) else {
        throw NSError(domain: "ChatToComputerMCP.Icon", code: 1)
    }
    bitmap.size = NSSize(width: pixels, height: pixels)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = context
    context.imageInterpolation = .high
    NSColor.clear.setFill()
    NSRect(x: 0, y: 0, width: pixels, height: pixels).fill(using: .copy)
    render(NSRect(x: 0, y: 0, width: pixels, height: pixels))
    NSGraphicsContext.restoreGraphicsState()
    context.flushGraphics()
    guard let data = bitmap.representation(using: .png, properties: [:]) else {
        throw NSError(domain: "ChatToComputerMCP.Icon", code: 2)
    }
    return data
}

private func ico() throws -> Data {
    let sizes = [16, 24, 32, 48, 64, 128, 256]
    let images = try sizes.map(image)
    var out = Data()
    func u16(_ v: Int) { out.append(UInt8(v & 255)); out.append(UInt8((v >> 8) & 255)) }
    func u32(_ v: Int) { for i in stride(from: 0, to: 32, by: 8) { out.append(UInt8((v >> i) & 255)) } }
    u16(0); u16(1); u16(sizes.count)
    var pos = 6 + sizes.count * 16
    for (size, data) in zip(sizes, images) {
        out.append(size == 256 ? 0 : UInt8(size))
        out.append(size == 256 ? 0 : UInt8(size))
        out.append(0); out.append(0); u16(1); u16(32)
        u32(data.count); u32(pos); pos += data.count
    }
    for data in images { out.append(data) }
    return out
}

let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
let asset = root.appendingPathComponent("assets/branding/chat-to-computer-mcp")
let build = root.appendingPathComponent("build")
try FileManager.default.createDirectory(at: asset, withIntermediateDirectories: true)
try FileManager.default.createDirectory(at: build, withIntermediateDirectories: true)
try image(1024).write(to: build.appendingPathComponent("chat-to-computer-mcp-icon.png"), options: .atomic)
try image(128).write(to: asset.appendingPathComponent("app-icon-128.png"), options: .atomic)
try image(256).write(to: asset.appendingPathComponent("app-icon-256.png"), options: .atomic)
try ico().write(to: asset.appendingPathComponent("ChatToComputerMCP.ico"), options: .atomic)
let svg = """
<svg xmlns="http://www.w3.org/2000/svg" width="780" height="120" viewBox="0 0 780 120">
<rect width="780" height="120" rx="22" fill="#07162E"/>
<text x="35" y="80" fill="#73F0FA" font-family="Arial, sans-serif" font-size="55" font-weight="700">ChatToComputerMCP</text>
</svg>
"""
try svg.write(to: asset.appendingPathComponent("wordmark.svg"), atomically: true, encoding: .utf8)
print("Brand assets generated: ChatToComputerMCP")
