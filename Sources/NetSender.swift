import Foundation
import Darwin

/// 发送端网络部分：把交织双声道 Float 切成 5 ms 一包发出去，同时
///  · 每 0.5 s 发一个保活（没声音时对方也知道我们还在）；
///  · 收对方的回执，据此判断「对方确实收到了 / 没回应 / 正忙」；
///  · 停止时发再见包。
/// UDP 无连接：对方掉线、重启、换网络后恢复，都不需要这边做任何事，包一到就自动续上。
final class NetSender {
    struct Report {
        var at = Date.distantPast
        var name = ""
        var peerID: UInt64 = 0
        var playing = false
        var muted = false
        var busy = false
        var packets: UInt32 = 0
        var lost: UInt32 = 0
        var jitterMs: Double = 0
        var bufferMs: Double = 0
        var targetMs: Double = 0
    }

    struct Stats {
        var packets = 0
        var bytes = 0
        var sendErrors = 0
        var rate: Double = 0
        var startedAt = Date.distantPast
        var lastAudioAt = Date.distantPast
        var report = Report()
        var peerLeftAt = Date.distantPast
    }

    private let queue = DispatchQueue(label: "monitor-audio.send", qos: .userInteractive)
    private var fd: Int32 = -1
    private var readSource: DispatchSourceRead?
    private var timer: DispatchSourceTimer?
    private var rx = [UInt8](repeating: 0, count: 2048)

    private var lock = os_unfair_lock_s()
    private var target: sockaddr_in?
    private var state = Stats()
    private var peakL: Float = 0
    private var peakR: Float = 0

    // 只在采集线程上用
    private var packet = [UInt8](repeating: 0, count: Proto.headerSize + Proto.framesPerPacket * 8)
    private var filled = 0
    private var seq: UInt32 = 0
    private var rate: Double = 48_000
    private var silentFrames = 0
    /// 上一个已发包的 PCM：开启冗余时附在当前包尾部，接收端丢一包可以直接补回来。
    private var prevPayload = [UInt8](repeating: 0, count: Proto.framesPerPacket * 4)
    private var prevValid = false
    private var prevRate: Double = 0

    /// 丢包冗余：每包多带一份上一个包的 PCM（流量约 ×2），丢一包时接收端不用做丢包隐藏。
    /// 界面线程只写这个开关；采集线程在每个包边界（filled == 0）自己切过去，
    /// 绝不在这里清 filled——那是采集线程的状态，跨线程改会让下标越过 packet 缓冲（A 阶段发现的崩溃）。
    var redundancy = false
    /// 正在攒的那一类包用不用冗余（只在采集线程上读写）。
    private var activeRedundancy = false

    private(set) var isRunning = false

    var stats: Stats {
        os_unfair_lock_lock(&lock); defer { os_unfair_lock_unlock(&lock) }
        return state
    }

    func takePeaks() -> (Float, Float) {
        os_unfair_lock_lock(&lock); defer { os_unfair_lock_unlock(&lock) }
        let p = (peakL, peakR)
        peakL = 0; peakR = 0
        return p
    }

    var targetDescription: String {
        os_unfair_lock_lock(&lock); defer { os_unfair_lock_unlock(&lock) }
        guard let t = target else { return "" }
        return "\(Sock.ip(t)):\(UInt16(bigEndian: t.sin_port))"
    }

    func start(to addr: sockaddr_in) throws {
        stop()
        let s = socket(AF_INET, SOCK_DGRAM, 0)
        guard s >= 0 else { throw POSIXError(.ENFILE) }
        Sock.setInt(s, SOL_SOCKET, SO_SNDBUF, 1 << 20)
        Sock.setNonBlocking(s)
        fd = s

        os_unfair_lock_lock(&lock)
        target = addr
        state = Stats()
        state.startedAt = Date()
        os_unfair_lock_unlock(&lock)
        filled = 0

        let src = DispatchSource.makeReadSource(fileDescriptor: s, queue: queue)
        src.setEventHandler { [weak self] in self?.drain() }
        src.setCancelHandler { close(s) }
        readSource = src
        src.resume()

        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now(), repeating: 0.5, leeway: .milliseconds(50))
        t.setEventHandler { [weak self] in self?.keepalive() }
        timer = t
        t.resume()
        isRunning = true
    }

    /// 对端换了 IP（信标里认出是同一台）时改目标，不打断采集。
    func retarget(_ addr: sockaddr_in) {
        os_unfair_lock_lock(&lock)
        target = addr
        os_unfair_lock_unlock(&lock)
    }

    func stop() {
        guard isRunning else { return }
        isRunning = false
        os_unfair_lock_lock(&lock)
        let t = target
        target = nil
        os_unfair_lock_unlock(&lock)
        if let t, fd >= 0 {
            let bye = Proto.bye()
            Sock.send(fd, bye, to: t)
            Sock.send(fd, bye, to: t)
        }
        timer?.cancel(); timer = nil
        readSource?.cancel(); readSource = nil
        fd = -1
    }

    // MARK: - 音频

    /// 采集回调里调用：交织双声道 Float。
    func push(_ src: UnsafePointer<Float>, frames: Int, rate newRate: Double) {
        guard isRunning, frames > 0 else { return }
        os_unfair_lock_lock(&lock)
        let dst = target
        os_unfair_lock_unlock(&lock)
        guard let dst else { return }

        if newRate != rate { rate = newRate; filled = 0 }

        // 持续 0.5 s 的数字静音就不再发音频包（保活照发）：省流量，对方界面也能如实显示「当前没有声音」。
        // Windows 的 loopback 在没声音时本来就不出数据，这样两个平台行为一致。
        var quiet = true
        for i in 0..<(frames * 2) where src[i] != 0 { quiet = false; break }
        if quiet {
            silentFrames += frames
            if Double(silentFrames) > rate * 0.5 { filled = 0; return }
        } else {
            silentFrames = 0
        }

        var pl: Float = 0, pr: Float = 0
        var sent = 0, sentBytes = 0, errors = 0
        // 只在包边界切换：正在攒的半包按原来的帧数攒完，不会出现 filled 大于 per 的越界。
        if filled == 0, activeRedundancy != redundancy {
            activeRedundancy = redundancy
            prevValid = false
        }
        // 带副本时每包 120 帧：24 + 480 + 480 = 984 字节，不触发 IP 分片。
        let per = activeRedundancy ? Proto.redundantFramesPerPacket : Proto.framesPerPacket
        for i in 0..<frames {
            let l = src[i * 2], r = src[i * 2 + 1]
            let al = abs(l), ar = abs(r)
            if al > pl { pl = al }
            if ar > pr { pr = ar }
            let o = Proto.headerSize + filled * 4
            let sl = UInt16(bitPattern: Int16(max(-1, min(1, l)) * 32_767))
            let sr = UInt16(bitPattern: Int16(max(-1, min(1, r)) * 32_767))
            packet[o] = UInt8(sl & 0xFF); packet[o + 1] = UInt8(sl >> 8)
            packet[o + 2] = UInt8(sr & 0xFF); packet[o + 3] = UInt8(sr >> 8)
            filled += 1
            if filled >= per {
                Proto.putMagic(&packet, type: Proto.typeAudio)
                packet[5] = 2
                Proto.putU32(&packet, 6, UInt32(rate))
                Proto.putU32(&packet, 10, seq)
                Proto.putU16(&packet, 14, UInt16(per))
                Proto.putU64(&packet, 16, DispatchTime.now().uptimeNanoseconds)
                let payloadBytes = per * 4
                var length = Proto.headerSize + payloadBytes
                packet[23] = 0
                if activeRedundancy, prevValid, prevRate == rate {
                    for i in 0..<payloadBytes { packet[Proto.headerSize + payloadBytes + i] = prevPayload[i] }
                    packet[23] = Proto.audioFlagRedundant
                    length += payloadBytes
                }
                packet.withUnsafeBytes { raw in
                    let n = Sock.send(fd, raw.baseAddress!, length, to: dst)
                    if n < 0 { errors += 1 } else { sent += 1; sentBytes += n }
                }
                if activeRedundancy {
                    for i in 0..<payloadBytes { prevPayload[i] = packet[Proto.headerSize + i] }
                    prevValid = true
                    prevRate = rate
                } else {
                    prevValid = false
                }
                seq &+= 1
                filled = 0
            }
        }

        os_unfair_lock_lock(&lock)
        if pl > peakL { peakL = pl }
        if pr > peakR { peakR = pr }
        state.packets += sent
        state.bytes += sentBytes
        state.sendErrors += errors
        state.rate = rate
        if sent > 0 { state.lastAudioAt = Date() }
        os_unfair_lock_unlock(&lock)
    }

    // MARK: - 保活 / 回执

    private func keepalive() {
        os_unfair_lock_lock(&lock)
        let dst = target
        os_unfair_lock_unlock(&lock)
        guard let dst, fd >= 0 else { return }
        Sock.send(fd, Proto.keepalive(rate: UInt32(rate), seq: seq), to: dst)
    }

    private func drain() {
        while true {
            let n = rx.withUnsafeMutableBytes { recv(fd, $0.baseAddress, $0.count, 0) }
            if n <= 0 { break }
            rx.withUnsafeBufferPointer { p in
                let b = p.baseAddress!
                guard Proto.hasMagic(b, n) else { return }
                if b[4] == Proto.typeReport, n >= 33 {
                    var r = Report()
                    r.at = Date()
                    let flags = b[5]
                    r.playing = flags & Proto.flagPlaying != 0
                    r.muted = flags & Proto.flagMuted != 0
                    r.busy = flags & Proto.flagBusy != 0
                    r.bufferMs = Double(Proto.u16(b, 6))
                    r.packets = Proto.u32(b, 8)
                    r.lost = Proto.u32(b, 12)
                    r.jitterMs = Double(Proto.u32(b, 16)) / 1000.0
                    r.targetMs = Double(Proto.u16(b, 20))
                    r.peerID = Proto.u64(b, 24)
                    r.name = Proto.name(b, 32, count: n)
                    os_unfair_lock_lock(&lock)
                    state.report = r
                    os_unfair_lock_unlock(&lock)
                } else if b[4] == Proto.typeBye {
                    os_unfair_lock_lock(&lock)
                    state.report.at = .distantPast
                    state.peerLeftAt = Date()
                    os_unfair_lock_unlock(&lock)
                }
            }
        }
    }
}
