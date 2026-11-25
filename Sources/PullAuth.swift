import Foundation
import CryptoKit
import Darwin

/// 拉流鉴权的纯逻辑：密钥不再明文上线，配对时下发一次，之后每次拉流走「质询 → HMAC 应答」。
///
/// 抽成独立文件是为了能被 --nettest 直接测：这些判断如果散在 AppModel 里（要主线程、要弹窗），
/// 安全相关的分支就没法自动回归。AppModel 只负责发包、弹窗和启停。
enum PullAuth {
    /// 鉴权包里的设备名（nameLen 在 48）。单独抽出来，测试能走真实解析路径。
    static func authName(_ data: [UInt8]) -> String {
        guard data.count >= 49 else { return "" }
        return data.withUnsafeBufferPointer { Proto.name($0.baseAddress!, 48, count: $0.count) }
    }


    /// HMAC 的输入：nonce‖requester‖senderID，各 8 字节小端（与 Proto.putU64 一致），共 24 字节。
    static func authInput(nonce: UInt64, requester: UInt64, senderID: UInt64) -> [UInt8] {
        var d = [UInt8](repeating: 0, count: 24)
        Proto.putU64(&d, 0, nonce)
        Proto.putU64(&d, 8, requester)
        Proto.putU64(&d, 16, senderID)
        return d
    }

    /// HMAC-SHA256(key, nonce‖requester‖senderID) 的前 16 字节。密钥按 8 字节小端参与运算。
    static func mac(key: UInt64, nonce: UInt64, requester: UInt64, senderID: UInt64) -> [UInt8] {
        var k = [UInt8](repeating: 0, count: 8)
        Proto.putU64(&k, 0, key)
        let sym = SymmetricKey(data: Data(k))
        let code = HMAC<SHA256>.authenticationCode(for: Data(authInput(nonce: nonce, requester: requester, senderID: senderID)), using: sym)
        return Array(code.prefix(16))
    }

    static func verify(key: UInt64?, nonce: UInt64, requester: UInt64, senderID: UInt64, mac: [UInt8]) -> Bool {
        guard let key, key != 0, mac.count >= 16 else { return false }
        let want = self.mac(key: key, nonce: nonce, requester: requester, senderID: senderID)
        var diff: UInt8 = 0
        for i in 0..<16 { diff |= want[i] ^ mac[i] }
        return diff == 0
    }

    /// 再见是否成立：长度够、类型对、正在发送、来源就是当前目标、令牌是本次会话的。
    static func byeAccepted(data: [UInt8], from: String, targetIP: String,
                            sending: Bool, token: UInt64) -> Bool {
        guard data.count >= 16, Proto.hasMagic(data, data.count), data[4] == Proto.typeBye else { return false }
        guard sending, token != 0, !targetIP.isEmpty, from == targetIP else { return false }
        return Proto.u64(data, 8) == token
    }

    /// 应答是否该采信：必须来自我们正在请求的那台机器，而且我们确实在等。
    static func replyAccepted(data: [UInt8], from: String, pullIP: String,
                              instanceID: UInt64, waiting: Bool) -> Bool {
        guard data.count >= 41, Proto.hasMagic(data, data.count), data[4] == Proto.typePullReply else { return false }
        guard waiting, !pullIP.isEmpty, from == pullIP else { return false }
        return Proto.u64(data, 8) == instanceID
    }

    /// 非零随机数（质询/会话令牌）。
    static func randomNonzero() -> UInt64 {
        var v: UInt64 = 0
        repeat {
            var bytes = [UInt8](repeating: 0, count: 8)
            _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
            v = 0
            for i in 0..<8 { v = (v << 8) | UInt64(bytes[i]) }
        } while v == 0
        return v
    }
}

/// 发出过、但还没被应答的质询。单次使用、15 秒过期、绑定来源 IP，防止应答被重放或跨机挪用。
struct ChallengeBook {
    struct Entry {
        let nonce: UInt64
        let ip: String
        let expires: Date
    }

    /// 按「设备号 + 来源 IP」存：别人冒用同一个设备号从别的 IP 发请求，不会把正主的质询顶掉。
    private struct Key: Hashable {
        let requester: UInt64
        let ip: String
    }

    static let lifetime: TimeInterval = 15
    static let maxEntries = 64

    private var pending: [Key: Entry] = [:]

    var count: Int { pending.count }

    mutating func issue(requester: UInt64, ip: String, now: Date = Date()) -> UInt64 {
        prune(now: now)
        // 已经在等这个来源的鉴权：重复「质询」必须给同一个 nonce，否则对方在途的 pullAuth 会被判失效。
        let key = Key(requester: requester, ip: ip)
        if let e = pending[key], e.expires > now { return e.nonce }
        if pending.count >= Self.maxEntries,
           let oldest = pending.min(by: { $0.value.expires < $1.value.expires })?.key {
            pending.removeValue(forKey: oldest)
        }
        let nonce = PullAuth.randomNonzero()
        pending[Key(requester: requester, ip: ip)] =
            Entry(nonce: nonce, ip: ip, expires: now.addingTimeInterval(Self.lifetime))
        return nonce
    }

    /// 取出并作废；nonce / IP 不匹配或已过期都算没取到（调用方应静默重新质询，不计失败）。
    mutating func consume(requester: UInt64, nonce: UInt64, ip: String, now: Date = Date()) -> Bool {
        prune(now: now)
        let key = Key(requester: requester, ip: ip)
        guard let e = pending[key] else { return false }
        guard e.nonce == nonce, e.expires > now else { return false }
        pending.removeValue(forKey: key)
        return true
    }

    /// 只查不消费：用于「对方说没有密钥」时确认这个来源确实是我们刚质询过的那台机器。
    func hasPending(requester: UInt64, ip: String, now: Date = Date()) -> Bool {
        guard let e = pending[Key(requester: requester, ip: ip)] else { return false }
        return e.expires > now
    }

    /// 作废这台设备的全部质询（所有来源 IP）。
    mutating func cancel(requester: UInt64) {
        var out: [Key: Entry] = [:]
        for (k, v) in pending where k.requester != requester { out[k] = v }
        pending = out
    }

    mutating func cancel(requester: UInt64, ip: String) {
        pending.removeValue(forKey: Key(requester: requester, ip: ip))
    }

    private mutating func prune(now: Date) {
        var kept: [Key: Entry] = [:]
        for (k, v) in pending where v.expires > now { kept[k] = v }
        pending = kept
    }
}

/// 被拒绝的来源 30 秒内直接回「拒绝」，不再弹窗骚扰用户。
struct PullDenyList {
    static let cooldown: TimeInterval = 30
    /// 连续算错几次凭据就冷却：防止有人拿一个 IP 狂试。
    static let maxFailures = 5
    static let failureWindow: TimeInterval = 30
    /// 表的上限：长期运行也不该无限长。
    static let maxEntries = 128

    private var until: [String: Date] = [:]
    private var failures: [String: (count: Int, first: Date)] = [:]

    /// 记一次凭据失败；返回 true 表示该 IP 刚进入冷却。
    @discardableResult
    mutating func noteFailure(ip: String, now: Date = Date()) -> Bool {
        var rec = failures[ip] ?? (0, now)
        if now.timeIntervalSince(rec.first) > Self.failureWindow { rec = (0, now) }
        rec.count += 1
        failures[ip] = rec
        if rec.count >= Self.maxFailures {
            failures.removeValue(forKey: ip)
            deny(ip: ip, now: now)
            return true
        }
        return false
    }

    var count: Int { until.count + failures.count }

    mutating func deny(ip: String, now: Date = Date()) {
        until[ip] = now.addingTimeInterval(Self.cooldown)
        prune(now: now)
    }

    /// 过期条目清掉；仍然超过上限就丢掉最早的一批。
    private mutating func prune(now: Date) {
        var live: [String: Date] = [:]
        for (ip, t) in until where t > now { live[ip] = t }
        until = live
        var liveF: [String: (count: Int, first: Date)] = [:]
        for (ip, r) in failures where now.timeIntervalSince(r.first) <= Self.failureWindow { liveF[ip] = r }
        failures = liveF
        if until.count > Self.maxEntries {
            let kept = until.sorted { $0.value > $1.value }.prefix(Self.maxEntries)
            until = Dictionary(uniqueKeysWithValues: kept.map { ($0.key, $0.value) })
        }
        if failures.count > Self.maxEntries {
            let kept = failures.sorted { $0.value.first > $1.value.first }.prefix(Self.maxEntries)
            failures = Dictionary(uniqueKeysWithValues: kept.map { ($0.key, $0.value) })
        }
    }

    func isCooling(ip: String, now: Date = Date()) -> Bool {
        guard let t = until[ip] else { return false }
        return t > now
    }

    mutating func clear(ip: String) {
        until.removeValue(forKey: ip)
        failures.removeValue(forKey: ip)
    }
}

/// 一次拉流会话的身份：令牌 + 目标地址。
/// 每次重新拉流（睡眠唤醒、切混音、链路闪断）都会拿到新令牌，发送端必须同步更新，
/// 否则接收端随后发来的「再见」会因为令牌对不上被拒，用户点取消也停不掉。
struct PullSession {
    var token: UInt64 = 0
    var ip: String = ""
    var port: UInt16 = 0

    var active: Bool { token != 0 }
}

extension PullSession {
    /// 收到一次（可能是重新发起的）拉流请求后，会话该怎么更新、要不要重启发送。
    /// 提前返回（正在给同一台机器同一个端口发）也必须把令牌换掉，这是 A-2 的修法。
    static func adopt(current: PullSession, ip: String, port: UInt16, token: UInt64,
                     alreadySendingSame: Bool) -> (session: PullSession, restart: Bool) {
        let next = PullSession(token: token, ip: ip, port: port)
        return (next, !alreadySendingSame)
    }
}

/// 发现信标没有任何凭据（设备号是公开的），能不能跟着它换地址要非常保守。
enum BeaconPolicy {
    /// - 有会话令牌的拉流会话期间：绝不跟（对方真换 IP 会重新拉流，走质询）。
    /// - 同 IP（只是端口变了）：跟。
    /// - 不同 IP：只有旧地址 2.5 秒没有回执才跟。
    static func shouldFollow(currentIP: String, beaconIP: String,
                             isPullSession: Bool, lastReplyAge: TimeInterval) -> Bool {
        if isPullSession { return false }
        if beaconIP == currentIP { return true }
        return lastReplyAge >= 2.5
    }
}

/// 单路模式下「要不要把当前来源让给新来的拉流方」的判断（纯函数，便于测试）。
enum AdmitPolicy {
    /// - currentIP: 现在占着单路锁的机器；incomingIP: 新来的；priorityIP: 我们主动拉的那台
    static func shouldYield(currentIP: String, incomingIP: String, priorityIP: String,
                            mixEnabled: Bool) -> Bool {
        guard !mixEnabled, !priorityIP.isEmpty else { return false }
        guard incomingIP == priorityIP else { return false }
        return currentIP != incomingIP
    }
}
