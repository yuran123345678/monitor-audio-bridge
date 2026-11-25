import Foundation
import AVFoundation
import AudioToolbox
import CoreAudio

enum PlayerError: LocalizedError {
    case format
    case start(String)

    var errorDescription: String? {
        switch self {
        case .format: return "无法创建输出音频格式"
        case .start(let m): return "启动播放失败：\(m)"
        }
    }
}

/// AVAudioEngine + AVAudioSourceNode：从 RingBridge 按 48 kHz 拉数据，
/// 交给系统混音器/采样率转换后送到指定输出设备（默认跟随系统输出）。
///
/// 每次 start 都新建一个 AVAudioEngine：插拔耳机、切换输出、睡眠唤醒之后旧引擎的状态不可信，
/// 重建最省事也最稳。引擎因为硬件配置变化自己停下时，通过 onConfigurationChange 通知上层重启。
final class PlayerEngine {
    private var engine: AVAudioEngine?
    private var source: AVAudioSourceNode?
    private var configObserver: NSObjectProtocol?
    private let bridge: RingBridge
    /// 混音模式：非 nil 时渲染线程直接从混音器取数（bridge 不再使用）。
    /// 界面线程会在重启接收端后换一个新的，渲染线程每帧读，必须加锁。
    private let mixerLock = NSLock()
    private var _mixer: MixBus?
    var mixer: MixBus? {
        get {
            mixerLock.lock(); defer { mixerLock.unlock() }
            return _mixer
        }
        set {
            mixerLock.lock(); _mixer = newValue; mixerLock.unlock()
        }
    }
    private var scratch: UnsafeMutablePointer<Float>
    private let renderRate: Double = 48_000

    var volume: Float = 0.8
    var muted = false
    var inputRate: Double = 48_000
    /// 目标缓冲深度（毫秒）。
    var targetMs: Double = 40

    private(set) var isRunning = false
    private(set) var outputRate: Double = 48_000
    /// 输出设备自身的延迟（毫秒，估算值，用来在界面上给出总延迟）。
    private(set) var deviceLatencyMs: Double = 0

    /// 硬件配置变化（插拔耳机、默认输出切换、采样率被改）导致引擎停下时回调（主队列）。
    var onConfigurationChange: (() -> Void)?

    /// 电平表：自上次读取以来的峰值（音量/静音之前的信号电平）。渲染线程写、界面线程读后清零，
    /// 竞争只会让表头偶尔少跳一格，不值得为它加锁。
    private var peakL: Float = 0
    private var peakR: Float = 0

    /// 诊断/自测用：把混音器最终输出的交织双声道抓一份出来。
    var onCapture: (([Float], Double) -> Void)?
    private var captureInstalled = false

    init(bridge: RingBridge) {
        self.bridge = bridge
        scratch = UnsafeMutablePointer<Float>.allocate(capacity: 8192)
        scratch.initialize(repeating: 0, count: 8192)
    }

    deinit {
        stop()
        scratch.deinitialize(count: 8192)
        scratch.deallocate()
    }

    func takePeaks() -> (Float, Float) {
        let p = (peakL, peakR)
        peakL = 0; peakR = 0
        return p
    }

    func start(outputDeviceID: AudioDeviceID?) throws {
        stop()
        guard let format = AVAudioFormat(standardFormatWithSampleRate: renderRate, channels: 2) else {
            throw PlayerError.format
        }
        let eng = AVAudioEngine()
        let node = AVAudioSourceNode(format: format) { [weak self] _, _, frameCount, abl in
            guard let self else { return noErr }
            return self.render(frames: Int(frameCount), abl: abl)
        }
        eng.attach(node)
        eng.connect(node, to: eng.mainMixerNode, format: format)

        if let dev = outputDeviceID, let au = eng.outputNode.audioUnit {
            var d = dev
            AudioUnitSetProperty(au, kAudioOutputUnitProperty_CurrentDevice,
                                 kAudioUnitScope_Global, 0, &d,
                                 UInt32(MemoryLayout<AudioDeviceID>.size))
        }

        eng.prepare()
        do {
            try eng.start()
        } catch {
            eng.detach(node)
            throw PlayerError.start(error.localizedDescription)
        }
        engine = eng
        source = node
        isRunning = true
        outputRate = eng.outputNode.outputFormat(forBus: 0).sampleRate
        deviceLatencyMs = eng.outputNode.presentationLatency * 1000

        configObserver = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange, object: eng, queue: .main
        ) { [weak self] _ in
            guard let self else { return }
            self.isRunning = false
            self.onConfigurationChange?()
        }
    }

    func stop() {
        if let o = configObserver {
            NotificationCenter.default.removeObserver(o)
            configObserver = nil
        }
        stopCapture()
        if let eng = engine {
            eng.stop()
            if let node = source { eng.detach(node) }
        }
        source = nil
        engine = nil
        isRunning = false
    }

    /// 引擎是否真的还在转（看门狗用：有些情况下引擎停了但不发通知）。
    var isEngineAlive: Bool { engine?.isRunning ?? false }

    private func render(frames: Int, abl: UnsafeMutablePointer<AudioBufferList>) -> OSStatus {
        guard frames > 0, frames <= 4096 else { return noErr }
        let inRate = inputRate
        if let m = mixer {
            m.pull(scratch, frames: frames, outRate: renderRate)
        } else {
            bridge.pull(scratch, frames: frames, outRate: renderRate,
                        inRate: inRate, targetFrames: targetMs / 1000.0 * inRate)
        }

        var pl: Float = 0, pr: Float = 0
        for i in 0..<frames {
            let l = abs(scratch[i * 2]), r = abs(scratch[i * 2 + 1])
            if l > pl { pl = l }
            if r > pr { pr = r }
        }
        if pl > peakL { peakL = pl }
        if pr > peakR { peakR = pr }

        let gain: Float = muted ? 0 : volume
        let buffers = UnsafeMutableAudioBufferListPointer(abl)
        if buffers.count >= 2,
           let lRaw = buffers[0].mData,
           let rRaw = buffers[1].mData {
            let lp = lRaw.assumingMemoryBound(to: Float.self)
            let rp = rRaw.assumingMemoryBound(to: Float.self)
            for i in 0..<frames {
                lp[i] = scratch[i * 2] * gain
                rp[i] = scratch[i * 2 + 1] * gain
            }
        } else if buffers.count == 1, let mRaw = buffers[0].mData {
            let mp = mRaw.assumingMemoryBound(to: Float.self)
            for i in 0..<(frames * 2) {
                mp[i] = scratch[i] * gain
            }
        }
        return noErr
    }

    /// 在混音器上装一个 tap，把最终听得到的音频抓出来（自测和诊断录音用）。
    func startCapture() {
        guard !captureInstalled, let mixer = engine?.mainMixerNode else { return }
        let fmt = mixer.outputFormat(forBus: 0)
        mixer.installTap(onBus: 0, bufferSize: 1024, format: fmt) { [weak self] buf, _ in
            guard let self, let cb = self.onCapture, let ch = buf.floatChannelData else { return }
            let n = Int(buf.frameLength)
            guard n > 0 else { return }
            var out = [Float](repeating: 0, count: n * 2)
            if buf.format.channelCount >= 2 {
                for i in 0..<n {
                    out[i * 2] = ch[0][i]
                    out[i * 2 + 1] = ch[1][i]
                }
            } else {
                for i in 0..<n {
                    out[i * 2] = ch[0][i]
                    out[i * 2 + 1] = ch[0][i]
                }
            }
            cb(out, buf.format.sampleRate)
        }
        captureInstalled = true
    }

    func stopCapture() {
        guard captureInstalled else { return }
        engine?.mainMixerNode.removeTap(onBus: 0)
        captureInstalled = false
    }
}
