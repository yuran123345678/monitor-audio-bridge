import AppKit
import SwiftUI
import CoreAudio
import Foundation

// MARK: - 命令行入口

let argv = CommandLine.arguments

// 输出重定向到文件时也要能立刻看到（否则被 SIGTERM 杀掉会丢日志）
setvbuf(stdout, nil, _IONBF, 0)

func printUsage() {
    print("""
    音频桥（Mac 端，可收可发）

    用法：
      MonitorAudioBridge                 图形界面（默认）；加 --background 只进菜单栏
      MonitorAudioBridge --devices       列出音频设备
      MonitorAudioBridge --selftest [秒] 自测：会真的响几秒 440 Hz（音量很低）
      MonitorAudioBridge --nettest       静音自测：只测协议/网络/缓冲逻辑，不出声
      MonitorAudioBridge --recv          不开界面，只收包播放（终端里看统计）
      MonitorAudioBridge --send HOST [PORT] [秒]  发 440 Hz 测试音
      MonitorAudioBridge --sendsys HOST [PORT]    不开界面，把本机系统声音发出去

    音频端口 \(Proto.audioPort)/udp，发现端口 \(Proto.discoveryPort)/udp。
    """)
}

if argv.contains("--help") || argv.contains("-h") {
    printUsage()
    exit(0)
}

if argv.contains("--devices") {
    for d in AudioDevices.list() {
        let pad = String(repeating: " ", count: max(2, 34 - d.name.count))
        let def = d.id == AudioDevices.defaultID(kAudioHardwarePropertyDefaultOutputDevice) ? "  ← 默认输出"
            : (d.id == AudioDevices.defaultID(kAudioHardwarePropertyDefaultInputDevice) ? "  ← 默认输入" : "")
        print("\(d.name)\(pad)in=\(d.inputs) out=\(d.outputs)  \(d.transportLabel)  \(d.uid)\(def)")
    }
    exit(0)
}

if let i = argv.firstIndex(of: "--selftest") {
    let secs = (i + 1 < argv.count ? Double(argv[i + 1]) : nil) ?? 3.0
    exit(SelfTest.run(seconds: secs))
}

/// 往 NetSender 里按实时节奏喂 440 Hz（走完整的发送端逻辑：切包、保活、收回执）。
func runTone(_ sender: NetSender, seconds: Double) {
    var buf = [Float](repeating: 0, count: 480 * 2)
    var phase = 0.0
    var next = DispatchTime.now().uptimeNanoseconds
    let end = seconds > 0 ? next + UInt64(seconds * 1e9) : UInt64.max
    while next < end {
        for i in 0..<480 {
            let v = Float(sin(phase) * 0.2)
            phase += 2 * Double.pi * 440 / 48_000
            if phase > 2 * Double.pi { phase -= 2 * Double.pi }
            buf[i * 2] = v; buf[i * 2 + 1] = v
        }
        buf.withUnsafeBufferPointer { sender.push($0.baseAddress!, frames: 480, rate: 48_000) }
        next += 10_000_000
        let now = DispatchTime.now().uptimeNanoseconds
        if next > now { usleep(UInt32((next - now) / 1000)) }
    }
}

if let i = argv.firstIndex(of: "--send") {
    guard i + 1 < argv.count else { print("缺 HOST"); exit(2) }
    let host = argv[i + 1]
    let port = (i + 2 < argv.count ? UInt16(argv[i + 2]) : nil) ?? NetReceiver.defaultPort
    let secs = (i + 3 < argv.count ? Double(argv[i + 3]) : nil) ?? 0
    guard let addr = Sock.resolve(host: host, port: port) else { print("地址无法解析"); exit(2) }
    let sender = NetSender()
    do { try sender.start(to: addr) } catch {
        print("启动失败：\(error.localizedDescription)")
        exit(1)
    }
    print("发 440 Hz 到 \(host):\(port)" + (secs > 0 ? "，\(secs) 秒" : "，Ctrl-C 结束"))
    runTone(sender, seconds: secs)
    let st = sender.stats
    let rep = st.report
    let fresh = Date().timeIntervalSince(rep.at) < 2
    print("已发 \(st.packets) 包；" + (fresh
        ? "对方「\(rep.name)」回执：收 \(rep.packets) 丢 \(rep.lost) 缓冲 \(Int(rep.bufferMs)) ms" + (rep.busy ? "（正忙）" : "")
        : "对方无回执"))
    sender.stop()
    exit(fresh ? 0 : 1)
}

// 不出声的接收端：收包 + 模拟播放端拉取，用来和另一端的实现对测。--recvnull PORT 秒
if let i = argv.firstIndex(of: "--recvnull") {
    let port = (i + 1 < argv.count ? UInt16(argv[i + 1]) : nil) ?? NetReceiver.defaultPort
    let secs = (i + 2 < argv.count ? Double(argv[i + 2]) : nil) ?? 5
    let bridge = RingBridge(seconds: 2.0, sampleRate: 48_000)
    let receiver = NetReceiver(bridge: bridge, port: port)
    receiver.reportInfo = { (bridge.lastFillFrames / 48, 40, false) }
    do { try receiver.start() } catch { print("端口 \(port) 绑定失败"); exit(1) }
    let disc = Discovery()
    disc.role = Proto.roleReceiver
    disc.audioPort = port
    var peers: [Peer] = []
    disc.onChange = { peers = $0 }
    disc.start()
    nonisolated(unsafe) var stop = false
    // 发送端停发 2.5 s 后接收端会把这一路撤掉，stats 就清零了；
    // 这里留一份「最热闹时」的快照，结束时打印它，免得把结果误读成「一个包都没收到」。
    nonisolated(unsafe) var best = receiver.stats
    nonisolated(unsafe) var peak: Float = 0
    nonisolated(unsafe) var crossings = 0
    nonisolated(unsafe) var loud = 0
    let t = Thread {
        var out = [Float](repeating: 0, count: 480 * 2)
        var prev: Float = 0
        var next = DispatchTime.now().uptimeNanoseconds
        while !stop {
            out.withUnsafeMutableBufferPointer {
                bridge.pull($0.baseAddress!, frames: 480, outRate: 48_000, inRate: receiver.stats.rate, targetFrames: 0.04 * receiver.stats.rate)
            }
            let cur = receiver.stats
            if cur.packets > best.packets { best = cur }
            for k in 0..<480 {
                let v = out[k * 2]
                if abs(v) > peak { peak = abs(v) }
                if abs(v) > 0.01 { loud += 1 }
                if prev <= 0 && v > 0 { crossings += 1 }
                prev = v
            }
            next += 10_000_000
            let now = DispatchTime.now().uptimeNanoseconds
            if next > now { usleep(UInt32((next - now) / 1000)) }
        }
    }
    t.start()
    RunLoop.main.run(until: Date().addingTimeInterval(secs))
    stop = true
    let s = best
    let hz = loud > 4800 ? Double(crossings) / (Double(loud) / 48_000) : 0
    print("收 \(s.packets) 包 丢 \(s.lost) 迟 \(s.late) · 来自「\(s.senderName)」\(s.sender) · \(Int(s.rate)) Hz · 峰值 \(String(format: "%.3f", peak)) · 音高约 \(String(format: "%.0f", hz)) Hz")
    print("发现的设备：" + (peers.isEmpty ? "无" : peers.map { "\($0.name)[\($0.platformLabel) 角色\($0.role) \($0.ip):\($0.port)]" }.joined(separator: "，")))
    receiver.stop()
    exit(s.packets > 0 ? 0 : 1)
}

if argv.contains("--nettest") {
    exit(NetTest.run())
}

if let i = argv.firstIndex(of: "--sendsys") {
    guard i + 1 < argv.count else { print("缺 HOST"); exit(2) }
    let port = (i + 2 < argv.count ? UInt16(argv[i + 2]) : nil) ?? Proto.audioPort
    guard let addr = Sock.resolve(host: argv[i + 1], port: port) else { print("地址无法解析"); exit(2) }
    let sender = NetSender()
    let tap = SystemAudioTap()
    tap.onAudio = { p, n, rate in sender.push(p, frames: n, rate: rate) }
    do {
        try sender.start(to: addr)
        try tap.start(muteLocal: argv.contains("--mute"))
    } catch {
        print("启动失败：\(error.localizedDescription)")
        exit(1)
    }
    print("系统声音 → \(argv[i + 1]):\(port)（\(Int(tap.sampleRate)) Hz），Ctrl-C 结束")
    signal(SIGINT, SIG_IGN)
    let sig = DispatchSource.makeSignalSource(signal: SIGINT, queue: .main)
    sig.setEventHandler { tap.stop(); sender.stop(); exit(0) }
    sig.resume()
    Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { _ in
        let s = sender.stats
        let (l, r) = sender.takePeaks()
        let rep = s.report
        let fresh = Date().timeIntervalSince(rep.at) < 2
        print(String(format: "已发 %d 包 · 峰值 L %.3f R %.3f · %@", s.packets, l, r,
                     fresh ? (rep.busy ? "对方正忙" : String(format: "对方已收 %d 丢 %d 缓冲 %.0f ms", rep.packets, rep.lost, rep.bufferMs)) : "对方无回应"))
    }
    RunLoop.main.run()
}

if argv.contains("--recv") {
    let bridge = RingBridge(seconds: 2.0, sampleRate: 48_000)
    let player = PlayerEngine(bridge: bridge)
    let receiver = NetReceiver(bridge: bridge)
    receiver.onRateChange = { player.inputRate = $0 }
    player.targetMs = 40
    player.volume = 0.8
    do {
        try receiver.start()
        try player.start(outputDeviceID: nil)
    } catch {
        print("启动失败：\(error.localizedDescription)")
        exit(1)
    }
    print("收听中，端口 \(NetReceiver.defaultPort)（Ctrl-C 结束）")
    var tick = 0
    Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { _ in
        tick += 1
        let s = receiver.stats
        let fill = bridge.lastFillFrames / max(player.inputRate, 1) * 1000
        print(String(format: "[%3ds] 包=%-6d 丢=%-3d 迟=%-3d 抖动=%.1fms 最大间隔=%.0fms 缓冲=%.0fms %@",
                     tick, s.packets, s.lost, s.late, s.jitterMs, s.gapMaxMs, fill,
                     receiver.isLinked ? "● \(s.senderName) \(s.sender)" : "○ 无信号"))
    }
    RunLoop.main.run()
}

// 冒烟测试：不开窗口，把 AppModel 的真实启停流程走一遍（收 → 发 → 停），结束后把偏好恢复原样。
if argv.contains("--smoke") {
    _ = NSApplication.shared
    let d = UserDefaults.standard
    let keys = ["role", "running", "useManual", "manualHost"]
    let saved = keys.map { d.object(forKey: $0) }
    let m = AppModel()
    m.start()
    func step(_ what: String, _ secs: Double) {
        RunLoop.main.run(until: Date().addingTimeInterval(secs))
        print("\(what) → [\(m.status.state)] \(m.status.title)｜\(m.status.detail)")
    }
    m.setRole(.receiver); m.setRunning(true)
    step("接收模式", 1.5)
    m.setRole(.sender)
    m.manualHost = "127.0.0.1:47859"
    m.selectManual()
    step("发送模式（目标无人接听）", 3.2)
    m.setRunning(false)
    step("停止", 0.4)
    m.setRole(.receiver)
    m.shutdown()
    for (k, v) in zip(keys, saved) { if let v { d.set(v, forKey: k) } else { d.removeObject(forKey: k) } }
    print("输出设备 \(m.outputDevices.count) 个，本机地址 \(m.localAddress)")
    exit(0)
}

// 界面预览：--snapshot 输出目录。把几种典型状态各渲染一张 PNG（明/暗），改界面后不用手动点一遍。
if let i = argv.firstIndex(of: "--snapshot"), i + 1 < argv.count {
    _ = NSApplication.shared
    let dir = URL(fileURLWithPath: argv[i + 1], isDirectory: true)
    try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    for kind in ["receiver-live", "receiver-wait", "sender-live", "sender-warn", "off"] {
        for dark in [false, true] {
            let m = AppModel()
            m.loadDemo(kind)
            let host = NSHostingView(rootView: MainView(model: m))
            host.frame = NSRect(x: 0, y: 0, width: 400, height: 560)
            host.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
            let win = NSWindow(contentRect: host.frame, styleMask: [.borderless], backing: .buffered, defer: false)
            win.appearance = host.appearance
            win.contentView = host
            host.layoutSubtreeIfNeeded()
            RunLoop.main.run(until: Date().addingTimeInterval(0.3))
            guard let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { continue }
            host.cacheDisplay(in: host.bounds, to: rep)
            let url = dir.appendingPathComponent("\(kind)-\(dark ? "dark" : "light").png")
            try? rep.representation(using: .png, properties: [:])?.write(to: url)
            print(url.path)
        }
    }
    exit(0)
}

// MARK: - 单实例保护

let supportDir = FileManager.default.homeDirectoryForCurrentUser
    .appendingPathComponent("Library/Application Support/MonitorAudioBridge", isDirectory: true)
try? FileManager.default.createDirectory(at: supportDir, withIntermediateDirectories: true)
let lockPath = supportDir.appendingPathComponent("instance.lock").path
let lockFD = open(lockPath, O_CREAT | O_RDWR, 0o644)
let showNote = Notification.Name("com.dafeiyu.monitor-audio.show")
let ackNote = Notification.Name("com.dafeiyu.monitor-audio.shown")
if lockFD >= 0 && flock(lockFD, LOCK_EX | LOCK_NB) != 0 {
    // 已经有一个在跑：叫它把窗口亮出来。0.8 s 内没应答，说明占着位置的是不认识这个通知的 1.x 旧版。
    var acked = false
    let dnc = DistributedNotificationCenter.default()
    let obs = dnc.addObserver(forName: ackNote, object: nil, queue: .main) { _ in acked = true }
    dnc.postNotificationName(showNote, object: nil, userInfo: nil, deliverImmediately: true)
    RunLoop.main.run(until: Date().addingTimeInterval(0.8))
    dnc.removeObserver(obs)
    if !acked {
        let a = NSApplication.shared
        a.setActivationPolicy(.accessory)
        a.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "旧版「显示器音频桥」还在运行"
        alert.informativeText = "请先点菜单栏里的喇叭图标 → 退出，然后重新打开音频桥。"
        alert.runModal()
    }
    exit(0)
}

let app = NSApplication.shared
let delegate = AppDelegate()
delegate.startHidden = argv.contains("--background")
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
