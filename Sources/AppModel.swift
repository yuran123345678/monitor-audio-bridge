import AppKit
import Combine
import CoreAudio

enum Role: String { case receiver, sender }

enum BufferMode: String, CaseIterable, Identifiable {
    case auto, low, balanced, stable
    var id: String { rawValue }
    var title: String {
        switch self {
        case .auto: return "自动"
        case .low: return "低延迟"
        case .balanced: return "均衡"
        case .stable: return "稳定"
        }
    }
    var fixedMs: Double? {
        switch self {
        case .auto: return nil
        case .low: return 20
        case .balanced: return 40
        case .stable: return 100
        }
    }
}

enum LinkState { case off, waiting, connecting, idle, live, warning, error }

/// 接收端主动拉流的状态。
enum PullState: Equatable {
    case none          // 没在拉
    case requesting    // 已发出请求，等对方确认
    case accepted      // 对方同意了，等声音过来
    case denied        // 对方拒绝
    case busy          // 对方正在发送给另一台
    case disabled      // 对方关了「允许拉取」或没在运行
    case failed        // 没人应答

    var waiting: Bool { self == .requesting || self == .accepted }
}

struct StatusInfo: Equatable {
    var state: LinkState = .off
    var title = ""
    var detail = ""
}

struct Metric: Identifiable, Equatable {
    let id: String
    var label: String
    var value: String
}

/// 整个应用的状态与调度：角色切换、启动/停止、设备与网络变化后的自动恢复、给界面的可观察数据。
/// 只在主线程上用。
final class AppModel: ObservableObject {
    // 角色与总开关
    @Published private(set) var role: Role = .receiver
    @Published private(set) var running = true

    // 接收
    @Published var volume: Double = 0.75 { didSet { player.volume = Float(volume); save("volume", volume) } }
    @Published var muted = false { didSet { player.muted = muted; save("muted", muted) } }
    @Published var bufferMode: BufferMode = .auto { didSet { save("bufferMode", bufferMode.rawValue); applyBuffer(immediate: true) } }
    @Published private(set) var bufferMs: Double = 40
    @Published private(set) var outputDevices: [AudioDeviceInfo] = []
    @Published var outputUID: String = "" { didSet { if outputUID != oldValue { save("outputUID", outputUID); if isReceiving { restartPlayer() } } } }

    // 发送
    @Published private(set) var peers: [Peer] = []
    @Published private(set) var targetPeerID: UInt64 = 0
    @Published private(set) var targetPeerName = ""
    @Published private(set) var useManual = false
    @Published var manualHost = "" { didSet { save("manualHost", manualHost) } }
    @Published var muteLocal = false { didSet { if muteLocal != oldValue { save("muteLocal", muteLocal); if isSending { restartCapture() } } } }

    // 拉流与信任（v3）
    @Published private(set) var pullState: PullState = .none
    @Published private(set) var pullTargetID: UInt64 = 0
    @Published private(set) var pullTargetName = ""
    @Published private(set) var trustedDevices: [TrustStore.Device] = []
    /// 允许别的设备主动拉这台电脑的声音（发送端）。
    @Published var allowPull = false { didSet { save("allowPull", allowPull); updateCaps() } }
    /// 同时接多路并混音（接收端）。
    @Published var mixMode = false {
        didSet {
            save("mixMode", mixMode)
            updateCaps()
            if !loading, isReceiving { restartReceiving() }
        }
    }
    /// 每包多带一份上一包的 PCM（发送端，流量约 ×2）。
    @Published var redundancy = false {
        didSet { save("redundancy", redundancy); sender.redundancy = redundancy }
    }
    /// 拉流失败时给界面的说明。
    @Published private(set) var pullError = ""

    // 状态
    @Published private(set) var status = StatusInfo()
    @Published private(set) var metrics: [Metric] = []
    @Published private(set) var levelL: Double = 0
    @Published private(set) var levelR: Double = 0
    @Published private(set) var localAddress = ""
    @Published var autostart = Autostart.isEnabled { didSet { if autostart != Autostart.isEnabled { Autostart.setEnabled(autostart) } } }

    let deviceName = Proto.deviceName

    private let bridge = RingBridge(seconds: 2.0, sampleRate: 48_000)
    private lazy var player = PlayerEngine(bridge: bridge)
    private lazy var receiver = NetReceiver(bridge: bridge)
    private let sender = NetSender()
    private let tap = SystemAudioTap()
    private let discovery = Discovery()
    private let defaults = UserDefaults.standard

    private var fastTimer: Timer?
    private var slowTimer: Timer?
    private var receiverError: String?
    private var playerError: String?
    private var captureError: String?
    private var targetError: String?
    /// 发送模式但还没选目标：这不是错误，是等用户选。
    private var needsTarget = false
    private var targetLastIP = ""
    private var targetPort: UInt16 = Proto.audioPort
    private var activeOutputUID = ""
    private var lastPlayerRestart = Date.distantPast
    private var lastRetry = Date.distantPast
    private var pendingPlayerRestart = false
    private var pendingCaptureRestart = false
    private var deviceListeners: [(AudioObjectPropertyAddress, AudioObjectPropertyListenerBlock)] = []
    private var loading = true

    // 拉流（接收端）
    private let trust = TrustStore()
    private var pullIP = ""
    private var pullRequestedAt = Date.distantPast
    private var pullResentAt = Date.distantPast
    private var pullConnected = false
    private var pullToken: UInt64 = 0
    // 拉流确认（发送端）
    private var pendingPullDialogs: Set<UInt64> = []
    private var pullDialogOpen = false
    private var challenges = ChallengeBook()
    private var denyList = PullDenyList()
    private var sessionToken: UInt64 = 0
    private var sessionIP = ""
    /// 这台机器是因为被拉流才从接收切到发送的；拉流结束后要恢复原来的角色。
    private var switchedByPull = false
    /// 等对方在界面上点「允许」：这种情况下不按 20 秒判失败。
    private var awaitingConfirm = false
    /// 最近一次收到某台设备拉流请求的时间：对方放弃后我们再点「允许」就不该生效。
    private var lastPullRequestAt: [UInt64: Date] = [:]
    /// 被拉流之前用户自己选的推送目标：拉流结束要还回去，不能把人家的手动选择抹掉。
    private var restoreTargetAfterPull: (useManual: Bool, id: UInt64, name: String, ip: String, port: UInt16)?
    /// 内部切角色（被拉流）时不要把 switchedByPull 清掉。
    private var internalRoleChange = false
    private let lastPullRequestLifetime: TimeInterval = 300

    var isReceiving: Bool { running && role == .receiver }
    var isSending: Bool { running && role == .sender }

    // MARK: - 启动

    init() {
        if let r = defaults.string(forKey: "role"), let v = Role(rawValue: r) { role = v }
        if defaults.object(forKey: "running") != nil { running = defaults.bool(forKey: "running") }
        if defaults.object(forKey: "volume") != nil { volume = defaults.double(forKey: "volume") }
        muted = defaults.bool(forKey: "muted")
        if let m = defaults.string(forKey: "bufferMode"), let v = BufferMode(rawValue: m) { bufferMode = v }
        outputUID = defaults.string(forKey: "outputUID") ?? ""
        manualHost = defaults.string(forKey: "manualHost") ?? ""
        muteLocal = defaults.bool(forKey: "muteLocal")
        useManual = defaults.bool(forKey: "useManual")
        targetPeerID = UInt64(defaults.string(forKey: "targetPeerID") ?? "") ?? 0
        targetPeerName = defaults.string(forKey: "targetPeerName") ?? ""
        targetLastIP = defaults.string(forKey: "targetLastIP") ?? ""
        targetPort = UInt16(defaults.integer(forKey: "targetPort")) == 0 ? Proto.audioPort : UInt16(defaults.integer(forKey: "targetPort"))
        if defaults.object(forKey: "allowPull") != nil { allowPull = defaults.bool(forKey: "allowPull") }
        if defaults.object(forKey: "mixMode") != nil { mixMode = defaults.bool(forKey: "mixMode") }
        if defaults.object(forKey: "redundancy") != nil { redundancy = defaults.bool(forKey: "redundancy") }
        receiver.mixEnabled = mixMode
        sender.redundancy = redundancy
        trustedDevices = trust.devices.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        loading = false
        player.volume = Float(volume)
        player.muted = muted
        bufferTarget = bufferMode.fixedMs ?? 40
        bufferMs = bufferTarget
        player.targetMs = bufferTarget
    }

    func start() {
        refreshDevices()
        localAddress = LocalNet.primaryIPv4() ?? ""

        receiver.onRateChange = { [weak self] rate in
            guard let self else { return }
            self.player.inputRate = self.mixMode ? 48_000 : rate
        }
        receiver.reportInfo = { [weak self] in
            guard let self else { return (0, 0, false) }
            if self.mixMode {
                let fill = self.receiver.mixer?.lastFillFrames ?? 0
                return (fill / 48_000 * 1000, self.player.targetMs, self.player.muted)
            }
            let rate = max(self.player.inputRate, 1)
            return (self.bridge.lastFillFrames / rate * 1000, self.player.targetMs, self.player.muted)
        }
        player.onConfigurationChange = { [weak self] in self?.schedulePlayerRestart() }
        tap.onAudio = { [weak self] p, frames, rate in self?.sender.push(p, frames: frames, rate: rate) }
        discovery.onChange = { [weak self] list in self?.peersChanged(list) }
        discovery.onMessage = { [weak self] type, data, ip in self?.handleControl(type: type, data: data, from: ip) }
        updateCaps()
        discovery.start()

        installDeviceListeners()
        NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didWakeNotification, object: nil, queue: .main
        ) { [weak self] _ in
            // 唤醒后网络和声卡都要缓一下再重启，否则会绑在一个还没就绪的设备上。
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { self?.restartAll() }
        }

        fastTimer = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] _ in self?.fastTick() }
        slowTimer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in self?.slowTick() }
        RunLoop.main.add(fastTimer!, forMode: .common)
        RunLoop.main.add(slowTimer!, forMode: .common)

        applyRunState()
    }

    func shutdown() {
        fastTimer?.invalidate()
        slowTimer?.invalidate()
        stopEngines()
        discovery.stop()
    }

    /// 界面预览用的假数据（--snapshot），不启动任何音频/网络。
    func loadDemo(_ kind: String) {
        localAddress = "192.168.1.23"
        outputDevices = AudioDevices.list().filter { $0.outputs >= 1 }
        switch kind {
        case "receiver-live":
            role = .receiver; running = true
            status = StatusInfo(state: .live, title: "正在接收", detail: "DESKTOP-7Q2 · 192.168.1.36")
            metrics = [Metric(id: "lat", label: "延迟（估算）", value: "46 ms"), Metric(id: "jit", label: "网络抖动", value: "1.3 ms"),
                       Metric(id: "loss", label: "丢包", value: "0%"), Metric(id: "rate", label: "采样率", value: "48.0 kHz")]
            levelL = 0.72; levelR = 0.64; bufferMs = 26
        case "receiver-wait":
            role = .receiver; running = true
            status = StatusInfo(state: .waiting, title: "等待发送端", detail: "在另一台电脑上选择「\(deviceName)」，或手动输入 192.168.1.23")
        case "sender-live":
            role = .sender; running = true
            peers = [Peer(id: 1, name: "DESKTOP-7Q2", ip: "192.168.1.36", port: 47_831, role: Proto.roleReceiver, platform: Proto.platformWindows, state: 1, lastSeen: Date()),
                     Peer(id: 2, name: "客厅 Mac mini", ip: "192.168.1.40", port: 47_831, role: Proto.roleReceiver, platform: Proto.platformMac, state: 0, lastSeen: Date())]
            targetPeerID = 1; targetPeerName = "DESKTOP-7Q2"; useManual = false
            status = StatusInfo(state: .live, title: "正在发送", detail: "DESKTOP-7Q2 · 192.168.1.36:47831")
            metrics = [Metric(id: "buf", label: "对方缓冲", value: "24 ms"), Metric(id: "jit", label: "网络抖动", value: "0.9 ms"),
                       Metric(id: "loss", label: "丢包", value: "0%"), Metric(id: "rate", label: "采样率", value: "48.0 kHz")]
            levelL = 0.58; levelR = 0.93
        case "sender-warn":
            role = .sender; running = true
            targetPeerID = 1; targetPeerName = "DESKTOP-7Q2"; useManual = false
            status = StatusInfo(state: .warning, title: "对方没有回应", detail: "声音仍在发出。请确认「DESKTOP-7Q2」已打开音频桥并处于接收模式，且两台设备在同一网络、防火墙已放行")
        default:
            role = .receiver; running = false
            status = StatusInfo(state: .off, title: "接收已停止", detail: "开启后，其它设备可以把声音送到这台 Mac")
        }
    }

    // MARK: - 用户操作

    func setRole(_ r: Role) {
        guard r != role else { return }
        if !internalRoleChange {
            // 用户自己切角色：不再算是「被拉流切过去的」。
            switchedByPull = false
            restoreTargetAfterPull = nil
        }
        stopEngines()
        role = r
        save("role", r.rawValue)
        applyRunState()
    }

    func setRunning(_ on: Bool) {
        guard on != running else { return }
        running = on
        save("running", on)
        applyRunState()
    }

    func toggleRunning() { setRunning(!running) }

    func selectPeer(_ p: Peer) {
        switchedByPull = false
        restoreTargetAfterPull = nil
        useManual = false
        targetPeerID = p.id
        targetPeerName = p.name
        targetLastIP = p.ip
        targetPort = p.port
        saveTarget()
        if isSending { startSending() }
    }

    func selectManual() {
        switchedByPull = false
        restoreTargetAfterPull = nil
        useManual = true
        saveTarget()
        if isSending { startSending() }
    }

    func copyLocalAddress() {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(localAddress, forType: .string)
    }

    private func saveTarget() {
        save("useManual", useManual)
        save("targetPeerID", String(targetPeerID))
        save("targetPeerName", targetPeerName)
        save("targetLastIP", targetLastIP)
        save("targetPort", Int(targetPort))
    }

    private func save(_ key: String, _ value: Any) {
        guard !loading else { return }
        defaults.set(value, forKey: key)
    }


    // MARK: - 拉流（接收端主动请发送端把声音送过来）

    /// 从发现列表里选一台设备，请它开始发送。对方同意后音频会直接发到本机音频端口。
    /// 换目标之前，先停掉旧会话（给旧目标发再见、放出接收端对它的锁定）。
    private func endPreviousPull() {
        guard pullState != .none, !pullIP.isEmpty else { return }
        discovery.sendControl(Proto.bye(token: pullToken), to: pullIP)
        receiver.releaseSource(ip: pullIP)
        pullToken = 0
        pullConnected = false
        awaitingConfirm = false
        pullState = .none
    }

    func pull(from peer: Peer) {
        guard role == .receiver, running else { return }
        pullError = ""
        if pullIP != peer.ip { endPreviousPull() }
        pullTargetID = peer.id
        pullTargetName = peer.name
        pullIP = peer.ip
        startPull()
    }

    /// 手动输入发送端 IP 拉流（对方不在发现列表里，或跨网段）。返回是否真的发起了。
    @discardableResult
    func pullManual(host raw: String = "") -> Bool {
        guard role == .receiver, running else { return false }
        pullError = ""
        let source = raw.isEmpty ? manualHost : raw
        var host = source.trimmingCharacters(in: .whitespaces)
        if let c = host.lastIndex(of: ":"), host[host.index(after: c)...].allSatisfy({ $0.isNumber }) {
            host = String(host[..<c])
        }
        guard !host.isEmpty else { pullError = "请输入发送端的 IP 地址"; return false }
        guard let addr = Sock.resolve(host: host, port: Proto.discoveryPort) else {
            pullError = "地址「\(host)」无法解析"; return false
        }
        if pullIP != Sock.ip(addr) { endPreviousPull() }
        pullTargetID = 0
        pullTargetName = host
        pullIP = Sock.ip(addr)
        startPull()
        return true
    }

    /// 拉流应答连发三遍：UDP 丢一个也不至于让配对/取消卡住（A-3）。接收方对重复包是幂等的。
    private func sendReply(to ip: String, result: UInt8, token: UInt64 = 0, key: UInt64 = 0, requester: UInt64) {
        let pkt = Proto.pullReply(to: requester, result: result, token: token, key: key)
        for _ in 0..<3 { discovery.sendControl(pkt, to: ip) }
    }

    /// 取消拉流，并请对方停下来（带本次会话令牌，对方才认）。再见也连发三遍。
    func cancelPull() {
        if !pullIP.isEmpty {
            let bye = Proto.bye(token: pullToken)
            for _ in 0..<3 { discovery.sendControl(bye, to: pullIP) }
        }
        // 本地立刻生效：别再让被取消的那台机器占着优先级，也别让它继续把当前来源挤掉。
        if !pullIP.isEmpty {
            receiver.prioritize(ip: "")
            receiver.releaseSource(ip: pullIP)
        }
        pullToken = 0
        pullState = .none
        pullTargetID = 0
        pullTargetName = ""
        pullConnected = false
        pullError = ""
        slowTick()
    }

    func revokeTrusted(_ id: UInt64) {
        trust.revoke(id: id)
        refreshTrusted()
    }

    func clearTrusted() {
        trust.revokeAll()
        refreshTrusted()
    }

    private func startPull() {
        receiver.prioritize(ip: pullIP)
        pullConnected = false
        awaitingConfirm = false
        pullRequestedAt = Date()
        pullResentAt = pullRequestedAt
        pullState = .requesting
        sendPull()
        slowTick()
    }

    private func sendPull() {
        guard role == .receiver, running, !pullIP.isEmpty else { return }
        // v4：请求里不带密钥，先请对方给一个随机数。
        discovery.sendControl(Proto.pull(target: pullTargetID, audioPort: Proto.audioPort), to: pullIP)
    }

    /// 收到质询后用存着的密钥算 MAC 回过去（还没配对过就换不来密钥，只能等对方弹窗）。
    private func answerChallenge(nonce: UInt64, senderID: UInt64, audioPort: UInt16) {
        let stored = trust.key(forSender: senderID)
        guard stored != 0 else {
            // 本机没有它的密钥（首次配对的应答包丢了 / 信任表被删）：明确告诉对方，请它重新弹窗配对。
            let noKey = Proto.pullNoKey(to: senderID, audioPort: Proto.audioPort)
            for _ in 0..<2 { discovery.sendControl(noKey, to: pullIP) }
            awaitingConfirm = true
            pullError = "「\(pullTargetName)」需要重新确认"
            slowTick()
            return
        }
        let mac = PullAuth.mac(key: stored, nonce: nonce, requester: Proto.instanceID, senderID: senderID)
        discovery.sendControl(Proto.pullAuth(to: senderID, nonce: nonce, mac: mac, audioPort: audioPort), to: pullIP)
    }

    /// 每 0.25 s 跑一次：重发请求（丢一两个 UDP 包不至于要重来）、判超时、确认声音来了。
    private func tickPull() {
        guard role == .receiver, pullState.waiting else { return }
        let now = Date()
        // 连上没有：看来源表里有没有这台（保活包也算来源，安静时也在）。
        if receiver.hasSource(ip: pullIP) {
            if !pullConnected {
                pullConnected = true
                awaitingConfirm = false
                pullState = .accepted
                pullError = ""
                slowTick()
            }
            return
        }
        if pullConnected {
            // 连着连着断了（发送端掉线/停发）：重新请求，别一直停在「已同意」。
            pullConnected = false
            pullState = .requesting
            pullRequestedAt = now
            pullResentAt = now
            sendPull()
            slowTick()
            return
        }
        // 对方弹窗等人确认时会回「等待确认」：这时给 2 分钟，不按 20 秒判失败。
        let limit: TimeInterval = awaitingConfirm ? 120 : 20
        if now.timeIntervalSince(pullRequestedAt) > limit {
            pullState = .failed
            pullError = awaitingConfirm ? "「\(pullTargetName)」一直没有确认" : "「\(pullTargetName)」没有回应"
        } else if now.timeIntervalSince(pullResentAt) > 1.2 {
            pullResentAt = now
            sendPull()
        }
    }

    private func refreshTrusted() {
        trustedDevices = trust.devices.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    private func updateCaps() {
        var c: UInt8 = 0
        if allowPull { c |= Proto.capPull }
        if mixMode { c |= Proto.capMix }
        discovery.caps = c
    }

    // MARK: - 控制消息（拉流请求/应答/再见）

    private func handleControl(type: UInt8, data: [UInt8], from ip: String) {
        // 版本不同就是两套字节布局：拉流相关的包一律不认，免得把对方的包当成本版解析。
        if type != Proto.typeBye {
            guard data.count >= 6, Proto.acceptsVersion(data[5]) else { return }
        }
        switch type {
        case Proto.typePull: handlePullRequest(data, from: ip)
        case Proto.typePullAuth: handlePullAuth(data, from: ip)
        case Proto.typePullChallenge: handlePullChallenge(data, from: ip)
        case Proto.typePullReply: handlePullReply(data, from: ip)
        case Proto.typePullNoKey: handlePullNoKey(data, from: ip)
        case Proto.typeBye:
            // 只认「正在发送 + 来源就是当前目标 + 令牌对得上」的再见；短包一律忽略。
            guard PullAuth.byeAccepted(data: data, from: ip, targetIP: senderTargetIP(),
                                       sending: isSending, token: sessionToken) else { return }
            sessionToken = 0
            sessionIP = ""
            sender.stop()
            tap.stop()
            let restore = switchedByPull
            switchedByPull = false
            if let saved = restoreTargetAfterPull {
                // 拉流结束：把用户之前自己选的推送目标还回去，不留在「被拉流的目标」上。
                restoreTargetAfterPull = nil
                useManual = saved.useManual
                targetPeerID = saved.id
                targetPeerName = saved.name
                targetLastIP = saved.ip
                targetPort = saved.port
                saveTarget()
                needsTarget = saved.id == 0 && saved.ip.isEmpty
            } else {
                targetPeerID = 0
                targetPeerName = ""
                saveTarget()
                needsTarget = true
            }
            if restore {
                internalRoleChange = true
                setRole(.receiver)
                internalRoleChange = false
            } else if role == .sender, !needsTarget {
                startSending()
                slowTick()
            } else {
                slowTick()
            }
        default: break
        }
    }

    /// 当前正在往哪台机器的地址发声音（手动 IP 也在 resolveTarget 里写回 targetLastIP）。
    private func senderTargetIP() -> String { targetLastIP }

    private func currentSenderPeerID() -> UInt64 {
        if !useManual { return targetPeerID }
        let rep = sender.stats.report
        return Date().timeIntervalSince(rep.at) < 2.5 ? rep.peerID : 0
    }

    private func sendChallenge(requester: UInt64, ip: String) {
        let nonce = challenges.issue(requester: requester, ip: ip)
        discovery.sendControl(Proto.pullChallenge(to: requester, nonce: nonce, senderID: Proto.instanceID), to: ip)
    }

    private func handlePullRequest(_ data: [UInt8], from ip: String) {
        var target: UInt64 = 0, requester: UInt64 = 0, audioPort: UInt16 = 0
        var name = ""
        data.withUnsafeBufferPointer { p in
            guard let b = p.baseAddress, p.count >= 25 else { return }
            audioPort = Proto.u16(b, 6)
            target = Proto.u64(b, 8)
            requester = Proto.u64(b, 16)
            name = Proto.name(b, 24, count: p.count)
        }
        guard requester != 0, requester != Proto.instanceID else { return }
        guard target == 0 || target == Proto.instanceID else { return }
        if name.isEmpty { name = ip }

        // 我们刚给这台机器发过质询：这是它重试，先把质询再发一次（不能被冷却挡掉）。
        if challenges.hasPending(requester: requester, ip: ip) {
            sendChallenge(requester: requester, ip: ip)
            return
        }
        // 刚被拒绝过的来源，30 秒内直接回拒绝，不再弹窗。
        if denyList.isCooling(ip: ip) {
            sendReply(to: ip, result: Proto.pullDenied, requester: requester)
            return
        }
        lastPullRequestAt[requester] = Date()
        guard running, allowPull else {
            sendReply(to: ip, result: Proto.pullDisabled, requester: requester)
            return
        }
        // 受信任设备：不回密钥，只发一次性质询；对方算对 MAC 才放行。
        if trust.key(forDevice: requester) != nil {
            sendChallenge(requester: requester, ip: ip)
            return
        }
        // 已经在给别的设备发，就不抢当前会话（回执还在 2.5 s 内说明对方在线）。
        if role == .sender, sender.isRunning, senderPeerOnline(), targetLastIP != ip {
            sendReply(to: ip, result: Proto.pullBusy, requester: requester)
            return
        }
        askPullPermission(id: requester, name: name, ip: ip, audioPort: audioPort)
    }

    /// 接收端说「我没有你的密钥」：作废这台设备的旧密钥，回到弹窗重新配对。
    /// 只有「刚给这个设备、这个来源 IP 发过质询」才认，别人冒用设备号清不掉信任关系。
    private func handlePullNoKey(_ data: [UInt8], from ip: String) {
        var requester: UInt64 = 0, audioPort: UInt16 = 0
        var name = ""
        data.withUnsafeBufferPointer { p in
            guard let b = p.baseAddress, p.count >= 25 else { return }
            audioPort = Proto.u16(b, 6)
            requester = Proto.u64(b, 8)
            name = Proto.name(b, 24, count: p.count)
        }
        guard requester != 0, requester != Proto.instanceID else { return }
        guard challenges.hasPending(requester: requester, ip: ip) else { return }
        challenges.cancel(requester: requester)
        trust.revoke(id: requester)
        refreshTrusted()
        if name.isEmpty { name = ip }
        askPullPermission(id: requester, name: name, ip: ip, audioPort: audioPort)
    }

    /// 受信任设备带着 MAC 回来：校验随机数（一次性、绑定来源 IP）和 MAC，才对放行。
    private func handlePullAuth(_ data: [UInt8], from ip: String) {
        var audioPort: UInt16 = 0, nonce: UInt64 = 0, requester: UInt64 = 0, senderID: UInt64 = 0
        var mac: [UInt8] = []
        var name = ""
        data.withUnsafeBufferPointer { p in
            guard let b = p.baseAddress, p.count >= 49 else { return }
            audioPort = Proto.u16(b, 6)
            nonce = Proto.u64(b, 8)
            requester = Proto.u64(b, 16)
            senderID = Proto.u64(b, 24)
            mac = Array(UnsafeBufferPointer(start: b + 32, count: 16))
            name = PullAuth.authName(data)
        }
        guard senderID == Proto.instanceID, requester != 0, requester != Proto.instanceID else { return }
        if name.isEmpty { name = ip }

        // 质询过期/不匹配（可能是重传、或有人冒用设备号）：静默重新发一个质询，不算失败。
        guard challenges.consume(requester: requester, nonce: nonce, ip: ip) else {
            sendChallenge(requester: requester, ip: ip)
            return
        }
        // 质询对上了但 MAC 算错：这才算一次失败（连续多次才冷却）。
        let key = trust.key(forDevice: requester)
        guard PullAuth.verify(key: key, nonce: nonce, requester: requester,
                              senderID: Proto.instanceID, mac: mac) else {
            denyList.noteFailure(ip: ip)
            sendReply(to: ip, result: Proto.pullDenied, requester: requester)
            return
        }
        // 即使是受信任设备，也仍受「允许其它设备拉取」总开关与本程序是否运行约束。
        guard running, allowPull else {
            sendReply(to: ip, result: Proto.pullDisabled, requester: requester)
            return
        }
        // 会话令牌就用这次的一次性随机数：对方拿到后「再见」要带回同一个。
        acceptPull(id: requester, name: name, ip: ip, audioPort: audioPort,
                   key: 0, token: nonce, newPairing: false)
    }

    /// 接收端收到质询：用本机存的发送端密钥算 MAC 回过去。
    private func handlePullChallenge(_ data: [UInt8], from ip: String) {
        guard role == .receiver, pullState == .requesting, ip == pullIP else { return }
        var requester: UInt64 = 0, senderID: UInt64 = 0, nonce: UInt64 = 0
        data.withUnsafeBufferPointer { p in
            guard let b = p.baseAddress, p.count >= 33 else { return }
            requester = Proto.u64(b, 8)
            senderID = Proto.u64(b, 16)
            nonce = Proto.u64(b, 24)
        }
        guard requester == Proto.instanceID, senderID != 0, nonce != 0 else { return }
        if pullTargetID == 0 { pullTargetID = senderID }   // 手动 IP 拉流时从质询里学到设备号
        answerChallenge(nonce: nonce, senderID: senderID, audioPort: Proto.audioPort)
    }

    private func senderPeerOnline() -> Bool {
        let rep = sender.stats.report
        return Date().timeIntervalSince(rep.at) < 2.5 && !rep.name.isEmpty
    }

    private func askPullPermission(id: UInt64, name: String, ip: String, audioPort: UInt16) {
        // 弹窗期间告诉对方「在等人确认」，对方就不会按 20 秒判超时。
        sendReply(to: ip, result: Proto.pullWait, requester: id)
        guard !pendingPullDialogs.contains(id), !pullDialogOpen else { return }
        pendingPullDialogs.insert(id)
        pullDialogOpen = true

        // 只驻留菜单栏时也要能看见：先把应用激活（不改窗口层级，保持现有观感）。
        NSApp.activate(ignoringOtherApps: true)

        let alert = NSAlert()
        alert.messageText = "「\(name)」请求接收这台电脑的声音"
        alert.informativeText = """
        来自 \(ip)。允许后这台电脑会切换到发送模式，把系统声音送到「\(name)」，并把它记为受信任设备（以后不再询问，可在发送界面的「受信任设备」里移除）。
        """
        alert.alertStyle = .informational
        alert.addButton(withTitle: "允许")
        alert.addButton(withTitle: "拒绝")

        let finish: (NSApplication.ModalResponse) -> Void = { [weak self] resp in
            guard let self else { return }
            self.pendingPullDialogs.remove(id)
            self.pullDialogOpen = false
            if resp == .alertFirstButtonReturn {
                // 对方已经放弃/取消（不再重发请求）：我们这边点「允许」也不该切角色、开采集。
                guard let last = self.lastPullRequestAt[id],
                      Date().timeIntervalSince(last) < 8 else {
                    self.sendReply(to: ip, result: Proto.pullDisabled, requester: id)
                    self.slowTick()
                    return
                }
                let newKey = self.trust.grant(id: id, name: name)
                self.refreshTrusted()
                self.denyList.clear(ip: ip)
                self.acceptPull(id: id, name: name, ip: ip, audioPort: audioPort,
                                key: newKey, token: PullAuth.randomNonzero(), newPairing: true)
            } else {
                self.denyList.deny(ip: ip)
                self.sendReply(to: ip, result: Proto.pullDenied, requester: id)
            }
        }
        if let w = NSApp.windows.first(where: { $0.isVisible }) {
            alert.beginSheetModal(for: w, completionHandler: finish)
        } else {
            finish(alert.runModal())
        }
    }

    /// 接受拉流：回应答（首次配对才带密钥和令牌），把发送目标切到请求方并开始发送。
    private func acceptPull(id: UInt64, name: String, ip: String, audioPort: UInt16,
                            key: UInt64, token: UInt64, newPairing: Bool) {
        let port = audioPort == 0 ? Proto.audioPort : audioPort
        sendReply(to: ip, result: newPairing ? Proto.pullOKNew : Proto.pullOK,
                  token: token, key: newPairing ? key : 0, requester: id)

        // 本来就在给同一台机器同一个端口发：只回应答，不重启采集和发送 socket。
        let same = role == .sender && sender.isRunning && targetLastIP == ip && targetPort == port
            && sessionIP == ip && sessionToken != 0
        let adopted = PullSession.adopt(current: PullSession(token: sessionToken, ip: sessionIP, port: targetPort),
                                        ip: ip, port: port, token: token,
                                        alreadySendingSame: same)
        // 令牌必须先跟着换掉，否则接收端拿新令牌发再见，这里按旧令牌比对会拒（A-2）。
        sessionToken = adopted.session.token
        sessionIP = adopted.session.ip
        if !adopted.restart {
            slowTick()
            return
        }
        // 记住用户原来的推送目标，拉流结束后还回去。
        if restoreTargetAfterPull == nil,
           useManual || targetPeerID != 0 || !targetLastIP.isEmpty {
            restoreTargetAfterPull = (useManual, targetPeerID, targetPeerName, targetLastIP, targetPort)
        }
        useManual = false
        targetPeerID = id
        targetPeerName = name
        targetLastIP = ip
        targetPort = port
        saveTarget()

        if role != .sender {
            switchedByPull = true     // 拉流结束后恢复接收，别把用户的机器留在发送模式
            internalRoleChange = true
            setRole(.sender)          // 内部会 applyRunState → 开始发送
            internalRoleChange = false
        } else if !running {
            setRunning(true)
        } else {
            startSending()
        }
        slowTick()
    }

    private func handlePullReply(_ data: [UInt8], from ip: String) {
        // 只认「正在请求的那台机器 + 我们确实还在等」；连上之后重放的旧应答一律忽略。
        guard PullAuth.replyAccepted(data: data, from: ip, pullIP: pullIP,
                                     instanceID: Proto.instanceID,
                                     waiting: pullState == .requesting) else { return }
        var result: UInt8 = 255, flags: UInt8 = 0
        var senderID: UInt64 = 0, token: UInt64 = 0, key: UInt64 = 0
        var name = ""
        data.withUnsafeBufferPointer { p in
            guard let b = p.baseAddress else { return }
            result = b[6]
            flags = b[7]
            senderID = Proto.u64(b, 16)
            token = Proto.u64(b, 24)
            key = Proto.u64(b, 32)
            name = Proto.name(b, 40, count: p.count)
        }
        switch result {
        case Proto.pullOK, Proto.pullOKNew:
            awaitingConfirm = false
            if senderID != 0 { pullTargetID = senderID }
            if !name.isEmpty { pullTargetName = name }
            // 只在「本机还没有这个设备的密钥」时接受下发的密钥：同 IP 能伪造 pullOKNew，
            // 已有密钥时覆盖会被用来做密钥投毒（改密钥必须走 revoke / NO_KEY 重配对）。
            if flags & 1 != 0, senderID != 0, key != 0, trust.key(forSender: senderID) == 0 {
                trust.setKey(key, forSender: senderID)
            }
            pullToken = token
            pullState = .accepted
            pullError = ""
        case Proto.pullWait:
            pullState = .requesting
            awaitingConfirm = true
            pullError = ""
        case Proto.pullDenied:
            pullState = .denied; pullError = "「\(pullTargetName)」拒绝了请求"
        case Proto.pullBusy:
            pullState = .busy; pullError = "「\(pullTargetName)」正在给另一台设备发送"
        case Proto.pullDisabled:
            pullState = .disabled; pullError = "「\(pullTargetName)」没有允许拉取，或已经停止运行"
        default:
            pullState = .failed; pullError = "「\(pullTargetName)」返回了无法识别的应答"
        }
        slowTick()
    }

    // MARK: - 启停

    private func applyRunState(preservePull: Bool = false) {
        stopEngines(cancelPull: !preservePull)
        if running {
            switch role {
            case .receiver: startReceiving()
            case .sender: startSending()
            }
        }
        discovery.role = running ? (role == .receiver ? Proto.roleReceiver : Proto.roleSender) : Proto.roleIdle
        discovery.state = 0
        updateCaps()
        discovery.announceNow()
        // 睡眠唤醒之类的内部重启：拉流没被用户取消，起来后接着拉。
        if preservePull, role == .receiver, running, !pullIP.isEmpty, pullState != .none {
            pullConnected = false
            pullState = .requesting
            awaitingConfirm = false
            pullRequestedAt = Date()
            pullResentAt = pullRequestedAt
            sendPull()
        }
        slowTick()
    }

    private func stopEngines(cancelPull shouldCancel: Bool = true) {
        if shouldCancel, pullState != .none { cancelPull() }
        tap.stop()
        sender.stop()
        receiver.stop()
        player.stop()
        receiverError = nil; playerError = nil; captureError = nil; targetError = nil; needsTarget = false
        levelL = 0; levelR = 0
    }

    private func restartAll() {
        localAddress = LocalNet.primaryIPv4() ?? ""
        discovery.start()
        let keep = pullState != .none
        applyRunState(preservePull: keep)
    }

    /// 接收链内部重启（切换混音开关）：不取消拉流，起来后自动接着拉。
    private func restartReceiving() {
        let keep = pullState != .none
        receiver.stop()
        startReceiving()
        if keep, role == .receiver, running, !pullIP.isEmpty {
            pullConnected = false
            pullState = .requesting
            awaitingConfirm = false
            pullRequestedAt = Date()
            pullResentAt = pullRequestedAt
            sendPull()
        }
    }

    private func startReceiving() {
        receiver.mixEnabled = mixMode
        // 混音时固定 48 kHz；单路先按 48 kHz 起，第一包到达后由 onRateChange 纠正成对方真实采样率。
        player.inputRate = 48_000
        do {
            try receiver.start()
            receiverError = nil
            // 混音模式由播放回调直接向混音器取数；单路仍走主缓冲。
            player.mixer = mixMode ? receiver.mixer : nil
        } catch {
            receiverError = "端口 \(Proto.audioPort) 被占用，可能已有另一个音频桥在运行"
        }
        restartPlayer()
    }

    private func restartPlayer() {
        pendingPlayerRestart = false
        lastPlayerRestart = Date()
        // 钉住的设备不在（比如蓝牙耳机没连）就先走系统默认；设备回来后 devicesChanged 会切回去。
        let dev = outputUID.isEmpty ? nil : AudioDevices.device(uid: outputUID)
        activeOutputUID = dev?.uid ?? ""
        bridge.reset()
        do {
            try player.start(outputDeviceID: dev?.id)
            playerError = nil
        } catch {
            playerError = error.localizedDescription
        }
    }

    private func schedulePlayerRestart() {
        guard isReceiving, !pendingPlayerRestart else { return }
        pendingPlayerRestart = true
        // 插拔耳机时系统会连发好几次配置变化，等它消停了再重启一次。
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [weak self] in
            guard let self, self.pendingPlayerRestart else { return }
            if self.isReceiving { self.restartPlayer() } else { self.pendingPlayerRestart = false }
        }
    }

    private func resolveTarget() -> sockaddr_in? {
        if useManual {
            var host = manualHost.trimmingCharacters(in: .whitespaces)
            var port = Proto.audioPort
            if let c = host.lastIndex(of: ":"), let p = UInt16(host[host.index(after: c)...]) {
                port = p
                host = String(host[..<c])
            }
            guard !host.isEmpty else { targetError = "请输入接收端的 IP 地址"; return nil }
            guard let a = Sock.resolve(host: host, port: port) else { targetError = "地址「\(host)」无法解析"; return nil }
            return a
        }
        if let p = peers.first(where: { $0.id == targetPeerID }) {
            targetLastIP = p.ip; targetPort = p.port
            return Sock.addr(ip: p.ip, port: p.port)
        }
        if targetPeerID != 0, !targetLastIP.isEmpty {
            return Sock.addr(ip: targetLastIP, port: targetPort)
        }
        needsTarget = true
        return nil
    }

    private func startSending() {
        sender.redundancy = redundancy
        tap.stop()
        sender.stop()
        targetError = nil
        captureError = nil
        needsTarget = false
        guard let addr = resolveTarget() else { return }
        do {
            try sender.start(to: addr)
        } catch {
            targetError = "无法创建网络连接：\(error.localizedDescription)"
            return
        }
        restartCapture()
    }

    private func restartCapture() {
        pendingCaptureRestart = false
        guard isSending, sender.isRunning else { return }
        do {
            try tap.start(muteLocal: muteLocal)
            captureError = nil
        } catch {
            captureError = error.localizedDescription
        }
    }

    private func scheduleCaptureRestart() {
        guard isSending, !pendingCaptureRestart else { return }
        pendingCaptureRestart = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { [weak self] in
            guard let self, self.pendingCaptureRestart else { return }
            self.restartCapture()
        }
    }

    // MARK: - 设备 / 发现

    private func refreshDevices() {
        outputDevices = AudioDevices.list().filter { $0.outputs >= 1 && !$0.name.hasPrefix("音频桥") }
    }

    private func installDeviceListeners() {
        for sel in [kAudioHardwarePropertyDevices, kAudioHardwarePropertyDefaultOutputDevice] {
            var addr = CA.addr(sel)
            let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
                DispatchQueue.main.async { self?.devicesChanged(defaultChanged: sel == kAudioHardwarePropertyDefaultOutputDevice) }
            }
            AudioObjectAddPropertyListenerBlock(CA.system, &addr, DispatchQueue.main, block)
            deviceListeners.append((addr, block))
        }
    }

    private func devicesChanged(defaultChanged: Bool) {
        refreshDevices()
        if isReceiving {
            let want = outputUID.isEmpty ? "" : (AudioDevices.device(uid: outputUID)?.uid ?? "")
            if want != activeOutputUID { schedulePlayerRestart() }
        }
        if isSending, defaultChanged {
            let cur = AudioDevices.defaultID(kAudioHardwarePropertyDefaultOutputDevice)
                .flatMap { CA.string($0, kAudioDevicePropertyDeviceUID) } ?? ""
            if cur != tap.outputUID { scheduleCaptureRestart() }
        }
    }

    private func peersChanged(_ list: [Peer]) {
        peers = list
        guard !useManual, let p = list.first(where: { $0.id == targetPeerID }) else { return }
        if p.name != targetPeerName { targetPeerName = p.name; saveTarget() }
        let moved = p.ip != targetLastIP || p.port != targetPort
        if moved {
            // 信标没有凭据，谁都能冒用设备号：拉流会话期间绝不跟；普通推送只在旧地址没回执时才跟。
            let replyAge = Date().timeIntervalSince(sender.stats.report.at)
            guard BeaconPolicy.shouldFollow(currentIP: targetLastIP, beaconIP: p.ip,
                                            isPullSession: sessionToken != 0,
                                            lastReplyAge: replyAge) else {
                if isSending, !sender.isRunning { startSending() }
                return
            }
            // 对端确实换了地址：跟过去，采集不用停。
            targetLastIP = p.ip; targetPort = p.port
            saveTarget()
            if isSending {
                if sender.isRunning, let a = Sock.addr(ip: p.ip, port: p.port) { sender.retarget(a) }
                else { startSending() }
            }
        } else if isSending, !sender.isRunning {
            startSending()
        }
    }

    // MARK: - 定时刷新

    private static func meter(_ peak: Float) -> Double {
        guard peak > 0.000_01 else { return 0 }
        let db = 20 * log10(Double(peak))
        return max(0, min(1, (db + 60) / 60))
    }

    private func fastTick() {
        var p: (Float, Float) = (0, 0)
        if isReceiving { p = player.takePeaks() } else if isSending { p = sender.takePeaks() }
        let l = max(Self.meter(p.0), levelL - 0.035)
        let r = max(Self.meter(p.1), levelR - 0.035)
        if abs(l - levelL) > 0.002 { levelL = l }
        if abs(r - levelR) > 0.002 { levelR = r }
    }

    /// 实际生效的目标缓冲（带小数）；bufferMs 只在整数毫秒变化时才发布，免得界面白刷。
    private var bufferTarget: Double = 40

    private func applyBuffer(immediate: Bool = false) {
        if let fixed = bufferMode.fixedMs {
            bufferTarget = fixed
        } else if receiver.isStreaming {
            // 自动：盯最近 10 s 的最大到达间隔。要涨立刻涨（马上就要卡了），要降慢慢降。
            let want = max(20, min(250, receiver.stats.gapMaxMs * 1.25 + 12))
            if want > bufferTarget { bufferTarget = want } else { bufferTarget += (want - bufferTarget) * 0.04 }
        }
        player.targetMs = bufferTarget
        if mixMode { receiver.setMixTarget(ms: max(20, bufferTarget)) }
        if bufferTarget.rounded() != bufferMs { bufferMs = bufferTarget.rounded() }
    }

    /// 清掉过期的「最近拉流请求」记录，别让表无限长（每台机器每次请求都会写一条）。
    private func prunePullTables() {
        let now = Date()
        if lastPullRequestAt.count > 0 {
            lastPullRequestAt = lastPullRequestAt.filter { now.timeIntervalSince($0.value) < lastPullRequestLifetime }
            if lastPullRequestAt.count > 64 {
                let kept = lastPullRequestAt.sorted { $0.value > $1.value }.prefix(64)
                lastPullRequestAt = Dictionary(uniqueKeysWithValues: kept.map { ($0.key, $0.value) })
            }
        }
    }

    private func slowTick() {
        prunePullTables()
        watchdog()
        let ip = LocalNet.primaryIPv4() ?? ""
        if ip != localAddress { localAddress = ip }

        var s = StatusInfo()
        var m: [Metric] = []

        if !running {
            s.state = .off
            s.title = role == .receiver ? "接收已停止" : "发送已停止"
            s.detail = role == .receiver ? "开启后，其它设备可以把声音送到这台 Mac" : "开启后，这台 Mac 的系统声音会送到所选设备"
        } else if role == .receiver {
            applyBuffer()
            tickPull()
            let st = receiver.stats
            let linked = receiver.isLinked
            let who = st.senderName.isEmpty ? st.sender : st.senderName
            if let e = receiverError ?? playerError {
                s.state = .error; s.title = "无法接收"; s.detail = e
            } else if linked && receiver.isStreaming {
                s.state = .live; s.title = "正在接收"
                s.detail = st.sourceCount > 1 ? "\(who) 等 \(st.sourceCount) 路混音" : "\(who) · \(st.sender)"
            } else if linked {
                s.state = .idle; s.title = "已连接"
                s.detail = "\(who) 当前没有声音在播放"
            } else if pullState == .requesting || pullState == .accepted {
                s.state = .connecting; s.title = "正在请求「\(pullTargetName)」"
                s.detail = "已向 \(pullIP) 发出请求，对方同意后会自动开始发送"
            } else if pullState == .denied || pullState == .busy || pullState == .disabled || pullState == .failed {
                s.state = .warning
                s.title = pullState == .busy ? "对方正忙" : (pullState == .denied ? "对方拒绝了请求" : "拉取没有成功")
                s.detail = pullError.isEmpty ? "可以选择另一台设备" : pullError
            } else {
                s.state = .waiting; s.title = "等待发送端"
                s.detail = localAddress.isEmpty ? "未连接到局域网" : "点「选择发送端」主动拉取另一台电脑，或在另一台电脑上选择「\(deviceName)」"
            }
            if linked {
                let rate = max(player.inputRate, 1)
                let fill = mixMode
                    ? (receiver.mixer?.lastFillFrames ?? 0) / 48_000 * 1000
                    : bridge.lastFillFrames / rate * 1000
                let latency = (receiver.isStreaming ? fill : player.targetMs) + player.deviceLatencyMs + 10
                let total = st.packets + st.lost
                let lossPct = total > 0 ? Double(st.lost) / Double(total) * 100 : 0
                m = [
                    Metric(id: "lat", label: "延迟（估算）", value: String(format: "%.0f ms", latency)),
                    Metric(id: "jit", label: "网络抖动", value: String(format: "%.1f ms", st.jitterMs)),
                    Metric(id: "loss", label: "丢包", value: lossPct < 0.005 ? "0%" : String(format: "%.2f%%", lossPct)),
                    Metric(id: "rate", label: "采样率", value: String(format: "%.1f kHz", st.rate / 1000)),
                ]
            }
            discovery.state = linked ? 1 : 0
        } else {
            let st = sender.stats
            let now = Date()
            let rep = st.report
            let fresh = now.timeIntervalSince(rep.at) < 2.0
            let hasAudio = now.timeIntervalSince(st.lastAudioAt) < 0.5
            let who = useManual ? manualHost : (rep.name.isEmpty ? targetPeerName : rep.name)
            if needsTarget && !sender.isRunning {
                s.state = .waiting; s.title = "选择接收端"
                s.detail = "在下方列表里选择要把声音送到哪台设备，或手动输入对方的 IP 地址"
            } else if let e = targetError ?? captureError {
                s.state = .error; s.title = "无法发送"; s.detail = e
            } else if fresh && rep.busy {
                s.state = .warning; s.title = "对方正忙"
                s.detail = "\(who) 正在接收另一台设备的声音"
            } else if fresh {
                if hasAudio {
                    s.state = .live; s.title = "正在发送"
                    s.detail = "\(who) · \(sender.targetDescription)" + (rep.muted ? " · 对方已静音" : "")
                } else {
                    s.state = .idle; s.title = "已连接"
                    // 没授权「系统录音」时采集到的也是静音，两种情况在这里长得一样，所以把授权路径一并写上。
                    s.detail = "这台 Mac 当前没有声音在播放。若确实在播放，请到 系统设置 › 隐私与安全性 › 屏幕与系统录音，允许「音频桥」录制系统音频"
                }
                let total = Double(rep.packets) + Double(rep.lost)
                let lossPct = total > 0 ? Double(rep.lost) / total * 100 : 0
                m = [
                    Metric(id: "buf", label: "对方缓冲", value: String(format: "%.0f ms", rep.bufferMs)),
                    Metric(id: "jit", label: "网络抖动", value: String(format: "%.1f ms", rep.jitterMs)),
                    Metric(id: "loss", label: "丢包", value: lossPct < 0.005 ? "0%" : String(format: "%.2f%%", lossPct)),
                    Metric(id: "rate", label: "采样率", value: st.rate > 0 ? String(format: "%.1f kHz", st.rate / 1000) : "—"),
                ]
            } else if now.timeIntervalSince(st.startedAt) < 2.5 {
                s.state = .connecting; s.title = "正在连接"
                s.detail = "\(who) · \(sender.targetDescription)"
            } else {
                s.state = .warning; s.title = "对方没有回应"
                s.detail = "声音仍在发出。请确认「\(who)」已打开音频桥并处于接收模式，且两台设备在同一网络、防火墙已放行"
            }
            discovery.state = (fresh && !rep.busy) ? 1 : 0
        }

        if s != status { status = s }
        if m != metrics { metrics = m }
    }

    /// 兜底：该转的东西没在转，就定期再拉一把。
    private func watchdog() {
        let now = Date()
        guard running, now.timeIntervalSince(lastRetry) > 3 else { return }
        if role == .receiver {
            if !receiver.isRunning {
                lastRetry = now
                do {
                    try receiver.start()
                    receiverError = nil
                    // 新的接收端会建一个新的混音器，旧的已经停了：必须同步给播放器，否则混音没声。
                    player.mixer = mixMode ? receiver.mixer : nil
                } catch {}
            }
            if !pendingPlayerRestart, !player.isEngineAlive, now.timeIntervalSince(lastPlayerRestart) > 2 {
                lastRetry = now
                restartPlayer()
            }
        } else {
            if !sender.isRunning {
                // 目标还没解析出来（例如手动地址是个暂时解析不了的主机名）
                if useManual ? !manualHost.isEmpty : targetPeerID != 0 { lastRetry = now; startSending() }
            } else if !tap.isRunning, !pendingCaptureRestart {
                lastRetry = now
                restartCapture()
            }
        }
    }
}
