import Foundation

/// 多路混音：每一路发送端各自一个 RingBridge（各自做时钟伺服与重采样），
/// 播放回调每次要多少帧，就逐路取多少帧、相加、限幅后直接输出。
///
/// 为什么不再用「5 ms 定时器 → 主缓冲」：定时器和播放时钟本来就不同步，
/// 一旦「够不够取」的门槛贴着 RingBridge 的伺服目标，就会周期性跳过一拍、重放上一块，
/// 听感是持续的断点；而且多一层缓冲就多一份延迟。改由播放回调驱动之后，
/// 每路的预缓冲、饿空淡出、时钟伺服都交给 RingBridge 自己处理。
final class MixBus {
    private final class Channel {
        let ring: RingBridge
        var rate: Double
        /// 移除时保留一小段并线性淡出，避免信号突然归零产生爆音。
        var fade = 0
        var fadeTotal = 0
        init(rate: Double) {
            self.rate = rate
            self.ring = RingBridge(seconds: 1.5, sampleRate: 48_000)
        }
    }

    private let lock = NSLock()
    private var channels: [UInt32: Channel] = [:]
    /// 正在淡出的通道（已从 channels 里摘掉，但还要再响 5 ms）。
    private var dying: [Channel] = []

    /// 每路预缓冲目标（ms）。25 ms 足够吸收 Wi-Fi 抖动，又不会让叠加延迟太难看。
    var targetMs: Double = 25
    /// 上一次播放回调时各路里最满的一路有多少帧（延迟估算用）。
    private(set) var lastFillFrames: Double = 0

    /// 当前有几路在混（不含正在淡出的）。
    var channelCount: Int {
        lock.lock(); defer { lock.unlock() }
        return channels.count
    }

    private let capacity = 4096
    private var mix = [Float](repeating: 0, count: 4096 * 2)
    private var tmp = [Float](repeating: 0, count: 4096 * 2)

    /// 新建一路（同一路重复调用返回已有的）。
    func add(_ key: UInt32, rate: Double) -> RingBridge {
        lock.lock(); defer { lock.unlock() }
        if let c = channels[key] { return c.ring }
        let c = Channel(rate: rate)
        channels[key] = c
        return c.ring
    }

    func remove(_ key: UInt32) {
        lock.lock(); defer { lock.unlock() }
        guard let c = channels.removeValue(forKey: key) else { return }
        c.fadeTotal = 240
        c.fade = 240
        dying.append(c)
    }

    /// 某一路换了采样率：旧数据按新速率读会变调，直接倒掉重新攒。
    func setRate(_ key: UInt32, _ rate: Double) {
        lock.lock(); defer { lock.unlock() }
        guard let c = channels[key] else { return }
        if c.rate != rate {
            c.rate = rate
            c.ring.reset()
        }
    }

    func setTarget(ms: Double) {
        lock.lock()
        targetMs = max(5, min(120, ms))
        lock.unlock()
    }

    func stop() {
        lock.lock()
        channels.removeAll()
        dying.removeAll()
        lastFillFrames = 0
        lock.unlock()
    }

    /// 播放回调调用：把 frames 帧交织双声道混好写进 dst（这是唯一的取数入口）。
    func pull(_ dst: UnsafeMutablePointer<Float>, frames: Int, outRate: Double) {
        let n = min(frames, capacity)
        lock.lock()
        let list = Array(channels.values) + dying
        let target = targetMs
        lock.unlock()

        for i in 0..<(n * 2) { dst[i] = 0 }
        for c in list {
            // 每路各自按自己的采样率伺服，trace 的目标深度与界面上的缓冲设置挂钩。
            tmp.withUnsafeMutableBufferPointer { p in
                c.ring.pull(p.baseAddress!, frames: n, outRate: outRate, inRate: c.rate,
                            targetFrames: target / 1000.0 * c.rate)
            }
            if c.fadeTotal > 0 {
                let total = Float(c.fadeTotal)
                for i in 0..<n {
                    let g = max(0, min(1, Float(c.fade - i) / total))
                    dst[i * 2] += tmp[i * 2] * g
                    dst[i * 2 + 1] += tmp[i * 2 + 1] * g
                }
                c.fade -= n
            } else {
                for i in 0..<(n * 2) { dst[i] += tmp[i] }
            }
        }
        // 两路满幅叠加会削顶：超过 ±1 就压回来（软限幅，听感比硬切好）。
        for i in 0..<(n * 2) {
            let v = dst[i]
            if v > 1 { dst[i] = 1 } else if v < -1 { dst[i] = -1 }
        }

        lock.lock()
        if !dying.isEmpty { dying.removeAll { $0.fade <= 0 } }
        var fill: Double = 0
        for c in channels.values { fill = max(fill, c.ring.availableFrames) }
        lastFillFrames = fill
        lock.unlock()
    }
}
