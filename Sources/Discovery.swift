import Foundation
import Darwin

/// 局域网里发现到的另一台「音频桥」。
struct Peer: Identifiable, Equatable {
    let id: UInt64
    var name: String
    var ip: String
    var port: UInt16
    var role: UInt8
    var platform: UInt8
    var state: UInt8
    var caps: UInt8 = 0
    /// 对方信标里报的协议版本。版本不同时照常列出，但不允许拉流/选为推送目标。
    var version: UInt8 = 0
    var lastSeen: Date

    var isReceiver: Bool { role == Proto.roleReceiver }
    var isSender: Bool { role == Proto.roleSender }
    var allowsPull: Bool { caps & Proto.capPull != 0 }
    var mixing: Bool { caps & Proto.capMix != 0 }
    var platformLabel: String { platform == Proto.platformWindows ? "Windows" : (platform == Proto.platformMac ? "Mac" : "设备") }
    /// 对方协议版本与本机不同：音频包结构对不上，不能互连。
    var versionMismatch: Bool { version != 0 && version != Proto.version }
}

/// 设备发现：每秒往各网卡的广播地址发一个信标，同时听别人的信标。
/// 这样选目标时不用手抄 IP；对端换了 IP（重连 Wi-Fi、DHCP 续租）也能凭实例号认出来并自动跟过去。
/// 广播过不了路由器/VPN（Tailscale 等），那种情况用手动地址。
final class Discovery {
    private let queue = DispatchQueue(label: "monitor-audio.discovery", qos: .utility)
    private var fd: Int32 = -1
    private var readSource: DispatchSourceRead?
    private var timer: DispatchSourceTimer?
    private var rx = [UInt8](repeating: 0, count: 2048)
    private var peers: [UInt64: Peer] = [:]

    // 信标里播报的本机状态（主线程写，发现队列读；都是单字节，撕裂读无害）
    var role: UInt8 = Proto.roleIdle
    var state: UInt8 = 0
    var audioPort: UInt16 = Proto.audioPort
    var caps: UInt8 = 0
    /// 监听端口。测试里可以换成空闲端口，避免和正在运行的实例抢单播包。
    var listenPort: UInt16 = Proto.discoveryPort

    /// 设备表有变化时回调（主队列），按名字排序。
    var onChange: (([Peer]) -> Void)?
    /// 收到发现端口上的控制包（拉流请求/应答）时回调（主队列）。data 是完整包的拷贝。
    var onMessage: ((_ type: UInt8, _ data: [UInt8], _ fromIP: String) -> Void)?
    private var lastPublished: [Peer] = []

    func start() {
        stop()
        let s = socket(AF_INET, SOCK_DGRAM, 0)
        guard s >= 0 else { return }
        Sock.setInt(s, SOL_SOCKET, SO_REUSEADDR, 1)
        Sock.setInt(s, SOL_SOCKET, SO_REUSEPORT, 1)
        Sock.setInt(s, SOL_SOCKET, SO_BROADCAST, 1)
        guard Sock.bind(s, port: listenPort) else { close(s); return }
        Sock.setNonBlocking(s)
        fd = s

        let src = DispatchSource.makeReadSource(fileDescriptor: s, queue: queue)
        src.setEventHandler { [weak self] in self?.drain() }
        src.setCancelHandler { close(s) }
        readSource = src
        src.resume()

        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + 0.1, repeating: 1.0, leeway: .milliseconds(100))
        t.setEventHandler { [weak self] in self?.tick() }
        timer = t
        t.resume()
    }

    func stop() {
        timer?.cancel(); timer = nil
        readSource?.cancel(); readSource = nil
        fd = -1
    }

    /// 状态刚变（开始/停止/换角色）时立刻播报一次，对方界面不用等一秒。
    func announceNow() {
        queue.async { [weak self] in self?.sendBeacon() }
    }

    private func tick() {
        sendBeacon()
        let now = Date()
        peers = peers.filter { now.timeIntervalSince($0.value.lastSeen) < 4.5 }
        publish()
    }

    private func sendBeacon() {
        guard fd >= 0 else { return }
        var b = [UInt8](repeating: 0, count: 21 + 61)
        Proto.putMagic(&b, type: Proto.typeBeacon)
        b[5] = Proto.version
        b[6] = role
        b[7] = Proto.platformMac
        b[8] = state
        b[9] = caps
        Proto.putU16(&b, 10, audioPort)
        Proto.putU64(&b, 12, Proto.instanceID)
        let end = Proto.putName(&b, 20, Proto.deviceName)
        for ip in LocalNet.broadcastAddresses() {
            guard let a = Sock.addr(ip: ip, port: listenPort) else { continue }
            b.withUnsafeBytes { _ = Sock.send(fd, $0.baseAddress!, end, to: a) }
        }
    }

    private func drain() {
        while true {
            var sa = sockaddr_in()
            var sl = socklen_t(MemoryLayout<sockaddr_in>.size)
            let n = rx.withUnsafeMutableBytes { raw -> Int in
                withUnsafeMutablePointer(to: &sa) { sp in
                    sp.withMemoryRebound(to: sockaddr.self, capacity: 1) { sap in
                        recvfrom(fd, raw.baseAddress, raw.count, 0, sap, &sl)
                    }
                }
            }
            if n <= 0 { break }
            var control: (UInt8, [UInt8], String)?
            rx.withUnsafeBufferPointer { p in
                let b = p.baseAddress!
                guard Proto.hasMagic(b, n), n >= 6 else { return }
                let type = b[4]
                if type == Proto.typePull || type == Proto.typePullReply
                    || type == Proto.typePullChallenge || type == Proto.typePullAuth
                    || type == Proto.typePullNoKey || type == Proto.typeBye {
                    // 控制包：交回主队列处理（信任校验、弹窗确认都在主线程）。
                    let copy = Array(UnsafeBufferPointer(start: b, count: n))
                    control = (type, copy, Sock.ip(sa))
                    return
                }
                guard type == Proto.typeBeacon, n >= 21 else { return }
                let id = Proto.u64(b, 12)
                guard id != 0, id != Proto.instanceID else { return }
                var name = Proto.name(b, 20, count: n)
                let ip = Sock.ip(sa)
                if name.isEmpty { name = ip }
                peers[id] = Peer(id: id, name: name, ip: ip, port: Proto.u16(b, 10),
                                 role: b[6], platform: b[7], state: b[8], caps: b[9],
                                 version: b[5], lastSeen: Date())
            }
            if let c = control {
                DispatchQueue.main.async { [weak self] in self?.onMessage?(c.0, c.1, c.2) }
            }
        }
        publish()
    }

    /// 往某台设备（或手动 IP）的发现端口发一个控制包。
    func sendControl(_ bytes: [UInt8], to ip: String, port: UInt16 = Proto.discoveryPort) {
        queue.async { [weak self] in
            guard let self, self.fd >= 0, let a = Sock.addr(ip: ip, port: port) else { return }
            bytes.withUnsafeBytes { _ = Sock.send(self.fd, $0.baseAddress!, $0.count, to: a) }
        }
    }

    private func publish() {
        let list = peers.values.sorted {
            $0.name != $1.name ? $0.name.localizedStandardCompare($1.name) == .orderedAscending : $0.id < $1.id
        }
        // lastSeen 每秒都变，比较时忽略它，免得界面每秒白刷一次。
        let same = list.count == lastPublished.count && zip(list, lastPublished).allSatisfy {
            $0.id == $1.id && $0.name == $1.name && $0.ip == $1.ip && $0.port == $1.port
                && $0.role == $1.role && $0.state == $1.state && $0.caps == $1.caps
        }
        guard !same else { return }
        lastPublished = list
        DispatchQueue.main.async { [weak self] in self?.onChange?(list) }
    }
}
