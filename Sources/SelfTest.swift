import Foundation
import Darwin

/// 测试用的 UDP 发送端：把一段 440 Hz 正弦按 5 ms 一包实时推出去。
/// 用来在没有 Windows 的情况下验证「收包 → 环形缓冲 → 播放」整条链路。
final class ToneSender {
    let host: String
    let port: UInt16
    let rate: Double = 48_000
    let framesPerPacket = 240
    let seconds: Double
    private var fd: Int32 = -1
    private var seq: UInt32 = 0
    private var phase: Double = 0
    private var thread: Thread?
    private var running = false

    init(host: String, port: UInt16, seconds: Double = 0) {
        self.host = host
        self.port = port
        self.seconds = seconds
    }

    func start() throws {
        fd = socket(AF_INET, SOCK_DGRAM, 0)
        guard fd >= 0 else { throw POSIXError(.ENFILE) }
        running = true
        let t = Thread { [weak self] in self?.loop() }
        t.qualityOfService = .userInteractive
        thread = t
        t.start()
    }

    func stop() {
        running = false
    }

    private func loop() {
        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = port.bigEndian
        inet_pton(AF_INET, host, &addr.sin_addr)

        let packetFrames = framesPerPacket
        let interval: UInt64 = UInt64(Double(packetFrames) / rate * 1_000_000_000)
        var payload = [UInt8](repeating: 0, count: 24 + packetFrames * 2 * 2)
        var t0 = DispatchTime.now().uptimeNanoseconds
        var index: UInt64 = 0
        let totalPackets = seconds > 0 ? UInt64(seconds * rate / Double(packetFrames)) : 0

        while running {
            if totalPackets > 0 && index >= totalPackets { break }

            var pcm = [Int16](repeating: 0, count: packetFrames * 2)
            for i in 0..<packetFrames {
                let v = Float(sin(phase) * 0.2)
                phase += 2 * Double.pi * 440 / rate
                if phase > 2 * Double.pi { phase -= 2 * Double.pi }
                let s = Int16(max(-1, min(1, v)) * 32_000)
                pcm[i * 2] = s
                pcm[i * 2 + 1] = s
            }

            payload[0] = 0x4D; payload[1] = 0x41; payload[2] = 0x42; payload[3] = 0x31
            payload[4] = 1
            payload[5] = 2
            writeU32(&payload, 6, UInt32(rate))
            writeU32(&payload, 10, seq)
            writeU16(&payload, 14, UInt16(packetFrames))
            writeU64(&payload, 16, DispatchTime.now().uptimeNanoseconds)
            for i in 0..<(packetFrames * 2) {
                let s = UInt16(bitPattern: pcm[i])
                payload[24 + i * 2] = UInt8(s & 0xFF)
                payload[24 + i * 2 + 1] = UInt8(s >> 8)
            }

            payload.withUnsafeBytes { raw in
                withUnsafePointer(to: &addr) { ap in
                    ap.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                        _ = sendto(fd, raw.baseAddress, raw.count, 0, sa,
                                   socklen_t(MemoryLayout<sockaddr_in>.size))
                    }
                }
            }

            seq &+= 1
            index += 1
            t0 += interval
            waitUntil(t0)
        }
        running = false
    }

    private func waitUntil(_ deadline: UInt64) {
        while true {
            let now = DispatchTime.now().uptimeNanoseconds
            if now >= deadline { return }
            let remain = deadline - now
            if remain > 1_500_000 {
                Thread.sleep(forTimeInterval: 0.001)
            }
        }
    }

    private func writeU16(_ b: inout [UInt8], _ o: Int, _ v: UInt16) {
        b[o] = UInt8(v & 0xFF); b[o + 1] = UInt8(v >> 8)
    }

    private func writeU32(_ b: inout [UInt8], _ o: Int, _ v: UInt32) {
        for i in 0..<4 { b[o + i] = UInt8((v >> (8 * UInt32(i))) & 0xFF) }
    }

    private func writeU64(_ b: inout [UInt8], _ o: Int, _ v: UInt64) {
        for i in 0..<8 { b[o + i] = UInt8((v >> (8 * UInt64(i))) & 0xFF) }
    }
}

// MARK: - 自测

enum SelfTest {
    /// 收自己发的 440 Hz，从混音器 tap 抓回来分析：RMS 够大、过零点数对得上，就算整条链路通了。
    static func run(seconds: Double = 3.0) -> Int32 {
        let bridge = RingBridge(seconds: 2.0, sampleRate: 48_000)
        let player = PlayerEngine(bridge: bridge)
        let receiver = NetReceiver(bridge: bridge, port: 47_831 + 7)  // 用旁边端口，避免和正在跑的面板 App 打架
        receiver.onRateChange = { player.inputRate = $0 }
        player.volume = 0.15
        player.targetMs = 40

        var captured: [Float] = []
        var capturedRate: Double = 48_000
        let lock = NSLock()
        player.onCapture = { buf, rate in
            lock.lock()
            captured.append(contentsOf: buf)
            capturedRate = rate
            lock.unlock()
        }

        do {
            try receiver.start()
            try player.start(outputDeviceID: nil)
            player.startCapture()
        } catch {
            print("FAIL 启动失败：\(error.localizedDescription)")
            return 1
        }

        let sender = ToneSender(host: "127.0.0.1", port: 47_831 + 7, seconds: seconds)
        do { try sender.start() } catch {
            print("FAIL 发送端启动失败：\(error.localizedDescription)")
            return 1
        }

        Thread.sleep(forTimeInterval: seconds + 0.6)
        sender.stop()
        Thread.sleep(forTimeInterval: 0.2)

        let stats = receiver.stats
        player.stopCapture()
        receiver.stop()
        player.stop()

        lock.lock()
        let samples = captured
        lock.unlock()

        // 掐掉头尾的静音（预缓冲、断流都会留静音），只在有声段上量过零点，
        // 否则静音会把计数拉低，频率看着像 410 Hz 那种假偏低。
        let win = Int(capturedRate * 0.01) * 2      // 10 ms（交织双声道）
        var windowRMS: [Double] = []
        var peakRMS: Double = 0
        var i0 = 0
        while i0 + win <= samples.count {
            var s: Double = 0
            for i in i0..<(i0 + win) { s += Double(samples[i]) * Double(samples[i]) }
            let r = (s / Double(win)).squareRoot()
            windowRMS.append(r)
            if r > peakRMS { peakRMS = r }
            i0 += win
        }
        let floor = peakRMS * 0.3        // 明显低于主体的当作静音/尾音
        guard peakRMS > 0.005,
              let firstLoud = windowRMS.firstIndex(where: { $0 > floor }),
              let lastLoud = windowRMS.lastIndex(where: { $0 > floor }) else {
            print("FAIL 抓到的音频基本是静音（峰值 RMS \(peakRMS)）")
            print("收到包 \(stats.packets)，丢 \(stats.lost)")
            return 1
        }
        let body = Array(samples[(firstLoud * win)..<min(samples.count, (lastLoud + 1) * win)])

        // 两端各让开 150 ms：避开预缓冲起点、淡出和尾音，量出来的频率才准。
        let guardLen = Int(capturedRate * 0.15) * 2
        let core = body.count > guardLen * 3 ? Array(body[guardLen..<(body.count - guardLen)]) : body

        var sum: Double = 0
        for v in core { sum += Double(v) * Double(v) }
        let rms = (sum / Double(core.count)).squareRoot()

        // 用 0.2 s 小窗逐个估频率再取中位：比整段数过零点更能抗头尾的淡入淡出和断流。
        var rates: [Double] = []
        let wLen = Int(capturedRate * 0.2) * 2
        var w0 = 0
        while w0 + wLen <= core.count {
            var e: Double = 0
            for i in w0..<(w0 + wLen) { e += Double(core[i]) * Double(core[i]) }
            if (e / Double(wLen)).squareRoot() > peakRMS * 0.9 {
                var c = 0
                var prev = core[w0]
                for i in (w0 + 1)..<(w0 + wLen) {
                    if (prev <= 0 && core[i] > 0) { c += 1 }
                    prev = core[i]
                }
                rates.append(Double(c) / (Double(wLen / 2) / capturedRate))
            }
            w0 += wLen
        }
        guard !rates.isEmpty else {
            print("FAIL 没量到稳定音高（收到包 \(stats.packets)）")
            return 1
        }
        let sortedRates = rates.sorted()
        let freq = sortedRates[sortedRates.count / 2]
        let dur = Double(core.count / 2) / capturedRate

        let wav = "/tmp/mab-selftest.wav"
        writeWAV(path: wav, interleaved: body, rate: capturedRate)

        print(String(format: "收包 %d · 丢 %d · 重同步 %d · 采样率 %.0f Hz", stats.packets, stats.lost, stats.resyncs, capturedRate))
        print(String(format: "RMS %.4f · 估计频率 %.1f Hz · 有声 %.1f s", rms, freq, dur))
        if let lo = rates.min(), let hi = rates.max() {
            print(String(format: "音高稳定度 %.1f–%.1f Hz（0.2 s 窗 %d 段）", lo, hi, rates.count))
        }
        let span = windowRMS[firstLoud...min(lastLoud, windowRMS.count - 1)]
        let holes = span.filter { $0 < peakRMS * 0.1 }.count
        print("中途空档 \(holes) 个（10 ms 窗，有声区间内）")
        print("录音：\(wav)")

        // 断流尾音：发送端停下之后不该再有任何声音。以前环形缓冲会把最后 40 ms 反复重放，
        // 听感是一串「嗒嗒嗒」，所以留一条硬指标防回归。
        let tailCount = min(samples.count, Int(capturedRate * 0.5) * 2)
        var tailSum: Double = 0
        if tailCount > 0 {
            for i in (samples.count - tailCount)..<samples.count { tailSum += Double(samples[i]) * Double(samples[i]) }
        }
        let tailRMS = tailCount > 0 ? (tailSum / Double(tailCount)).squareRoot() : 0
        print(String(format: "断流尾音 RMS %.5f（最后 0.5 s，应接近 0）", tailRMS))

        let okRMS = rms > 0.005
        let okTail = tailRMS < 0.002
        let okFreq = abs(freq - 440) < 40
        if okRMS && okFreq && okTail && stats.packets > 100 {
            print("PASS 链路通：网络包 → 环形缓冲 → 扬声器 全链路有真实音频，断流后干净")
            return 0
        }
        print("FAIL rms=\(okRMS) freq=\(okFreq) tail=\(okTail) packets=\(stats.packets)")
        return 1
    }

    static func writeWAV(path: String, interleaved: [Float], rate: Double) {
        var data = Data()
        let n = interleaved.count
        let byteRate = UInt32(rate) * 2 * 2
        func u32(_ v: UInt32) -> Data { var x = v.littleEndian; return withUnsafeBytes(of: &x) { Data($0) } }
        func u16(_ v: UInt16) -> Data { var x = v.littleEndian; return withUnsafeBytes(of: &x) { Data($0) } }

        data.append("RIFF".data(using: .ascii)!)
        data.append(u32(UInt32(36 + n * 2)))
        data.append("WAVE".data(using: .ascii)!)
        data.append("fmt ".data(using: .ascii)!)
        data.append(u32(16))
        data.append(u16(1))
        data.append(u16(2))
        data.append(u32(UInt32(rate)))
        data.append(u32(byteRate))
        data.append(u16(4))
        data.append(u16(16))
        data.append("data".data(using: .ascii)!)
        data.append(u32(UInt32(n * 2)))
        var pcm = [Int16](repeating: 0, count: n)
        for i in 0..<n {
            pcm[i] = Int16(max(-1, min(1, interleaved[i])) * 32_000)
        }
        pcm.withUnsafeBytes { data.append(contentsOf: $0) }
        try? data.write(to: URL(fileURLWithPath: path))
    }
}

// MARK: - 静音自测（不碰声卡，只验证协议 / 网络 / 缓冲逻辑）

enum NetTest {
    private static var failures = 0

    private static func check(_ ok: Bool, _ what: String) {
        print((ok ? "  ✓ " : "  ✗ ") + what)
        if !ok { failures += 1 }
    }

    private static func pump(_ seconds: Double) {
        RunLoop.main.run(until: Date().addingTimeInterval(seconds))
    }

    static func run() -> Int32 {
        failures = 0
        let port: UInt16 = Proto.audioPort + 9
        let bridge = RingBridge(seconds: 2.0, sampleRate: 48_000)
        let receiver = NetReceiver(bridge: bridge, port: port)
        receiver.reportInfo = { (33, 40, true) }
        do { try receiver.start() } catch {
            print("FAIL 端口 \(port) 绑定失败"); return 1
        }
        let addr = Sock.addr(ip: "127.0.0.1", port: port)!

        print("1. 只有保活、没有音频（对方系统静音时的情形）")
        let sender = NetSender()
        try? sender.start(to: addr)
        pump(1.3)
        var rs = receiver.stats
        check(receiver.isLinked, "接收端显示已连接")
        check(!receiver.isStreaming, "接收端知道当前没有音频")
        check(!rs.senderName.isEmpty, "接收端拿到对方设备名「\(rs.senderName)」")
        var rep = sender.stats.report
        check(Date().timeIntervalSince(rep.at) < 1.5, "发送端收到回执")
        check(rep.muted && rep.targetMs == 40 && rep.bufferMs == 33, "回执字段正确（静音/目标/缓冲）")
        check(rep.peerID == Proto.instanceID && !rep.name.isEmpty, "回执带设备号与设备名")

        print("2. 音频流：发 1.5 s 440 Hz，模拟播放端按 48 kHz 拉取")
        var pulled: [Float] = []
        nonisolated(unsafe) var stopPull = false
        let pullThread = Thread {
            var out = [Float](repeating: 0, count: 480 * 2)
            var next = DispatchTime.now().uptimeNanoseconds
            while !stopPull {
                out.withUnsafeMutableBufferPointer {
                    bridge.pull($0.baseAddress!, frames: 480, outRate: 48_000, inRate: 44_100, targetFrames: 0.04 * 44_100)
                }
                pulled.append(contentsOf: out)
                next += 10_000_000
                let now = DispatchTime.now().uptimeNanoseconds
                if next > now { usleep(UInt32((next - now) / 1000)) }
            }
        }
        pullThread.start()
        // 用 44.1 kHz 发，顺带验证收发采样率不同时的重采样
        let feed = Thread {
            var buf = [Float](repeating: 0, count: 441 * 2)
            var phase = 0.0
            var next = DispatchTime.now().uptimeNanoseconds
            for _ in 0..<150 {
                for i in 0..<441 {
                    let v = Float(sin(phase) * 0.3)
                    phase += 2 * Double.pi * 440 / 44_100
                    buf[i * 2] = v; buf[i * 2 + 1] = v
                }
                buf.withUnsafeBufferPointer { sender.push($0.baseAddress!, frames: 441, rate: 44_100) }
                next += 10_000_000
                let now = DispatchTime.now().uptimeNanoseconds
                if next > now { usleep(UInt32((next - now) / 1000)) }
            }
        }
        feed.start()
        pump(1.0)
        check(receiver.isStreaming, "接收端显示正在接收")
        pump(0.9)
        stopPull = true
        pump(0.1)
        rs = receiver.stats
        rep = sender.stats.report
        check(rs.packets > 250 && rs.lost == 0, "收到 \(rs.packets) 包，丢 \(rs.lost)")
        check(rs.rate == 44_100, "识别出发送端采样率 44.1 kHz")
        check(rep.packets > 100, "回执里的收包数在涨（\(rep.packets)）")
        check(rs.gapMaxMs > 0 && rs.gapMaxMs < 100, String(format: "最大到达间隔 %.1f ms", rs.gapMaxMs))
        // 分析拉出来的音频：有声段内应连续、频率 440
        let mono = stride(from: 0, to: pulled.count, by: 2).map { pulled[$0] }
        if let first = mono.firstIndex(where: { abs($0) > 0.05 }), let last = mono.lastIndex(where: { abs($0) > 0.05 }),
           last - first > 24_000 {
            let core = Array(mono[(first + 2400)..<(last - 2400)])
            var crossings = 0, maxFlat = 0, flat = 0
            for i in 1..<core.count {
                if core[i - 1] <= 0 && core[i] > 0 { crossings += 1 }
                if abs(core[i]) < 0.001 { flat += 1; maxFlat = max(maxFlat, flat) } else { flat = 0 }
            }
            let freq = Double(crossings) / (Double(core.count) / 48_000)
            check(abs(freq - 440) < 4, String(format: "重采样后音高 %.1f Hz", freq))
            check(maxFlat < 48, "有声段内没有空洞（最长静音 \(maxFlat) 帧）")
        } else {
            check(false, "拉取到的音频太短或是静音")
        }

        print("3. 停止发送：再见包让对方立刻回到等待")
        sender.stop()
        pump(0.15)
        check(!receiver.isLinked, "接收端 0.15 s 内显示断开")

        print("4. 丢包 / 乱序 / 重复")
        let raw = socket(AF_INET, SOCK_DGRAM, 0)
        func audio(_ seq: UInt32) {
            var b = [UInt8](repeating: 0, count: Proto.headerSize + 240 * 4)
            Proto.putMagic(&b, type: Proto.typeAudio)
            b[5] = 2
            Proto.putU32(&b, 6, 48_000)
            Proto.putU32(&b, 10, seq)
            Proto.putU16(&b, 14, 240)
            Sock.send(raw, b, to: addr)
            usleep(3000)
        }
        for s: UInt32 in [0, 1, 2, 4, 3, 5, 5, 6, 9, 10] { audio(s) }
        pump(0.2)
        rs = receiver.stats
        check(rs.packets == 8, "有效包 8（实际 \(rs.packets)）")
        check(rs.lost == 3, "记丢包 3：序号 3、7、8（实际 \(rs.lost)）")
        check(rs.late == 2, "迟到/重复 2：序号 3、重复的 5（实际 \(rs.late)）")
        // 序号回绕
        for s: UInt32 in [5_000_000, 0xFFFF_FFFE, 0xFFFF_FFFF, 0, 1] { audio(s) }
        pump(0.2)
        let rs2 = receiver.stats
        check(rs2.resyncs == rs.resyncs + 2 && rs2.lost == rs.lost && rs2.packets == rs.packets + 5,
              "序号大跳变按重同步处理，回绕不误报丢包")
        Sock.send(raw, Proto.bye(), to: addr)
        close(raw)
        pump(0.15)
        check(!receiver.isLinked, "收到再见包后断开")

        print("5. 畸形包不崩溃")
        let junk = socket(AF_INET, SOCK_DGRAM, 0)
        for n in [0, 1, 4, 5, 23, 24, 25, 33, 200] {
            for t: UInt8 in [1, 2, 3, 4, 16, 99] {
                var b = [UInt8](repeating: 0xFF, count: max(n, 0))
                if n >= 5 { Proto.putMagic(&b, type: t) }
                if n > 0 { Sock.send(junk, b, to: addr) }
                if n > 0, let d = Sock.addr(ip: "127.0.0.1", port: Proto.discoveryPort) { Sock.send(junk, b, to: d) }
            }
        }
        close(junk)
        pump(0.2)
        check(receiver.isRunning, "接收端仍在运行")


        print("7. 丢包冗余：丢一包也能补回来")
        let portR: UInt16 = port + 3
        let bridgeR = RingBridge(seconds: 2.0, sampleRate: 48_000)
        let rxR = NetReceiver(bridge: bridgeR, port: portR)
        do { try rxR.start() } catch { print("FAIL 端口 \(portR) 绑定失败"); return 1 }
        let addrR = Sock.addr(ip: "127.0.0.1", port: portR)!
        func audioPacket(_ seq: UInt32, _ value: Float, trailing: Float? = nil) -> [UInt8] {
            let frames = 240
            var b = [UInt8](repeating: 0, count: 24 + frames * 4 + (trailing != nil ? frames * 4 : 0))
            Proto.putMagic(&b, type: Proto.typeAudio)
            b[5] = 2
            Proto.putU32(&b, 6, 48_000)
            Proto.putU32(&b, 10, seq)
            Proto.putU16(&b, 14, UInt16(frames))
            let v = Int16(value * 32_000)
            for i in 0..<frames {
                let o = 24 + i * 4
                b[o] = UInt8(truncatingIfNeeded: v); b[o + 1] = UInt8(truncatingIfNeeded: v >> 8)
                b[o + 2] = b[o]; b[o + 3] = b[o + 1]
            }
            if let t = trailing {
                b[23] = Proto.audioFlagRedundant
                let tv = Int16(t * 32_000)
                for i in 0..<frames {
                    let o = 24 + frames * 4 + i * 4
                    b[o] = UInt8(truncatingIfNeeded: tv); b[o + 1] = UInt8(truncatingIfNeeded: tv >> 8)
                    b[o + 2] = b[o]; b[o + 3] = b[o + 1]
                }
            }
            return b
        }
        let rawR = socket(AF_INET, SOCK_DGRAM, 0)
        Sock.send(rawR, audioPacket(1, 0.10), to: addrR)
        Sock.send(rawR, audioPacket(3, 0.12, trailing: 0.11), to: addrR)
        pump(0.2)
        let rsR = rxR.stats
        check(rsR.recovered == 1 && rsR.lost == 0, "第 2 包由下一包尾部补回（recovered \(rsR.recovered) / lost \(rsR.lost)）")
        check(rxR.isStreaming, "补回的数据照常进缓冲")
        Sock.send(rawR, Proto.bye(), to: addrR)
        close(rawR)
        rxR.stop()

        print("7b. 丢包冗余：连丢多包时副本只补回最近的一包")
        func redundancyProbe(_ probePort: UInt16, gap: UInt32) -> (lost: Int, recovered: Int, frames: Double, late: Int) {
            let b = RingBridge(seconds: 2.0, sampleRate: 48_000)
            let rx = NetReceiver(bridge: b, port: probePort)
            do { try rx.start() } catch { print("FAIL 端口 \(probePort) 绑定失败"); return (0, 0, 0, 0) }
            let a = Sock.addr(ip: "127.0.0.1", port: probePort)!
            let fd = socket(AF_INET, SOCK_DGRAM, 0)
            Sock.send(fd, audioPacket(0, 0.1), to: a)
            Sock.send(fd, audioPacket(1, 0.1), to: a)
            pump(0.08)
            // 从 2 跳到 gap，包尾带 gap-1 的副本：它补回 1 包，缺口里剩下的才隐藏。
            Sock.send(fd, audioPacket(gap, 0.1, trailing: 0.1), to: a)
            pump(0.15)
            let st = rx.stats
            let frames = b.availableFrames
            // 再补一个迟到的原包（gap-1）：时间轴上已经补过，应计为迟到并丢弃。
            Sock.send(fd, audioPacket(gap - 1, 0.1), to: a)
            pump(0.1)
            let late = rx.stats.late
            check(b.availableFrames == frames, "迟到的原包被丢弃、缓冲帧数不变（\(Int(b.availableFrames)) 帧）")
            Sock.send(fd, Proto.bye(), to: a)
            close(fd)
            rx.stop()
            return (st.lost, st.recovered, frames, late)
        }
        let gap1 = redundancyProbe(port + 5, gap: 3)
        check(gap1.frames == 960 && gap1.lost == 0 && gap1.recovered == 1,
              "缺 1 包：副本补回、不记丢包（\(Int(gap1.frames)) 帧 / lost \(gap1.lost)）")
        let gap2 = redundancyProbe(port + 6, gap: 4)
        check(gap2.frames == 1_200 && gap2.lost == 1 && gap2.recovered == 1,
              "缺 2 包：副本补 1 包、隐藏 1 包（\(Int(gap2.frames)) 帧 / lost \(gap2.lost)）")
        let gap3 = redundancyProbe(port + 7, gap: 5)
        check(gap3.frames == 1_440 && gap3.lost == 2 && gap3.recovered == 1,
              "缺 3 包：副本补 1 包、隐藏 2 包（\(Int(gap3.frames)) 帧 / lost \(gap3.lost)）")

        print("7c. 冗余包缩到 120 帧：单包 984 字节，不超过 MTU")
        do {
            let b = RingBridge(seconds: 2.0, sampleRate: 48_000)
            let rx = NetReceiver(bridge: b, port: port + 8)
            try? rx.start()
            let a = Sock.addr(ip: "127.0.0.1", port: port + 8)!
            let fd = socket(AF_INET, SOCK_DGRAM, 0)
            func packet120(_ seq: UInt32, value: Float, trailing: Float?) -> [UInt8] {
                let frames = Proto.redundantFramesPerPacket
                let body = frames * 4 * (trailing == nil ? 1 : 2)
                var p = [UInt8](repeating: 0, count: Proto.headerSize + body)
                p[0] = 0x4D; p[1] = 0x41; p[2] = 0x42; p[3] = 0x31   // MAB1
                p[4] = Proto.typeAudio
                p[5] = 2
                let rate = UInt32(48_000)
                p[6] = UInt8(rate & 0xFF); p[7] = UInt8((rate >> 8) & 0xFF)
                p[8] = UInt8((rate >> 16) & 0xFF); p[9] = UInt8((rate >> 24) & 0xFF)
                p[10] = UInt8(seq & 0xFF); p[11] = UInt8((seq >> 8) & 0xFF)
                p[12] = UInt8((seq >> 16) & 0xFF); p[13] = UInt8((seq >> 24) & 0xFF)
                p[14] = UInt8(UInt8(truncatingIfNeeded: frames) & 0xFF)
                p[14] = UInt8(frames & 0xFF); p[15] = UInt8((frames >> 8) & 0xFF)
                p[23] = trailing == nil ? 0 : Proto.audioFlagRedundant
                func fill(_ at: Int, _ v: Float) {
                    for i in 0..<frames {
                        let s = UInt16(bitPattern: Int16(max(-1, min(1, v * sin(Float(i) * 0.05))) * 32_767))
                        p[at + i * 4] = UInt8(s & 0xFF); p[at + i * 4 + 1] = UInt8(s >> 8)
                        p[at + i * 4 + 2] = UInt8(s & 0xFF); p[at + i * 4 + 3] = UInt8(s >> 8)
                    }
                }
                fill(Proto.headerSize, value)
                if let t = trailing { fill(Proto.headerSize + frames * 4, t) }
                return p
            }
            let one = packet120(0, value: 0.2, trailing: nil)
            Sock.send(fd, one, to: a)
            pump(0.05)
            let two = packet120(2, value: 0.2, trailing: 0.2)   // 尾部带 seq 1 的副本
            Sock.send(fd, two, to: a)
            pump(0.15)
            let st = rx.stats
            check(one.count == 504 && two.count == 984, "120 帧包 504 字节、带副本 984 字节（\(one.count) / \(two.count)）")
            check(b.availableFrames == 360, "接收端按包头帧数收下 3 包共 360 帧（\(Int(b.availableFrames))）")
            check(st.lost == 0 && st.recovered == 1, "120 帧的包里副本照常补回（lost \(st.lost) / recovered \(st.recovered)）")
            check(st.jitterMs.isFinite && st.jitterMs >= 0, "抖动统计对 120 帧包也正常（\(String(format: "%.2f", st.jitterMs)) ms）")
            Sock.send(fd, Proto.bye(), to: a)
            close(fd)
            rx.stop()
        }

        print("8. 多路混音：两路信号合成一路")
        // 一台机器伪造不出第二个来源 IP（macOS 的 lo0 只有 127.0.0.1，bind 127.0.0.2 是 EADDRNOTAVAIL），
        // 所以信号通路由 MixBus 单独验证，接收端只验证「收到的音频确实进了混音器」。
        func toneMag(_ buf: [Float], _ hz: Double) -> Double {
            var re = 0.0, im = 0.0
            for i in 0..<(buf.count / 2) {
                let t = Double(i) / 48_000
                let w = 2 * Double.pi * hz * t
                re += Double(buf[i * 2]) * cos(w)
                im += Double(buf[i * 2]) * sin(w)
            }
            let n = Double(max(buf.count / 2, 1))
            return 2 * sqrt(re * re + im * im) / n
        }

        // 8a. 单路 3 s 1 kHz（幅度 0.49）：稳态不许有一拍偷停，RMS 应接近 0.346。
        let bus = MixBus()
        let chA = bus.add(1, rate: 48_000)
        var phaseA = 0.0
        var bufA = [Float](repeating: 0, count: 240 * 2)
        var out = [Float](repeating: 0, count: 240 * 2)
        var rmsAcc: Float = 0
        var counted = 0
        var clicks = 0
        var prev: Float? = nil
        for round in 0..<600 {
            for i in 0..<240 {
                let v = Float(sin(phaseA)) * 0.49
                phaseA += 2 * Double.pi * 1_000 / 48_000
                bufA[i * 2] = v; bufA[i * 2 + 1] = v
            }
            bufA.withUnsafeBufferPointer { chA.push($0.baseAddress!, frames: 240) }
            pump(0.005)
            out.withUnsafeMutableBufferPointer {
                bus.pull($0.baseAddress!, frames: 240, outRate: 48_000)
            }
            // 第 100 拍（0.5 s）之后算稳态：既算 RMS，也算波形断点。
            if round >= 100 {
                for i in 0..<240 {
                    let v = out[i * 2]
                    if let p = prev, abs(v - p) > 0.12 { clicks += 1 }
                    prev = v
                    rmsAcc += v * v
                    counted += 1
                }
            }
        }
        let rmsBus = sqrt(rmsAcc / Float(max(counted, 1)))
        check(bus.channelCount == 1, "混音器接住一路")
        check(clicks == 0, "稳态 2 s 内没有波形断点（\(clicks) 处）")
        check(rmsBus > 0.33 && rmsBus < 0.36, "单路混音电平正确（RMS \(String(format: "%.3f", rmsBus))）")

        // 8b. 只停音频、保活照旧：继续按播放节奏取数 0.5 s，之后必须已经是静音，
        // 不能像以前那样把最后一块 5 ms 无限循环重放。
        var tailAcc: Float = 0
        var tailCount = 0
        for round in 0..<100 {
            out.withUnsafeMutableBufferPointer {
                bus.pull($0.baseAddress!, frames: 240, outRate: 48_000)
            }
            if round >= 60 {
                for v in out { tailAcc += v * v }
                tailCount += out.count
            }
            pump(0.005)
        }
        let tailRms = sqrt(tailAcc / Float(max(tailCount, 1)))
        check(tailRms < 0.001, "断流 0.5 s 后输出静音（RMS \(String(format: "%.5f", tailRms))）")
        bus.stop()

        // 8c. 44.1 kHz 与 48 kHz 两路同时发：两路的频率成分都要在。
        let bus2 = MixBus()
        let chC = bus2.add(11, rate: 44_100)
        let chD = bus2.add(12, rate: 48_000)
        var phaseC = 0.0, phaseD = 0.0
        var carryC = 0.0
        var bufC = [Float](repeating: 0, count: 240 * 2)
        var bufD = [Float](repeating: 0, count: 240 * 2)
        var collected: [Float] = []
        for _ in 0..<500 {
            carryC += 0.005 * 44_100
            let nC = Int(carryC.rounded(.down))
            carryC -= Double(nC)
            for i in 0..<nC {
                let v = Float(sin(phaseC)) * 0.3
                phaseC += 2 * Double.pi * 440 / 44_100
                bufC[i * 2] = v; bufC[i * 2 + 1] = v
            }
            bufC.withUnsafeBufferPointer { chC.push($0.baseAddress!, frames: nC) }
            for i in 0..<240 {
                let v = Float(sin(phaseD)) * 0.3
                phaseD += 2 * Double.pi * 660 / 48_000
                bufD[i * 2] = v; bufD[i * 2 + 1] = v
            }
            bufD.withUnsafeBufferPointer { chD.push($0.baseAddress!, frames: 240) }
            pump(0.005)
            out.withUnsafeMutableBufferPointer {
                bus2.pull($0.baseAddress!, frames: 240, outRate: 48_000)
            }
            if collected.count > 48_000 { collected.removeFirst(24_000) }
            collected.append(contentsOf: out)
        }
        let mag440 = toneMag(collected, 440)
        let mag660 = toneMag(collected, 660)
        check(mag440 > 0.05 && mag660 > 0.05,
              "44.1k 与 48k 两路成分都在（440 Hz \(String(format: "%.2f", mag440)) / 660 Hz \(String(format: "%.2f", mag660))）")
        bus2.stop()

        // 接收端集成：单路音频应当走混音器，而不是直接进主缓冲。
        let masterM = RingBridge(seconds: 3.0, sampleRate: 48_000)
        let portM: UInt16 = port + 4
        let rxM = NetReceiver(bridge: masterM, port: portM)
        rxM.mixEnabled = true
        do { try rxM.start() } catch { print("FAIL 端口 \(portM) 绑定失败"); return 1 }
        let toneM = ToneSender(host: "127.0.0.1", port: portM, seconds: 1.6)
        try? toneM.start()
        var accM: Float = 0
        var countedM = 0
        var outM = [Float](repeating: 0, count: 240 * 2)
        for round in 0..<300 {
            pump(0.005)
            if let mx = rxM.mixer {
                outM.withUnsafeMutableBufferPointer {
                    mx.pull($0.baseAddress!, frames: 240, outRate: 48_000)
                }
            }
            if round >= 80 {
                for v in outM { accM += v * v }
                countedM += outM.count
            }
        }
        let rmsM = sqrt(accM / Float(max(countedM, 1)))
        check(rxM.stats.mixing && rxM.mixedChannelCount == 1, "接收端的音频进了混音器")
        // 测试音幅度 0.2（RMS≈0.14），混音链路留了点抖动余量，0.05 以上就算真的出了声音。
        check(rmsM > 0.05, "混音输出有声音（RMS \(String(format: "%.3f", rmsM))）")
        toneM.stop()
        rxM.stop()

        print("6. 设备发现")
        let disc = Discovery()
        disc.listenPort = Proto.discoveryPort &+ 3      // 避开可能正在运行的 GUI 实例
        var seen: [Peer] = []
        disc.onChange = { seen = $0 }
        disc.start()
        let bsock = socket(AF_INET, SOCK_DGRAM, 0)
        var beacon = [UInt8](repeating: 0, count: 21 + 61)
        Proto.putMagic(&beacon, type: Proto.typeBeacon)
        beacon[5] = Proto.version; beacon[6] = Proto.roleReceiver; beacon[7] = Proto.platformWindows; beacon[8] = 1
        Proto.putU16(&beacon, 10, 47_831)
        Proto.putU64(&beacon, 12, 0x1234_5678_9ABC_DEF0)
        let end = Proto.putName(&beacon, 20, "书房-PC")
        beacon.withUnsafeBytes { _ = Sock.send(bsock, $0.baseAddress!, end, to: Sock.addr(ip: "127.0.0.1", port: disc.listenPort)!) }
        close(bsock)
        pump(0.4)
        let p = seen.first { $0.id == 0x1234_5678_9ABC_DEF0 }
        check(p != nil && p!.name == "书房-PC" && p!.isReceiver && p!.port == 47_831 && p!.state == 1
                && p!.platform == Proto.platformWindows, "解析出对端信标（名字/角色/端口/状态）")
        pump(5.5)
        check(!seen.contains { $0.id == 0x1234_5678_9ABC_DEF0 }, "对端下线约 5 s 后从列表移除")
        disc.stop()

        print("9. 受信任设备表")
        let tmpDir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("mab-trust-\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: tmpDir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: tmpDir) }
        let trustURL = tmpDir.appendingPathComponent("trust.json")
        let t = TrustStore(url: trustURL)
        let senderID: UInt64 = 0x0BAD_F00D_1234_5678
        let k1 = t.grant(id: senderID, name: "客厅 Mac")
        check(k1 != 0 && t.verify(id: senderID, key: k1), "允许后凭密钥可验证")
        check(!t.verify(id: senderID, key: k1 &+ 1), "错误密钥被拒绝")
        let k2 = t.grant(id: senderID, name: "客厅 Mac mini")
        check(k2 == k1 && t.devices.count == 1, "再次允许沿用同一把密钥并更新名字")
        t.setKey(0xDEAD_BEEF_0000_0001, forSender: senderID)
        let reload = TrustStore(url: trustURL)
        check(reload.devices.count == 1 && reload.devices[0].name == "客厅 Mac mini"
                && reload.verify(id: senderID, key: k1), "写盘后重新读取，信任关系还在")
        check(reload.key(forSender: senderID) == 0xDEAD_BEEF_0000_0001, "接收端保存的发送端密钥也持久化")
        reload.revoke(id: senderID)
        check(!reload.verify(id: senderID, key: k1) && reload.devices.isEmpty, "移除后不再信任")

        print("10. 拉流控制包（v4 质询-应答，发现端口往返）")
        let ctlA = Discovery(); ctlA.listenPort = Proto.discoveryPort &+ 5
        let ctlB = Discovery(); ctlB.listenPort = Proto.discoveryPort &+ 6
        var gotPull: [UInt8]?
        var gotAuth: [UInt8]?
        var gotChallenge: [UInt8]?
        var gotReply: [UInt8]?
        ctlB.onMessage = { type, data, _ in
            if type == Proto.typePull { gotPull = data }
            if type == Proto.typePullAuth { gotAuth = data }
        }
        ctlA.onMessage = { type, data, _ in
            if type == Proto.typePullChallenge { gotChallenge = data }
            if type == Proto.typePullReply { gotReply = data }
        }
        ctlA.role = Proto.roleReceiver
        ctlB.role = Proto.roleSender
        ctlA.start()
        ctlB.start()
        ctlA.sendControl(Proto.pull(target: 0, audioPort: Proto.audioPort), to: "127.0.0.1", port: ctlB.listenPort)
        pump(0.4)
        if let d = gotPull, d.count >= 25 {
            check(Proto.u16(d, 6) == Proto.audioPort && Proto.u64(d, 16) == Proto.instanceID
                    && !Proto.name(d, 24, count: d.count).isEmpty,
                  "对方收到拉流请求（音频端口/设备号/名字）")
            check(Proto.name(d, 24, count: d.count) == Proto.deviceName && d.count < 25 + 61,
                  "v4 的拉流请求里不再带长期密钥（名字直接接在设备号后面）")
        } else {
            check(false, "没有收到拉流请求")
        }
        let cnonce: UInt64 = 0x0102_0304_0506_0708
        ctlB.sendControl(Proto.pullChallenge(to: Proto.instanceID, nonce: cnonce, senderID: 0xAAAA_BBBB_CCCC_DDDD),
                         to: "127.0.0.1", port: ctlA.listenPort)
        pump(0.4)
        if let d = gotChallenge, d.count >= 33 {
            check(Proto.u64(d, 24) == cnonce && Proto.u64(d, 16) == 0xAAAA_BBBB_CCCC_DDDD,
                  "收到质询（随机数/发送端设备号）")
        } else {
            check(false, "没有收到质询")
        }
        let mackey: UInt64 = 0x1122_3344_5566_7788
        let mac = PullAuth.mac(key: mackey, nonce: cnonce, requester: Proto.instanceID, senderID: 0xAAAA_BBBB_CCCC_DDDD)
        ctlA.sendControl(Proto.pullAuth(to: 0xAAAA_BBBB_CCCC_DDDD, nonce: cnonce, mac: mac, audioPort: Proto.audioPort),
                         to: "127.0.0.1", port: ctlB.listenPort)
        pump(0.4)
        if let d = gotAuth, d.count >= 49 {
            check(Proto.u64(d, 8) == cnonce && Proto.u64(d, 24) == 0xAAAA_BBBB_CCCC_DDDD
                    && Array(d[32..<48]) == mac && Proto.u16(d, 6) == Proto.audioPort,
                  "收到鉴权包（随机数/设备号/MAC/音频端口）")
        } else {
            check(false, "没有收到鉴权包")
        }
        ctlB.sendControl(Proto.pullReply(to: Proto.instanceID, result: Proto.pullOK, token: 0xDEAD_0000_0000_0001, key: 0),
                         to: "127.0.0.1", port: ctlA.listenPort)
        pump(0.4)
        if let d = gotReply, d.count >= 41 {
            check(d[6] == Proto.pullOK && Proto.u64(d, 24) == 0xDEAD_0000_0000_0001 && d[7] & 2 != 0,
                  "收到应答（结果/会话令牌）")
        } else {
            check(false, "没有收到拉流应答")
        }
        ctlA.stop()
        ctlB.stop()

        print("10b. 静音时也认得出对方（保活也算来源）")
        do {
            let b = RingBridge(seconds: 1.0, sampleRate: 48_000)
            let rx = NetReceiver(bridge: b, port: port + 9)
            try? rx.start()
            guard let a = Sock.addr(ip: "127.0.0.1", port: port + 9) else { check(false, "本地地址构造失败"); return Int32(failures) }
            let fd = socket(AF_INET, SOCK_DGRAM, 0)
            Sock.send(fd, Proto.keepalive(rate: 48_000, seq: 1), to: a)
            pump(0.12)
            check(rx.hasSource(ip: "127.0.0.1"), "只有保活、没有音频时也能认出这台设备")
            check(rx.stats.sender == "127.0.0.1", "状态行给出这台设备")
            rx.releaseSource(ip: "127.0.0.1")
            pump(0.08)
            check(!rx.hasSource(ip: "127.0.0.1"), "换拉流目标时能放掉对旧来源的锁定")
            Sock.send(fd, Proto.bye(), to: a)
            close(fd)
            rx.stop()
        }

        print("11. 拉流鉴权")
        let authKey: UInt64 = 0x0F1E_2D3C_4B5A_6978
        let authNonce: UInt64 = 0xAAAA_1111_BBBB_2222
        let reqID: UInt64 = 0x1234_5678_9ABC_DEF0
        let sndID: UInt64 = 0x0FED_CBA9_8765_4321
        let goodMac = PullAuth.mac(key: authKey, nonce: authNonce, requester: reqID, senderID: sndID)
        check(PullAuth.verify(key: authKey, nonce: authNonce, requester: reqID, senderID: sndID, mac: goodMac),
              "正确密钥算出的 MAC 通过验证")
        check(!PullAuth.verify(key: authKey &+ 1, nonce: authNonce, requester: reqID, senderID: sndID, mac: goodMac),
              "错误密钥算出的 MAC 被拒绝")
        check(!PullAuth.verify(key: authKey, nonce: authNonce &+ 1, requester: reqID, senderID: sndID, mac: goodMac),
              "换一个随机数后旧 MAC 失效（防重放）")
        check(!PullAuth.verify(key: nil, nonce: authNonce, requester: reqID, senderID: sndID, mac: goodMac),
              "没有存过密钥时不接受任何 MAC")
        var shortMac = goodMac
        shortMac[0] ^= 0xFF
        check(!PullAuth.verify(key: authKey, nonce: authNonce, requester: reqID, senderID: sndID, mac: shortMac),
              "篡改一个字节的 MAC 被拒绝")

        // 再见：长度、来源 IP、会话令牌、是否在发送，四者缺一不可。
        let sessToken: UInt64 = 0x5555_6666_7777_8888
        let goodBye = Proto.bye(token: sessToken)
        check(PullAuth.byeAccepted(data: goodBye, from: "192.168.1.20", targetIP: "192.168.1.20",
                                   sending: true, token: sessToken), "合法再见被接受")
        check(!PullAuth.byeAccepted(data: Array(goodBye.prefix(5)), from: "192.168.1.20",
                                    targetIP: "192.168.1.20", sending: true, token: sessToken),
              "5 字节的再见包被拒绝而不是抛异常")
        check(!PullAuth.byeAccepted(data: goodBye, from: "192.168.1.99", targetIP: "192.168.1.20",
                                    sending: true, token: sessToken), "来源 IP 不对的再见被拒绝")
        check(!PullAuth.byeAccepted(data: Proto.bye(token: sessToken &+ 1), from: "192.168.1.20",
                                    targetIP: "192.168.1.20", sending: true, token: sessToken),
              "令牌不对的再见被拒绝")
        check(!PullAuth.byeAccepted(data: goodBye, from: "192.168.1.20", targetIP: "192.168.1.20",
                                    sending: false, token: sessToken), "没在发送时再见不生效")
        check(!PullAuth.byeAccepted(data: Proto.bye(token: 0), from: "192.168.1.20",
                                    targetIP: "192.168.1.20", sending: true, token: sessToken),
              "没有令牌的再见被拒绝")

        // 应答：只认正在请求的那台机器，且我们确实在等。
        let replyOK = Proto.pullReply(to: reqID, result: Proto.pullOK, token: sessToken, key: 0)
        check(PullAuth.replyAccepted(data: replyOK, from: "192.168.1.20", pullIP: "192.168.1.20",
                                     instanceID: reqID, waiting: true), "来自请求目标的应答被接受")
        check(!PullAuth.replyAccepted(data: replyOK, from: "192.168.1.30", pullIP: "192.168.1.20",
                                      instanceID: reqID, waiting: true), "别的 IP 伪造的应答被忽略")
        let waitReply = Proto.pullReply(to: reqID, result: Proto.pullWait, token: 0, key: 0)
        check(PullAuth.replyAccepted(data: waitReply, from: "192.168.1.20", pullIP: "192.168.1.20",
                                     instanceID: reqID, waiting: true)
              && waitReply[6] == Proto.pullWait,
              "「等待对方确认」的应答被认作仍在请求中（不按 20 秒判失败）")
        check(!PullAuth.replyAccepted(data: replyOK, from: "192.168.1.20", pullIP: "192.168.1.20",
                                      instanceID: reqID, waiting: false), "已经连上/取消后，重放的旧应答被忽略")
        check(!PullAuth.replyAccepted(data: Proto.pullReply(to: reqID &+ 1, result: Proto.pullOK, token: sessToken, key: 0),
                                      from: "192.168.1.20", pullIP: "192.168.1.20", instanceID: reqID, waiting: true),
              "不是发给本机的应答被忽略")

        var book = ChallengeBook()
        let nowT = Date()
        let issued = book.issue(requester: reqID, ip: "10.0.0.5", now: nowT)
        check(book.consume(requester: reqID, nonce: issued, ip: "10.0.0.5", now: nowT),
              "质询用来源 IP 和随机数换到一次放行")
        check(!book.consume(requester: reqID, nonce: issued, ip: "10.0.0.5", now: nowT),
              "同一个质询不能二次使用（防重放）")
        let issued2 = book.issue(requester: reqID, ip: "10.0.0.5", now: nowT)
        check(!book.consume(requester: reqID, nonce: issued2, ip: "10.0.0.6", now: nowT),
              "换了来源 IP 的质询不成立")
        let issued3 = book.issue(requester: reqID, ip: "10.0.0.5", now: nowT)
        check(!book.consume(requester: reqID, nonce: issued3, ip: "10.0.0.5",
                            now: nowT.addingTimeInterval(16)), "过期的质询不成立")

        var deny = PullDenyList()
        deny.deny(ip: "10.0.0.9", now: nowT)
        check(deny.isCooling(ip: "10.0.0.9", now: nowT.addingTimeInterval(10)),
              "被拒绝的来源 30 秒内直接回拒绝")
        check(!deny.isCooling(ip: "10.0.0.9", now: nowT.addingTimeInterval(31)),
              "30 秒后允许重新请求")

        print("12. 会话令牌与信标跟随")
        // A-2：提前返回（正在给同一台机器发）也必须把令牌换成新的，否则「取消」的再见对不上。
        let oldSess = PullSession(token: 0x1111, ip: "10.0.0.7", port: 47_831)
        let adoptSame = PullSession.adopt(current: oldSess, ip: "10.0.0.7", port: 47_831,
                                          token: 0x2222, alreadySendingSame: true)
        check(!adoptSame.restart && adoptSame.session.token == 0x2222,
              "同一目标重新拉流：只回应答但令牌同步更新（取消才停得掉）")
        check(adoptSame.session.ip == "10.0.0.7" && adoptSame.session.port == 47_831,
              "同一目标重新拉流：地址不变")
        let adoptNew = PullSession.adopt(current: oldSess, ip: "10.0.0.8", port: 47_831,
                                         token: 0x3333, alreadySendingSame: false)
        check(adoptNew.restart && adoptNew.session.token == 0x3333, "换目标：要重启发送并采用新令牌")
        // 连续重拉三次之后，最后一次的令牌就是我们要带进再见的那一个。
        var sess = oldSess
        for i in 0..<3 {
            sess = PullSession.adopt(current: sess, ip: "10.0.0.7", port: 47_831,
                                     token: UInt64(0x4000 + i), alreadySendingSame: true).session
        }
        let bye3 = Proto.bye(token: sess.token)
        check(PullAuth.byeAccepted(data: bye3, from: "10.0.0.7", targetIP: "10.0.0.7",
                                   sending: true, token: sess.token),
              "连续重拉 3 次后，取消的再见仍然对得上")

        // A-4：信标没有凭据，只有旧地址确实没回执时才允许跟随。
        check(!BeaconPolicy.shouldFollow(currentIP: "10.0.0.7", beaconIP: "10.0.0.99",
                                         isPullSession: true, lastReplyAge: 30),
              "拉流会话期间，同设备号的陌生信标不改目标")
        check(!BeaconPolicy.shouldFollow(currentIP: "10.0.0.7", beaconIP: "10.0.0.99",
                                         isPullSession: false, lastReplyAge: 0.4),
              "推送模式下旧地址还在回执，不跟随不同 IP 的信标")
        check(BeaconPolicy.shouldFollow(currentIP: "10.0.0.7", beaconIP: "10.0.0.99",
                                        isPullSession: false, lastReplyAge: 3.0),
              "推送模式下旧地址静默超过 2.5 秒，才跟随新地址")
        check(!BeaconPolicy.shouldFollow(currentIP: "10.0.0.7", beaconIP: "10.0.0.7",
                                         isPullSession: true, lastReplyAge: 0),
              "拉流会话期间连同一 IP 的信标也不改目标（端口同样不能改）")

        // A-3：没有密钥的信号只认「刚质询过的那台、那个 IP」。
        var book2 = ChallengeBook()
        let n1 = book2.issue(requester: reqID, ip: "10.0.0.5", now: nowT)
        let n2 = book2.issue(requester: reqID, ip: "10.0.0.5", now: nowT)
        check(n1 == n2, "重复质询给同一个 nonce（否则在途的鉴权包会被判失效）")
        check(book2.hasPending(requester: reqID, ip: "10.0.0.5", now: nowT),
              "刚质询过的来源可以发「我没有密钥」")
        check(!book2.hasPending(requester: reqID, ip: "10.0.0.6", now: nowT),
              "第三方从别的 IP 冒用设备号发「没有密钥」不被采纳")
        check(!book2.hasPending(requester: reqID &+ 9, ip: "10.0.0.5", now: nowT),
              "没质询过的设备号发「没有密钥」不被采纳")
        let nk = Proto.pullNoKey(to: 0xAABB, audioPort: 47_831)
        var nkReq: UInt64 = 0, nkSender: UInt64 = 0
        nk.withUnsafeBufferPointer { p in
            guard let b = p.baseAddress else { return }
            nkReq = Proto.u64(b, 8); nkSender = Proto.u64(b, 16)
        }
        let nkPort = nk.withUnsafeBufferPointer { Proto.u16($0.baseAddress!, 6) }
        let nkType = nk[4]
        check(nkType == Proto.typePullNoKey, "「没有密钥」包 type 正确")
        check(nkSender == 0xAABB, "「没有密钥」包带发送端设备号")
        check(nkReq == Proto.instanceID, "「没有密钥」包带请求方设备号")
        check(nkPort == 47_831, "「没有密钥」包带音频端口")

        print("13. 第二轮审查修复")
        // 1）鉴权包里的设备名：nameLen 必须落在 48，名字从 49 起。
        // 不写「读回来等于写进去」的自证断言——那样打包与解析一起错位也全过。
        let authPkt = Proto.pullAuth(to: 0x1234, nonce: 0x5678,
                                     mac: [UInt8](repeating: 7, count: 16), audioPort: 47_831)
        let nameBytes = Array(Proto.deviceName.utf8).prefix(60)
        check(Int(authPkt[48]) == nameBytes.count, "48 处是名字长度（\(authPkt[48])）")
        check(Array(authPkt[49..<(49 + nameBytes.count)]) == Array(nameBytes), "49 起是名字正文")
        check(PullAuth.authName(authPkt) == String(decoding: nameBytes, as: UTF8.self),
              "真实解析路径读得出设备名（\(PullAuth.authName(authPkt))）")
        check(PullAuth.authName([UInt8](repeating: 0, count: 20)).isEmpty, "短包读不出名字")
        // 反证：若解析会「跟着打包一起错」，这里传 50 也应读不到——用短包与错位各验一次。
        var shifted = authPkt
        shifted[48] = UInt8(nameBytes.count + 1)
        check(PullAuth.authName(shifted) != String(decoding: nameBytes, as: UTF8.self)
              || nameBytes.count + 1 > 60,
              "长度字节被改大后，解析结果随之改变（说明真的按 48 读长度）")

        // 7）质询按「设备号 + IP」存：冒用设备号从别的 IP 发请求顶不掉正主。
        var book3 = ChallengeBook()
        let t0 = Date()
        _ = book3.issue(requester: reqID, ip: "10.0.0.5", now: t0)
        _ = book3.issue(requester: reqID, ip: "10.0.0.66", now: t0)   // 冒用者
        check(book3.hasPending(requester: reqID, ip: "10.0.0.5", now: t0),
              "冒用设备号从别的 IP 发请求，正主的质询仍在")
        check(!book3.hasPending(requester: reqID, ip: "10.0.0.77", now: t0),
              "没被质询过的 IP 查不到质询")
        var book4 = ChallengeBook()
        let nonceA = book4.issue(requester: reqID, ip: "10.0.0.5", now: t0)
        check(!book4.consume(requester: reqID, nonce: nonceA, ip: "10.0.0.9", now: t0),
              "换 IP 消费失败")
        check(book4.consume(requester: reqID, nonce: nonceA, ip: "10.0.0.5", now: t0),
              "换 IP 的失败尝试没有把正主的质询消耗掉")
        var book5 = ChallengeBook()
        for i in 0..<100 { _ = book5.issue(requester: UInt64(1000 + i), ip: "10.0.0.5", now: t0) }
        check(book5.count <= ChallengeBook.maxEntries, "质询表有条目上限（\(book5.count)）")

        // 8）单路锁：主动拉流的目标优先，别人占着就让位。
        check(AdmitPolicy.shouldYield(currentIP: "10.0.0.7", incomingIP: "10.0.0.9",
                                      priorityIP: "10.0.0.9", mixEnabled: false),
              "正在被 X 推送时，拉 S 会让位给 S")
        check(!AdmitPolicy.shouldYield(currentIP: "10.0.0.7", incomingIP: "10.0.0.9",
                                       priorityIP: "", mixEnabled: false),
              "没有主动拉流时不让位（保持 v3 的锁定语义）")
        check(!AdmitPolicy.shouldYield(currentIP: "10.0.0.7", incomingIP: "10.0.0.7",
                                       priorityIP: "10.0.0.7", mixEnabled: false),
              "同一台机器（同 IP）谈不上让位")

        // 15）版本字节：拉流控制包只认本版；信标照列但标记「版本不同」。
        check(Proto.acceptsVersion(Proto.version), "本版控制包被接受")
        check(!Proto.acceptsVersion(Proto.version - 1), "旧版控制包被忽略")
        let peerNow = Peer(id: 1, name: "x", ip: "10.0.0.1", port: 47_831, role: Proto.roleSender,
                           platform: Proto.platformMac, state: 0, caps: Proto.capPull,
                           version: Proto.version, lastSeen: Date())
        let peerOld = Peer(id: 2, name: "y", ip: "10.0.0.2", port: 47_831, role: Proto.roleSender,
                           platform: Proto.platformWindows, state: 0, caps: Proto.capPull,
                           version: Proto.version - 1, lastSeen: Date())
        check(!peerNow.versionMismatch, "同版本设备可以互连")
        check(peerOld.versionMismatch, "版本不同的设备被标记出来（不允许拉流/选目标）")

        // 16）拒绝表也有上限，不会无限长。
        var deny2 = PullDenyList()
        for i in 0..<200 { deny2.deny(ip: "10.0.1.\(i % 250)", now: t0) }
        check(deny2.count <= PullDenyList.maxEntries, "拒绝表有条目上限（\(deny2.count)）")

        // 6）冗余开关切换：在包边界切，不会把 filled 清零造成丢帧/重复。
        let sPort = port + 20
        let rBridge = RingBridge(seconds: 2.0, sampleRate: 48_000)
        let rTmp = NetReceiver(bridge: rBridge, port: sPort)
        rTmp.reportInfo = { (bufferMs: 0, targetMs: 40, muted: false) }
        do { try rTmp.start() } catch { check(false, "测试用接收端起不来：\(error)") }
        let snd = NetSender()
        let saddr = Sock.addr(ip: "127.0.0.1", port: sPort)!
        do { try snd.start(to: saddr) } catch { check(false, "测试用发送端起不来：\(error)") }
        let frame = 480
        var src = [Float](repeating: 0, count: frame * 2)
        for k in 0..<frame { src[k*2] = 0.2; src[k*2+1] = 0.2 }
        var sentFrames = 0
        for round in 0..<30 {
            snd.redundancy = (round / 3) % 2 == 0
            for _ in 0..<2 {
                src.withUnsafeBufferPointer { snd.push($0.baseAddress!, frames: frame, rate: 48_000) }
                sentFrames += frame
                pump(0.002)
            }
        }
        pump(0.15)
        let st = rTmp.stats
        check(st.lost == 0 && st.resyncs == 0 && st.recovered == 0,
              "反复切换冗余开关：不丢包、不重同步、不误判补包（lost \(st.lost) / resync \(st.resyncs) / rec \(st.recovered)）")
        check(st.packets > 0 && st.rate == 48_000,
              "反复切换冗余开关后仍在正常收（\(st.packets) 包 / \(Int(st.rate)) Hz）")
        snd.stop()
        rTmp.stop()

        receiver.stop()
        print(failures == 0 ? "PASS 全部通过" : "FAIL \(failures) 项未通过")
        return failures == 0 ? 0 : 1
    }
}
