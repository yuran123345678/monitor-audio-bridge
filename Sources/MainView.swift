import SwiftUI
import AppKit

// MARK: - 设计变量（与 windows/Ui.cs 里的 Theme 保持同一套数值）

enum Theme {
    static func dyn(_ light: UInt32, _ dark: UInt32) -> Color {
        Color(nsColor: NSColor(name: nil) { ap in
            let hex = ap.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? dark : light
            return NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
                           blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
        })
    }

    static let window = dyn(0xF4F4F6, 0x1B1B1D)
    static let card = dyn(0xFFFFFF, 0x262628)
    static let inset = dyn(0xEEEEF1, 0x1F1F21)
    static let hairline = dyn(0xE2E2E6, 0x38383B)
    static let text = dyn(0x1A1A1C, 0xF2F2F3)
    static let text2 = dyn(0x6C6C72, 0x9C9CA3)
    static let text3 = dyn(0x9A9AA1, 0x6E6E75)
    static let live = dyn(0x1F9D6B, 0x3CCB8A)
    static let warn = dyn(0xC98A12, 0xE5A93A)
    static let error = dyn(0xD13B40, 0xF0666A)
    static let track = dyn(0xE4E4E8, 0x3A3A3D)

    static func color(_ s: LinkState) -> Color {
        switch s {
        case .live: return live
        case .idle, .connecting: return live.opacity(0.75)
        case .warning: return warn
        case .error: return error
        case .waiting: return text3
        case .off: return text3
        }
    }
}

struct Card<Content: View>: View {
    var padding: CGFloat = 16
    @ViewBuilder var content: Content
    var body: some View {
        content
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Theme.card))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(Theme.hairline, lineWidth: 1))
    }
}

struct SectionLabel: View {
    let text: String
    var body: some View {
        Text(text).font(.system(size: 11, weight: .semibold)).foregroundStyle(Theme.text2)
            .textCase(.uppercase).kerning(0.6)
    }
}

// MARK: - 角色切换

struct RoleSwitch: View {
    let role: Role
    let onChange: (Role) -> Void

    var body: some View {
        HStack(spacing: 2) {
            seg(.receiver, "接收", "speaker.wave.2.fill")
            seg(.sender, "发送", "dot.radiowaves.left.and.right")
        }
        .padding(2)
        .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(Theme.track))
    }

    private func seg(_ r: Role, _ title: String, _ icon: String) -> some View {
        let on = role == r
        return Button { onChange(r) } label: {
            HStack(spacing: 5) {
                Image(systemName: icon).font(.system(size: 11, weight: .medium))
                Text(title).font(.system(size: 12.5, weight: on ? .semibold : .regular))
            }
            .foregroundStyle(on ? Theme.text : Theme.text2)
            .frame(width: 74, height: 26)
            .background(RoundedRectangle(cornerRadius: 7, style: .continuous)
                .fill(on ? Theme.card : Color.clear)
                .shadow(color: .black.opacity(on ? 0.12 : 0), radius: 1.5, y: 0.5))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(title + "模式")
    }
}

// MARK: - 电平表

struct LevelBar: View {
    let label: String
    let level: Double
    let active: Bool

    var body: some View {
        HStack(spacing: 8) {
            Text(label).font(.system(size: 10, weight: .medium, design: .monospaced)).foregroundStyle(Theme.text3)
                .frame(width: 10)
            GeometryReader { geo in
                let w = geo.size.width
                ZStack(alignment: .leading) {
                    Capsule().fill(Theme.track)
                    // −6 dB 以上换成琥珀色、贴顶变红：一眼能看出是不是录爆了
                    Capsule().fill(level > 0.985 ? Theme.error : (level > 0.9 ? Theme.warn : Theme.live))
                        .frame(width: max(0, w * level))
                        .opacity(active ? 1 : 0.35)
                }
            }
            .frame(height: 5)
        }
        .accessibilityElement()
        .accessibilityLabel(label == "L" ? "左声道电平" : "右声道电平")
        .accessibilityValue("\(Int(level * 100))%")
    }
}

// MARK: - 状态卡

struct StatusCard: View {
    @ObservedObject var model: AppModel
    @State private var pulse = false

    var body: some View {
        Card(padding: 18) {
            VStack(alignment: .leading, spacing: 14) {
                HStack(alignment: .top, spacing: 12) {
                    ZStack {
                        Circle().fill(Theme.color(model.status.state).opacity(0.16)).frame(width: 34, height: 34)
                        Image(systemName: icon).font(.system(size: 15, weight: .semibold))
                            .foregroundStyle(Theme.color(model.status.state))
                    }
                    VStack(alignment: .leading, spacing: 3) {
                        Text(model.status.title).font(.system(size: 18, weight: .semibold)).foregroundStyle(Theme.text)
                        Text(model.status.detail).font(.system(size: 12)).foregroundStyle(Theme.text2)
                            .lineLimit(3).fixedSize(horizontal: false, vertical: true)
                            .textSelection(.enabled)
                    }
                    Spacer(minLength: 0)
                }

                VStack(spacing: 6) {
                    LevelBar(label: "L", level: model.levelL, active: model.running)
                    LevelBar(label: "R", level: model.levelR, active: model.running)
                }

                if !model.metrics.isEmpty {
                    HStack(spacing: 0) {
                        ForEach(model.metrics) { m in
                            VStack(alignment: .leading, spacing: 2) {
                                Text(m.value).font(.system(size: 14, weight: .medium).monospacedDigit())
                                    .foregroundStyle(Theme.text)
                                Text(m.label).font(.system(size: 10.5)).foregroundStyle(Theme.text3)
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                        }
                    }
                    .padding(.top, 2)
                }
            }
        }
    }

    private var icon: String {
        switch model.status.state {
        case .live: return model.role == .receiver ? "speaker.wave.2.fill" : "dot.radiowaves.left.and.right"
        case .idle: return "checkmark"
        case .connecting: return "ellipsis"
        case .waiting: return "antenna.radiowaves.left.and.right"
        case .warning: return "exclamationmark"
        case .error: return "xmark"
        case .off: return "pause.fill"
        }
    }
}

// MARK: - 接收设置

struct ReceiverPanel: View {
    @ObservedObject var model: AppModel
    @State private var copied = false
    @State private var showPeers = false
    @State private var showTrust = false

    var body: some View {
        Card {
            VStack(alignment: .leading, spacing: 14) {
                SectionLabel(text: "接收来源")

                HStack(spacing: 8) {
                    VStack(alignment: .leading, spacing: 1) {
                        Text(sourceTitle).font(.system(size: 12.5, weight: .medium)).foregroundStyle(Theme.text)
                            .lineLimit(1)
                        Text(sourceSub).font(.system(size: 10.5)).foregroundStyle(Theme.text3).lineLimit(2)
                    }
                    Spacer(minLength: 6)
                    if model.pullState != .none {
                        Button("取消") { model.cancelPull() }
                            .buttonStyle(.plain).font(.system(size: 11.5)).foregroundStyle(Theme.text2)
                    }
                    Button("选择发送端…") { showPeers = true }
                        .controlSize(.small).disabled(!model.running)
                }

                Divider().overlay(Theme.hairline)

                SectionLabel(text: "播放")

                HStack(spacing: 10) {
                    Button { model.muted.toggle() } label: {
                        Image(systemName: model.muted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                            .font(.system(size: 13)).frame(width: 22, height: 22)
                            .foregroundStyle(model.muted ? Theme.warn : Theme.text2)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .help(model.muted ? "取消静音" : "静音")
                    .accessibilityLabel(model.muted ? "取消静音" : "静音")
                    Slider(value: $model.volume, in: 0...1).controlSize(.small)
                        .disabled(model.muted)
                        .accessibilityLabel("音量")
                    Text("\(Int((model.volume * 100).rounded()))")
                        .font(.system(size: 12).monospacedDigit()).foregroundStyle(Theme.text2)
                        .frame(width: 26, alignment: .trailing)
                }

                row("输出设备") {
                    Picker("", selection: $model.outputUID) {
                        Text("跟随系统").tag("")
                        Divider()
                        ForEach(model.outputDevices, id: \.uid) { d in
                            Text("\(d.name) · \(d.transportLabel)").tag(d.uid)
                        }
                        if !model.outputUID.isEmpty, !model.outputDevices.contains(where: { $0.uid == model.outputUID }) {
                            Text("已选设备未连接（暂用系统输出）").tag(model.outputUID)
                        }
                    }
                    .labelsHidden().controlSize(.small).frame(maxWidth: 230)
                }

                row("缓冲") {
                    Picker("", selection: $model.bufferMode) {
                        ForEach(BufferMode.allCases) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.segmented).labelsHidden().controlSize(.small).frame(width: 230)
                }
                Text(bufferHint).font(.system(size: 11)).foregroundStyle(Theme.text3)
                    .frame(maxWidth: .infinity, alignment: .trailing).padding(.top, -8)

                Toggle(isOn: $model.mixMode) {
                    VStack(alignment: .leading, spacing: 1) {
                        Text("同时接收多路").font(.system(size: 12.5)).foregroundStyle(Theme.text)
                        Text("最多 4 台设备一起混音").font(.system(size: 11)).foregroundStyle(Theme.text3)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .toggleStyle(.switch).controlSize(.small)

                // 平时停在接收模式的机器也要能开「允许拉取」、管理受信任设备。
                HStack(spacing: 8) {
                    Toggle(isOn: $model.allowPull) {
                        Text("允许其它设备拉取").font(.system(size: 12.5)).foregroundStyle(Theme.text)
                    }
                    .toggleStyle(.switch).controlSize(.mini)
                    .fixedSize()

                    Spacer(minLength: 6)

                    Button {
                        showTrust = true
                    } label: {
                        Text(model.trustedDevices.isEmpty ? "受信任设备" : "受信任设备 \(model.trustedDevices.count) 台")
                            .font(.system(size: 11.5)).foregroundStyle(Theme.text2)
                    }
                    .buttonStyle(.plain)
                }

                Divider().overlay(Theme.hairline)

                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("本机").font(.system(size: 11)).foregroundStyle(Theme.text3)
                        Text(model.deviceName).font(.system(size: 12.5, weight: .medium)).foregroundStyle(Theme.text)
                            .lineLimit(1)
                    }
                    Spacer()
                    Text(model.localAddress.isEmpty ? "未联网" : model.localAddress)
                        .font(.system(size: 12, design: .monospaced)).foregroundStyle(Theme.text2)
                        .textSelection(.enabled)
                    Button {
                        model.copyLocalAddress()
                        copied = true
                        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { copied = false }
                    } label: {
                        Image(systemName: copied ? "checkmark" : "doc.on.doc").font(.system(size: 11))
                            .foregroundStyle(copied ? Theme.live : Theme.text2).frame(width: 22, height: 22)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain).disabled(model.localAddress.isEmpty)
                    .help("复制本机地址").accessibilityLabel("复制本机地址")
                }
            }
        }
        .sheet(isPresented: $showTrust) { TrustSheet(model: model) }
        .sheet(isPresented: $showPeers) { PullSheet(model: model) }
    }

    private var sourceTitle: String {
        switch model.pullState {
        case .none: return "由发送端推送，或主动拉取"
        case .requesting: return "正在请求「\(model.pullTargetName)」"
        case .accepted: return "「\(model.pullTargetName)」已同意"
        case .denied: return "「\(model.pullTargetName)」拒绝了请求"
        case .busy: return "「\(model.pullTargetName)」正忙"
        case .disabled: return "「\(model.pullTargetName)」不能发送"
        case .failed: return "「\(model.pullTargetName)」没有回应"
        }
    }

    private var sourceSub: String {
        if model.pullError.isEmpty {
            return model.pullState == .none
                ? "点右边选择一台正在发送的电脑，让它把声音送过来"
                : "对方同意后会自动开始；也可以让对方在它的界面里选这台电脑"
        }
        return model.pullError
    }

    private var bufferHint: String {
        let ms = Int(model.bufferMs)
        switch model.bufferMode {
        case .auto: return "按网络状况自动调整，当前 \(ms) ms"
        case .low: return "\(ms) ms · 最跟手，网络差时可能断续"
        case .balanced: return "\(ms) ms · 多数网络下稳定"
        case .stable: return "\(ms) ms · 抗干扰最强，延迟明显"
        }
    }

    private func row<V: View>(_ title: String, @ViewBuilder _ content: () -> V) -> some View {
        HStack {
            Text(title).font(.system(size: 12.5)).foregroundStyle(Theme.text)
            Spacer()
            content()
        }
    }
}

// MARK: - 发送设置

struct SenderPanel: View {
    @ObservedObject var model: AppModel
    @FocusState private var manualFocused: Bool
    @State private var showTrust = false

    private var receivers: [Peer] { model.peers.filter { $0.isReceiver || $0.id == model.targetPeerID } }

    var body: some View {
        Card {
            VStack(alignment: .leading, spacing: 12) {
                SectionLabel(text: "发送到")

                VStack(spacing: 0) {
                    let list = receivers
                    if list.isEmpty && (model.targetPeerID == 0 || model.useManual) {
                        VStack(spacing: 4) {
                            Text("未发现接收端").font(.system(size: 12.5, weight: .medium)).foregroundStyle(Theme.text2)
                            Text("在另一台电脑上打开音频桥并切到「接收」，它会自动出现在这里")
                                .font(.system(size: 11)).foregroundStyle(Theme.text3).multilineTextAlignment(.center)
                        }
                        .frame(maxWidth: .infinity).padding(.vertical, 16).padding(.horizontal, 12)
                    }
                    // 上次的目标当前不在线，也留一行，方便看清「正在往哪发」
                    if !model.useManual, model.targetPeerID != 0, !list.contains(where: { $0.id == model.targetPeerID }) {
                        peerRow(name: model.targetPeerName.isEmpty ? "上次的设备" : model.targetPeerName,
                                sub: "未在线 · 上线后自动连接", icon: "desktopcomputer", selected: true, dim: true) {}
                        Divider().overlay(Theme.hairline).padding(.leading, 40)
                    }
                    ForEach(list) { p in
                        peerRow(name: p.name, sub: subtitle(p),
                                icon: p.platform == Proto.platformMac ? "laptopcomputer" : "desktopcomputer",
                                selected: !model.useManual && p.id == model.targetPeerID,
                                dim: !p.isReceiver || p.versionMismatch) {
                            if !p.versionMismatch { model.selectPeer(p) }
                        }
                        Divider().overlay(Theme.hairline).padding(.leading, 40)
                    }
                    HStack(spacing: 10) {
                        Image(systemName: "network").font(.system(size: 13)).foregroundStyle(Theme.text2).frame(width: 20)
                        TextField("手动输入 IP 地址", text: $model.manualHost)
                            .textFieldStyle(.plain).font(.system(size: 12.5, design: .monospaced))
                            .focused($manualFocused)
                            .onSubmit { model.selectManual() }
                        if model.useManual {
                            Image(systemName: "checkmark").font(.system(size: 11, weight: .bold)).foregroundStyle(Theme.live)
                        } else if !model.manualHost.isEmpty {
                            Button("使用") { model.selectManual() }
                                .buttonStyle(.plain).font(.system(size: 11.5, weight: .medium)).foregroundStyle(Theme.live)
                        }
                    }
                    .padding(.horizontal, 10).frame(height: 38)
                }
                .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(Theme.inset))

                Toggle(isOn: $model.muteLocal) {
                    VStack(alignment: .leading, spacing: 1) {
                        Text("发送时本机静音").font(.system(size: 12.5)).foregroundStyle(Theme.text)
                        Text("声音只从对方设备出来").font(.system(size: 11)).foregroundStyle(Theme.text3)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .toggleStyle(.switch).controlSize(.small)

                Divider().overlay(Theme.hairline)

                Toggle(isOn: $model.allowPull) {
                    VStack(alignment: .leading, spacing: 1) {
                        Text("允许其它设备拉取").font(.system(size: 12.5)).foregroundStyle(Theme.text)
                        Text("对方点一下就能听这台电脑，新设备需要你确认").font(.system(size: 11)).foregroundStyle(Theme.text3)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .toggleStyle(.switch).controlSize(.small)

                Button { showTrust = true } label: {
                    HStack(spacing: 10) {
                        Image(systemName: "checkmark.shield").font(.system(size: 13)).foregroundStyle(Theme.text2).frame(width: 20)
                        Text("受信任设备").font(.system(size: 12.5)).foregroundStyle(Theme.text)
                        Spacer()
                        Text(model.trustedDevices.isEmpty ? "尚无" : "\(model.trustedDevices.count) 台")
                            .font(.system(size: 11.5)).foregroundStyle(Theme.text2)
                        Image(systemName: "chevron.right").font(.system(size: 10, weight: .semibold)).foregroundStyle(Theme.text3)
                    }
                    .padding(.horizontal, 10).frame(height: 34)
                    .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(Theme.inset))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel("管理受信任设备")

                Toggle(isOn: $model.redundancy) {
                    VStack(alignment: .leading, spacing: 1) {
                        Text("丢包冗余").font(.system(size: 12.5)).foregroundStyle(Theme.text)
                        Text("每包重发上一包，丢一包不卡；流量约 ×2").font(.system(size: 11)).foregroundStyle(Theme.text3)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .toggleStyle(.switch).controlSize(.small)
            }
        }
        .sheet(isPresented: $showTrust) { TrustSheet(model: model) }
    }

    private func subtitle(_ p: Peer) -> String {
        var parts = [p.platformLabel, p.ip]
        if p.versionMismatch {
            parts.append("版本不同（\(p.version)），不能互连")
            return parts.joined(separator: " · ")
        }
        if p.isReceiver { parts.append(p.state == 1 ? "接收中" : "等待中") }
        else if p.isSender { parts.append("当前为发送模式") }
        else { parts.append("未开启接收") }
        return parts.joined(separator: " · ")
    }

    private func peerRow(name: String, sub: String, icon: String, selected: Bool, dim: Bool,
                         action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: icon).font(.system(size: 13)).foregroundStyle(Theme.text2).frame(width: 20)
                VStack(alignment: .leading, spacing: 1) {
                    Text(name).font(.system(size: 12.5, weight: .medium)).foregroundStyle(Theme.text).lineLimit(1)
                    Text(sub).font(.system(size: 10.5)).foregroundStyle(Theme.text3).lineLimit(1)
                }
                Spacer()
                if selected {
                    Image(systemName: "checkmark").font(.system(size: 11, weight: .bold)).foregroundStyle(Theme.live)
                }
            }
            .padding(.horizontal, 10).frame(height: 44)
            .opacity(dim ? 0.6 : 1)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel("\(name)，\(sub)" + (selected ? "，已选中" : ""))
    }
}


// MARK: - 选择发送端（接收端主动拉流）

struct PullSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var host = ""

    private var senders: [Peer] { model.peers.filter { $0.isSender || $0.allowsPull } }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("选择发送端").font(.system(size: 15, weight: .semibold)).foregroundStyle(Theme.text)
            Text("列出局域网里正在发送的电脑。对方第一次会询问是否允许，之后不再打扰。两端版本要一致，版本不同的设备会标出来。")
                .font(.system(size: 11.5)).foregroundStyle(Theme.text3).fixedSize(horizontal: false, vertical: true)

            VStack(spacing: 0) {
                if senders.isEmpty {
                    VStack(spacing: 4) {
                        Text("没有发现正在发送的设备").font(.system(size: 12.5, weight: .medium)).foregroundStyle(Theme.text2)
                        Text("让另一台电脑切到「发送」并开始运行，它会出现在这里")
                            .font(.system(size: 11)).foregroundStyle(Theme.text3)
                    }
                    .frame(maxWidth: .infinity).padding(.vertical, 18)
                }
                ForEach(senders) { p in
                    Button {
                        model.pull(from: p)
                        dismiss()
                    } label: {
                        HStack(spacing: 10) {
                            Image(systemName: p.platform == Proto.platformMac ? "laptopcomputer" : "desktopcomputer")
                                .font(.system(size: 13)).foregroundStyle(Theme.text2).frame(width: 20)
                            VStack(alignment: .leading, spacing: 1) {
                                Text(p.name).font(.system(size: 12.5, weight: .medium)).foregroundStyle(Theme.text).lineLimit(1)
                                Text("\(p.ip) · \(p.versionMismatch ? "版本不同（\(p.version)），不能互连" : (p.allowsPull ? "可以拉取" : "未开启允许拉取"))")
                                    .font(.system(size: 10.5)).foregroundStyle(Theme.text3).lineLimit(1)
                            }
                            Spacer()
                            Image(systemName: "arrow.down.circle").font(.system(size: 12)).foregroundStyle(Theme.live)
                        }
                        .padding(.horizontal, 10).frame(height: 44)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .disabled(!p.allowsPull || p.versionMismatch)
                    .opacity((p.allowsPull && !p.versionMismatch) ? 1 : 0.45)
                    Divider().overlay(Theme.hairline).padding(.leading, 40)
                }
                HStack(spacing: 8) {
                    Image(systemName: "network").font(.system(size: 12)).foregroundStyle(Theme.text2).frame(width: 20)
                    TextField("或手动输入发送端 IP", text: $host)
                        .textFieldStyle(.plain).font(.system(size: 12.5, design: .monospaced))
                        .onSubmit { submit() }
                    Button("拉取") { submit() }
                        .buttonStyle(.plain).font(.system(size: 11.5, weight: .medium))
                        .foregroundStyle(host.isEmpty ? Theme.text3 : Theme.live)
                        .disabled(host.isEmpty)
                }
                .padding(.horizontal, 10).frame(height: 38)
            }
            .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(Theme.inset))

            if let err = model.pullError.isEmpty ? nil : model.pullError {
                Text(err).font(.system(size: 11)).foregroundStyle(Theme.warn)
            }

            HStack {
                Spacer()
                Button("关闭") { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(18)
        .frame(width: 370)
        .background(Theme.window)
    }

    private func submit() {
        guard !host.isEmpty else { return }
        // 解析失败时把错误留在弹窗里，别把用户输的地址一起关掉。
        if model.pullManual(host: host) { dismiss() }
    }
}

// MARK: - 受信任设备

struct TrustSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("受信任设备").font(.system(size: 15, weight: .semibold)).foregroundStyle(Theme.text)
            Text("这些设备可以直接拉取这台电脑的声音，不再询问。移除后它们下次请求时会重新征求你的同意。")
                .font(.system(size: 11.5)).foregroundStyle(Theme.text3).fixedSize(horizontal: false, vertical: true)

            VStack(spacing: 0) {
                if model.trustedDevices.isEmpty {
                    Text("尚无受信任设备").font(.system(size: 12)).foregroundStyle(Theme.text3)
                        .frame(maxWidth: .infinity).padding(.vertical, 18)
                }
                ForEach(model.trustedDevices) { d in
                    HStack(spacing: 10) {
                        Image(systemName: "desktopcomputer").font(.system(size: 13)).foregroundStyle(Theme.text2).frame(width: 20)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(d.name).font(.system(size: 12.5, weight: .medium)).foregroundStyle(Theme.text).lineLimit(1)
                            Text("最近允许 " + d.lastSeen.formatted(date: .abbreviated, time: .shortened))
                                .font(.system(size: 10.5)).foregroundStyle(Theme.text3)
                        }
                        Spacer()
                        Button("移除") { model.revokeTrusted(d.id) }
                            .buttonStyle(.plain).font(.system(size: 11.5)).foregroundStyle(Theme.error)
                    }
                    .padding(.horizontal, 10).frame(height: 44)
                    Divider().overlay(Theme.hairline).padding(.leading, 40)
                }
            }
            .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(Theme.inset))

            HStack {
                Button("全部移除") { model.clearTrusted() }
                    .controlSize(.small).disabled(model.trustedDevices.isEmpty)
                Spacer()
                Button("完成") { dismiss() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(18)
        .frame(width: 370)
        .background(Theme.window)
    }
}

// MARK: - 主窗口

struct PrimaryButtonStyle: ButtonStyle {
    let running: Bool
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 13.5, weight: .semibold))
            .foregroundStyle(running ? Theme.text : Color.white)
            .frame(maxWidth: .infinity).frame(height: 38)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous)
                .fill(running ? Theme.track : Theme.live))
            .opacity(configuration.isPressed ? 0.8 : 1)
    }
}

struct MainView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        VStack(spacing: 12) {
            HStack {
                VStack(alignment: .leading, spacing: 1) {
                    Text("音频桥").font(.system(size: 15, weight: .semibold)).foregroundStyle(Theme.text)
                    Text("局域网实时音频").font(.system(size: 10.5)).foregroundStyle(Theme.text3)
                }
                Spacer()
                RoleSwitch(role: model.role) { model.setRole($0) }
            }
            .padding(.horizontal, 2)

            ScrollView(.vertical, showsIndicators: false) {
                VStack(spacing: 12) {
                    StatusCard(model: model)
                    if model.role == .receiver { ReceiverPanel(model: model) } else { SenderPanel(model: model) }
                }
                .padding(.vertical, 2)
            }
            .frame(maxHeight: .infinity)
            .clipped()

            Button(model.running ? (model.role == .receiver ? "停止接收" : "停止发送")
                                 : (model.role == .receiver ? "开始接收" : "开始发送")) {
                model.toggleRunning()
            }
            .buttonStyle(PrimaryButtonStyle(running: model.running))
            .keyboardShortcut(.defaultAction)

            HStack {
                Toggle("登录时自动启动", isOn: $model.autostart)
                    .toggleStyle(.checkbox).controlSize(.small).font(.system(size: 11.5)).foregroundStyle(Theme.text2)
                Spacer()
                Text("v\(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "3.0")")
                    .font(.system(size: 10.5)).foregroundStyle(Theme.text3)
            }
        }
        .padding(.horizontal, 16).padding(.bottom, 14).padding(.top, 6)
        .frame(width: 400, height: 720)
        .background(Theme.window)
    }
}
