import AppKit
import SwiftUI
import Combine

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate, NSWindowDelegate {
    private var statusItem: NSStatusItem!
    private let menu = NSMenu()
    private var window: NSWindow?
    private let model = AppModel()
    private var bag = Set<AnyCancellable>()
    /// 登录自启时带 --background：只进菜单栏，不弹窗口。
    var startHidden = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        menu.delegate = self
        statusItem.menu = menu
        model.start()

        model.$status.receive(on: RunLoop.main).sink { [weak self] s in self?.updateStatusIcon(s) }.store(in: &bag)
        updateStatusIcon(model.status)

        // 再次双击 App 时，后来的那个进程会发这个通知然后退出；这里负责把窗口亮出来并回一声。
        let dnc = DistributedNotificationCenter.default()
        dnc.addObserver(forName: Notification.Name("com.dafeiyu.monitor-audio.show"), object: nil, queue: .main) { [weak self] _ in
            dnc.postNotificationName(Notification.Name("com.dafeiyu.monitor-audio.shown"), object: nil,
                                     userInfo: nil, deliverImmediately: true)
            self?.showWindow()
        }

        if !startHidden { showWindow() }
    }

    func applicationWillTerminate(_ notification: Notification) {
        model.shutdown()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showWindow()
        return false
    }

    // MARK: - 窗口

    @objc private func showWindow() {
        if window == nil {
            let host = NSHostingView(rootView: MainView(model: model))
            let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 400, height: 720),
                             styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
            w.title = "音频桥"
            w.titleVisibility = .hidden
            w.titlebarAppearsTransparent = true
            w.isMovableByWindowBackground = true
            w.isReleasedWhenClosed = false
            w.backgroundColor = NSColor(name: nil) { ap in
                ap.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
                    ? NSColor(srgbRed: 0x1B / 255, green: 0x1B / 255, blue: 0x1D / 255, alpha: 1)
                    : NSColor(srgbRed: 0xF4 / 255, green: 0xF4 / 255, blue: 0xF6 / 255, alpha: 1)
            }
            w.contentView = host
            w.delegate = self
            w.center()
            w.setFrameAutosaveName("MainWindow")
            window = w
        }
        // 有窗口时在 Dock 里露面（能 ⌘-Tab 切回来），关掉窗口后只留菜单栏图标。
        NSApp.setActivationPolicy(.regular)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func windowWillClose(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
    }

    // MARK: - 菜单栏

    private func updateStatusIcon(_ s: StatusInfo) {
        let name: String
        switch s.state {
        case .live: name = "hifispeaker.2.fill"
        case .warning, .error: name = "exclamationmark.triangle"
        default: name = "hifispeaker.2"
        }
        let img = NSImage(systemSymbolName: name, accessibilityDescription: "音频桥")
        img?.isTemplate = true
        statusItem.button?.image = img
        statusItem.button?.toolTip = "音频桥 · \(s.title)"
    }

    func menuWillOpen(_ menu: NSMenu) {
        menu.removeAllItems()
        let s = model.status
        menu.addItem(disabled((model.role == .receiver ? "接收" : "发送") + " · " + s.title))
        if !s.detail.isEmpty, s.state != .off, s.state != .waiting {
            let d = s.detail.count > 44 ? String(s.detail.prefix(44)) + "…" : s.detail
            menu.addItem(disabled(d))
        }
        menu.addItem(.separator())
        menu.addItem(item("打开主窗口", #selector(showWindow), "o"))
        let run = model.running ? (model.role == .receiver ? "停止接收" : "停止发送")
                                : (model.role == .receiver ? "开始接收" : "开始发送")
        menu.addItem(item(run, #selector(toggleRun), ""))
        if model.role == .receiver {
            let m = item("静音", #selector(toggleMute), "m")
            m.state = model.muted ? .on : .off
            menu.addItem(m)
        }
        let sw = item(model.role == .receiver ? "切换为发送" : "切换为接收", #selector(switchRole), "")
        menu.addItem(sw)
        menu.addItem(.separator())
        menu.addItem(item("退出音频桥", #selector(quit), "q"))
    }

    private func item(_ title: String, _ action: Selector, _ key: String) -> NSMenuItem {
        let it = NSMenuItem(title: title, action: action, keyEquivalent: key)
        it.target = self
        return it
    }

    private func disabled(_ title: String) -> NSMenuItem {
        let it = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        it.isEnabled = false
        return it
    }

    @objc private func toggleRun() { model.toggleRunning() }
    @objc private func toggleMute() { model.muted.toggle() }
    @objc private func switchRole() { model.setRole(model.role == .receiver ? .sender : .receiver) }
    @objc private func quit() { NSApp.terminate(nil) }
}

// MARK: - 登录自启（LaunchAgent，免签名、免 App 沙盒）

enum Autostart {
    private static let label = "com.dafeiyu.monitor-audio"
    private static var plistPath: String {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/\(label).plist").path
    }

    static var isEnabled: Bool {
        FileManager.default.fileExists(atPath: plistPath)
    }

    @discardableResult
    static func setEnabled(_ on: Bool) -> Bool {
        let fm = FileManager.default
        if on {
            guard let exe = Bundle.main.executablePath else { return false }
            let dir = URL(fileURLWithPath: plistPath).deletingLastPathComponent()
            try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
            let plist: [String: Any] = [
                "Label": label,
                "ProgramArguments": [exe, "--background"],
                "RunAtLoad": true,
                "ProcessType": "Interactive",
                "KeepAlive": false,
            ]
            guard let data = try? PropertyListSerialization.data(fromPropertyList: plist,
                                                                 format: .xml, options: 0) else { return false }
            // 只写文件，下次登录由 launchd 自动加载；不在这里 load，避免立刻起第二个实例。
            return (try? data.write(to: URL(fileURLWithPath: plistPath))) != nil
        } else {
            try? fm.removeItem(atPath: plistPath)
            return true
        }
    }
}
