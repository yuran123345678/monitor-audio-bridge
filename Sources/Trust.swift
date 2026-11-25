import Foundation

/// 受信任设备表（Mac 端，存 Application Support/MonitorAudioBridge/trust.json）。
///
/// 两个方向共用一份文件：
///  · 发送端：`devices` 记「哪台设备被允许拉我的声音」，key 是我发给它的密钥；
///  · 接收端：`keys` 记「我拉过哪台发送端、它给我的密钥」。
/// 密钥只是防止局域网里别的程序随手拉流，不是加密——音频仍然是明文 UDP，和 v2 一样不设防窃听。
final class TrustStore {
    struct Device: Codable, Identifiable, Equatable {
        var id: UInt64
        var name: String
        var key: UInt64
        var lastSeen: Date
    }

    private struct File: Codable {
        var devices: [Device] = []
        var keys: [UInt64: UInt64] = [:]
    }

    private(set) var devices: [Device] = []
    private var keys: [UInt64: UInt64] = [:]
    private let url: URL
    private let lock = NSLock()

    /// 指定文件位置（测试用：不碰真实的 trust.json）。
    init(url: URL) {
        self.url = url
        load()
    }

    convenience init() {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("MonitorAudioBridge", isDirectory: true)
        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        self.init(url: base.appendingPathComponent("trust.json"))
    }

    private func load() {
        guard let data = try? Data(contentsOf: url),
              let f = try? JSONDecoder().decode(File.self, from: data) else { return }
        devices = f.devices
        keys = f.keys
    }

    private func save() {
        let f = File(devices: devices, keys: keys)
        guard let data = try? JSONEncoder().encode(f) else { return }
        try? data.write(to: url, options: .atomic)
    }

    // MARK: - 发送端

    func verify(id: UInt64, key: UInt64) -> Bool {
        guard key != 0 else { return false }
        lock.lock(); defer { lock.unlock() }
        return devices.contains { $0.id == id && $0.key == key }
    }

    /// 允许一台设备（已有就换新密钥并更新时间），返回密钥。
    @discardableResult
    func grant(id: UInt64, name: String) -> UInt64 {
        lock.lock(); defer { lock.unlock() }
        var key = UInt64.random(in: 1...UInt64.max)
        if let i = devices.firstIndex(where: { $0.id == id }) {
            key = devices[i].key
            devices[i].name = name
            devices[i].lastSeen = Date()
        } else {
            devices.append(Device(id: id, name: name, key: key, lastSeen: Date()))
        }
        save()
        return key
    }

    func revoke(id: UInt64) {
        lock.lock(); defer { lock.unlock() }
        devices.removeAll { $0.id == id }
        save()
    }

    func revokeAll() {
        lock.lock(); defer { lock.unlock() }
        devices.removeAll()
        save()
    }

    func rename(id: UInt64, name: String) {
        lock.lock(); defer { lock.unlock() }
        guard let i = devices.firstIndex(where: { $0.id == id }) else { return }
        devices[i].name = name
        devices[i].lastSeen = Date()
        save()
    }

    // MARK: - 接收端

    /// 发送端视角：查一台受信任设备（请求方）的密钥。
    func key(forDevice id: UInt64) -> UInt64? {
        lock.lock(); defer { lock.unlock() }
        return devices.first { $0.id == id }?.key
    }

    func key(forSender id: UInt64) -> UInt64 {
        lock.lock(); defer { lock.unlock() }
        return keys[id] ?? 0
    }

    func setKey(_ key: UInt64, forSender id: UInt64) {
        guard id != 0, key != 0 else { return }
        lock.lock(); defer { lock.unlock() }
        keys[id] = key
        save()
    }

    func forgetSender(_ id: UInt64) {
        lock.lock(); defer { lock.unlock() }
        keys.removeValue(forKey: id)
        save()
    }
}
