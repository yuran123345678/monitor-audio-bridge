import Foundation
import os

/// 采集线程写、播放线程读的立体声交织环形缓冲，内含异步采样率转换（ASRC）。
///
/// 两条音频设备各有自己的时钟（USB 声卡 vs 内置扬声器），标称 48 kHz 也会有 ppm 级偏差。
/// 这里用「填充量闭环 + Catmull-Rom 插值」做微幅重采样：缓冲偏多就把读取速率调快一点点，
/// 偏少就调慢，既不会周期性爆音，也不会有能听出来的音高漂移（修正量上限 ±0.25%）。
final class RingBridge {
    private let capacity: Int
    private let buf: UnsafeMutablePointer<Float>
    private var writeCount: Int64 = 0
    private var readPos: Double = 0
    private var lock = os_unfair_lock_s()

    /// 是否已经攒够预缓冲。断流后重新置 false，下次出声前先重新攒。
    private var primed = false
    /// 缓冲填充误差的慢速平滑值（时间常数约 2.6 s）。
    private var fillErr: Double = 0
    /// 速率微调上限：0.15%，约 ±2.6 音分。缓慢变化时不可闻，但足够抵消两台机器的时钟漂移。
    private let maxTrim: Double = 0.0015

    /// 当前是否在出声（攒够预缓冲且没饿空）。给界面和回执用。
    var isPrimed: Bool {
        os_unfair_lock_lock(&lock); defer { os_unfair_lock_unlock(&lock) }
        return primed
    }

    private(set) var underruns = 0
    private(set) var overruns = 0
    private(set) var lastFillFrames: Double = 0
    /// 当前可读帧数（含尚未取走的），只做调度判断，不消费数据。
    var availableFrames: Double {
        os_unfair_lock_lock(&lock)
        defer { os_unfair_lock_unlock(&lock) }
        return Double(writeCount) - readPos
    }

    init(seconds: Double = 2.0, sampleRate: Double = 48_000) {
        capacity = max(1024, Int(seconds * sampleRate))
        buf = UnsafeMutablePointer<Float>.allocate(capacity: capacity * 2)
        buf.initialize(repeating: 0, count: capacity * 2)
    }

    deinit {
        buf.deinitialize(count: capacity * 2)
        buf.deallocate()
    }

    func reset() {
        os_unfair_lock_lock(&lock)
        writeCount = 0
        readPos = 0
        primed = false
        fillErr = 0
        underruns = 0
        overruns = 0
        lastFillFrames = 0
        for i in 0..<(capacity * 2) { buf[i] = 0 }
        os_unfair_lock_unlock(&lock)
    }

    @inline(__always)
    private func sample(_ absIndex: Int64, _ ch: Int) -> Float {
        var idx = absIndex % Int64(capacity)
        if idx < 0 { idx += Int64(capacity) }
        return buf[Int(idx) * 2 + ch]
    }

    /// 采集回调里调用。输入是交织双声道 Float32，采样率为输入设备原生采样率。
    func push(_ src: UnsafePointer<Float>, frames: Int) {
        guard frames > 0 else { return }
        os_unfair_lock_lock(&lock)
        defer { os_unfair_lock_unlock(&lock) }

        let cap = Int64(capacity)
        let incoming = Int64(frames)
        // 缓冲将被写满时，向前挪读取位置丢掉最旧的一小段，避免覆盖未读数据。
        if writeCount - Int64(readPos) + incoming > cap - 16 {
            let newRead = Double(writeCount + incoming - (cap - 16))
            if newRead > readPos { readPos = newRead; overruns += 1 }
        }
        let base = writeCount
        for i in 0..<frames {
            var idx = (base + Int64(i)) % cap
            if idx < 0 { idx += cap }
            buf[Int(idx) * 2] = src[i * 2]
            buf[Int(idx) * 2 + 1] = src[i * 2 + 1]
        }
        writeCount = base + incoming
    }

    /// 播放回调里调用：从缓冲里按 outRate 取出 frames 帧，自动做 inRate→outRate 的重采样。
    /// targetFrames 以「输入采样率下的帧数」计。
    func pull(_ dst: UnsafeMutablePointer<Float>, frames: Int,
              outRate: Double, inRate: Double, targetFrames: Double) {
        guard frames > 0 else { return }
        os_unfair_lock_lock(&lock)
        defer { os_unfair_lock_unlock(&lock) }

        var avail = Double(writeCount) - readPos
        lastFillFrames = avail

        // 起播/断流之后先攒够目标深度再出声，否则开头那几帧就是静音加一下爆点。
        if !primed {
            if targetFrames > 0 && avail >= targetFrames {
                primed = true
            } else {
                for i in 0..<(frames * 2) { dst[i] = 0 }
                // 只允许「往前对齐」到实时位置，绝不把读指针往回扳。
                // 以前这里无条件 readPos = writeCount - targetFrames：发送端一退出，最后 40 ms
                // 就被反复重放，听感是一串「嗒嗒嗒」的异响（实测尾部 40 ms 周期性复读）。
                if targetFrames > 0 {
                    let pinned = max(0, Double(writeCount) - targetFrames)
                    if pinned > readPos { readPos = pinned }
                }
                return
            }
        }

        // 起播、发送端重启、或时钟严重漂移时，缓冲会一路涨上去；光靠微调要几十秒才收敛，
        // 这段时间延迟一直是几百毫秒。超过目标深度 80 ms 就直接对齐（只丢一小段，听感是一次轻微跳变）。
        if inRate > 0 && outRate > 0 && targetFrames > 0
            && avail > targetFrames + inRate * 0.08 {
            readPos = Double(writeCount) - targetFrames
            avail = Double(writeCount) - readPos
            overruns += 1
        }
        lastFillFrames = avail

        if inRate <= 0 || outRate <= 0 || avail < 8 {
            for i in 0..<(frames * 2) { dst[i] = 0 }
            if inRate > 0 {
                underruns += 1
                primed = false      // 饿空了：下次重新攒预缓冲，不要带着半截数据继续跑
            }
            return
        }

        // 时钟伺服：只允许极慢、极小（±0.15% ≈ ±2.6 音分）的速率微调。
        // 关键在「慢」：以前增益 0.15、上限 ±1%，缓冲的正常抖动就让 inc 在每个回调（约 21 ms）里
        // 来回扳 1%，等于给声音加了一级阶梯调频，持续音上能听出「水声/颤音」。
        // 现在先做 2.6 s 的平滑，再乘 0.05，trim 只随真实漂移缓慢移动，音高基本恒定为 1.000。
        let err = (avail - targetFrames) / max(targetFrames, 1)
        fillErr += (err - fillErr) * 0.008
        let trim = max(-maxTrim, min(maxTrim, fillErr * 0.05))
        let inc = inRate / outRate * (1 + trim)

        // 快饿空时把最后 20 ms 淡出：发送端退出、Wi-Fi 掉包都会走到这里，
        // 硬切会是「啪」的一声，淡出就听不出来。
        let fadeSpan = inRate * 0.02
        let fading = avail < fadeSpan

        var pos = readPos
        for n in 0..<frames {
            let base = Int64(pos.rounded(.down))
            let t = Float(pos - Double(base))
            let t2 = t * t
            let t3 = t2 * t
            // Catmull-Rom 系数
            let a0 = -0.5 * t3 + t2 - 0.5 * t
            let a1 = 1.5 * t3 - 2.5 * t2 + 1.0
            let a2 = -1.5 * t3 + 2.0 * t2 + 0.5 * t
            let a3 = 0.5 * t3 - 0.5 * t2

            let s0l = sample(base - 1, 0)
            let s1l = sample(base, 0)
            let s2l = sample(base + 1, 0)
            let s3l = sample(base + 2, 0)
            let s0r = sample(base - 1, 1)
            let s1r = sample(base, 1)
            let s2r = sample(base + 1, 1)
            let s3r = sample(base + 2, 1)
            let l = s0l * a0 + s1l * a1 + s2l * a2 + s3l * a3
            let r = s0r * a0 + s1r * a1 + s2r * a2 + s3r * a3
            var g: Float = 1
            if fading {
                let rem = avail - Double(n) * inc
                g = rem >= fadeSpan ? 1 : Float(max(0, min(1, rem / fadeSpan)))
            }
            dst[n * 2] = l * g
            dst[n * 2 + 1] = r * g
            pos += inc
        }
        readPos = pos
        if readPos > Double(writeCount) {
            readPos = Double(writeCount)
            underruns += 1
        }
    }

    /// 不做重采样地取原始采样（诊断录音用）。
    func readRaw(into dst: UnsafeMutablePointer<Float>, frames: Int) -> Int {
        os_unfair_lock_lock(&lock)
        defer { os_unfair_lock_unlock(&lock) }
        let avail = min(Int64(frames), writeCount - Int64(readPos))
        guard avail > 0 else { return 0 }
        var pos = readPos
        for i in 0..<Int(avail) {
            let idx = Int(pos) % capacity
            dst[i * 2] = buf[idx * 2]
            dst[i * 2 + 1] = buf[idx * 2 + 1]
            pos += 1
        }
        readPos = pos
        return Int(avail)
    }
}
