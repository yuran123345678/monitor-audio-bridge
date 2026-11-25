import Foundation
import CoreAudio
import AudioToolbox

enum TapError: LocalizedError {
    case noOutputDevice
    case createTap(OSStatus)
    case createAggregate(OSStatus)
    case format
    case ioProc(OSStatus)
    case start(OSStatus)

    var errorDescription: String? {
        switch self {
        case .noOutputDevice: return "找不到系统输出设备"
        case .createTap(let s): return "无法采集系统声音（\(s)）。请在 系统设置 › 隐私与安全性 › 屏幕与系统录音 里允许本应用录制系统音频"
        case .createAggregate(let s): return "无法建立采集设备（\(s)）"
        case .format: return "读取系统声音格式失败"
        case .ioProc(let s): return "无法挂接采集回调（\(s)）"
        case .start(let s): return "启动采集失败（\(s)）"
        }
    }
}

/// 采集 Mac 的系统声音（Core Audio Process Tap，macOS 14.2+）。
///
/// 不装虚拟声卡：系统给一个「全局立体声 tap」，把它塞进一个私有的聚合设备，再对聚合设备挂 IOProc 读数据。
/// 首次使用系统会弹「允许录制系统音频」；拒绝的话 tap 照样能建，但读到的全是静音（界面上用电平表提示）。
///
/// muteLocal = true 时用 mutedWhenTapped：声音只送到对端，本机扬声器不出声。
final class SystemAudioTap {
    /// 交织双声道 Float、帧数、采样率。在采集队列上回调。
    var onAudio: ((UnsafePointer<Float>, Int, Double) -> Void)?

    private let queue = DispatchQueue(label: "monitor-audio.tap", qos: .userInteractive)
    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggregateID = AudioObjectID(kAudioObjectUnknown)
    private var procID: AudioDeviceIOProcID?
    private var scratch = [Float](repeating: 0, count: 8192 * 2)
    private(set) var sampleRate: Double = 48_000
    private(set) var isRunning = false
    /// 采集所依附的输出设备 UID（默认输出一变就要重建）。
    private(set) var outputUID = ""

    deinit { stop() }

    func start(muteLocal: Bool) throws {
        stop()
        guard let outDev = AudioDevices.defaultID(kAudioHardwarePropertyDefaultOutputDevice),
              let outUID = CA.string(outDev, kAudioDevicePropertyDeviceUID) else {
            throw TapError.noOutputDevice
        }
        outputUID = outUID

        // 把自己排除在外：本应用在接收模式下放出来的声音不该再被抓回去。
        var exclude: [AudioObjectID] = []
        if let me = Self.ownProcessObject() { exclude.append(me) }

        let desc = CATapDescription(stereoGlobalTapButExcludeProcesses: exclude)
        desc.uuid = UUID()
        desc.name = "音频桥采集"
        desc.isPrivate = true
        desc.muteBehavior = muteLocal ? .mutedWhenTapped : .unmuted

        var tap = AudioObjectID(kAudioObjectUnknown)
        var st = AudioHardwareCreateProcessTap(desc, &tap)
        guard st == noErr, tap != kAudioObjectUnknown else { throw TapError.createTap(st) }
        tapID = tap

        var fmtAddr = CA.addr(kAudioTapPropertyFormat)
        var asbd = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        st = AudioObjectGetPropertyData(tap, &fmtAddr, 0, nil, &size, &asbd)
        guard st == noErr, asbd.mSampleRate > 0, asbd.mChannelsPerFrame > 0,
              asbd.mFormatFlags & kAudioFormatFlagIsFloat != 0, asbd.mBitsPerChannel == 32 else {
            cleanup()
            throw TapError.format
        }
        sampleRate = asbd.mSampleRate

        let aggDesc: [String: Any] = [
            kAudioAggregateDeviceNameKey: "音频桥采集",
            kAudioAggregateDeviceUIDKey: "com.dafeiyu.monitor-audio.tap." + UUID().uuidString,
            kAudioAggregateDeviceMainSubDeviceKey: outUID,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: outUID]],
            kAudioAggregateDeviceTapListKey: [[
                kAudioSubTapDriftCompensationKey: true,
                kAudioSubTapUIDKey: desc.uuid.uuidString,
            ]],
        ]
        var agg = AudioObjectID(kAudioObjectUnknown)
        st = AudioHardwareCreateAggregateDevice(aggDesc as CFDictionary, &agg)
        guard st == noErr, agg != kAudioObjectUnknown else {
            cleanup()
            throw TapError.createAggregate(st)
        }
        aggregateID = agg

        let rate = sampleRate
        var proc: AudioDeviceIOProcID?
        st = AudioDeviceCreateIOProcIDWithBlock(&proc, agg, queue) { [weak self] _, inData, _, _, _ in
            self?.handle(inData, rate: rate)
        }
        guard st == noErr, let proc else {
            cleanup()
            throw TapError.ioProc(st)
        }
        procID = proc

        st = AudioDeviceStart(agg, proc)
        guard st == noErr else {
            cleanup()
            throw TapError.start(st)
        }
        isRunning = true
    }

    func stop() {
        cleanup()
    }

    private func cleanup() {
        isRunning = false
        if aggregateID != kAudioObjectUnknown {
            if let p = procID {
                AudioDeviceStop(aggregateID, p)
                AudioDeviceDestroyIOProcID(aggregateID, p)
            }
            AudioHardwareDestroyAggregateDevice(aggregateID)
        }
        procID = nil
        aggregateID = AudioObjectID(kAudioObjectUnknown)
        if tapID != kAudioObjectUnknown {
            AudioHardwareDestroyProcessTap(tapID)
            tapID = AudioObjectID(kAudioObjectUnknown)
        }
        // 等采集队列上可能还在跑的回调收尾，之后 scratch 才安全。
        queue.sync {}
    }

    private func handle(_ inData: UnsafePointer<AudioBufferList>, rate: Double) {
        let list = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: inData))
        guard list.count > 0, let first = list[0].mData else { return }
        let cap = scratch.count / 2

        if list.count == 1 {
            // 交织：一个 buffer 里 N 声道
            let ch = max(1, Int(list[0].mNumberChannels))
            let frames = min(cap, Int(list[0].mDataByteSize) / (4 * ch))
            guard frames > 0 else { return }
            let p = first.assumingMemoryBound(to: Float.self)
            for i in 0..<frames {
                let l = p[i * ch]
                scratch[i * 2] = l
                scratch[i * 2 + 1] = ch >= 2 ? p[i * ch + 1] : l
            }
            scratch.withUnsafeBufferPointer { onAudio?($0.baseAddress!, frames, rate) }
        } else {
            // 非交织：每个 buffer 一个声道
            guard let second = list[1].mData else { return }
            let frames = min(cap, Int(min(list[0].mDataByteSize, list[1].mDataByteSize)) / 4)
            guard frames > 0 else { return }
            let lp = first.assumingMemoryBound(to: Float.self)
            let rp = second.assumingMemoryBound(to: Float.self)
            for i in 0..<frames {
                scratch[i * 2] = lp[i]
                scratch[i * 2 + 1] = rp[i]
            }
            scratch.withUnsafeBufferPointer { onAudio?($0.baseAddress!, frames, rate) }
        }
    }

    private static func ownProcessObject() -> AudioObjectID? {
        var addr = CA.addr(kAudioHardwarePropertyTranslatePIDToProcessObject)
        var pid = getpid()
        var obj = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        let st = AudioObjectGetPropertyData(CA.system, &addr, UInt32(MemoryLayout<pid_t>.size), &pid, &size, &obj)
        return (st == noErr && obj != kAudioObjectUnknown) ? obj : nil
    }
}
