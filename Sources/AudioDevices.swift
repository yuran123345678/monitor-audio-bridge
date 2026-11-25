import Foundation
import CoreAudio

/// CoreAudio 通用读取封装。
enum CA {
    static let system = AudioObjectID(kAudioObjectSystemObject)

    @inline(__always)
    static func addr(_ sel: AudioObjectPropertySelector,
                     _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal,
                     _ elem: AudioObjectPropertyElement = kAudioObjectPropertyElementMain) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: sel, mScope: scope, mElement: elem)
    }

    static func u32(_ obj: AudioObjectID, _ sel: AudioObjectPropertySelector,
                    _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal,
                    _ elem: AudioObjectPropertyElement = kAudioObjectPropertyElementMain) -> UInt32? {
        var a = addr(sel, scope, elem)
        var v: UInt32 = 0
        var s = UInt32(MemoryLayout<UInt32>.size)
        guard AudioObjectGetPropertyData(obj, &a, 0, nil, &s, &v) == noErr else { return nil }
        return v
    }

    static func f64(_ obj: AudioObjectID, _ sel: AudioObjectPropertySelector,
                    _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal,
                    _ elem: AudioObjectPropertyElement = kAudioObjectPropertyElementMain) -> Float64? {
        var a = addr(sel, scope, elem)
        var v: Float64 = 0
        var s = UInt32(MemoryLayout<Float64>.size)
        guard AudioObjectGetPropertyData(obj, &a, 0, nil, &s, &v) == noErr else { return nil }
        return v
    }

    static func deviceID(_ obj: AudioObjectID, _ sel: AudioObjectPropertySelector) -> AudioDeviceID? {
        var a = addr(sel)
        var v: AudioDeviceID = 0
        var s = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(obj, &a, 0, nil, &s, &v) == noErr, v != 0 else { return nil }
        return v
    }

    static func string(_ obj: AudioObjectID, _ sel: AudioObjectPropertySelector) -> String? {
        var a = addr(sel)
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        var cf: Unmanaged<CFString>?
        let st = AudioObjectGetPropertyData(obj, &a, 0, nil, &size, &cf)
        guard st == noErr, let v = cf else { return nil }
        return v.takeRetainedValue() as String
    }

    static func channels(_ dev: AudioDeviceID, scope: AudioObjectPropertyScope) -> Int {
        var a = addr(kAudioDevicePropertyStreamConfiguration, scope)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(dev, &a, 0, nil, &size) == noErr, size > 0 else { return 0 }
        let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { raw.deallocate() }
        guard AudioObjectGetPropertyData(dev, &a, 0, nil, &size, raw) == noErr else { return 0 }
        let list = UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self))
        var n = 0
        for b in list { n += Int(b.mNumberChannels) }
        return n
    }
}

struct AudioDeviceInfo: Hashable {
    let id: AudioDeviceID
    let uid: String
    let name: String
    let transport: UInt32
    let inputs: Int
    let outputs: Int
    let sampleRate: Double

    var isInput: Bool { inputs > 0 }
    var isOutput: Bool { outputs > 0 }

    var transportLabel: String {
        switch transport {
        case kAudioDeviceTransportTypeUSB: return "USB"
        case kAudioDeviceTransportTypeBuiltIn: return "内建"
        case kAudioDeviceTransportTypeVirtual: return "虚拟"
        case kAudioDeviceTransportTypeDisplayPort: return "DisplayPort"
        case kAudioDeviceTransportTypeHDMI: return "HDMI"
        case kAudioDeviceTransportTypeAggregate: return "聚合"
        case kAudioDeviceTransportTypeBluetooth: return "蓝牙"
        case kAudioDeviceTransportTypeAirPlay: return "AirPlay"
        default: return "其他"
        }
    }
}

enum AudioDevices {
    static func list() -> [AudioDeviceInfo] {
        var a = CA.addr(kAudioHardwarePropertyDevices)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(CA.system, &a, 0, nil, &size) == noErr, size > 0 else { return [] }
        let count = Int(size) / MemoryLayout<AudioDeviceID>.size
        var ids = [AudioDeviceID](repeating: 0, count: count)
        guard AudioObjectGetPropertyData(CA.system, &a, 0, nil, &size, &ids) == noErr else { return [] }
        return ids.compactMap { info($0) }
    }

    static func info(_ dev: AudioDeviceID) -> AudioDeviceInfo? {
        guard let uid = CA.string(dev, kAudioDevicePropertyDeviceUID) else { return nil }
        let name = CA.string(dev, kAudioObjectPropertyName)
            ?? CA.string(dev, kAudioDevicePropertyDeviceNameCFString)
            ?? "未命名设备"
        let transport = CA.u32(dev, kAudioDevicePropertyTransportType) ?? 0
        let inputs = CA.channels(dev, scope: kAudioObjectPropertyScopeInput)
        let outputs = CA.channels(dev, scope: kAudioObjectPropertyScopeOutput)
        let rate = CA.f64(dev, kAudioDevicePropertyNominalSampleRate) ?? 48_000
        return AudioDeviceInfo(id: dev, uid: uid, name: name, transport: transport,
                               inputs: inputs, outputs: outputs, sampleRate: rate)
    }

    static func device(uid: String) -> AudioDeviceInfo? {
        list().first { $0.uid == uid }
    }

    static func defaultID(_ sel: AudioObjectPropertySelector) -> AudioDeviceID? {
        CA.deviceID(CA.system, sel)
    }

    @discardableResult
    static func setDefault(_ sel: AudioObjectPropertySelector, device: AudioDeviceID) -> Bool {
        var a = CA.addr(sel)
        var d = device
        let st = AudioObjectSetPropertyData(CA.system, &a, 0, nil,
                                            UInt32(MemoryLayout<AudioDeviceID>.size), &d)
        return st == noErr
    }

    /// 挑一个像样的输入设备：优先 USB，其次内建之外的，最后任意有输入的。
    static func preferredInput(preferUID: String?) -> AudioDeviceInfo? {
        let all = list().filter { $0.isInput }
        if let uid = preferUID, !uid.isEmpty, let d = all.first(where: { $0.uid == uid }) { return d }
        if let d = all.first(where: { $0.transport == kAudioDeviceTransportTypeUSB }) { return d }
        if let d = all.first(where: { $0.transport == kAudioDeviceTransportTypeBuiltIn && $0.inputs > 0 && $0.name.contains("麦克风") == false }) { return d }
        return all.first
    }
}
