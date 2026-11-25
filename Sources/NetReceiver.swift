import Foundation
import Darwin

/// 局域网音频接收端：UDP 收包 → 解析 → 写进 RingBridge。
///
/// v2：锁定一个发送端；认保活/再见；每 0.5 s 回执；乱序重复丢弃；采样率变化重新对齐。
/// v3：多路混音（每路一个缓冲，MixBus 相加）；丢包冗余（上一包 PCM 附在包尾，丢一包直接补回来）；
///     每路独立回执，发送端看到的是自己这一路的丢包与抖动。
final class NetReceiver {
    static let defaultPort: UInt16 = Proto.audioPort
    /// 这么久没听到某个发送端（音频或保活），就认为它断了，撤掉这一路。
    private static let linkTimeout: TimeInterval = 2.5
    /// 混音最多同时接几路。再多就回「正忙」。
    static let maxMixedSources = 4

    /// 单路视角（主路）的统计，保持旧字段名，界面代码不用改。
    struct Stats {
        var packets = 0
        var lost = 0
        var late = 0
        var resyncs = 0
        var recovered = 0
        var bytes = 0
        var rate: Double = 48_000
        var sender = ""          // 主路 IP
        var senderName = ""      // 主路设备名
        var jitterMs: Double = 0
        var gapMaxMs: Double = 0
        var lastHeardAt: Date?
        var lastAudioAt: Date?
        var running = false
        var ignoredSources = 0
        /// 当前有几路在发（混音模式 > 1）。
        var sourceCount = 0
        var mixing = false
    }

    /// 单路明细（界面/回执用）。
    struct SourceInfo {
        var ip = ""
        var name = ""
        var packets = 0
        var lost = 0
        var recovered = 0
        var jitterMs: Double = 0
        var rate: Double = 48_000
        var lastAudioAt: Date?
        var active = false
    }

    /// 每路发送端的网络状态，只在 queue 上读写。
    private final class Source {
        var ip = ""
        var addr: sockaddr_in!
        var name = ""
        var nextSeq: UInt32 = 0
        var haveSeq = false
        var minTscDelta: UInt64?
        var linkEma: Double = 0
        var jitterEma: Double = 0
        var lastHeard: UInt64 = 0          // uptime ns
        var lastAudioArrival: UInt64 = 0
        var gapBuckets = [Double](repeating: 0, count: 10)
        var gapBucketIndex = 0
        var gapBucketStart: UInt64 = 0
        var rate: Double = 0
        var lastL: Float = 0
        var lastR: Float = 0
        var packets = 0
        var lost = 0
        var late = 0
        var resyncs = 0
        var recovered = 0
        var bytes = 0
        var lastHeardAt: Date?
        var lastAudioAt: Date?
        var ring: RingBridge?              // 混音模式下每路自己的缓冲
    }

    private let bridge: RingBridge
    private let port: UInt16
    private let queue = DispatchQueue(label: "monitor-audio.net", qos: .userInteractive)
    /// 用来判断「当前是不是已经在收包队列上」。deinit 可能由队列上的回调触发，
    /// 那时再 queue.sync 会自死锁（libdispatch 直接 SIGTRAP）。
    private static let queueKey = DispatchSpecificKey<UInt8>()
    private var fd: Int32 = -1
    private var source: DispatchSourceRead?
    private var reportTimer: DispatchSourceTimer?
    private var rxBuffer = [UInt8](repeating: 0, count: 65_536)
    private var scratch = [Float](repeating: 0, count: 16_384)

    // 以下只在 queue 上读写
    private var sources: [UInt32: Source] = [:]   // 按 IP 识别
    private var primaryKey: UInt32 = 0
    private var others: [UInt32: UInt64] = [:]    // 被拒绝过的来源 → 上次提醒时间
    private var mixBus: MixBus?
    private var active = false

    private var lock = NSLock()
    private var state = Stats()

    /// 混音模式：接多路。必须在 start() 前设置（切换时 AppModel 会重启接收）。
    var mixEnabled = false

    /// 发送端采样率变化时回调（主队列）。混音模式下恒为 48 kHz，不会回调。
    var onRateChange: ((Double) -> Void)?
    /// 回执里要带的本端状态：(缓冲 ms, 目标 ms, 是否静音)。在收包队列上调用，必须线程安全。
    var reportInfo: (() -> (bufferMs: Double, targetMs: Double, muted: Bool))?

    init(bridge: RingBridge, port: UInt16 = NetReceiver.defaultPort) {
        self.bridge = bridge
        self.port = port
        queue.setSpecific(key: NetReceiver.queueKey, value: 1)
    }

    /// 在收包队列上串行执行；已经在队列上时直接跑，避免自死锁。
    private func onQueue(_ body: () -> Void) {
        if DispatchQueue.getSpecific(key: NetReceiver.queueKey) != nil {
            body()
        } else {
            queue.sync(execute: body)
        }
    }

    deinit { stop() }

    var stats: Stats {
        lock.lock(); defer { lock.unlock() }
        return state
    }

    /// 各路明细（按最近听到排序）。
    var sourceInfos: [SourceInfo] {
        lock.lock(); defer { lock.unlock() }
        return stateSources
    }
    private var stateSources: [SourceInfo] = []

    var isRunning: Bool { source != nil }
    var listenPort: UInt16 { port }

    func start() throws {
        stop()
        let s = socket(AF_INET, SOCK_DGRAM, 0)
        guard s >= 0 else { throw POSIXError(.ENFILE) }
        Sock.setInt(s, SOL_SOCKET, SO_REUSEADDR, 1)
        // 收包缓存调大，避免 Wi-Fi 抖动时内核直接丢包。
        Sock.setInt(s, SOL_SOCKET, SO_RCVBUF, 4 << 20)
        guard Sock.bind(s, port: port) else {
            let e = POSIXErrorCode(rawValue: errno) ?? .EIO
            close(s)
            throw POSIXError(e)
        }
        Sock.setNonBlocking(s)

        // 混音器必须先于收包就绪：第一包若在 bus 建好前到达，sink() 会把这一路的
        // 缓冲缓存成主缓冲，之后这一路就永远绕过混音器（曾经实测：混音输出全零）。
        if mixEnabled {
            mixBus = MixBus()
        }

        fd = s
        let src = DispatchSource.makeReadSource(fileDescriptor: s, queue: queue)
        src.setEventHandler { [weak self] in self?.drain() }
        src.setCancelHandler { close(s) }
        source = src
        src.resume()

        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + 0.5, repeating: 0.5, leeway: .milliseconds(50))
        t.setEventHandler { [weak self] in self?.tick() }
        reportTimer = t
        t.resume()

        active = true
        queue.async { [weak self] in self?.resetSources() }
        lock.lock()
        state = Stats()
        state.running = true
        lock.unlock()
    }

    func stop() {
        // start() 在建好混音器之后才 bind：若 bind 失败，这里也必须把混音器收掉，
        // 否则 mixer 会一直返回一个已停的 bus，播放端永远静音。
        guard let src = source else {
            onQueue { mixBus?.stop(); mixBus = nil; active = false }
            return
        }
        onQueue {
            if fd >= 0 {
                let bye = Proto.bye()
                for s in sources.values {
                    Sock.send(fd, bye, to: s.addr)
                    Sock.send(fd, bye, to: s.addr)
                }
            }
            mixBus?.stop()
            mixBus = nil
            active = false
            sources.removeAll()
            others.removeAll()
            primaryKey = 0
        }
        reportTimer?.cancel()
        reportTimer = nil
        src.cancel()
        source = nil
        fd = -1
        lock.lock()
        state.running = false
        state.lastHeardAt = nil
        state.lastAudioAt = nil
        state.sourceCount = 0
        stateSources = []
        lock.unlock()
    }

    /// 是否连着发送端（最近听到过音频或保活）。
    var isLinked: Bool {
        guard let t = stats.lastHeardAt else { return false }
        return Date().timeIntervalSince(t) < Self.linkTimeout
    }

    /// 是否真的有音频在流动（0.4 s 内有音频包）。
    var isStreaming: Bool {
        guard let t = stats.lastAudioAt else { return false }
        return Date().timeIntervalSince(t) < 0.4
    }

    /// 主动断开某一台（拉流被取消时用）。
    func sendBye(to ip: String, port: UInt16) {
        queue.async { [weak self] in
            guard let self, self.fd >= 0, let a = Sock.addr(ip: ip, port: port) else { return }
            let bye = Proto.bye()
            Sock.send(self.fd, bye, to: a)
        }
    }

    /// 当前混音路数（非混音模式恒为 0）。
    var mixedChannelCount: Int { mixBus?.channelCount ?? 0 }

    /// 来源表里有没有这个 IP（保活包也算）。拉流是否连上用它判断，别用每包都在变的主路。
    /// 主动拉流时优先放行的 IP（只在网络队列上读写）。
    private var pullPriorityIP: String = ""

    /// 主动拉流时让这个 IP 优先（单路模式下会让出别人占着的锁）。
    func prioritize(ip: String) {
        queue.async { [weak self] in self?.pullPriorityIP = ip }
    }

    func hasSource(ip: String) -> Bool {
        var found = false
        onQueue { found = sources.values.contains { $0.ip == ip } }
        return found
    }

    /// 主动放掉对某个 IP 的锁定（接收端换拉流目标时用）。
    func releaseSource(ip: String) {
        queue.async { [weak self] in
            guard let self else { return }
            if let key = self.sources.first(where: { $0.value.ip == ip })?.key { self.dropSource(key) }
        }
    }
    /// 混音器（给播放引擎直连；混音关闭时为 nil）。
    var mixer: MixBus? { mixEnabled ? mixBus : nil }

    /// 混音器目标深度（ms）。
    func setMixTarget(ms: Double) {
        mixBus?.setTarget(ms: ms)
    }

    // MARK: - 收包

    private func drain() {
        while true {
            var sa = sockaddr_in()
            var sl = socklen_t(MemoryLayout<sockaddr_in>.size)
            let n = rxBuffer.withUnsafeMutableBytes { raw -> Int in
                withUnsafeMutablePointer(to: &sa) { sp in
                    sp.withMemoryRebound(to: sockaddr.self, capacity: 1) { sap in
                        recvfrom(fd, raw.baseAddress, raw.count, 0, sap, &sl)
                    }
                }
            }
            if n < 0 { break }
            if n == 0 { continue }
            rxBuffer.withUnsafeBufferPointer { p in
                handle(p.baseAddress!, count: n, from: sa)
            }
        }
    }

    private func resetSources() {
        sources.removeAll()
        others.removeAll()
        primaryKey = 0
        bridge.reset()
        refreshState()
    }

    private func source(for ip: UInt32) -> Source? { sources[ip] }

    /// 来源准入。返回 nil 表示这包不归我们管（已回「正忙」）。
    private func admit(_ from: sockaddr_in, now: UInt64) -> Source? {
        // 主动拉流：新来源优先。单路模式下先让出别人占着的锁，否则「正在被 X 推送」时永远拉不进 S。
        if let other = sources.keys.first,
           AdmitPolicy.shouldYield(currentIP: Sock.ip(sources[other]!.addr),
                                   incomingIP: Sock.ip(from), priorityIP: pullPriorityIP,
                                   mixEnabled: mixEnabled) {
            dropSource(other)
        }
        let key = from.sin_addr.s_addr
        if let cur = sources[key] {
            // 同一台机器换了源端口＝对方程序重启了：序号和缓冲都要重来，不能接着旧时间轴读。
            if cur.addr.sin_port != from.sin_port {
                cur.addr = from
                cur.haveSeq = false
                cur.minTscDelta = nil
                cur.lastAudioArrival = 0
                cur.ring?.reset()
                if !mixEnabled { bridge.reset() }
            } else {
                cur.addr = from
            }
            cur.lastHeard = now
            return cur
        }
        if mixEnabled {
            if sources.count >= Self.maxMixedSources {
                throttleBusy(key: key, from: from, now: now)
                return nil
            }
            let s = makeSource(ip: key, from: from, now: now)
            sources[key] = s
            primaryKey = key
            refreshState()
            return s
        }
        // 单路：锁定一台。别的设备在超时前一律拒绝（并限速回一个「正忙」）。
        if let curKey = sources.keys.first, let cur = sources[curKey] {
            let silentFor = Double(now &- cur.lastHeard) / 1e9
            if silentFor < Self.linkTimeout {
                throttleBusy(key: key, from: from, now: now)
                return nil
            }
            dropSource(curKey)
        }
        let s = makeSource(ip: key, from: from, now: now)
        sources[key] = s
        primaryKey = key
        bridge.reset()
        refreshState()
        return s
    }

    private func throttleBusy(key: UInt32, from: sockaddr_in, now: UInt64) {
        let last = others[key] ?? 0
        guard now &- last > 1_000_000_000 else { return }
        others[key] = now
        sendReport(to: from, busy: true, s: nil)
    }

    private func makeSource(ip: UInt32, from: sockaddr_in, now: UInt64) -> Source {
        let s = Source()
        s.ip = Sock.ip(from)
        s.addr = from
        s.lastHeard = now
        return s
    }

    private func dropSource(_ key: UInt32) {
        if let s = sources.removeValue(forKey: key) {
            if let ring = s.ring { _ = ring }
            mixBus?.remove(key)
        }
        if primaryKey == key { primaryKey = sources.keys.first ?? 0 }
        refreshState()
    }

    private func handle(_ data: UnsafePointer<UInt8>, count: Int, from: sockaddr_in) {
        guard Proto.hasMagic(data, count) else { return }
        let type = data[4]
        let now = DispatchTime.now().uptimeNanoseconds
        let key = from.sin_addr.s_addr

        switch type {
        case Proto.typeBye:
            if sources[key] != nil { dropSource(key) }
            return
        case Proto.typeKeepalive:
            guard count >= 33, let s = admit(from, now: now) else { return }
            let name = Proto.name(data, 32, count: count)
            if !name.isEmpty, name != s.name { s.name = name }
            s.lastHeardAt = Date()
            refreshState()
            return
        case Proto.typeAudio:
            break
        default:
            return
        }

        guard count >= Proto.headerSize else { return }
        let ch = Int(data[5])
        let rate = Double(Proto.u32(data, 6))
        let seq = Proto.u32(data, 10)
        let frames = Int(Proto.u16(data, 14))
        // 最高字节留给冗余标志（header 24 字节全用满了），时间戳按 56 位读。
        let tsc = Proto.u64(data, 16) & 0x00FF_FFFF_FFFF_FFFF

        guard ch >= 1, ch <= 2, rate >= 8_000, rate <= 384_000,
              frames > 0, frames <= 4096 else { return }
        let payloadBytes = frames * ch * 2
        guard count >= Proto.headerSize + payloadBytes else { return }
        guard let s = admit(from, now: now) else { return }

        let hasRedundant = (data[23] & Proto.audioFlagRedundant) != 0
            && count >= Proto.headerSize + payloadBytes * 2

        // 采样率变了（对方换了输出设备）：旧数据按新速率读会变调，直接清空重来。
        var rateChanged: Double?
        if rate != s.rate {
            if s.rate != 0 {
                s.ring?.reset()
                if !mixEnabled { bridge.reset() }
                s.haveSeq = false
            }
            s.rate = rate
            mixBus?.setRate(key, rate)
            rateChanged = rate
        }

        if tsc > 0 {
            let delta = now &- tsc
            if s.minTscDelta == nil || delta < s.minTscDelta! { s.minTscDelta = delta }
            if let m = s.minTscDelta {
                let ms = Double(delta &- m) / 1_000_000.0
                if s.linkEma == 0 { s.linkEma = ms } else { s.linkEma += (ms - s.linkEma) * 0.05 }
                s.jitterEma += (abs(ms - s.linkEma) - s.jitterEma) * 0.05
            }
        }

        // 到达间隔：自动缓冲的依据。停顿超过 0.4 s 视为对方暂停播放，不算抖动。
        if s.lastAudioArrival != 0 {
            let gap = Double(now &- s.lastAudioArrival) / 1_000_000.0
            if gap < 400 {
                if now &- s.gapBucketStart > 1_000_000_000 {
                    s.gapBucketStart = now
                    s.gapBucketIndex = (s.gapBucketIndex + 1) % s.gapBuckets.count
                    s.gapBuckets[s.gapBucketIndex] = 0
                }
                if gap > s.gapBuckets[s.gapBucketIndex] { s.gapBuckets[s.gapBucketIndex] = gap }
            }
        }
        s.lastAudioArrival = now

        var lost = 0, resyncs = 0, recovered = 0
        if !s.haveSeq {
            s.haveSeq = true
        } else {
            let delta = seq &- s.nextSeq
            if delta == 0 {
                // 正常
            } else if delta <= 64 {
                if hasRedundant {
                    // 包尾带着上一包（序号 seq-1）：它原样补回来、不算丢包；
                    // delta 表示从 nextSeq 到 seq 缺了几包，扣掉被副本补回的这 1 包，剩下的才隐藏。
                    let missing = Int(delta) - 1
                    if missing > 0 {
                        conceal(s, gapFrames: missing * frames)
                        lost += missing
                    }
                    pushPayload(s, base: data + Proto.headerSize + payloadBytes, frames: frames, ch: ch)
                    recovered = 1
                } else {
                    // 缺口：按丢包补齐时间轴，免得丢一包就整体提前 5 ms。
                    lost = Int(delta)
                    conceal(s, gapFrames: lost * frames)
                }
            } else if delta >= 0xFFFF_FF00 {
                // 迟到/重复的旧包：时间轴上那一段已经补过了，丢弃。
                s.late += 1
                s.lastHeardAt = Date()
                refreshState()
                return
            } else {
                // 发送端重启或长时间断流：直接对齐
                resyncs = 1
            }
        }
        s.nextSeq = seq &+ 1

        pushPayload(s, base: data + Proto.headerSize, frames: frames, ch: ch)

        let nowDate = Date()
        s.packets += 1
        s.lost += lost
        s.resyncs += resyncs
        s.recovered += recovered
        s.bytes += count
        s.lastHeardAt = nowDate
        s.lastAudioAt = nowDate
        refreshState()

        if let r = rateChanged, !mixEnabled {
            DispatchQueue.main.async { [weak self] in self?.onRateChange?(r) }
        }
    }

    private func sink(_ s: Source) -> RingBridge {
        if mixEnabled {
            if let r = s.ring { return r }
            // bus 还没就绪时不要缓存主缓冲，否则这一路以后都绕过混音。
            guard let bus = mixBus else { return bridge }
            let ring = bus.add(s.addr.sin_addr.s_addr, rate: s.rate > 0 ? s.rate : 48_000)
            s.ring = ring
            return ring
        }
        return bridge
    }

    private func pushPayload(_ s: Source, base: UnsafePointer<UInt8>, frames: Int, ch: Int) {
        convert(s, base: base, frames: frames, ch: ch)
        scratch.withUnsafeBufferPointer { p in
            sink(s).push(p.baseAddress!, frames: frames)
        }
    }

    // MARK: - 回执

    private func tick() {
        let now = DispatchTime.now().uptimeNanoseconds
        for (key, s) in sources {
            if Double(now &- s.lastHeard) / 1e9 > Self.linkTimeout {
                dropSource(key)
                continue
            }
            sendReport(to: s.addr, busy: false, s: s)
        }
        refreshState()
    }

    private func sendReport(to addr: sockaddr_in, busy: Bool, s: Source?) {
        guard fd >= 0 else { return }
        let info = reportInfo?() ?? (bufferMs: 0, targetMs: 0, muted: false)
        // 混音模式下每一路的缓冲要按这一路自己的采样率换算，不能用主缓冲（主缓冲不再喂数据）。
        var bufferMs = info.bufferMs
        if mixEnabled, let s {
            bufferMs = Double(s.ring?.lastFillFrames ?? 0) / max(s.rate, 1) * 1000
        }
        var b = [UInt8](repeating: 0, count: 33 + 61)
        Proto.putMagic(&b, type: Proto.typeReport)
        var flags: UInt8 = 0
        if busy {
            flags |= Proto.flagBusy
        } else {
            let sink = s.map { $0.ring ?? bridge } ?? bridge
            if sink.isPrimed { flags |= Proto.flagPlaying }
            if info.muted { flags |= Proto.flagMuted }
        }
        b[5] = flags
        Proto.putU16(&b, 6, UInt16(max(0, min(65_535, bufferMs))))
        Proto.putU32(&b, 8, UInt32(truncatingIfNeeded: busy ? 0 : (s?.packets ?? 0)))
        Proto.putU32(&b, 12, UInt32(truncatingIfNeeded: busy ? 0 : (s?.lost ?? 0)))
        let jitter = s?.jitterEma ?? 0
        Proto.putU32(&b, 16, UInt32(max(0, min(4e9, jitter * 1000))))
        Proto.putU16(&b, 20, UInt16(max(0, min(65_535, info.targetMs))))
        Proto.putU64(&b, 24, Proto.instanceID)
        let end = Proto.putName(&b, 32, Proto.deviceName)
        b.withUnsafeBytes { _ = Sock.send(fd, $0.baseAddress!, end, to: addr) }
    }

    // MARK: - 统计

    private func refreshState() {
        var agg = Stats()
        agg.running = active
        agg.mixing = mixEnabled
        agg.sourceCount = sources.count
        var infos: [SourceInfo] = []
        var primary: Source?
        var gapMax: Double = 0
        var lastHeard: Date?
        var lastAudio: Date?
        for (key, s) in sources {
            agg.packets += s.packets
            agg.lost += s.lost
            agg.late += s.late
            agg.resyncs += s.resyncs
            agg.recovered += s.recovered
            agg.bytes += s.bytes
            gapMax = max(gapMax, s.gapBuckets.max() ?? 0)
            if let t = s.lastHeardAt, lastHeard == nil || t > lastHeard! { lastHeard = t }
            if let t = s.lastAudioAt, lastAudio == nil || t > lastAudio! { lastAudio = t }
            if key == primaryKey { primary = s }
            infos.append(SourceInfo(ip: s.ip, name: s.name, packets: s.packets, lost: s.lost,
                                    recovered: s.recovered, jitterMs: s.jitterEma,
                                    rate: s.rate > 0 ? s.rate : 48_000,
                                    lastAudioAt: s.lastAudioAt, active: true))
        }
        if primary == nil { primary = sources.values.first }
        agg.gapMaxMs = gapMax
        agg.lastHeardAt = lastHeard
        agg.lastAudioAt = lastAudio
        if let s = primary {
            agg.sender = s.ip
            agg.senderName = s.name
            agg.rate = s.rate > 0 ? s.rate : 48_000
            agg.jitterMs = s.jitterEma
        }
        infos.sort { ($0.lastAudioAt ?? .distantPast) > ($1.lastAudioAt ?? .distantPast) }
        lock.lock()
        state = agg
        stateSources = infos
        lock.unlock()
    }

    // MARK: - PCM

    /// 丢包隐藏：保持最后一帧电平并线性淡出（约 5 ms），比直接补静音少一次「咔哒」。
    private func conceal(_ s: Source, gapFrames: Int) {
        guard gapFrames > 0 else { return }
        var done = 0
        while done < gapFrames {
            let n = min(gapFrames - done, scratch.count / 2)
            for i in 0..<n {
                let g = max(0.0, 1.0 - Float(done + i) / 240.0)
                scratch[i * 2] = s.lastL * g
                scratch[i * 2 + 1] = s.lastR * g
            }
            scratch.withUnsafeBufferPointer { p in
                sink(s).push(p.baseAddress!, frames: n)
            }
            done += n
        }
        s.lastL = 0
        s.lastR = 0
    }

    private func convert(_ s: Source, base: UnsafePointer<UInt8>, frames: Int, ch: Int) {
        // Int16 LE → Float32，单声道复制成双声道。
        if ch == 2 {
            for i in 0..<(frames * 2) {
                let v = Int16(bitPattern: UInt16(base[i * 2]) | UInt16(base[i * 2 + 1]) << 8)
                scratch[i] = Float(v) / 32_768.0
            }
        } else {
            for i in 0..<frames {
                let v = Int16(bitPattern: UInt16(base[i * 2]) | UInt16(base[i * 2 + 1]) << 8)
                let f = Float(v) / 32_768.0
                scratch[i * 2] = f
                scratch[i * 2 + 1] = f
            }
        }
        s.lastL = scratch[(frames - 1) * 2]
        s.lastR = scratch[(frames - 1) * 2 + 1]
    }
}
