// 音频桥 · Windows 端 · 应用状态与调度
//
// 对应 Mac 端 AppModel.swift：角色切换、启停、目标选择、状态文案、自动缓冲、看门狗。
// 所有公开方法都只在 UI 线程上调用；Tick() 由界面的定时器驱动。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace AudioBridge
{
    internal enum Role { Receiver, Sender }
    internal enum LinkState { Off, Waiting, Connecting, Idle, Live, Warning, Error }

    internal enum PullState { None, Requesting, Accepted, Denied, Busy, Disabled, Failed }

    internal sealed class Metric
    {
        public string Label, Value;
        public Metric(string label, string value) { Label = label; Value = value; }
    }

    internal sealed class AppModel
    {
        public static readonly string[] BufferTitles = { "自动", "低延迟", "均衡", "稳定" };
        private static readonly double[] BufferFixed = { 0, 20, 40, 100 };

        // —— 界面读取的状态 ——
        public Role Role = Role.Receiver;
        public bool Running = true;

        public double Volume = 0.75;
        public bool Muted;
        public int BufferMode;              // 0 自动 1 低延迟 2 均衡 3 稳定
        public double BufferMs = 40;
        public string OutputId = "";        // 接收：播放设备（空 = 跟随系统）
        public string CaptureId = "";       // 发送：采集哪个播放设备（空 = 跟随系统）
        public int CaptureProcessId;        // 发送：只采集这个进程（0 = 按播放设备整机采集）
        public string CaptureProcessName = "";
        /// 启动时找不到采集目标程序时的提示（清空即无提示）。
        public string CaptureNotice = "";
        public List<AudioDevice> Devices = new List<AudioDevice>();

        public List<Peer> Peers = new List<Peer>();
        public ulong TargetPeerId;
        public string TargetPeerName = "";
        public bool UseManual;
        public string ManualHost = "";

        public LinkState State = LinkState.Off;
        public string Title = "", Detail = "";
        public List<Metric> Metrics = new List<Metric>();
        public double LevelL, LevelR;
        public string LocalAddress = "";

        // 拉流：接收端主动要某台电脑把声音送过来；发送端可以允许别人来拉。
        public bool AllowPull;
        public bool MixMode;
        public bool Redundancy;
        public PullState Pull = PullState.None;
        public ulong PullTargetId;
        public string PullTargetName = "";
        public string PullError = "";
        public List<TrustDevice> TrustedDevices = new List<TrustDevice>();
        private readonly TrustStore trust = new TrustStore();
        private string pullIp = "";
        private long pullRequestedAt, pullResentAt;
        private bool pullConnected;
        private bool awaitingConfirm;
        /// 因为被拉流才从接收切到发送；拉流结束后恢复接收。
        private bool switchedByPull;
        /// <summary>被拉流之前用户自己选的推送目标，拉流结束要还回去。</summary>
        private TargetMemory restoreTarget;
        /// <summary>内部切角色（被拉流）时不要把 switchedByPull 清掉。</summary>
        private bool internalRoleChange;
        private const double LastPullRequestLifetime = 300;
        private readonly Dictionary<ulong, long> lastPullRequestAt = new Dictionary<ulong, long>();
        private ulong pullToken;
        private ulong sessionToken;
        private string sessionIp = "";
        private readonly ChallengeBook challenges = new ChallengeBook();
        private readonly PullDenyList denyList = new PullDenyList();
        private bool pullDialogOpen;
        private readonly List<ulong> pendingPullDialogs = new List<ulong>();
        private readonly Queue<ControlPacket> controls = new Queue<ControlPacket>();
        private readonly object controlGate = new object();
        private readonly object askGate = new object();

        /// 来确认「允许某台设备拉取」的对话框（标题、正文 → 是否允许）。由界面设置。
        public Func<string, string, bool> AskPermission;

        private sealed class ControlPacket
        {
            public byte Type;
            public byte[] Data;
            public string Ip;
        }

        /// 递增 = 有需要整窗重画的变化（电平表不算，它单独重画）。
        public int Revision;

        // —— 引擎 ——
        private readonly RingBridge bridge = new RingBridge(2.0, 48000);
        private readonly RenderPlayer player;
        private readonly NetReceiver receiver;
        private readonly NetSender sender = new NetSender();
        private readonly LoopbackCapture capture = new LoopbackCapture();
        private readonly Discovery discovery = new Discovery();

        private string receiverError, targetError;
        /// 发送模式但还没选目标：这不是错误，是等用户选。
        private bool needsTarget;
        private string targetLastIp = "";
        private int targetPort = Proto.AudioPort;
        private double bufferTarget = 40;
        private long lastRetry, lastSlow, lastDeviceScan;
        private volatile bool peersDirty;
        private volatile bool devicesDirty;
        private List<AudioDevice> pendingDevices;
        private readonly object deviceGate = new object();
        private int scanning;
        private string lastSignature = "";

        public bool IsReceiving { get { return Running && Role == Role.Receiver; } }
        public bool IsSending { get { return Running && Role == Role.Sender; } }

        public AppModel()
        {
            player = new RenderPlayer(bridge);
            receiver = new NetReceiver(bridge, Proto.AudioPort);

            Role = Settings.Get("role", "receiver") == "sender" ? Role.Sender : Role.Receiver;
            Running = Settings.GetBool("running", true);
            Volume = Math.Max(0, Math.Min(1, Settings.GetDouble("volume", 0.75)));
            Muted = Settings.GetBool("muted", false);
            int bm;
            if (int.TryParse(Settings.Get("bufferMode", "0"), out bm) && bm >= 0 && bm <= 3) BufferMode = bm;
            OutputId = Settings.Get("outputId", "");
            CaptureId = Settings.Get("captureId", "");
            // 进程号每次开机都会变，不能存；只存程序名，启动时重新找。
            CaptureProcessName = Settings.Get("capturePName", "");
            CaptureProcessId = ResolveProcessByName(CaptureProcessName);
            UseManual = Settings.GetBool("useManual", false);
            ManualHost = Settings.Get("manualHost", "");
            ulong.TryParse(Settings.Get("targetPeerID", "0"), out TargetPeerId);
            TargetPeerName = Settings.Get("targetPeerName", "");
            targetLastIp = Settings.Get("targetLastIP", "");
            int tp;
            if (int.TryParse(Settings.Get("targetPort", ""), out tp) && tp > 0 && tp < 65536) targetPort = tp;

            AllowPull = Settings.GetBool("allowPull", false);
            MixMode = Settings.GetBool("mixMode", false);
            Redundancy = Settings.GetBool("redundancy", false);
            receiver.MixEnabled = MixMode;
            sender.Redundancy = Redundancy;
            capture.SetProcess(CaptureProcessId, CaptureProcessName);
            RefreshTrusted();

            bufferTarget = BufferFixed[BufferMode] > 0 ? BufferFixed[BufferMode] : 40;
            BufferMs = bufferTarget;
            player.Volume = (float)Volume;
            player.Muted = Muted;
            player.TargetMs = bufferTarget;
        }

        public void Start()
        {
            LocalAddress = LocalNet.PrimaryIPv4();
            receiver.RateChanged = delegate(double r) { player.InputRate = r; };
            receiver.BufferMs = delegate
            {
                if (MixMode)
                {
                    MixBus mx = receiver.Mixer;
                    return (mx == null ? 0 : mx.LastFillFrames) / 48000 * 1000;
                }
                return bridge.LastFillFrames / Math.Max(player.InputRate, 1) * 1000;
            };
            receiver.TargetMs = delegate { return player.TargetMs; };
            receiver.Muted = delegate { return player.Muted; };
            capture.Audio = delegate(float[] p, int frames, int rate) { sender.Push(p, frames, rate); };
            discovery.Changed = delegate { peersDirty = true; };
            discovery.Message = delegate(byte type, byte[] data, string ip)
            {
                ControlPacket c = new ControlPacket();
                c.Type = type; c.Data = data; c.Ip = ip;
                lock (controlGate) controls.Enqueue(c);
            };
            UpdateCaps();
            discovery.Start();
            // 睡眠唤醒后网卡和声卡都可能换了一茬，整体重来最稳。
            SystemEvents.PowerModeChanged += OnPowerMode;
            ScanDevices();
            ApplyRunState();
        }

        public void Shutdown()
        {
            try { SystemEvents.PowerModeChanged -= OnPowerMode; } catch { }
            StopEngines();
            discovery.Stop();
        }

        private volatile bool resumePending;
        private void OnPowerMode(object s, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) resumePending = true;
        }

        /// 界面预览用的假数据（--snapshot），不启动任何音频/网络。
        public void LoadDemo(string kind)
        {
            LocalAddress = "192.168.1.36";
            Metrics = new List<Metric>();
            Peers = new List<Peer>();
            LevelL = 0; LevelR = 0;
            UseManual = false; TargetPeerId = 0; Running = true; BufferMode = 0; Volume = 0.75; Muted = false;
            if (kind == "receiver-live")
            {
                Role = Role.Receiver; State = LinkState.Live; Title = "正在接收"; Detail = "MacBook Pro · 192.168.1.23";
                Metrics.Add(new Metric("延迟（估算）", "46 ms")); Metrics.Add(new Metric("网络抖动", "1.3 ms"));
                Metrics.Add(new Metric("丢包", "0%")); Metrics.Add(new Metric("采样率", "48.0 kHz"));
                LevelL = 0.72; LevelR = 0.64; BufferMs = 26;
            }
            else if (kind == "receiver-wait")
            {
                Role = Role.Receiver; State = LinkState.Waiting; Title = "等待发送端";
                Detail = "在另一台电脑上选择「" + Proto.DeviceName + "」，或手动输入 192.168.1.36";
            }
            else if (kind == "sender-live" || kind == "sender-warn")
            {
                Role = Role.Sender;
                Peer a = new Peer(); a.Id = 1; a.Name = "MacBook Pro"; a.Ip = "192.168.1.23"; a.Port = 47831;
                a.Role = Proto.RoleReceiver; a.Platform = Proto.PlatformMac; a.State = 1;
                Peer b = new Peer(); b.Id = 2; b.Name = "BEDROOM-PC"; b.Ip = "192.168.1.40"; b.Port = 47831;
                b.Role = Proto.RoleReceiver; b.Platform = Proto.PlatformWindows;
                Peer c = new Peer(); c.Id = 3; c.Name = "客厅 Mac mini"; c.Ip = "192.168.1.41"; c.Port = 47831;
                c.Role = Proto.RoleReceiver; c.Platform = Proto.PlatformMac;
                TargetPeerId = 1; TargetPeerName = a.Name;
                if (kind == "sender-live")
                {
                    Peers.Add(a); Peers.Add(b); Peers.Add(c);
                    State = LinkState.Live; Title = "正在发送"; Detail = a.Name + " · 192.168.1.23:47831";
                    Metrics.Add(new Metric("对方缓冲", "24 ms")); Metrics.Add(new Metric("网络抖动", "0.9 ms"));
                    Metrics.Add(new Metric("丢包", "0%")); Metrics.Add(new Metric("采样率", "48.0 kHz"));
                    LevelL = 0.58; LevelR = 0.93;
                }
                else
                {
                    State = LinkState.Warning; Title = "对方没有回应";
                    Detail = "声音仍在发出。请确认「" + a.Name + "」已打开音频桥并处于接收模式，且两台设备在同一网络、防火墙已放行";
                }
            }
            else
            {
                Role = Role.Receiver; Running = false; State = LinkState.Off; Title = "接收已停止";
                Detail = "开启后，其它设备可以把声音送到这台电脑";
            }
        }

        // —— 用户操作 ——

        public void SetRole(Role r)
        {
            if (r == Role) return;
            if (!internalRoleChange) { switchedByPull = false; restoreTarget = null; }
            StopEngines();
            Role = r;
            Settings.Set("role", r == Role.Sender ? "sender" : "receiver");
            ApplyRunState();
        }

        public void SetRunning(bool on)
        {
            if (on == Running) return;
            Running = on;
            Settings.Set("running", on);
            ApplyRunState();
        }

        public void SetVolume(double v)
        {
            Volume = Math.Max(0, Math.Min(1, v));
            player.Volume = (float)Volume;
            Revision++;
        }

        public void SaveVolume() { Settings.Set("volume", Volume); }

        public void SetMuted(bool m)
        {
            Muted = m;
            player.Muted = m;
            Settings.Set("muted", m);
            Revision++;
        }

        public void SetBufferMode(int mode)
        {
            BufferMode = mode;
            Settings.Set("bufferMode", mode.ToString());
            ApplyBuffer();
            Revision++;
        }

        public void SetAllowPull(bool on)
        {
            AllowPull = on;
            Settings.Set("allowPull", on);
            UpdateCaps();
            Revision++;
        }

        public void SetMixMode(bool on)
        {
            if (on == MixMode) return;
            MixMode = on;
            Settings.Set("mixMode", on);
            UpdateCaps();
            if (IsReceiving) RestartReceiving();
            Revision++;
        }

        public void SetRedundancy(bool on)
        {
            Redundancy = on;
            sender.Redundancy = on;
            Settings.Set("redundancy", on);
            Revision++;
        }

        public void SetOutput(string id)
        {
            OutputId = id ?? "";
            Settings.Set("outputId", OutputId);
            player.SetDevice(OutputId);
            Revision++;
        }

        public void SetCapture(string id)
        {
            CaptureNotice = "";
            CaptureId = id ?? "";
            CaptureProcessId = 0;
            CaptureProcessName = "";
            Settings.Set("captureId", CaptureId);
            Settings.Set("capturePName", "");
            capture.SetDevice(CaptureId);
            capture.SetProcess(0, "");
            Revision++;
        }

        /// 按程序采集：只把这一个程序（含它的子进程）的声音发出去。pid 为 0 表示退回按设备采集。
        public void SetCaptureProcess(int pid, string name)
        {
            CaptureNotice = "";
            CaptureProcessId = pid > 0 ? pid : 0;
            CaptureProcessName = CaptureProcessId > 0 ? (name ?? "") : "";
            // 只存名字：pid 重启即失效。
            Settings.Set("capturePName", CaptureProcessName);
            capture.SetProcess(CaptureProcessId, CaptureProcessName);
            Revision++;
        }

        /// 按名字重新找进程（同名取第一个有主窗口的）。找不到返回 0。
        internal static int ResolveProcessByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            try
            {
                System.Diagnostics.Process[] all = System.Diagnostics.Process.GetProcesses();
                int fallback = 0;
                foreach (System.Diagnostics.Process p in all)
                {
                    if (!string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.MainWindowHandle != IntPtr.Zero) return p.Id;
                    if (fallback == 0) fallback = p.Id;
                }
                return fallback;
            }
            catch { return 0; }
        }

        /// 采集来源那一行显示什么。
        public string CaptureLabel()
        {
            if (CaptureProcessId > 0)
            {
                CapturePlan plan = capture.Plan;
                if (plan.FellBack)
                    return "整机（按程序采集不可用）";
                return "程序：" + (CaptureProcessName.Length > 0 ? CaptureProcessName : "进程 " + CaptureProcessId);
            }
            if (CaptureNotice.Length > 0) return "整机（找不到程序）";
            return DeviceLabel(CaptureId);
        }

        /// 按程序采集失败时的提示（界面可以拿来显示一行说明），正常时为空。
        public string CaptureFallback()
        {
            CapturePlan plan = capture.Plan;
            return plan.FellBack ? plan.FallbackReason : "";
        }

        public void SelectPeer(Peer p)
        {
            if (p.VersionMismatch) return;
            switchedByPull = false;
            restoreTarget = null;
            UseManual = false;
            TargetPeerId = p.Id;
            TargetPeerName = p.Name;
            targetLastIp = p.Ip;
            targetPort = p.Port;
            SaveTarget();
            if (IsSending) StartSending();
            Revision++;
        }

        public void SelectManual(string host)
        {
            switchedByPull = false;
            restoreTarget = null;
            ManualHost = (host ?? "").Trim();
            Settings.Set("manualHost", ManualHost);
            UseManual = true;
            SaveTarget();
            if (IsSending) StartSending();
            Revision++;
        }

        private void SaveTarget()
        {
            Settings.Set("useManual", UseManual);
            Settings.Set("targetPeerID", TargetPeerId.ToString());
            Settings.Set("targetPeerName", TargetPeerName);
            Settings.Set("targetLastIP", targetLastIp);
            Settings.Set("targetPort", targetPort.ToString());
        }

        public string DeviceLabel(string id)
        {
            if (string.IsNullOrEmpty(id)) return "跟随系统";
            foreach (AudioDevice d in Devices) if (d.Id == id) return d.Name;
            return "已选设备未连接（暂用系统默认）";
        }

        // —— 启停 ——

        private void ApplyRunState()
        {
            ApplyRunState(false);
        }

        /// <summary>内部重启（睡眠唤醒等）传 true：不给对方发再见，起来后自动重新请求。</summary>
        private void ApplyRunState(bool preservePull)
        {
            StopEngines(!preservePull);
            if (Running)
            {
                if (Role == Role.Receiver) StartReceiving(); else StartSending();
            }
            discovery.Role = Running ? (Role == Role.Receiver ? Proto.RoleReceiver : Proto.RoleSender) : Proto.RoleIdle;
            discovery.State = 0;
            UpdateCaps();
            discovery.AnnounceNow();
            lastSlow = 0;
            if (ResumePolicy.ShouldResend(preservePull, Pull, pullIp))
            {
                pullConnected = false;
                awaitingConfirm = false;
                Pull = PullState.Requesting;
                pullRequestedAt = Proto.NowNs();
                pullResentAt = pullRequestedAt;
                SendPull();
            }
            Revision++;
        }

        private void StopEngines()
        {
            StopEngines(true);
        }

        private void StopEngines(bool cancelPull)
        {
            if (cancelPull && Pull != PullState.None) CancelPull();
            capture.Stop();
            sender.Stop();
            receiver.Stop();
            player.Stop();
            receiverError = null;
            targetError = null;
            needsTarget = false;
            LevelL = 0; LevelR = 0;
        }

        /// 接收链内部重启（切换混音开关）：不取消拉流，起来后接着拉。
        private void RestartReceiving()
        {
            bool keep = Pull != PullState.None;
            StopEngines(false);
            StartReceiving();
            if (keep && IsReceiving && pullIp.Length > 0)
            {
                pullConnected = false;
                awaitingConfirm = false;
                Pull = PullState.Requesting;
                pullRequestedAt = Proto.NowNs();
                pullResentAt = pullRequestedAt;
                SendPull();
            }
            Revision++;
        }

        /// 换目标之前先和旧的告别，并放出接收端对旧来源的锁定。
        private void EndPreviousPull()
        {
            if (Pull == PullState.None || pullIp.Length == 0) return;
            byte[] bye = Proto.Bye(pullToken);
            discovery.SendControl(bye, bye.Length, pullIp, Proto.DiscoveryPort);
            receiver.ReleaseSource(pullIp);
            pullToken = 0;
            pullConnected = false;
            awaitingConfirm = false;
            Pull = PullState.None;
            Revision++;
        }

        private void StartReceiving()
        {
            receiver.MixEnabled = MixMode;
            // 混音时固定 48 kHz；单路先按 48 kHz 起，第一包到达后由 RateChanged 纠正。
            player.InputRate = 48000;
            try
            {
                receiver.Start();
                receiverError = null;
            }
            catch
            {
                receiverError = "端口 " + Proto.AudioPort + " 被占用，可能已有另一个音频桥在运行";
            }
            // 混音模式由播放线程直接向混音器取数；单路仍走主缓冲。
            player.Mixer = MixMode ? receiver.Mixer : null;
            bridge.Reset();
            player.SetDevice(OutputId);
            player.Start();
        }

        private IPEndPoint ResolveTarget()
        {
            if (UseManual)
            {
                string host = ManualHost.Trim();
                int port = Proto.AudioPort;
                int c = host.LastIndexOf(':');
                int p;
                if (c > 0 && int.TryParse(host.Substring(c + 1), out p) && p > 0 && p < 65536)
                {
                    port = p;
                    host = host.Substring(0, c);
                }
                if (host.Length == 0) { targetError = "请输入接收端的 IP 地址"; return null; }
                IPAddress ip;
                if (IPAddress.TryParse(host, out ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                    return new IPEndPoint(ip, port);
                try
                {
                    foreach (IPAddress a in Dns.GetHostAddresses(host))
                        if (a.AddressFamily == AddressFamily.InterNetwork) return new IPEndPoint(a, port);
                }
                catch { }
                targetError = "地址「" + host + "」无法解析";
                return null;
            }
            foreach (Peer p in Peers)
            {
                if (p.Id != TargetPeerId) continue;
                targetLastIp = p.Ip;
                targetPort = p.Port;
                IPAddress ip;
                if (IPAddress.TryParse(p.Ip, out ip)) return new IPEndPoint(ip, p.Port);
            }
            if (TargetPeerId != 0 && targetLastIp.Length > 0)
            {
                IPAddress ip;
                if (IPAddress.TryParse(targetLastIp, out ip)) return new IPEndPoint(ip, targetPort);
            }
            needsTarget = true;
            return null;
        }

        private void StartSending()
        {
            capture.Stop();
            sender.Stop();
            targetError = null;
            needsTarget = false;
            IPEndPoint ep = ResolveTarget();
            if (ep == null) return;
            sender.Redundancy = Redundancy;
            try
            {
                sender.Start(ep);
            }
            catch (Exception ex)
            {
                targetError = "无法创建网络连接：" + ex.Message;
                return;
            }
            // 目标进程不在了就直接回整机，别静音。
            if (CaptureProcessId > 0)
            {
                int live = ResolveProcessByName(CaptureProcessName);
                if (live == 0)
                {
                    CaptureNotice = "找不到「" + CaptureProcessName + "」，已改为整机采集";
                    CaptureProcessId = 0;
                    CaptureProcessName = "";
                    Settings.Set("capturePName", "");
                }
                else CaptureProcessId = live;
            }
            capture.SetDevice(CaptureId);
            capture.SetProcess(CaptureProcessId, CaptureProcessName);
            capture.Start();
        }

        // —— 设备 / 发现 ——

        private void ScanDevices()
        {
            if (Interlocked.Exchange(ref scanning, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    List<AudioDevice> list = Wasapi.ListRenderDevices();
                    lock (deviceGate) pendingDevices = list;
                    devicesDirty = true;
                }
                catch { }
                finally { Interlocked.Exchange(ref scanning, 0); }
            });
        }

        private void PeersChanged()
        {
            Peers = discovery.Peers;
            Revision++;
            if (UseManual) return;
            Peer t = null;
            foreach (Peer p in Peers) if (p.Id == TargetPeerId) t = p;
            if (t == null) return;
            if (t.Name != TargetPeerName) { TargetPeerName = t.Name; SaveTarget(); }
            if (t.Ip != targetLastIp || t.Port != targetPort)
            {
                // 信标没有凭据：拉流会话期间不跟；普通推送只在旧地址没回执时才跟（A-4）。
                SenderReport rep = sender.Stats.Report;
                double age = rep.AtNs == 0 ? 1e9 : (Proto.NowNs() - rep.AtNs) / 1e9;
                if (!BeaconPolicy.ShouldFollow(targetLastIp, t.Ip, sessionToken != 0, age))
                {
                    if (IsSending && !sender.IsRunning) StartSending();
                    return;
                }
                // 对端换了地址：跟过去，采集不用停。
                targetLastIp = t.Ip;
                targetPort = t.Port;
                SaveTarget();
                if (IsSending)
                {
                    IPAddress ip;
                    if (sender.IsRunning && IPAddress.TryParse(t.Ip, out ip)) sender.Retarget(new IPEndPoint(ip, t.Port));
                    else StartSending();
                }
            }
            else if (IsSending && !sender.IsRunning)
            {
                StartSending();
            }
        }

        // —— 定时刷新（UI 定时器每 50 ms 调一次）——

        private static double Meter(float peak)
        {
            if (peak <= 0.00001f) return 0;
            double db = 20 * Math.Log10(peak);
            return Math.Max(0, Math.Min(1, (db + 60) / 60));
        }

        /// 返回电平表是否有变化。
        public bool Tick()
        {
            long now = Proto.NowNs();
            if (peersDirty) { peersDirty = false; PeersChanged(); }
            List<ControlPacket> pending = null;
            lock (controlGate)
            {
                if (controls.Count > 0) { pending = new List<ControlPacket>(controls); controls.Clear(); }
            }
            if (pending != null)
            {
                // 单个控制包处理出错（比如确认弹窗抛异常）不能让同一拍里剩下的包和后续逻辑一起丢。
                foreach (ControlPacket c in pending)
                {
                    try { HandleControl(c.Type, c.Data, c.Ip); }
                    catch (Exception ex) { LogError("处理控制包失败", ex); }
                }
            }
            if (devicesDirty)
            {
                devicesDirty = false;
                lock (deviceGate) { if (pendingDevices != null) Devices = pendingDevices; pendingDevices = null; }
                Revision++;
            }
            if (resumePending)
            {
                resumePending = false;
                LocalAddress = LocalNet.PrimaryIPv4();
                discovery.Start();
                // 睡眠唤醒：内部重启不发再见，起来后接着拉（A-5）。
                ApplyRunState(true);
            }
            if (now - lastDeviceScan > 3000000000L) { lastDeviceScan = now; ScanDevices(); }

            float pl = 0, pr = 0;
            if (IsReceiving) player.TakePeaks(out pl, out pr);
            else if (IsSending) sender.TakePeaks(out pl, out pr);
            double l = Math.Max(Meter(pl), LevelL - 0.035);
            double r = Math.Max(Meter(pr), LevelR - 0.035);
            bool levelChanged = Math.Abs(l - LevelL) > 0.002 || Math.Abs(r - LevelR) > 0.002;
            LevelL = l; LevelR = r;

            if (now - lastSlow > 250000000L)
            {
                lastSlow = now;
                Slow(now);
            }
            return levelChanged;
        }

        private void ApplyBuffer()
        {
            double fixedMs = BufferFixed[BufferMode];
            if (fixedMs > 0)
            {
                bufferTarget = fixedMs;
            }
            else if (receiver.IsStreaming)
            {
                // 自动：盯最近 10 s 的最大到达间隔。要涨立刻涨，要降慢慢降。
                double want = Math.Max(20, Math.Min(250, receiver.Stats.GapMaxMs * 1.25 + 12));
                if (want > bufferTarget) bufferTarget = want; else bufferTarget += (want - bufferTarget) * 0.04;
            }
            player.TargetMs = bufferTarget;
            BufferMs = Math.Round(bufferTarget);
            if (MixMode) receiver.SetMixTarget(Math.Max(20, bufferTarget));
        }

        /// <summary>清掉过期的「最近拉流请求」记录，别让表无限长。</summary>
        private void PrunePullTables()
        {
            if (lastPullRequestAt.Count == 0) return;
            long now = Proto.NowNs();
            List<ulong> dead = new List<ulong>();
            foreach (KeyValuePair<ulong, long> kv in lastPullRequestAt)
                if ((now - kv.Value) / 1e9 > LastPullRequestLifetime) dead.Add(kv.Key);
            foreach (ulong k in dead) lastPullRequestAt.Remove(k);
            if (lastPullRequestAt.Count > 64)
            {
                List<KeyValuePair<ulong, long>> all = new List<KeyValuePair<ulong, long>>(lastPullRequestAt);
                all.Sort(delegate(KeyValuePair<ulong, long> a, KeyValuePair<ulong, long> b) { return b.Value.CompareTo(a.Value); });
                lastPullRequestAt.Clear();
                for (int i = 0; i < 64 && i < all.Count; i++) lastPullRequestAt[all[i].Key] = all[i].Value;
            }
        }

        private void Slow(long now)
        {
            PrunePullTables();
            Watchdog(now);
            string ip = LocalNet.PrimaryIPv4();
            if (ip != LocalAddress) LocalAddress = ip;

            LinkState st = LinkState.Off;
            string title = "", detail = "";
            List<Metric> m = new List<Metric>();

            if (!Running)
            {
                st = LinkState.Off;
                title = Role == Role.Receiver ? "接收已停止" : "发送已停止";
                detail = Role == Role.Receiver ? "开启后，其它设备可以把声音送到这台电脑" : "开启后，这台电脑的系统声音会送到所选设备";
            }
            else if (Role == Role.Receiver)
            {
                ApplyBuffer();
                TickPull(now);
                ReceiverStats rs = receiver.Stats;
                bool linked = receiver.IsLinked;
                bool streaming = receiver.IsStreaming;
                string who = rs.SenderName.Length > 0 ? rs.SenderName : rs.Sender;
                string err = receiverError ?? player.Error;
                if (err != null)
                {
                    st = LinkState.Error; title = "无法接收"; detail = err;
                }
                else if (linked && streaming)
                {
                    st = LinkState.Live; title = "正在接收";
                    detail = who + " · " + rs.Sender + (MixMode && rs.SourceCount > 1 ? " · " + rs.SourceCount + " 路混音" : "");
                }
                else if (linked)
                {
                    st = LinkState.Idle; title = "已连接"; detail = who + " 当前没有声音在播放";
                }
                else if (Pull == PullState.Requesting && pullIp.Length > 0)
                {
                    st = LinkState.Connecting;
                    title = "正在请求「" + (PullTargetName.Length > 0 ? PullTargetName : pullIp) + "」";
                    detail = "等对方确认；对方开启「允许其它设备拉取」后就能直接连上";
                }
                else if (Pull == PullState.Failed || Pull == PullState.Denied
                    || Pull == PullState.Busy || Pull == PullState.Disabled)
                {
                    st = LinkState.Warning; title = "拉取没有成功";
                    detail = PullError.Length > 0 ? PullError : "对方没有回应拉取请求";
                }
                else
                {
                    st = LinkState.Waiting; title = "等待发送端";
                    detail = LocalAddress.Length == 0 ? "未连接到局域网"
                        : "在另一台电脑上选择「" + Proto.DeviceName + "」，或点击「选择发送端」主动拉取";
                }
                if (linked)
                {
                    double rate = Math.Max(player.InputRate, 1);
                    MixBus mx = receiver.Mixer;
                    double fill = MixMode && mx != null ? mx.LastFillFrames / 48000 * 1000 : bridge.LastFillFrames / rate * 1000;
                    double latency = (streaming ? fill : player.TargetMs) + player.DeviceLatencyMs + 10;
                    double total = rs.Packets + rs.Lost;
                    double loss = total > 0 ? rs.Lost / total * 100 : 0;
                    m.Add(new Metric("延迟（估算）", latency.ToString("0") + " ms"));
                    m.Add(new Metric("网络抖动", rs.JitterMs.ToString("0.0") + " ms"));
                    m.Add(new Metric("丢包", loss < 0.005 ? "0%" : loss.ToString("0.00") + "%"));
                    m.Add(new Metric("采样率", (rs.Rate / 1000).ToString("0.0") + " kHz"));
                }
                discovery.State = (byte)(linked ? 1 : 0);
            }
            else
            {
                SenderStats ss = sender.Stats;
                SenderReport rep = ss.Report;
                bool fresh = rep.AtNs != 0 && (now - rep.AtNs) / 1e9 < 2.0;
                bool hasAudio = ss.LastAudioNs != 0 && (now - ss.LastAudioNs) / 1e9 < 0.5;
                string who = UseManual ? ManualHost : (rep.Name.Length > 0 ? rep.Name : TargetPeerName);
                string err = targetError ?? (sender.IsRunning ? capture.Error : null);
                if (needsTarget && !sender.IsRunning)
                {
                    st = LinkState.Waiting; title = "选择接收端";
                    detail = "在下方列表里选择要把声音送到哪台设备，或手动输入对方的 IP 地址";
                }
                else if (err != null)
                {
                    st = LinkState.Error; title = "无法发送"; detail = err;
                }
                else if (fresh && rep.Busy)
                {
                    st = LinkState.Warning; title = "对方正忙"; detail = who + " 正在接收另一台设备的声音";
                }
                else if (fresh)
                {
                    if (hasAudio)
                    {
                        st = LinkState.Live; title = "正在发送";
                        detail = who + " · " + sender.TargetDescription + (rep.Muted ? " · 对方已静音" : "");
                    }
                    else
                    {
                        st = LinkState.Idle; title = "已连接"; detail = "这台电脑当前没有声音在播放";
                    }
                    double total = (double)rep.Packets + rep.Lost;
                    double loss = total > 0 ? rep.Lost / total * 100 : 0;
                    m.Add(new Metric("对方缓冲", rep.BufferMs.ToString("0") + " ms"));
                    m.Add(new Metric("网络抖动", rep.JitterMs.ToString("0.0") + " ms"));
                    m.Add(new Metric("丢包", loss < 0.005 ? "0%" : loss.ToString("0.00") + "%"));
                    m.Add(new Metric("采样率", ss.Rate > 0 ? (ss.Rate / 1000).ToString("0.0") + " kHz" : "—"));
                }
                else if (ss.StartedNs != 0 && (now - ss.StartedNs) / 1e9 < 2.5)
                {
                    st = LinkState.Connecting; title = "正在连接"; detail = who + " · " + sender.TargetDescription;
                }
                else
                {
                    st = LinkState.Warning; title = "对方没有回应";
                    detail = "声音仍在发出。请确认「" + who + "」已打开音频桥并处于接收模式，且两台设备在同一网络、防火墙已放行";
                }
                discovery.State = (byte)((fresh && !rep.Busy) ? 1 : 0);
            }

            string sig = (int)st + "|" + title + "|" + detail + "|" + BufferMs + "|" + LocalAddress;
            foreach (Metric x in m) sig += "|" + x.Value;
            State = st; Title = title; Detail = detail; Metrics = m;
            if (sig != lastSignature)
            {
                lastSignature = sig;
                Revision++;
            }
        }

        // —— 拉流：接收端主动要求某台电脑把声音送过来 ——

        public bool PullWaiting { get { return Pull == PullState.Requesting || Pull == PullState.Accepted; } }

        public void PullFrom(Peer p)
        {
            if (!IsReceiving) return;
            if (p.Ip != pullIp) EndPreviousPull();
            PullTargetId = p.Id;
            PullTargetName = p.Name;
            pullIp = p.Ip;
            PullError = "";
            StartPull();
        }

        public void PullManual(string host)
        {
            if (!IsReceiving) return;
            if (host == null) host = "";
            host = host.Trim();
            if (host.Length == 0) host = ManualHost.Trim();
            if (host.Length == 0) { PullError = "请输入对方的 IP 地址"; Revision++; return; }
            int c = host.LastIndexOf(':');
            int p;
            if (c > 0 && int.TryParse(host.Substring(c + 1), out p) && p > 0 && p < 65536)
                host = host.Substring(0, c);
            string ip = HostToIp(host);
            if (ip == null) { PullError = "地址「" + host + "」无法解析"; Revision++; return; }
            if (ip != pullIp) EndPreviousPull();
            PullTargetId = 0;
            PullTargetName = host;
            pullIp = ip;
            PullError = "";
            StartPull();
        }

        public void CancelPull()
        {
            if (pullIp.Length > 0)
            {
                byte[] bye = Proto.Bye(pullToken);
                discovery.SendControl(bye, bye.Length, pullIp, Proto.DiscoveryPort);
                discovery.SendControl(bye, bye.Length, pullIp, Proto.DiscoveryPort);
                discovery.SendControl(bye, bye.Length, pullIp, Proto.DiscoveryPort);
            }
            if (pullIp.Length > 0)
            {
                receiver.Prioritize("");
                receiver.ReleaseSource(pullIp);
            }
            pullToken = 0;
            pullIp = "";
            Pull = PullState.None;
            PullTargetId = 0;
            PullTargetName = "";
            PullError = "";
            pullConnected = false;
            Revision++;
        }

        public void RevokeTrusted(ulong id)
        {
            trust.Revoke(id);
            trust.Save();
            RefreshTrusted();
            Revision++;
        }

        public void ClearTrusted()
        {
            trust.RevokeAll();
            trust.Save();
            RefreshTrusted();
            Revision++;
        }

        private void RefreshTrusted()
        {
            TrustedDevices = trust.Snapshot();
        }

        private void UpdateCaps()
        {
            byte caps = 0;
            if (AllowPull) caps |= Proto.CapPull;
            if (MixMode) caps |= Proto.CapMix;
            discovery.Caps = caps;
        }

        private static string HostToIp(string host)
        {
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip) && ip.AddressFamily == AddressFamily.InterNetwork) return ip.ToString();
            try
            {
                foreach (IPAddress a in Dns.GetHostAddresses(host))
                    if (a.AddressFamily == AddressFamily.InterNetwork) return a.ToString();
            }
            catch { }
            return null;
        }

        private void StartPull()
        {
            pullConnected = false;
            awaitingConfirm = false;
            pullRequestedAt = Proto.NowNs();
            pullResentAt = pullRequestedAt;
            Pull = PullState.Requesting;
            SendPull();
            Revision++;
        }

        private void SendPull()
        {
            if (!IsReceiving || pullIp.Length == 0) return;
            int len;
            // v4：请求里不带密钥，先请对方给一个随机数。
            byte[] b = Proto.Pull(PullTargetId, Proto.AudioPort, out len);
            discovery.SendControl(b, len, pullIp, Proto.DiscoveryPort);
        }

        private void TickPull(long now)
        {
            if (Pull == PullState.None) return;
            if (!IsReceiving)
            {
                Pull = PullState.None;
                pullIp = "";
                Revision++;
                return;
            }
            // 被拒绝/正忙/没开启之后不再重发，也不把原因盖成「没有回应」。
            if (Pull != PullState.Requesting && Pull != PullState.Accepted) return;

            // 连上没有看来源表（保活包也算），别用每包都可能变的主路。
            if (receiver.HasSource(pullIp))
            {
                if (!pullConnected)
                {
                    pullConnected = true;
                    awaitingConfirm = false;
                    Pull = PullState.Accepted;
                    PullError = "";
                    Revision++;
                }
                return;
            }
            if (pullConnected)
            {
                // 连上又断了：重新请求，不要一直停在「已同意」。
                pullConnected = false;
                Pull = PullState.Requesting;
                pullRequestedAt = now;
                pullResentAt = now;
                SendPull();
                Revision++;
                return;
            }
            double limit = awaitingConfirm ? 120.0 : 20.0;
            if ((now - pullRequestedAt) / 1e9 > limit)
            {
                Pull = PullState.Failed;
                PullError = awaitingConfirm ? "对方一直没有确认" : "对方没有回应拉取请求";
                Revision++;
                return;
            }
            if ((now - pullResentAt) / 1e9 > 1.2)
            {
                pullResentAt = now;
                SendPull();
            }
        }

        private void HandleControl(byte type, byte[] data, string ip)
        {
            // 版本不同就是两套字节布局：拉流相关的包一律不认（再见没有版本字节，单独校验令牌）。
            if (type != Proto.TypeBye && (data == null || data.Length < 6 || data[5] != Proto.Version)) return;
            if (type == Proto.TypePull) { HandlePullRequest(data, ip); return; }
            if (type == Proto.TypePullAuth) { HandlePullAuth(data, ip); return; }
            if (type == Proto.TypePullChallenge) { HandlePullChallenge(data, ip); return; }
            if (type == Proto.TypePullReply) { HandlePullReply(data, ip); return; }
            if (type == Proto.TypePullNoKey) { HandlePullNoKey(data, ip); return; }
            if (type != Proto.TypeBye) return;

            // 长短包都要先判长度；再要求「正在发 + 来源就是当前目标 + 会话令牌对得上」。
            if (!PullAuth.ByeAccepted(data, ip, targetLastIp, sender.IsRunning, sessionToken)) return;
            sessionToken = 0;
            sessionIp = "";
            bool restore = switchedByPull;
            switchedByPull = false;
            // 对方不要这个声音了（取消拉流）：停掉采集和网络，等用户重新选目标。
            if (sender.IsRunning) { capture.Stop(); sender.Stop(); }
            if (restoreTarget != null)
            {
                TargetMemory m = restoreTarget;
                restoreTarget = null;
                UseManual = m.UseManual;
                TargetPeerId = m.Id;
                TargetPeerName = m.Name;
                targetLastIp = m.Ip;
                targetPort = m.Port;
                needsTarget = m.Id == 0 && string.IsNullOrEmpty(m.Ip);
            }
            else
            {
                UseManual = false;
                TargetPeerId = 0;
                TargetPeerName = "";
                targetLastIp = "";
                needsTarget = true;
            }
            SaveTarget();
            Revision++;
            if (restore) SetRole(Role.Receiver);
            else if (Role == Role.Sender && !needsTarget) StartSending();
        }

        private void HandlePullRequest(byte[] d, string ip)
        {
            if (d.Length < 25) return;
            int audioPort = Proto.U16(d, 6);
            ulong target = Proto.U64(d, 8);
            ulong requester = Proto.U64(d, 16);
            int nameLen = d[24];
            string name = nameLen > 0 ? Proto.GetName(d, 24, d.Length) : "";
            if (requester == 0 || requester == Proto.InstanceId) return;
            if (target != 0 && target != Proto.InstanceId) return;
            if (name.Length == 0) name = ip;

            // 已经在等这个来源的鉴权：重发同一个 nonce，别让在途的 pullAuth 失效（也别被冷却挡住）。
            if (challenges.HasPending(requester, ip))
            {
                ulong again = challenges.Issue(requester, ip);
                int l2;
                byte[] c2 = Proto.PullChallenge(requester, again, Proto.InstanceId, out l2);
                discovery.SendControl(c2, l2, ip, Proto.DiscoveryPort);
                return;
            }
            // 刚被拒绝过的来源 30 秒内直接回拒绝，不再弹窗。
            if (denyList.IsCooling(ip)) { ReplyPull(requester, Proto.PullDenied, 0, 0, ip); return; }
            if (!Running || !AllowPull) { ReplyPull(requester, Proto.PullDisabled, 0, 0, ip); return; }
            lastPullRequestAt[requester] = Proto.NowNs();
            // 受信任设备：不回密钥，先给一次性随机数。
            if (trust.KeyForDevice(requester) != 0)
            {
                ulong nonce = challenges.Issue(requester, ip);
                int clen;
                byte[] c = Proto.PullChallenge(requester, nonce, Proto.InstanceId, out clen);
                discovery.SendControl(c, clen, ip, Proto.DiscoveryPort);
                return;
            }
            if (Role == Role.Sender && sender.IsRunning && SenderPeerOnline() && targetLastIp != ip)
            {
                ReplyPull(requester, Proto.PullBusy, 0, 0, ip);
                return;
            }
            AskPullPermission(requester, name, ip, audioPort);
        }

        /// <summary>接收端说「我没有你的密钥」：作废旧密钥、回到弹窗重新配对。只认真刚质询过的设备+IP。</summary>
        private void HandlePullNoKey(byte[] d, string ip)
        {
            if (d.Length < 25) return;
            int audioPort = Proto.U16(d, 6);
            ulong requester = Proto.U64(d, 8);
            ulong senderId = Proto.U64(d, 16);
            int nameLen = d[24];
            string name = nameLen > 0 ? Proto.GetName(d, 24, d.Length) : "";
            if (requester == 0 || requester == Proto.InstanceId) return;
            if (senderId != 0 && senderId != Proto.InstanceId) return;
            if (!challenges.HasPending(requester, ip)) return;
            challenges.Cancel(requester);
            trust.Revoke(requester);
            trust.Save();
            RefreshTrusted();
            if (name.Length == 0) name = ip;
            AskPullPermission(requester, name, ip, audioPort);
        }

        private void HandlePullAuth(byte[] d, string ip)
        {
            if (d.Length < 49) return;
            int audioPort = Proto.U16(d, 6);
            ulong nonce = Proto.U64(d, 8);
            ulong requester = Proto.U64(d, 16);
            ulong senderId = Proto.U64(d, 24);
            int nameLen = d[48];
            string name = nameLen > 0 ? Proto.GetName(d, 48, d.Length) : "";
            if (senderId != Proto.InstanceId || requester == 0 || requester == Proto.InstanceId) return;
            if (name.Length == 0) name = ip;

            if (!challenges.HasPending(requester, ip))
            {
                // 质询过期/丢了（或有人冒用设备号）：静默重发，不算失败。
                ulong again = challenges.Issue(requester, ip);
                int l3;
                byte[] c3 = Proto.PullChallenge(requester, again, Proto.InstanceId, out l3);
                discovery.SendControl(c3, l3, ip, Proto.DiscoveryPort);
                return;
            }
            ulong key = trust.KeyForDevice(requester);
            if (!challenges.Consume(requester, nonce, ip)
                || !PullAuth.Verify(key, nonce, requester, Proto.InstanceId, d, 32))
            {
                // 质询对上了但 MAC 算错才算一次失败。
                denyList.NoteFailure(ip);
                ReplyPull(requester, Proto.PullDenied, 0, 0, ip);
                return;
            }
            // 受信任设备也仍受总开关与运行状态约束。
            if (!Running || !AllowPull) { ReplyPull(requester, Proto.PullDisabled, 0, 0, ip); return; }
            // 会话令牌就用这次的一次性随机数。
            AcceptPull(requester, name, ip, audioPort, 0, nonce, false);
        }

        private void HandlePullChallenge(byte[] d, string ip)
        {
            if (!IsReceiving || Pull != PullState.Requesting || ip != pullIp) return;
            if (d.Length < 33) return;
            ulong requester = Proto.U64(d, 8);
            ulong senderId = Proto.U64(d, 16);
            ulong nonce = Proto.U64(d, 24);
            if (requester != Proto.InstanceId || senderId == 0 || nonce == 0) return;
            if (PullTargetId == 0) PullTargetId = senderId;
            ulong key = trust.KeyForSender(senderId);
            if (key == 0)
            {
                // 明确告诉对方「我没有密钥」，请它作废旧密钥并重新弹窗配对（A-3）。
                int nlen;
                byte[] nk = Proto.PullNoKey(senderId, Proto.AudioPort, out nlen);
                discovery.SendControl(nk, nlen, pullIp, Proto.DiscoveryPort);
                discovery.SendControl(nk, nlen, pullIp, Proto.DiscoveryPort);
                awaitingConfirm = true;
                Pull = PullState.Requesting;
                PullError = "需要对方重新确认一次";
                Revision++;
                return;
            }
            int len;
            byte[] mac = PullAuth.Mac(key, nonce, Proto.InstanceId, senderId);
            byte[] b = Proto.PullAuth(senderId, nonce, mac, Proto.AudioPort, out len);
            discovery.SendControl(b, len, pullIp, Proto.DiscoveryPort);
        }

        private void ReplyPull(ulong requester, byte result, ulong key, ulong token, string ip)
        {
            int len;
            byte[] b = Proto.PullReply(requester, result, token, key, out len);
            // 连发三遍：UDP 丢一个也不至于让配对/取消卡住；接收方对重复包幂等（A-3）。
            discovery.SendControl(b, len, ip, Proto.DiscoveryPort);
            discovery.SendControl(b, len, ip, Proto.DiscoveryPort);
            discovery.SendControl(b, len, ip, Proto.DiscoveryPort);
        }

        private void HandlePullReply(byte[] d, string ip)
        {
            // 只认「正在请求的那台机器 + 我们还在等」；连上之后重放的旧应答一律忽略。
            if (!PullAuth.ReplyAccepted(d, ip, pullIp, Proto.InstanceId, Pull == PullState.Requesting)) return;
            byte result = d[6];
            byte flags = d[7];
            ulong senderId = Proto.U64(d, 16);
            ulong token = Proto.U64(d, 24);
            ulong key = Proto.U64(d, 32);
            int nameLen = d[40];
            string name = nameLen > 0 ? Proto.GetName(d, 40, d.Length) : "";

            if (result == Proto.PullOk || result == Proto.PullOkNew)
            {
                if ((flags & 1) != 0 && senderId != 0 && key != 0 && trust.KeyForDevice(senderId) == 0) { trust.SetKey(senderId, key); trust.Save(); }
                awaitingConfirm = false;
                if (PullTargetId == 0 && senderId != 0) PullTargetId = senderId;
                if (PullTargetName.Length == 0 && name.Length > 0) PullTargetName = name;
                pullToken = token;
                Pull = PullState.Accepted;
                PullError = "";
            }
            else if (result == Proto.PullWait)
            {
                Pull = PullState.Requesting;
                awaitingConfirm = true;
                PullError = "";
            }
            else if (result == Proto.PullDenied)
            {
                Pull = PullState.Denied;
                PullError = "「" + (name.Length > 0 ? name : PullTargetName) + "」拒绝了拉取请求";
            }
            else if (result == Proto.PullBusy)
            {
                Pull = PullState.Busy;
                PullError = "「" + (name.Length > 0 ? name : PullTargetName) + "」正在给别的设备发声音";
            }
            else if (result == Proto.PullDisabled)
            {
                Pull = PullState.Disabled;
                PullError = "对方没有开启「允许其它设备拉取」";
            }
            else
            {
                Pull = PullState.Failed;
                PullError = "对方没有接受拉取请求";
            }
            Revision++;
        }

        private bool SenderPeerOnline()
        {
            SenderReport rep = sender.Stats.Report;
            if (rep.AtNs == 0) return false;
            return (Proto.NowNs() - rep.AtNs) / 1e9 < 2.5 && rep.Name.Length > 0;
        }

        private ulong CurrentSenderPeerId()
        {
            if (!UseManual) return TargetPeerId;
            SenderReport rep = sender.Stats.Report;
            if (rep.AtNs != 0 && (Proto.NowNs() - rep.AtNs) / 1e9 < 2.5) return rep.PeerId;
            return 0;
        }

        private void AcceptPull(ulong id, string name, string ip, int audioPort, ulong key, ulong token, bool newPairing)
        {
            int port = audioPort > 0 ? audioPort : Proto.AudioPort;
            ReplyPull(id, newPairing ? Proto.PullOkNew : Proto.PullOk, newPairing ? key : 0, token, ip);

            // 本来就在给同一台机器同一个端口发：只回应答，不重启采集和发送 socket。
            bool same = Role == Role.Sender && sender.IsRunning && targetLastIp == ip && targetPort == port
                && sessionIp == ip && sessionToken != 0;
            PullSessionState cur = new PullSessionState();
            cur.Token = sessionToken; cur.Ip = sessionIp; cur.Port = targetPort;
            bool restart;
            PullSessionState next = PullSessionState.Adopt(cur, ip, port, token, same, out restart);
            // 令牌必须先跟着换掉，否则接收端拿新令牌发再见、这里按旧令牌比对会拒（A-2）。
            sessionToken = next.Token;
            sessionIp = next.Ip;
            if (!restart)
            {
                Revision++;
                return;
            }
            // 记住用户原来的推送目标，拉流结束后还回去。
            if (restoreTarget == null && (UseManual || TargetPeerId != 0 || !string.IsNullOrEmpty(targetLastIp)))
                restoreTarget = new TargetMemory(UseManual, TargetPeerId, TargetPeerName, targetLastIp, targetPort);
            UseManual = false;
            TargetPeerId = id;
            TargetPeerName = name.Length > 0 ? name : ip;
            targetLastIp = ip;
            targetPort = port;
            SaveTarget();
            if (!Running)
            {
                if (Role != Role.Sender) switchedByPull = true;
                SetRunning(true);
                if (Role != Role.Sender)
                {
                    internalRoleChange = true;
                    SetRole(Role.Sender);
                    internalRoleChange = false;
                }
            }
            else if (Role != Role.Sender)
            {
                switchedByPull = true;
                internalRoleChange = true;
                SetRole(Role.Sender);
                internalRoleChange = false;
            }
            else
            {
                StartSending();
            }
            Revision++;
        }

        /// <summary>确认弹窗是否正开着、是否已被置顶（供自测观察；界面线程只会短暂看到 true）。</summary>
        public volatile bool DialogTopMost;

        /// <summary>调界面弹窗；任何异常都按「拒绝」处理，并且一定把置顶状态复原（A-1）。</summary>
        private bool ShowDialog(string title, string text)
        {
            return PullDialogGuard.Run(AskPermission, title, text,
                delegate(bool on) { DialogTopMost = on; }, LogError);
        }

        /// <summary>写错误日志（wine 瓶里 %APPDATA% 不可写时静默放弃）。</summary>
        internal static void LogError(string what, Exception ex)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioBridge");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + what + ": " + ex + "\r\n");
            }
            catch { }
        }

        private void AskPullPermission(ulong id, string name, string ip, int audioPort)
        {
            // 弹窗期间告诉对方在等人确认，对方不会按 20 秒判超时。
            ReplyPull(id, Proto.PullWait, 0, 0, ip);
            lock (askGate)
            {
                if (pullDialogOpen || pendingPullDialogs.Contains(id)) return;
                pendingPullDialogs.Add(id);
                pullDialogOpen = true;
            }
            bool allow = false;
            try
            {
                string who = name.Length > 0 ? name : ip;
                string text = "「" + who + "」（" + ip + "）请求接收这台电脑的声音。\n\n"
                    + "允许后，这台电脑会切换到发送模式，开始把系统声音送到对方；"
                    + "对方会被记为受信任设备，以后它再拉取就不用再问。";
                allow = ShowDialog("拉取请求", text);
            }
            finally
            {
                lock (askGate)
                {
                    pendingPullDialogs.Remove(id);
                    pullDialogOpen = false;
                }
            }
            if (allow)
            {
                long last;
                if (!lastPullRequestAt.TryGetValue(id, out last)
                    || (Proto.NowNs() - last) / 1e9 > 8)
                {
                    // 对方已经放弃（不再重发请求）：点「允许」也不该切角色、开采集。
                    ReplyPull(id, Proto.PullDisabled, 0, 0, ip);
                    return;
                }
                ulong newKey = trust.Grant(id, name);
                trust.Save();
                RefreshTrusted();
                denyList.Clear(ip);
                AcceptPull(id, name, ip, audioPort, newKey, PullAuth.RandomNonzero(), true);
            }
            else
            {
                denyList.Deny(ip);
                ReplyPull(id, Proto.PullDenied, 0, 0, ip);
            }
        }

        /// 兜底：该转的东西没在转，就定期再拉一把。（音频线程自己会重试，这里只管网络侧。）
        private void Watchdog(long now)
        {
            if (!Running || (now - lastRetry) / 1e9 < 3) return;
            if (Role == Role.Receiver)
            {
                if (!receiver.IsRunning)
                {
                    lastRetry = now;
                    try
                    {
                        receiver.Start();
                        receiverError = null;
                        // 新的接收端会建一个新的混音器，旧的已经停了：不换过去混音就没声。
                        player.Mixer = MixMode ? receiver.Mixer : null;
                    }
                    catch { }
                }
            }
            else if (sender.IsRunning && !capture.Alive)
            {
                // 采集线程自己停了（比如按程序采集的目标退出了）：按「设备采集」重新起来，别一直静音。
                lastRetry = now;
                StartSending();
            }
            else if (!sender.IsRunning)
            {
                if (UseManual ? ManualHost.Length > 0 : TargetPeerId != 0)
                {
                    lastRetry = now;
                    StartSending();
                }
            }
        }
    }

    /// 拉流鉴权的纯逻辑（与 Mac 端 Sources/PullAuth.swift 一一对应）。
    internal static class PullAuth
    {
        public static byte[] AuthInput(ulong nonce, ulong requester, ulong senderId)
        {
            byte[] d = new byte[24];
            Proto.PutU64(d, 0, nonce);
            Proto.PutU64(d, 8, requester);
            Proto.PutU64(d, 16, senderId);
            return d;
        }

        public static byte[] Mac(ulong key, ulong nonce, ulong requester, ulong senderId)
        {
            byte[] k = new byte[8];
            Proto.PutU64(k, 0, key);
            using (HMACSHA256 h = new HMACSHA256(k))
            {
                byte[] full = h.ComputeHash(AuthInput(nonce, requester, senderId));
                byte[] mac = new byte[16];
                Array.Copy(full, mac, 16);
                return mac;
            }
        }

        public static bool Verify(ulong key, ulong nonce, ulong requester, ulong senderId, byte[] mac, int offset)
        {
            if (key == 0 || mac == null || mac.Length < offset + 16) return false;
            byte[] want = Mac(key, nonce, requester, senderId);
            int diff = 0;
            for (int i = 0; i < 16; i++) diff |= want[i] ^ mac[offset + i];
            return diff == 0;
        }

        /// 再见：长度够、类型对、正在发送、来源就是当前目标、令牌是本次会话的。
        public static bool ByeAccepted(byte[] data, string from, string targetIp, bool sending, ulong token)
        {
            if (data == null || data.Length < 16 || !Proto.HasMagic(data, data.Length)) return false;
            if (data[4] != Proto.TypeBye) return false;
            if (!sending || token == 0 || targetIp.Length == 0 || from != targetIp) return false;
            return Proto.U64(data, 8) == token;
        }

        /// 应答只认「正在请求的那台机器 + 我们确实还在等」。
        public static bool ReplyAccepted(byte[] data, string from, string pullIp, ulong instanceId, bool waiting)
        {
            if (data == null || data.Length < 41 || !Proto.HasMagic(data, data.Length)) return false;
            if (data[4] != Proto.TypePullReply) return false;
            if (!waiting || pullIp.Length == 0 || from != pullIp) return false;
            return Proto.U64(data, 8) == instanceId;
        }

        public static ulong RandomNonzero()
        {
            byte[] b = new byte[8];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                do { rng.GetBytes(b); } while (BitConverter.ToUInt64(b, 0) == 0);
            }
            return BitConverter.ToUInt64(b, 0);
        }
    }

    /// 发出过、还没被应答的质询：单次使用、15 秒过期、绑定来源 IP。
    internal sealed class ChallengeBook
    {
        private sealed class Entry
        {
            public ulong Nonce;
            public string Ip;
            public long ExpiresNs;
        }

        private const double Lifetime = 15;
        private const int MaxEntries = 64;
        // 「设备号 + 来源 IP」：别人冒用设备号从别的 IP 发请求，顶不掉正主的质询。
        private readonly Dictionary<string, Entry> pending = new Dictionary<string, Entry>();
        private readonly List<string> order = new List<string>();

        private static string Key(ulong requester, string ip) { return requester.ToString() + "|" + (ip ?? ""); }

        public int Count { get { Prune(); return pending.Count; } }

        public ulong Issue(ulong requester, string ip)
        {
            Prune();
            // 已经在等这个来源的鉴权：重复「质询」必须给同一个 nonce，否则对方在途的 pullAuth 会被判失效。
            string same = Key(requester, ip);
            Entry cur;
            if (pending.TryGetValue(same, out cur) && cur.ExpiresNs > Proto.NowNs()) return cur.Nonce;
            if (pending.Count >= MaxEntries && order.Count > 0)
            {
                string oldest = order[0];
                order.RemoveAt(0);
                pending.Remove(oldest);
            }
            ulong nonce = PullAuth.RandomNonzero();
            Entry e = new Entry();
            e.Nonce = nonce;
            e.Ip = ip == null ? "" : ip;
            e.ExpiresNs = Proto.NowNs() + (long)(Lifetime * 1e9);
            string k = Key(requester, ip);
            pending[k] = e;
            order.Remove(k);
            order.Add(k);
            return nonce;
        }

        public bool Consume(ulong requester, ulong nonce, string ip)
        {
            Prune();
            string k = Key(requester, ip);
            Entry e;
            if (!pending.TryGetValue(k, out e)) return false;
            if (e.Nonce != nonce || e.Ip != ip || e.ExpiresNs <= Proto.NowNs()) return false;
            pending.Remove(k);
            order.Remove(k);
            return true;
        }

        /// <summary>只查不消费：确认「我没有你的密钥」确实来自刚被质询的那台机器、那个 IP。</summary>
        public bool HasPending(ulong requester, string ip)
        {
            Entry e;
            if (!pending.TryGetValue(Key(requester, ip), out e)) return false;
            return e.ExpiresNs > Proto.NowNs();
        }

        public void Cancel(ulong requester)
        {
            List<string> dead = new List<string>();
            string prefix = requester.ToString() + "|";
            foreach (string k in pending.Keys) if (k.StartsWith(prefix)) dead.Add(k);
            foreach (string k in dead) { pending.Remove(k); order.Remove(k); }
        }

        public void Cancel(ulong requester, string ip)
        {
            string k = Key(requester, ip);
            pending.Remove(k);
            order.Remove(k);
        }

        private void Prune()
        {
            long now = Proto.NowNs();
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, Entry> kv in pending) if (kv.Value.ExpiresNs <= now) dead.Add(kv.Key);
            foreach (string k in dead) { pending.Remove(k); order.Remove(k); }
        }
    }

    internal sealed class PullDenyList
    {
        private const double Cooldown = 30;
        private const int MaxFailures = 5;
        private const double FailureWindow = 30;
        private readonly Dictionary<string, long> until = new Dictionary<string, long>();
        private readonly Dictionary<string, int> failures = new Dictionary<string, int>();
        private readonly Dictionary<string, long> firstFail = new Dictionary<string, long>();

        private const int MaxEntries = 128;

        public int Count { get { return until.Count + failures.Count; } }

        public void Deny(string ip)
        {
            until[ip] = Proto.NowNs() + (long)(Cooldown * 1e9);
            Prune();
        }

        /// 过期条目清掉；仍超过上限就丢掉最早的一批。
        private void Prune()
        {
            long now = Proto.NowNs();
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, long> kv in until) if (kv.Value <= now) dead.Add(kv.Key);
            foreach (string k in dead) until.Remove(k);
            dead.Clear();
            foreach (KeyValuePair<string, long> kv in firstFail)
                if ((now - kv.Value) / 1e9 > FailureWindow) dead.Add(kv.Key);
            foreach (string k in dead) { firstFail.Remove(k); failures.Remove(k); }
            if (until.Count > MaxEntries)
            {
                List<KeyValuePair<string, long>> all = new List<KeyValuePair<string, long>>(until);
                all.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
                until.Clear();
                for (int i = 0; i < MaxEntries && i < all.Count; i++) until[all[i].Key] = all[i].Value;
            }
            if (failures.Count > MaxEntries)
            {
                // 失败计数太多：丢掉最老的一批（不影响冷却表）。
                List<string> keys = new List<string>(failures.Keys);
                for (int i = 0; i < keys.Count - MaxEntries; i++) { failures.Remove(keys[i]); firstFail.Remove(keys[i]); }
            }
        }

        /// 记一次凭据失败；连续 5 次（30 秒窗口内）才冷却 30 秒。
        public bool NoteFailure(string ip)
        {
            long now = Proto.NowNs();
            long first;
            if (!firstFail.TryGetValue(ip, out first) || (now - first) / 1e9 > FailureWindow)
            {
                firstFail[ip] = now;
                failures[ip] = 0;
            }
            int n;
            failures.TryGetValue(ip, out n);
            n++;
            failures[ip] = n;
            if (n >= MaxFailures)
            {
                failures.Remove(ip);
                firstFail.Remove(ip);
                Deny(ip);
                return true;
            }
            return false;
        }

        public bool IsCooling(string ip)
        {
            long t;
            if (!until.TryGetValue(ip, out t)) return false;
            return t > Proto.NowNs();
        }

        public void Clear(string ip) { until.Remove(ip); failures.Remove(ip); firstFail.Remove(ip); }
    }

    internal sealed class TrustDevice
    {
        public ulong Id;
        public ulong Key;
        public string Name = "";
        public long LastSeenNs;
    }

    /// 受信任设备与拉流密钥。存 %APPDATA%\AudioBridge\trust.ini，一行一条：
    /// device|id|key|name 与 key|senderId|key。没有 System.Web 可用，所以不写 JSON。
    internal sealed class TrustStore
    {
        private readonly List<TrustDevice> devices = new List<TrustDevice>();
        private readonly Dictionary<ulong, ulong> keys = new Dictionary<ulong, ulong>();
        private readonly object gate = new object();
        private readonly string path;

        public TrustStore()
            : this(System.IO.Path.Combine(DefaultDirectory(), "trust.ini"))
        {
        }

        /// 指定文件位置（自动化测试用，不碰真实的 trust.ini）。
        public TrustStore(string filePath)
        {
            try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath)); } catch { }
            path = filePath;
            Load();
        }

        private static string DefaultDirectory()
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioBridge");
            try { System.IO.Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        public List<TrustDevice> Snapshot()
        {
            lock (gate)
            {
                List<TrustDevice> list = new List<TrustDevice>();
                foreach (TrustDevice d in devices)
                {
                    TrustDevice c = new TrustDevice();
                    c.Id = d.Id; c.Key = d.Key; c.Name = d.Name; c.LastSeenNs = d.LastSeenNs;
                    list.Add(c);
                }
                list.Sort(delegate(TrustDevice a, TrustDevice b) { return a.Name.CompareTo(b.Name); });
                return list;
            }
        }

        public bool Verify(ulong id, ulong key)
        {
            if (id == 0 || key == 0) return false;
            lock (gate)
            {
                foreach (TrustDevice d in devices)
                {
                    if (d.Id != id) continue;
                    if (d.Key == key) { d.LastSeenNs = Proto.NowNs(); return true; }
                    return false;
                }
            }
            return false;
        }

        public ulong Grant(ulong id, string name)
        {
            ulong key;
            lock (gate)
            {
                key = 0;
                foreach (TrustDevice d in devices)
                {
                    if (d.Id != id) continue;
                    d.LastSeenNs = Proto.NowNs();
                    if (!string.IsNullOrEmpty(name)) d.Name = name;
                    key = d.Key;
                    break;
                }
                if (key == 0)
                {
                    TrustDevice n = new TrustDevice();
                    n.Id = id;
                    n.Name = name ?? "";
                    n.Key = NewKey();
                    n.LastSeenNs = Proto.NowNs();
                    devices.Add(n);
                    key = n.Key;
                }
            }
            Save();
            return key;
        }

        private static ulong NewKey()
        {
            // 这把密钥是拉流鉴权的唯一凭据，不能用 System.Random（可预测）。
            return PullAuth.RandomNonzero();
        }

        public void Revoke(ulong id)
        {
            lock (gate) devices.RemoveAll(delegate(TrustDevice d) { return d.Id == id; });
            Save();
        }

        public void RevokeAll()
        {
            lock (gate) devices.Clear();
            Save();
        }

        public void Rename(ulong id, string name)
        {
            lock (gate)
            {
                foreach (TrustDevice d in devices)
                {
                    if (d.Id != id) continue;
                    d.Name = name;
                    d.LastSeenNs = Proto.NowNs();
                }
            }
            Save();
        }

        public ulong KeyForDevice(ulong id)
        {
            lock (gate)
            {
                foreach (TrustDevice d in devices) if (d.Id == id) return d.Key;
            }
            return 0;
        }

        public ulong KeyForSender(ulong id)
        {
            lock (gate)
            {
                ulong k;
                return keys.TryGetValue(id, out k) ? k : 0;
            }
        }

        public void SetKey(ulong id, ulong key)
        {
            if (id == 0 || key == 0) return;
            lock (gate) keys[id] = key;
            Save();
        }

        public void ForgetSender(ulong id)
        {
            lock (gate) keys.Remove(id);
            Save();
        }

        private void Load()
        {
            try
            {
                if (!System.IO.File.Exists(path)) return;
                foreach (string raw in System.IO.File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] f = line.Split('|');
                    if (f.Length >= 3 && f[0] == "device")
                    {
                        TrustDevice d = new TrustDevice();
                        ulong.TryParse(f[1], out d.Id);
                        ulong.TryParse(f[2], out d.Key);
                        d.Name = f.Length > 3 ? f[3] : "";
                        if (d.Id != 0 && d.Key != 0) devices.Add(d);
                    }
                    else if (f.Length >= 3 && f[0] == "key")
                    {
                        ulong id, k;
                        if (ulong.TryParse(f[1], out id) && ulong.TryParse(f[2], out k) && id != 0)
                            keys[id] = k;
                    }
                }
            }
            catch { }
        }

        public void Save()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("# 音频桥：受信任设备与拉流密钥\n");
            lock (gate)
            {
                foreach (TrustDevice d in devices)
                {
                    string name = (d.Name ?? "").Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ');
                    sb.Append("device|").Append(d.Id).Append('|').Append(d.Key).Append('|').Append(name).Append('\n');
                }
                foreach (KeyValuePair<ulong, ulong> kv in keys)
                {
                    sb.Append("key|").Append(kv.Key).Append('|').Append(kv.Value).Append('\n');
                }
            }
            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                string tmp = path + ".tmp";
                System.IO.File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (System.IO.File.Exists(path)) System.IO.File.Replace(tmp, path, null);
                else System.IO.File.Move(tmp, path);
            }
            catch { }
        }
    }

    /// 登录自启：HKCU\...\Run，不需要管理员权限。
    internal static class Autostart
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "AudioBridge";

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                    {
                        return k != null && k.GetValue(ValueName) != null;
                    }
                }
                catch { return false; }
            }
        }

        public static void SetEnabled(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (k == null) return;
                    if (on)
                    {
                        string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        k.SetValue(ValueName, "\"" + exe + "\" --background");
                    }
                    else
                    {
                        k.DeleteValue(ValueName, false);
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>一次拉流会话的身份：令牌 + 目标地址，两端一致（对应 Mac 的 PullSession）。</summary>
    internal sealed class PullSessionState
    {
        public ulong Token;
        public string Ip = "";
        public int Port;

        public bool Active { get { return Token != 0; } }

        /// <summary>收到（可能重发的）拉流请求后，会话该怎么更新、要不要重启发送。</summary>
        public static PullSessionState Adopt(PullSessionState current, string ip, int port, ulong token,
                                             bool alreadySendingSame, out bool restart)
        {
            PullSessionState next = new PullSessionState();
            next.Token = token;
            next.Ip = ip;
            next.Port = port;
            restart = !alreadySendingSame;
            return next;
        }
    }

    /// <summary>发现信标没有凭据，能不能跟着它换地址（对应 Mac 的 BeaconPolicy）。</summary>
    internal static class BeaconPolicy
    {
        public static bool ShouldFollow(string currentIp, string beaconIp, bool isPullSession, double lastReplyAge)
        {
            if (isPullSession) return false;
            if (beaconIp == currentIp) return true;
            return lastReplyAge >= 2.5;
        }
    }

    /// <summary>内部重启后要不要继续拉流（对应 Mac 的 applyRunState(preservePull:)）。</summary>
    internal static class ResumePolicy
    {
        public static bool ShouldResend(bool preservePull, PullState pull, string pullIp)
        {
            if (!preservePull) return false;
            if (pull == PullState.None) return false;
            return pullIp != null && pullIp.Length > 0;
        }
    }

    /// <summary>确认弹窗的统一入口：异常按拒绝、置顶状态一定复原（可注入委托测试）。</summary>
    internal static class PullDialogGuard
    {
        public static bool Run(Func<string, string, bool> ask, string title, string text,
                               Action<bool> setTopMost, Action<string, Exception> log)
        {
            if (setTopMost != null) setTopMost(true);
            try
            {
                return ask != null && ask(title, text);
            }
            catch (Exception ex)
            {
                if (log != null) log("确认弹窗失败", ex);
                return false;
            }
            finally
            {
                if (setTopMost != null) setTopMost(false);
            }
        }
    }

    /// <summary>被拉流之前的推送目标（用户自己选的），拉流结束后恢复。</summary>
    internal sealed class TargetMemory
    {
        public readonly bool UseManual;
        public readonly ulong Id;
        public readonly string Name;
        public readonly string Ip;
        public readonly int Port;

        public TargetMemory(bool useManual, ulong id, string name, string ip, int port)
        {
            UseManual = useManual; Id = id; Name = name ?? ""; Ip = ip ?? ""; Port = port;
        }
    }
}

namespace AudioBridge
{
    /// <summary>单路模式下「要不要把当前来源让给新来的拉流方」的判断（纯函数，便于自测）。</summary>
    internal static class AdmitPolicy
    {
        public static bool ShouldYield(string currentIp, string incomingIp, string priorityIp, bool mixEnabled)
        {
            if (mixEnabled) return false;
            if (string.IsNullOrEmpty(priorityIp)) return false;
            if (incomingIp != priorityIp) return false;
            return currentIp != incomingIp;
        }
    }
}
