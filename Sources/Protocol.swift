import Foundation
import Darwin

/// 线上协议（小端）。Mac / Windows 两端实现必须逐字节一致，改这里就要同步改 windows/Protocol.cs。
///
/// 所有包都以 "MAB1" + type(u8) 开头。
///
/// type 1  音频（发送端 → 接收端，音频端口）
///   5  ch u8 · 6 rate u32 · 10 seq u32 · 14 frames u16 · 16 tsc u64 · 24 Int16 交织 PCM
/// type 2  保活（发送端 → 接收端，每 0.5 s 一个；系统没声音时 Windows 不出音频包，靠它维持「已连接」）
///   5..23 同音频头（frames = 0）· 24 id u64 · 32 nameLen u8 · 33 name utf8
/// type 3  回执（接收端 → 发送端，每 0.5 s 一个，发回音频包的来源地址）
///   5 flags u8（bit0 正在出声 · bit1 静音 · bit3 正忙：已在接收另一台）
///   6 bufferMs u16 · 8 packets u32 · 12 lost u32 · 16 jitterUs u32 · 20 targetMs u16
///   24 id u64 · 32 nameLen u8 · 33 name utf8
/// type 4  再见（任一方向，退出/停止时发）
///   8 id u64
/// type 16 发现信标（广播到发现端口，每秒一个）
///   5 ver u8 · 6 role u8 · 7 platform u8 · 8 state u8 · 9 caps u8 · 10 audioPort u16
///   12 id u64 · 20 nameLen u8 · 21 name utf8
/// type 5  拉流请求（接收端 → 发送端，单播到发现端口）。v4 起不再携带长期密钥。
///   5 ver u8 · 6 audioPort u16 · 8 targetID u64 · 16 requesterID u64 · 24 nameLen u8 · 25 name utf8
/// type 7  拉流质询（发送端 → 接收端）：回一个一次性随机数，让请求方证明自己持有密钥
///   5 ver u8 · 8 requesterID u64 · 16 senderID u64 · 24 nonce u64 · 32 nameLen u8 · 33 name utf8
/// type 8  拉流鉴权（接收端 → 发送端）：HMAC-SHA256(key, nonce‖requesterID‖senderID) 的前 16 字节
///   5 ver u8 · 6 audioPort u16 · 8 nonce u64 · 16 requesterID u64 · 24 senderID u64
///   32 mac[16] · 48 nameLen u8 · 49 name utf8
/// type 6  拉流应答（发送端 → 接收端，单播到发现端口）
///   5 ver u8 · 6 result u8 · 7 flags u8（bit0 带密钥 · bit1 带会话令牌）· 8 requesterID u64
///   16 senderID u64 · 24 token u64 · 32 key u64 · 40 nameLen u8 · 41 name utf8
///
/// 音频头第 23 字节（tsc 最高字节）在 v3 里被挪用成标志位：bit0 = 本包尾部附带上一个包的 PCM（丢包冗余）。
/// 接收端因此必须按 56 位读 tsc（& 0x00FF_FFFF_FFFF_FFFF），否则抖动统计会跳变。
/// 注意：v3 起音频包与 v2 不再互通；v4 起拉流/再见换成质询-应答且再见带会话令牌，与 v3 也不互通——两端要一起升级。
///
/// 信标第 9 字节 caps：bit0 允许被拉流 · bit1 接收端开启混音（v2 端忽略，也不代表不安全，只是老版本看不到）。
enum Proto {
    static let audioPort: UInt16 = 47_831
    static let discoveryPort: UInt16 = 47_832
    static let version: UInt8 = 5

    static let headerSize = 24
    static let framesPerPacket = 240
    /// 带冗余副本时的每包帧数：24 + 120*4*2 = 984 字节，留在 MTU 内。
    static let redundantFramesPerPacket = 120

    static let typeAudio: UInt8 = 1
    static let typeKeepalive: UInt8 = 2
    static let typeReport: UInt8 = 3
    static let typeBye: UInt8 = 4
    static let typePull: UInt8 = 5
    static let typePullReply: UInt8 = 6
    static let typePullChallenge: UInt8 = 7
    static let typePullAuth: UInt8 = 8
    static let typePullNoKey: UInt8 = 9

    /// 拉流控制包是否属于本版协议（版本字节只写不读是之前的缺陷）。
    static func acceptsVersion(_ v: UInt8) -> Bool { v == version }
    static let typeBeacon: UInt8 = 16

    /// 信标能力位（第 9 字节）。
    static let capPull: UInt8 = 1
    static let capMix: UInt8 = 2

    /// 音频头第 23 字节扩展位。
    static let audioFlagRedundant: UInt8 = 1

    /// 拉流应答结果。
    static let pullOK: UInt8 = 0        // 受信任，直接放行
    static let pullOKNew: UInt8 = 1     // 新设备，带新密钥
    static let pullDenied: UInt8 = 2    // 对方点了拒绝
    static let pullBusy: UInt8 = 3      // 对方正在发送给另一台
    static let pullDisabled: UInt8 = 4  // 对方关掉了「允许拉取」或不在发送模式
    static let pullChallenge: UInt8 = 5 // 保留：实际用 type 7 单独发包
    static let pullWait: UInt8 = 6      // 对方正在等用户确认

    static let roleIdle: UInt8 = 0
    static let roleSender: UInt8 = 1
    static let roleReceiver: UInt8 = 2

    static let platformMac: UInt8 = 1
    static let platformWindows: UInt8 = 2

    static let flagPlaying: UInt8 = 1
    static let flagMuted: UInt8 = 2
    static let flagBusy: UInt8 = 8

    /// 设备号：首次运行随机生成后存盘。用来在信标里认出自己、在对端换 IP 或重启之后认出同一台，
    /// 「自动连回上次的设备」靠的就是它。
    static let instanceID: UInt64 = {
        let key = "deviceID"
        if let s = UserDefaults.standard.string(forKey: key), let v = UInt64(s), v != 0 { return v }
        let v = UInt64.random(in: 1...UInt64.max)
        UserDefaults.standard.set(String(v), forKey: key)
        return v
    }()

    static var deviceName: String {
        // 预览/截图时可以用 MAB_DEVICE_NAME 覆盖本机名字，避免把真实的电脑名带进公开图片。
        if let override = ProcessInfo.processInfo.environment["MAB_DEVICE_NAME"], !override.isEmpty {
            return override
        }
        let n = Host.current().localizedName ?? ProcessInfo.processInfo.hostName
        return n.isEmpty ? "Mac" : n
    }

    @inline(__always)
    static func hasMagic(_ b: UnsafePointer<UInt8>, _ count: Int) -> Bool {
        count >= 5 && b[0] == 0x4D && b[1] == 0x41 && b[2] == 0x42 && b[3] == 0x31
    }

    static func putMagic(_ b: inout [UInt8], type: UInt8) {
        b[0] = 0x4D; b[1] = 0x41; b[2] = 0x42; b[3] = 0x31
        b[4] = type
    }

    static func putU16(_ b: inout [UInt8], _ o: Int, _ v: UInt16) {
        b[o] = UInt8(v & 0xFF); b[o + 1] = UInt8(v >> 8)
    }

    static func putU32(_ b: inout [UInt8], _ o: Int, _ v: UInt32) {
        for i in 0..<4 { b[o + i] = UInt8((v >> (8 * UInt32(i))) & 0xFF) }
    }

    static func putU64(_ b: inout [UInt8], _ o: Int, _ v: UInt64) {
        for i in 0..<8 { b[o + i] = UInt8((v >> (8 * UInt64(i))) & 0xFF) }
    }

    @inline(__always)
    static func u16(_ b: UnsafePointer<UInt8>, _ o: Int) -> UInt16 {
        UInt16(b[o]) | UInt16(b[o + 1]) << 8
    }

    @inline(__always)
    static func u32(_ b: UnsafePointer<UInt8>, _ o: Int) -> UInt32 {
        UInt32(b[o]) | UInt32(b[o + 1]) << 8 | UInt32(b[o + 2]) << 16 | UInt32(b[o + 3]) << 24
    }

    static func u64(_ b: UnsafePointer<UInt8>, _ o: Int) -> UInt64 {
        var v: UInt64 = 0
        for i in 0..<8 { v |= UInt64(b[o + i]) << (8 * UInt64(i)) }
        return v
    }

    /// 在 offset 处写 nameLen(u8) + utf8，返回写完后的偏移。名字最长 60 字节（按字符截断，不切坏 UTF-8）。
    static func putName(_ b: inout [UInt8], _ o: Int, _ name: String) -> Int {
        var s = name
        while s.utf8.count > 60 { s.removeLast() }
        let bytes = Array(s.utf8)
        b[o] = UInt8(bytes.count)
        for (i, c) in bytes.enumerated() { b[o + 1 + i] = c }
        return o + 1 + bytes.count
    }

    static func name(_ b: UnsafePointer<UInt8>, _ o: Int, count: Int) -> String {
        guard count > o else { return "" }
        let n = Int(b[o])
        guard n > 0, count >= o + 1 + n else { return "" }
        return String(decoding: UnsafeBufferPointer(start: b + o + 1, count: n), as: UTF8.self)
    }

    static func keepalive(rate: UInt32, seq: UInt32) -> [UInt8] {
        var b = [UInt8](repeating: 0, count: 33 + 61)
        putMagic(&b, type: typeKeepalive)
        putU32(&b, 6, rate)
        putU32(&b, 10, seq)
        putU64(&b, 16, DispatchTime.now().uptimeNanoseconds)
        putU64(&b, 24, instanceID)
        let end = putName(&b, 32, deviceName)
        return Array(b[0..<end])
    }

    /// 再见：带走会话令牌，接收方据此确认「是同一个会话」才停发。
    static func bye(token: UInt64 = 0) -> [UInt8] {
        var b = [UInt8](repeating: 0, count: 16)
        putMagic(&b, type: typeBye)
        putU64(&b, 8, token)
        return b
    }

    /// 拉流请求：请 target 开始把声音发到本机的 audioPort（请求本身从发现端口发出）。
    static func pull(target: UInt64, audioPort: UInt16) -> [UInt8] {
        var b = [UInt8](repeating: 0, count: 25 + 61)
        putMagic(&b, type: typePull)
        b[5] = version
        putU16(&b, 6, audioPort)
        putU64(&b, 8, target)
        putU64(&b, 16, instanceID)
        let end = putName(&b, 24, deviceName)
        return Array(b[0..<end])
    }

    /// 质询：发送端回一次性随机数。
    static func pullChallenge(to requester: UInt64, nonce: UInt64, senderID: UInt64) -> [UInt8] {
        var b = [UInt8](repeating: 0, count: 33 + 61)
        putMagic(&b, type: typePullChallenge)
        b[5] = version
        putU64(&b, 8, requester)
        putU64(&b, 16, senderID)
        putU64(&b, 24, nonce)
        let end = putName(&b, 32, deviceName)
        return Array(b[0..<end])
    }

    /// 鉴权：带着对质询的应答。
    static func pullAuth(to senderID: UInt64, nonce: UInt64, mac: [UInt8], audioPort: UInt16) -> [UInt8] {
        var b = [UInt8](repeating: 0, count: 49 + 61)
        putMagic(&b, type: typePullAuth)
        b[5] = version
        putU16(&b, 6, audioPort)
        putU64(&b, 8, nonce)
        putU64(&b, 16, instanceID)
        putU64(&b, 24, senderID)
        for i in 0..<min(16, mac.count) { b[32 + i] = mac[i] }
        let end = putName(&b, 48, deviceName)
        return Array(b[0..<end])
    }

    /// 「我没有你的密钥」：接收端收到质询但本机没有密钥（首次应答包丢了、或信任表被删）。
    /// 发送端只在「刚给这个设备、这个来源 IP 发过质询」时才认，随后作废旧密钥并重新弹窗配对。
    static func pullNoKey(to senderID: UInt64, audioPort: UInt16) -> [UInt8] {
        var b = [UInt8](repeating: 0, count: 25 + 61)
        putMagic(&b, type: typePullNoKey)
        b[5] = version
        putU16(&b, 6, audioPort)
        putU64(&b, 8, instanceID)
        putU64(&b, 16, senderID)
        let end = putName(&b, 24, deviceName)
        return Array(b[0..<end])
    }

    /// 拉流应答。result 见 pullOK / pullOKNew / pullDenied / pullBusy / pullDisabled / pullWait。
    static func pullReply(to requester: UInt64, result: UInt8, token: UInt64, key: UInt64) -> [UInt8] {
        var b = [UInt8](repeating: 0, count: 41 + 61)
        putMagic(&b, type: typePullReply)
        b[5] = version
        b[6] = result
        b[7] = (key != 0 ? 1 : 0) | (token != 0 ? 2 : 0)
        putU64(&b, 8, requester)
        putU64(&b, 16, instanceID)
        putU64(&b, 24, token)
        putU64(&b, 32, key)
        let end = putName(&b, 40, deviceName)
        return Array(b[0..<end])
    }
}

/// sockaddr_in 小工具。
enum Sock {
    static func addr(ip: String, port: UInt16) -> sockaddr_in? {
        var a = sockaddr_in()
        a.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        a.sin_family = sa_family_t(AF_INET)
        a.sin_port = port.bigEndian
        guard inet_pton(AF_INET, ip, &a.sin_addr) == 1 else { return nil }
        return a
    }

    static func any(port: UInt16) -> sockaddr_in {
        var a = sockaddr_in()
        a.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        a.sin_family = sa_family_t(AF_INET)
        a.sin_port = port.bigEndian
        a.sin_addr.s_addr = INADDR_ANY
        return a
    }

    static func ip(_ sa: sockaddr_in) -> String {
        var a = sa.sin_addr
        var buf = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
        inet_ntop(AF_INET, &a, &buf, socklen_t(INET_ADDRSTRLEN))
        return String(cString: buf)
    }

    @discardableResult
    static func send(_ fd: Int32, _ bytes: UnsafeRawPointer, _ count: Int, to addr: sockaddr_in) -> Int {
        var a = addr
        return withUnsafePointer(to: &a) { ap in
            ap.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                sendto(fd, bytes, count, 0, sa, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
    }

    @discardableResult
    static func send(_ fd: Int32, _ bytes: [UInt8], to addr: sockaddr_in) -> Int {
        bytes.withUnsafeBytes { send(fd, $0.baseAddress!, $0.count, to: addr) }
    }

    static func bind(_ fd: Int32, port: UInt16) -> Bool {
        var a = any(port: port)
        return withUnsafePointer(to: &a) { ap in
            ap.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                Darwin.bind(fd, sa, socklen_t(MemoryLayout<sockaddr_in>.size)) == 0
            }
        }
    }

    static func setNonBlocking(_ fd: Int32) {
        let flags = fcntl(fd, F_GETFL, 0)
        _ = fcntl(fd, F_SETFL, flags | O_NONBLOCK)
    }

    static func setInt(_ fd: Int32, _ level: Int32, _ name: Int32, _ value: Int32) {
        var v = value
        setsockopt(fd, level, name, &v, socklen_t(MemoryLayout<Int32>.size))
    }

    /// 域名或 IP → IPv4 地址（阻塞，别在音频线程调）。
    static func resolve(host: String, port: UInt16) -> sockaddr_in? {
        if let a = addr(ip: host, port: port) { return a }
        var hints = addrinfo()
        hints.ai_family = AF_INET
        hints.ai_socktype = SOCK_DGRAM
        var res: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(host, nil, &hints, &res) == 0, let first = res else { return nil }
        defer { freeaddrinfo(res) }
        var out = first.pointee.ai_addr.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee }
        out.sin_port = port.bigEndian
        return out
    }
}
