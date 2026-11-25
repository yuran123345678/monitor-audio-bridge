// 生成应用图标：Resources/AppIcon.icns（Mac）和 windows/app.ico（Windows）。
// 用法（在工程根目录）：./scripts/make-icon.sh     —— 图标已随工程提供，只有想改图标时才需要重跑。
// 图形（v3，极简）：石墨底板 + 三个形状 —— 两根白色立柱（音箱 / 桥塔，各开一个圆孔当喇叭）
// 和中间一道绿色下垂的弧（桥的主缆 / 传过去的声音）。全部纯色平涂，没有阴影和细节，所有尺寸同一画法。
// Windows 托盘图标（windows/Ui.cs 的 MakeIcon）用的是同一组坐标，改这里的几何时要同步改那边。
// 调试：./mkicon <输出目录> sheet   —— 只出一张各方案、各尺寸的对比图 icon-sheet.png。
import AppKit

func rgb(_ hex: Int, _ a: CGFloat = 1) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: a)
}
func circle(_ cx: CGFloat, _ cy: CGFloat, _ r: CGFloat) -> NSBezierPath {
    NSBezierPath(ovalIn: NSRect(x: cx - r, y: cy - r, width: 2 * r, height: 2 * r))
}

let green = rgb(0x3CCB8A)      // 与界面里的「已连接」绿、Windows 托盘的绿是同一个色
let white = rgb(0xF4F5F7)

/// 带圆孔的圆角矩形（孔是真镂空，透出底板）
func slab(_ r: NSRect, radius: CGFloat, holes: [(CGFloat, CGFloat, CGFloat)]) -> NSBezierPath {
    let p = NSBezierPath(roundedRect: r, xRadius: radius, yRadius: radius)
    for h in holes { p.append(circle(h.0, h.1, h.2)) }
    p.windingRule = .evenOdd
    return p
}

/// variant: 0 = 立柱 + 弧（采用）  1 = 一对音箱（石墨底）  2 = 一对音箱（绿底）
func render(_ px: Int, margin: CGFloat, variant: Int = 0) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = NSSize(width: px, height: px)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let s = CGFloat(px), m = s * margin
    let t = NSAffineTransform()
    t.translateX(by: m, yBy: m)
    t.scale(by: s - 2 * m)
    t.concat()                                   // 之后的坐标都以「底板边长 = 1」为单位，原点在左下

    let plate = NSBezierPath(roundedRect: NSRect(x: 0, y: 0, width: 1, height: 1), xRadius: 0.225, yRadius: 0.225)
    if variant == 2 {
        NSGradient(colors: [rgb(0x48D897), rgb(0x1E9E6B)])!.draw(in: plate, angle: -90)
    } else {
        NSGradient(colors: [rgb(0x2C2D33), rgb(0x151619)])!.draw(in: plate, angle: -90)
    }

    switch variant {
    case 0:
        // 两根立柱：宽 0.17，高 0.50，上下居中
        let w: CGFloat = 0.17, h: CGFloat = 0.50, y: CGFloat = 0.25
        let lx: CGFloat = 0.20, rx: CGFloat = 1 - 0.20 - w
        // 弧：从左柱垂到中间再升到右柱（抛物线），两端藏在立柱后面
        let top: CGFloat = 0.66, low: CGFloat = 0.40
        let x0 = lx + w / 2, x1 = rx + w / 2
        let arc = NSBezierPath()
        for i in 0...64 {
            let u = CGFloat(i) / 32 - 1                      // -1 .. 1
            let p = NSPoint(x: 0.5 + u * (x1 - x0) / 2, y: low + (top - low) * u * u)
            if i == 0 { arc.move(to: p) } else { arc.line(to: p) }
        }
        arc.lineWidth = 0.068; arc.lineJoinStyle = .round
        green.setStroke(); arc.stroke()
        white.setFill()
        for x in [lx, rx] {
            slab(NSRect(x: x, y: y, width: w, height: h), radius: 0.055, holes: [(x + w / 2, y + 0.135, 0.046)]).fill()
        }
    default:
        let w: CGFloat = 0.255, h: CGFloat = 0.48, y: CGFloat = 0.26
        let lx: CGFloat = 0.20, rx: CGFloat = 1 - 0.20 - w
        white.setFill()
        for x in [lx, rx] {
            let cx = x + w / 2
            slab(NSRect(x: x, y: y, width: w, height: h), radius: 0.065,
                 holes: [(cx, y + 0.155, 0.078), (cx, y + 0.365, 0.034)]).fill()
            if variant == 1 { green.setFill(); circle(cx, y + 0.155, 0.036).fill(); white.setFill() }
        }
    }
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

let out = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : ".")
let chosen = 0

if CommandLine.arguments.count > 2 && CommandLine.arguments[2] == "sheet" {
    let W = 1000, H = 3 * 300
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: W, pixelsHigh: H, bitsPerSample: 8, samplesPerPixel: 4,
                               hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)!
    rgb(0xE6E7EB).setFill(); NSRect(x: 0, y: 0, width: 620, height: H).fill()
    rgb(0x2A2B30).setFill(); NSRect(x: 620, y: 0, width: 380, height: H).fill()
    for v in 0..<3 {
        let y0 = CGFloat(H - (v + 1) * 300)
        func put(_ px: Int, _ x: CGFloat, _ y: CGFloat, scale: CGFloat = 1) {
            let img = NSBitmapImageRep(data: render(px, margin: 0.098, variant: v))!
            let ip = scale == 1 ? NSImageInterpolation.high : NSImageInterpolation.none
            img.draw(in: NSRect(x: x, y: y0 + y, width: CGFloat(px) * scale, height: CGFloat(px) * scale), from: .zero,
                     operation: .sourceOver, fraction: 1, respectFlipped: false, hints: [.interpolation: ip.rawValue])
        }
        put(256, 20, 22); put(128, 300, 86); put(64, 450, 118); put(32, 540, 134); put(16, 590, 142)
        put(128, 640, 86); put(32, 790, 102, scale: 3); put(16, 900, 118, scale: 4)
    }
    NSGraphicsContext.restoreGraphicsState()
    try! rep.representation(using: .png, properties: [:])!.write(to: out.appendingPathComponent("icon-sheet.png"))
    exit(0)
}

let iconset = out.appendingPathComponent("AppIcon.iconset")
try? FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
for (name, px) in [("16x16", 16), ("16x16@2x", 32), ("32x32", 32), ("32x32@2x", 64), ("128x128", 128),
                   ("128x128@2x", 256), ("256x256", 256), ("256x256@2x", 512), ("512x512", 512), ("512x512@2x", 1024)] {
    // Mac 图标按系统规范四周留白约 10%
    try! render(px, margin: 0.098, variant: chosen).write(to: iconset.appendingPathComponent("icon_\(name).png"))
}

// ICO：目录 + 若干 PNG（Vista 起支持 PNG 压缩的图标项）。Windows 图标不留白。
let sizes = [16, 24, 32, 48, 64, 256]
let pngs = sizes.map { render($0, margin: 0.02, variant: chosen) }
var ico = Data()
func le16(_ v: Int) -> [UInt8] { [UInt8(v & 0xFF), UInt8((v >> 8) & 0xFF)] }
func le32(_ v: Int) -> [UInt8] { [UInt8(v & 0xFF), UInt8((v >> 8) & 0xFF), UInt8((v >> 16) & 0xFF), UInt8((v >> 24) & 0xFF)] }
ico.append(contentsOf: le16(0) + le16(1) + le16(sizes.count))
var offset = 6 + 16 * sizes.count
for (i, px) in sizes.enumerated() {
    ico.append(contentsOf: [UInt8(px >= 256 ? 0 : px), UInt8(px >= 256 ? 0 : px), 0, 0] + le16(1) + le16(32)
               + le32(pngs[i].count) + le32(offset))
    offset += pngs[i].count
}
for p in pngs { ico.append(p) }
try! ico.write(to: out.appendingPathComponent("app.ico"))
try! render(512, margin: 0.098, variant: chosen).write(to: out.appendingPathComponent("icon-preview.png"))
