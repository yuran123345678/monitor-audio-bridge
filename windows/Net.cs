// 音频桥 · Windows 端 · 网络层（接收 / 发送 / 设备发现）
//
// 与 Mac 端 NetReceiver.swift / NetSender.swift / Discovery.swift 行为一致。
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace AudioBridge
{
    internal sealed class ReceiverStats
    {
        public int Packets, Lost, Late, Resyncs, Recovered;
        public int SourceCount;
        public bool Mixing;
        public double Rate = 48000;
        public string Sender = "";
        public string SenderName = "";
        public double JitterMs;
        public double GapMaxMs;
        public long LastHeardNs;   // 0 = 从未
        public long LastAudioNs;
        public bool Running;

        public ReceiverStats Clone() { return (ReceiverStats)MemberwiseClone(); }
    }

    /// UDP 收包 → 解析 → 写进 RingBridge；锁定一个发送端、认保活/再见、定期发回执。
    /// 混音模式下的一路来源（给界面显示用）。
    internal sealed class SourceInfo
    {
        public string Ip = "";
        public string Name = "";
        public double Rate = 48000;
        public uint Packets;
        public uint Lost;
        public uint Recovered;
        public double JitterMs;
        public long LastHeardNs;
    }

    internal sealed class NetReceiver
    {
        private const double LinkTimeoutSec = 2.5;
        private const int MaxMixedSources = 4;

        private readonly RingBridge bridge;
        private readonly int port;
        private Socket sock;
        private Thread thread;
        private volatile bool running;

        private readonly byte[] rx = new byte[65536];
        private readonly float[] scratch = new float[4096 * 2];

        /// 一路发送端的全部状态。按来源 IP 索引：单路模式下只有一台，
        /// 混音模式下最多 MaxMixedSources 台各自独立计时、独立重采样。
        private sealed class Source
        {
            public IPEndPoint Addr;
            public string Name = "";
            public uint NextSeq;
            public bool HaveSeq;
            public bool HaveMinDelta;
            public long MinTscDelta;
            public double LinkEma, JitterEma;
            public long LastHeard;
            public long LastAudioArrival;
            public readonly double[] GapBuckets = new double[10];
            public int GapBucketIndex;
            public long GapBucketStart;
            public double Rate;
            public float LastL, LastR;
            public RingBridge Ring;
            public uint Packets, Lost, Recovered, Late, Resyncs;
        }

        private readonly Dictionary<string, Source> sources = new Dictionary<string, Source>();
        private readonly Dictionary<string, long> others = new Dictionary<string, long>();
        private string primaryKey = "";
        private MixBus mixBus;
        private volatile bool mixEnabled;

        private readonly object gate = new object();
        private ReceiverStats state = new ReceiverStats();

        public Func<double> BufferMs;
        public Func<double> TargetMs;
        public Func<bool> Muted;
        public Action<double> RateChanged;

        public NetReceiver(RingBridge bridge, int port)
        {
            this.bridge = bridge;
            this.port = port;
        }

        public ReceiverStats Stats { get { lock (gate) return state.Clone(); } }
        public bool IsRunning { get { return running; } }

        /// 混音开关必须在 Start() 之前设置：它决定播放端是否固定 48 kHz，以及有没有混音器。
        public bool MixEnabled { get { return mixEnabled; } set { mixEnabled = value; } }

        public int MixedChannelCount { get { MixBus b = mixBus; return b == null ? 0 : b.ChannelCount; } }

        /// 混音器（给播放线程直连；混音关闭时为 null）。
        public MixBus Mixer { get { return mixEnabled ? mixBus : null; } }

        /// 混音器的目标缓冲（毫秒）。混音时比单路更怕抖动，调用方会给一个略小的值。
        public void SetMixTarget(double ms)
        {
            MixBus b = mixBus;
            if (b != null) b.SetTarget(ms);
        }

        public bool IsLinked
        {
            get
            {
                lock (gate)
                {
                    if (!state.Running || state.LastHeardNs == 0) return false;
                    return (Proto.NowNs() - state.LastHeardNs) / 1e9 < LinkTimeoutSec;
                }
            }
        }

        public bool IsStreaming
        {
            get
            {
                lock (gate)
                {
                    if (!state.Running || state.LastAudioNs == 0) return false;
                    return (Proto.NowNs() - state.LastAudioNs) / 1e9 < 0.6;
                }
            }
        }

        /// 来源表里有没有这个 IP（保活包也算）。拉流是否连上用它判断。
        public bool HasSource(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return false;
            lock (gate)
            {
                foreach (Source s in sources.Values)
                    if (s.Addr != null && s.Addr.Address.ToString() == ip) return true;
            }
            return false;
        }

        /// 主动放掉对某个 IP 的锁定（接收端换拉流目标时用）。
        /// 只记一个请求，真正的 Drop 在网络线程的 Tick 里做——sources 只归网络线程改。
        private volatile string releaseRequest;
        public void ReleaseSource(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return;
            releaseRequest = ip;
        }

        /// 主动拉流时优先放行的 IP：单路模式下会让出别人占着的锁。
        private volatile string priorityIp;
        public void Prioritize(string ip)
        {
            priorityIp = ip ?? "";
        }

        private void ApplyRelease()
        {
            string ip = releaseRequest;
            if (ip == null) return;
            releaseRequest = null;
            string key = null;
            foreach (KeyValuePair<string, Source> kv in sources)
                if (kv.Value.Addr != null && kv.Value.Addr.Address.ToString() == ip) { key = kv.Key; break; }
            if (key != null) Drop(key);
        }

        public List<SourceInfo> SourceInfos()
        {
            List<SourceInfo> list = new List<SourceInfo>();
            lock (gate)
            {
                foreach (Source s in sources.Values)
                {
                    SourceInfo i = new SourceInfo();
                    i.Ip = s.Addr != null ? s.Addr.Address.ToString() : "";
                    i.Name = s.Name;
                    i.Rate = s.Rate;
                    i.Packets = s.Packets;
                    i.Lost = s.Lost;
                    i.Recovered = s.Recovered;
                    i.JitterMs = s.JitterEma;
                    i.LastHeardNs = s.LastHeard;
                    list.Add(i);
                }
            }
            return list;
        }

        public void Start()
        {
            Stop();
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                try { s.ReceiveBufferSize = 4 << 20; } catch { }
                Proto.DisableConnReset(s);
                s.Bind(new IPEndPoint(IPAddress.Any, port));
                s.ReceiveTimeout = 250;
            }
            catch
            {
                try { s.Close(); } catch { }
                throw;
            }
            sock = s;

            // 混音器必须先于收包就绪：第一包若在它建好前到达，这一路的缓冲会被缓存成主缓冲，
            // 之后它就一直绕过混音器（Mac 端实测过：混音输出全零）。
            if (mixEnabled) mixBus = new MixBus();

            lock (gate)
            {
                lock (gate) sources.Clear();
                others.Clear();
                primaryKey = "";
                state = new ReceiverStats();
                state.Running = true;
            }
            running = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "ab-recv";
            thread.Start();
        }

        public void Stop()
        {
            if (!running) { return; }
            running = false;
            Thread t = thread;
            if (t != null && t != Thread.CurrentThread) t.Join(1000);
            thread = null;
            Socket s = sock;
            sock = null;
            if (s != null)
            {
                lock (gate)
                {
                    foreach (Source src in sources.Values)
                    {
                        if (src.Addr == null) continue;
                        try { byte[] bye = Proto.Bye(0); s.SendTo(bye, src.Addr); s.SendTo(bye, src.Addr); } catch { }
                    }
                }
                try { s.Close(); } catch { }
            }
            MixBus bus = mixBus;
            mixBus = null;
            if (bus != null) bus.Stop();
            lock (gate)
            {
                lock (gate) sources.Clear();
                others.Clear();
                primaryKey = "";
                state = new ReceiverStats();
            }
        }

        private void Loop()
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            long lastTick = Proto.NowNs();
            while (running)
            {
                Socket s = sock;
                if (s == null) break;
                int n = 0;
                try
                {
                    n = s.ReceiveFrom(rx, ref from);
                }
                catch (SocketException ex)
                {
                    if (!running) break;
                    if (ex.SocketErrorCode != SocketError.TimedOut) Thread.Sleep(20);
                    n = 0;
                }
                catch (ObjectDisposedException) { break; }

                if (n > 0) Handle(n, (IPEndPoint)from);

                long now = Proto.NowNs();
                if (now - lastTick > 500000000L)
                {
                    lastTick = now;
                    Tick(now);
                }
            }
        }

        private Source Find(IPEndPoint ep)
        {
            Source s;
            return sources.TryGetValue(ep.Address.ToString(), out s) ? s : null;
        }

        /// 来源准入。返回 null 表示这包不归我们管（已回「正忙」）。
        private Source Admit(IPEndPoint from, long now)
        {
            string key = from.Address.ToString();
            // 主动拉流：新来源优先，先让出别人占着的单路锁，否则「正在被 X 推送」时永远拉不进 S。
            if (primaryKey.Length > 0 && sources.ContainsKey(primaryKey)
                && AdmitPolicy.ShouldYield(primaryKey, key, priorityIp ?? "", mixEnabled))
            {
                Drop(primaryKey);
            }
            Source cur;
            if (sources.TryGetValue(key, out cur))
            {
                // 同一台机器换了源端口＝对方程序重启了：序号和缓冲都要重来。
                if (!cur.Addr.Equals(from))
                {
                    cur.Addr = from;
                    cur.HaveSeq = false;
                    cur.HaveMinDelta = false;
                    cur.LastAudioArrival = 0;
                    if (cur.Ring != null) cur.Ring.Reset();
                    if (!mixEnabled) bridge.Reset();
                }
                cur.LastHeard = now;
                return cur;
            }
            if (mixEnabled)
            {
                if (sources.Count >= MaxMixedSources)
                {
                    ThrottleBusy(key, from, now);
                    return null;
                }
                Source ns = new Source();
                ns.Addr = from;
                ns.LastHeard = now;
                lock (gate) sources[key] = ns;
                primaryKey = key;
                Refresh(key);
                return ns;
            }
            // 单路：锁定一台。别的设备在超时前一律拒绝（并限速回一个「正忙」）。
            if (primaryKey.Length > 0)
            {
                Source primary;
                if (sources.TryGetValue(primaryKey, out primary))
                {
                    double silentFor = (now - primary.LastHeard) / 1e9;
                    if (silentFor < LinkTimeoutSec)
                    {
                        ThrottleBusy(key, from, now);
                        return null;
                    }
                    Drop(primaryKey);
                }
            }
            Source s = new Source();
            s.Addr = from;
            s.LastHeard = now;
            sources[key] = s;
            primaryKey = key;
            bridge.Reset();
            Refresh(key);
            return s;
        }

        private void ThrottleBusy(string key, IPEndPoint from, long now)
        {
            long last;
            if (others.TryGetValue(key, out last) && now - last <= 1000000000L) return;
            others[key] = now;
            SendReport(from, true, null);
        }

        private void Drop(string key)
        {
            Source s;
            if (sources.TryGetValue(key, out s))
            {
                lock (gate) sources.Remove(key);
                MixBus bus = mixBus;
                if (bus != null) bus.Remove(key);
            }
            if (primaryKey == key) primaryKey = HasAnySource() ? FirstKey() : "";
            Refresh("");
        }

        private bool HasAnySource()
        {
            lock (gate) return sources.Count > 0;
        }

        private string FirstKey()
        {
            lock (gate)
            {
                foreach (string k in sources.Keys) return k;
                return "";
            }
        }

        private void Handle(int count, IPEndPoint from)
        {
            if (!Proto.HasMagic(rx, count)) return;
            byte type = rx[4];
            long now = Proto.NowNs();

            if (type == Proto.TypeBye)
            {
                Source bye = Find(from);
                if (bye != null) Drop(from.Address.ToString());
                return;
            }
            if (type == Proto.TypeKeepalive)
            {
                if (count < 33) return;
                Source ks = Admit(from, now);
                if (ks == null) return;
                string name = Proto.GetName(rx, 32, count);
                if (name.Length > 0) ks.Name = name;
                ks.LastHeard = now;
                Refresh(from.Address.ToString());
                return;
            }
            if (type != Proto.TypeAudio || count < Proto.HeaderSize) return;

            int ch = rx[5];
            double rate = Proto.U32(rx, 6);
            uint seq = Proto.U32(rx, 10);
            int frames = Proto.U16(rx, 14);
            // 最高字节留给冗余标志（header 24 字节全用满了），时间戳按 56 位读。
            ulong tsc = Proto.U64(rx, 16) & 0x00FFFFFFFFFFFFFFUL;

            if (ch < 1 || ch > 2 || rate < 8000 || rate > 384000 || frames <= 0 || frames > 4096) return;
            int payloadBytes = frames * ch * 2;
            if (count < Proto.HeaderSize + payloadBytes) return;

            Source s = Admit(from, now);
            if (s == null) return;

            bool hasRedundant = (rx[23] & Proto.AudioFlagRedundant) != 0
                && count >= Proto.HeaderSize + payloadBytes * 2;

            bool rateChanged = false;
            if (rate != s.Rate)
            {
                if (s.Rate != 0)
                {
                    if (s.Ring != null) s.Ring.Reset();
                    if (!mixEnabled) bridge.Reset();
                    s.HaveSeq = false;
                }
                s.Rate = rate;
                MixBus bus = mixBus;
                if (bus != null) bus.SetRate(from.Address.ToString(), rate);
                rateChanged = true;
            }

            if (tsc > 0)
            {
                // 两台机器的时钟零点不同，差值本身没意义，只看它相对最小值的波动。
                long delta = unchecked(now - (long)tsc);
                if (!s.HaveMinDelta || delta < s.MinTscDelta) { s.MinTscDelta = delta; s.HaveMinDelta = true; }
                double ms = (delta - s.MinTscDelta) / 1000000.0;
                if (s.LinkEma == 0) s.LinkEma = ms; else s.LinkEma += (ms - s.LinkEma) * 0.05;
                s.JitterEma += (Math.Abs(ms - s.LinkEma) - s.JitterEma) * 0.05;
            }

            if (s.LastAudioArrival != 0)
            {
                double gap = (now - s.LastAudioArrival) / 1000000.0;
                if (gap < 400)
                {
                    if (now - s.GapBucketStart > 1000000000L)
                    {
                        s.GapBucketStart = now;
                        s.GapBucketIndex = (s.GapBucketIndex + 1) % s.GapBuckets.Length;
                        s.GapBuckets[s.GapBucketIndex] = 0;
                    }
                    if (gap > s.GapBuckets[s.GapBucketIndex]) s.GapBuckets[s.GapBucketIndex] = gap;
                }
            }
            s.LastAudioArrival = now;

            int lost = 0, resyncs = 0, recovered = 0;
            if (!s.HaveSeq)
            {
                s.HaveSeq = true;
            }
            else
            {
                uint delta = unchecked(seq - s.NextSeq);
                if (delta == 0)
                {
                }
                else if (delta <= 64)
                {
                    if (hasRedundant)
                    {
                        // 包尾带着上一包（序号 seq-1）：它原样补回来、不算丢包；
                        // delta 表示从 NextSeq 到 seq 缺了几包，扣掉被副本补回的这 1 包，剩下的才隐藏。
                        int missing = (int)delta - 1;
                        if (missing > 0)
                        {
                            Conceal(s, missing * frames);
                            lost += missing;
                        }
                        Convert(s, Proto.HeaderSize + payloadBytes, frames, ch);
                        RingBridge ring = Sink(s);
                        if (ring != null) ring.Push(scratch, 0, frames);
                        recovered = 1;
                    }
                    else
                    {
                        lost = (int)delta;
                        Conceal(s, lost * frames);
                    }
                }
                else if (delta >= 0xFFFFFF00u)
                {
                    s.Late++;
                    s.LastHeard = now;
                    Refresh(from.Address.ToString());
                    return;
                }
                else
                {
                    resyncs = 1;
                }
            }
            s.NextSeq = unchecked(seq + 1);

            Convert(s, Proto.HeaderSize, frames, ch);
            RingBridge target = Sink(s);
            if (target != null) target.Push(scratch, 0, frames);

            s.Packets++;
            s.Lost += (uint)lost;
            s.Resyncs += (uint)resyncs;
            s.Recovered += (uint)recovered;
            s.LastHeard = now;
            Refresh(from.Address.ToString());

            if (rateChanged && !mixEnabled)
            {
                Action<double> cb = RateChanged;
                if (cb != null) cb(rate);
            }
        }

        private RingBridge Sink(Source s)
        {
            if (!mixEnabled) return bridge;
            if (s.Ring != null) return s.Ring;
            MixBus bus = mixBus;
            if (bus == null) return bridge;   // bus 还没就绪：先走主缓冲，别把主缓冲缓存进这一路
            s.Ring = bus.Add(s.Addr.Address.ToString(), s.Rate > 0 ? s.Rate : 48000);
            return s.Ring;
        }

        private void Tick(long now)
        {
            ApplyRelease();
            List<string> dead = new List<string>();
            List<Source> live = new List<Source>();
            lock (gate)
            {
                foreach (KeyValuePair<string, Source> kv in sources)
                {
                    if ((now - kv.Value.LastHeard) / 1e9 > LinkTimeoutSec) dead.Add(kv.Key);
                    else live.Add(kv.Value);
                }
            }
            foreach (string k in dead) Drop(k);
            foreach (Source s in live) SendReport(s.Addr, false, s);
            Refresh("");
        }

        private void SendReport(IPEndPoint to, bool busy, Source s)
        {
            Socket sockNow = sock;
            if (sockNow == null || to == null) return;
            double bufferMs = BufferMs != null ? BufferMs() : 0;
            // 混音模式下每一路按自己的采样率换算，不能用主缓冲（主缓冲已不喂数据）。
            if (mixEnabled && s != null && s.Ring != null && s.Rate > 0)
                bufferMs = s.Ring.LastFillFrames / s.Rate * 1000.0;
            double targetMs = TargetMs != null ? TargetMs() : 0;
            bool muted = Muted != null && Muted();
            uint packets = 0, lost = 0;
            double jitter = 0;
            if (s != null)
            {
                packets = s.Packets;
                lost = s.Lost;
                jitter = s.JitterEma;
            }
            byte[] b = new byte[33 + 61];
            Proto.PutMagic(b, Proto.TypeReport);
            byte flags = 0;
            if (busy)
            {
                flags |= Proto.FlagBusy;
            }
            else
            {
                // 混音模式下主缓冲不喂数据，要按这一路自己的 ring 判断「对方在放」。
                RingBridge sink = Sink(s);
                if (sink != null && sink.IsPrimed) flags |= Proto.FlagPlaying;
                if (muted) flags |= Proto.FlagMuted;
            }
            b[5] = flags;
            Proto.PutU16(b, 6, (int)Math.Max(0, Math.Min(65535, bufferMs)));
            Proto.PutU32(b, 8, busy ? 0u : packets);
            Proto.PutU32(b, 12, busy ? 0u : lost);
            Proto.PutU32(b, 16, (uint)Math.Max(0, Math.Min(4e9, jitter * 1000)));
            Proto.PutU16(b, 20, (int)Math.Max(0, Math.Min(65535, targetMs)));
            Proto.PutU64(b, 24, Proto.InstanceId);
            int end = Proto.PutName(b, 32, Proto.DeviceName);
            try { sockNow.SendTo(b, 0, end, SocketFlags.None, to); } catch { }
        }

        /// 把某一路（或主路）的状态汇总进对外快照。key 为空时按 primaryKey 取。
        private void Refresh(string key)
        {
            // 混音模式：各路交替报「最近一个包」会让状态行闪来闪去，只发布主路统计。
            if (mixEnabled) key = primaryKey;
            double gapMax;
            lock (gate)
            {
                if (key.Length == 0) key = primaryKey;
                Source s;
                if (key.Length > 0 && sources.TryGetValue(key, out s))
                {
                    state.Sender = s.Addr != null ? s.Addr.Address.ToString() : "";
                    state.SenderName = s.Name;
                    state.Rate = s.Rate;
                    state.Packets = (int)s.Packets;
                    state.Lost = (int)s.Lost;
                    state.Late = (int)s.Late;
                    state.Resyncs = (int)s.Resyncs;
                    state.Recovered = (int)s.Recovered;
                    state.JitterMs = s.JitterEma;
                    gapMax = 0;
                    for (int i = 0; i < s.GapBuckets.Length; i++) if (s.GapBuckets[i] > gapMax) gapMax = s.GapBuckets[i];
                    state.GapMaxMs = gapMax;
                    state.LastHeardNs = s.LastHeard;
                    state.LastAudioNs = s.LastAudioArrival;
                }
                else if (sources.Count == 0)
                {
                    state.Sender = "";
                    state.SenderName = "";
                    state.LastHeardNs = 0;
                    state.LastAudioNs = 0;
                    state.Packets = 0;
                    state.Lost = 0;
                    state.Recovered = 0;
                }
                state.SourceCount = sources.Count;
                state.Mixing = mixEnabled;
            }
        }

        /// 丢包隐藏：保持最后一帧电平并线性淡出（约 5 ms）。
        private void Conceal(Source s, int gapFrames)
        {
            if (gapFrames <= 0) return;
            RingBridge ring = Sink(s);
            if (ring == null) return;
            int done = 0;
            while (done < gapFrames)
            {
                int n = Math.Min(gapFrames - done, scratch.Length / 2);
                for (int i = 0; i < n; i++)
                {
                    float g = Math.Max(0f, 1f - (done + i) / 240f);
                    scratch[i * 2] = s.LastL * g;
                    scratch[i * 2 + 1] = s.LastR * g;
                }
                ring.Push(scratch, 0, n);
                done += n;
            }
            s.LastL = 0;
            s.LastR = 0;
        }

        private void Convert(Source s, int offset, int frames, int ch)
        {
            if (ch == 2)
            {
                for (int i = 0; i < frames * 2; i++)
                {
                    short v = (short)(rx[offset + i * 2] | (rx[offset + i * 2 + 1] << 8));
                    scratch[i] = v / 32768f;
                }
            }
            else
            {
                for (int i = 0; i < frames; i++)
                {
                    short v = (short)(rx[offset + i * 2] | (rx[offset + i * 2 + 1] << 8));
                    float f = v / 32768f;
                    scratch[i * 2] = f;
                    scratch[i * 2 + 1] = f;
                }
            }
            s.LastL = scratch[(frames - 1) * 2];
            s.LastR = scratch[(frames - 1) * 2 + 1];
        }
    }

    /// 多路混音总线：每路有自己的 RingBridge（各自做时钟伺服），每 5 ms 取出来相加再写进主缓冲。
    /// 多路混音：每路一个 RingBridge（各自做时钟伺服），播放线程每次要多少帧就逐路取多少帧，
    /// 相加、限幅后直接交给声卡。不再有 5 ms 定时器和主缓冲：定时器与播放时钟不同步，
    /// 门槛贴着伺服目标时会周期性跳过一拍、重放上一块，听感是持续的断点。
    internal sealed class MixBus
    {
        private sealed class Channel
        {
            public RingBridge Ring;
            public double Rate;
            public int Fade;
            public int FadeTotal;
        }

        private readonly Dictionary<string, Channel> channels = new Dictionary<string, Channel>();
        /// <summary>已从 channels 摘掉、但还要再响 5 ms 淡出的通道。</summary>
        private readonly List<Channel> dying = new List<Channel>();
        private readonly object gate = new object();
        private double targetMs = 25;
        private double lastFillFrames;
        private float[] tmp = new float[Proto.FramesPerPacket * 2];

        public int ChannelCount { get { lock (gate) return channels.Count; } }
        public double LastFillFrames { get { lock (gate) return lastFillFrames; } }

        public void Stop()
        {
            lock (gate)
            {
                channels.Clear();
                dying.Clear();
                lastFillFrames = 0;
            }
        }

        public RingBridge Add(string key, double rate)
        {
            lock (gate)
            {
                Channel c;
                if (channels.TryGetValue(key, out c)) return c.Ring;
                c = new Channel();
                c.Rate = rate > 0 ? rate : 48000;
                c.Ring = new RingBridge(1.5, 48000);
                channels[key] = c;
                return c.Ring;
            }
        }

        public void Remove(string key)
        {
            lock (gate)
            {
                Channel c;
                if (!channels.TryGetValue(key, out c)) return;
                channels.Remove(key);
                c.FadeTotal = 240;
                c.Fade = 240;
                dying.Add(c);
            }
        }

        public void SetRate(string key, double rate)
        {
            lock (gate)
            {
                Channel c;
                if (!channels.TryGetValue(key, out c)) return;
                if (c.Rate != rate && rate > 0)
                {
                    c.Rate = rate;
                    c.Ring.Reset();
                }
            }
        }

        public void SetTarget(double ms)
        {
            lock (gate) targetMs = Math.Max(5, Math.Min(120, ms));
        }

        private void EnsureTmp(int frames)
        {
            if (tmp.Length >= frames * 2) return;
            tmp = new float[frames * 2];
        }

        /// <summary>播放线程调用：把 frames 帧交织双声道混好写进 dst（唯一的取数入口）。</summary>
        public void Pull(float[] dst, int frames, double outRate)
        {
            List<Channel> list;
            double target;
            lock (gate)
            {
                list = new List<Channel>(channels.Count + dying.Count);
                foreach (Channel c in channels.Values) list.Add(c);
                list.AddRange(dying);
                target = targetMs;
            }
            for (int i = 0; i < frames * 2; i++) dst[i] = 0f;
            EnsureTmp(frames);
            foreach (Channel c in list)
            {
                c.Ring.Pull(tmp, frames, outRate, c.Rate, target / 1000.0 * c.Rate);
                if (c.FadeTotal > 0)
                {
                    float total = c.FadeTotal;
                    for (int i = 0; i < frames; i++)
                    {
                        float g = (c.Fade - i) / total;
                        if (g < 0f) g = 0f; else if (g > 1f) g = 1f;
                        dst[i * 2] += tmp[i * 2] * g;
                        dst[i * 2 + 1] += tmp[i * 2 + 1] * g;
                    }
                    c.Fade -= frames;
                }
                else
                {
                    for (int i = 0; i < frames * 2; i++) dst[i] += tmp[i];
                }
            }
            // 两路满幅叠加会削顶：超过 ±1 就压回来。
            for (int i = 0; i < frames * 2; i++)
            {
                float v = dst[i];
                if (v > 1f) dst[i] = 1f;
                else if (v < -1f) dst[i] = -1f;
            }
            lock (gate)
            {
                if (dying.Count > 0) dying.RemoveAll(IsFaded);
                double fill = 0;
                foreach (Channel c in channels.Values)
                    if (c.Ring.AvailableFrames > fill) fill = c.Ring.AvailableFrames;
                lastFillFrames = fill;
            }
        }

        private static bool IsFaded(Channel c) { return c.Fade <= 0; }
    }

    internal sealed class SenderReport
    {
        public long AtNs;          // 0 = 没收到过
        public string Name = "";
        public ulong PeerId;
        public bool Playing, Muted, Busy;
        public uint Packets, Lost;
        public double JitterMs, BufferMs, TargetMs;
    }

    internal sealed class SenderStats
    {
        public long Packets;
        public double Rate;
        public long StartedNs;
        public long LastAudioNs;
        public SenderReport Report = new SenderReport();
    }

    /// 把交织双声道切成 5 ms 一包发出去；发保活、收回执、停止时发再见。
    internal sealed class NetSender
    {
        private Socket sock;
        private Thread thread;
        private volatile bool running;
        private readonly object gate = new object();
        private IPEndPoint target;
        private SenderStats state = new SenderStats();
        private float peakL, peakR;

        // 只在采集线程上用
        private readonly byte[] packet = new byte[Proto.HeaderSize + Proto.FramesPerPacket * 4 * 2];
        private readonly byte[] prevPayload = new byte[Proto.FramesPerPacket * 4];
        private bool prevValid;
        private double prevRate;
        private volatile bool redundancy;
        /// <summary>界面线程只写 Redundancy；采集线程在每个包边界自己切到 activeRedundancy。
        /// 绝不在 setter 里清 filled——那是采集线程的状态，跨线程改会让下标越过 packet 缓冲。</summary>
        private bool activeRedundancy;
        public bool Redundancy
        {
            get { return redundancy; }
            set { redundancy = value; }
        }
        private int filled;
        private uint seq;
        private double rate = 48000;
        private long silentFrames;

        public bool IsRunning { get { return running; } }

        public SenderStats Stats
        {
            get
            {
                lock (gate)
                {
                    SenderStats c = new SenderStats();
                    c.Packets = state.Packets;
                    c.Rate = state.Rate;
                    c.StartedNs = state.StartedNs;
                    c.LastAudioNs = state.LastAudioNs;
                    c.Report = state.Report;
                    return c;
                }
            }
        }

        public void TakePeaks(out float l, out float r)
        {
            lock (gate) { l = peakL; r = peakR; peakL = 0; peakR = 0; }
        }

        public string TargetDescription
        {
            get
            {
                lock (gate) return target == null ? "" : target.Address + ":" + target.Port;
            }
        }

        public void Start(IPEndPoint to)
        {
            Stop();
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                s.SendBufferSize = 1 << 20;
                Proto.DisableConnReset(s);
                s.Bind(new IPEndPoint(IPAddress.Any, 0));
                s.ReceiveTimeout = 250;
            }
            catch
            {
                s.Close();
                throw;
            }
            sock = s;
            lock (gate)
            {
                target = to;
                state = new SenderStats();
                state.StartedNs = Proto.NowNs();
            }
            filled = 0;
            running = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "ab-send-ctl";
            thread.Start();
        }

        public void Retarget(IPEndPoint to)
        {
            lock (gate) target = to;
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            Thread t = thread;
            if (t != null && t != Thread.CurrentThread) t.Join(1000);
            thread = null;
            Socket s = sock;
            sock = null;
            IPEndPoint ep;
            lock (gate) { ep = target; target = null; }
            if (s != null)
            {
                if (ep != null)
                {
                    try { byte[] bye = Proto.Bye(0); s.SendTo(bye, ep); s.SendTo(bye, ep); } catch { }
                }
                try { s.Close(); } catch { }
            }
        }

        /// 采集线程调用：src 为交织双声道。
        public void Push(float[] src, int frames, double newRate)
        {
            Socket s = sock;
            if (!running || s == null || frames <= 0) return;
            IPEndPoint dst;
            lock (gate) dst = target;
            if (dst == null) return;

            // 只在包边界切换冗余：正在攒的半包按原来的帧数攒完，不会出现 filled 大于 per 的越界。
            if (filled == 0 && activeRedundancy != redundancy)
            {
                activeRedundancy = redundancy;
                prevValid = false;
            }
            int per = activeRedundancy ? Proto.RedundantFramesPerPacket : Proto.FramesPerPacket;

            if (newRate != rate) { rate = newRate; filled = 0; }

            // 持续 0.5 s 的数字静音就不再发音频包（保活照发），和 Mac 端行为一致。
            bool quiet = true;
            for (int i = 0; i < frames * 2; i++) { if (src[i] != 0f) { quiet = false; break; } }
            if (quiet)
            {
                silentFrames += frames;
                if (silentFrames > rate * 0.5) { filled = 0; return; }
            }
            else
            {
                silentFrames = 0;
            }

            float pl = 0, pr = 0;
            int sent = 0;
            for (int i = 0; i < frames; i++)
            {
                float l = src[i * 2], r = src[i * 2 + 1];
                float al = l < 0 ? -l : l, ar = r < 0 ? -r : r;
                if (al > pl) pl = al;
                if (ar > pr) pr = ar;
                if (l > 1f) l = 1f; else if (l < -1f) l = -1f;
                if (r > 1f) r = 1f; else if (r < -1f) r = -1f;
                short sl = (short)(l * 32767f);
                short sr = (short)(r * 32767f);
                if (filled >= per)
                {
                    int nframes = filled;
                    Proto.PutMagic(packet, Proto.TypeAudio);
                    packet[5] = 2;
                    Proto.PutU32(packet, 6, (uint)rate);
                    Proto.PutU32(packet, 10, seq);
                    Proto.PutU16(packet, 14, (ushort)nframes);
                    Proto.PutU64(packet, 16, (ulong)Proto.NowNs());
                    int len = Proto.HeaderSize + nframes * 4;
                    bool withCopy = activeRedundancy && prevValid && prevRate == rate;
                    if (withCopy)
                    {
                        // 包尾再带一份上一包的 PCM：对端丢一包可以直接补回，不用做隐藏。
                        packet[23] = Proto.AudioFlagRedundant;
                        Buffer.BlockCopy(prevPayload, 0, packet, Proto.HeaderSize + nframes * 4, nframes * 4);
                        len += nframes * 4;
                    }
                    else
                    {
                        packet[23] = 0;
                    }
                    try { s.SendTo(packet, 0, len, SocketFlags.None, dst); sent++; } catch { }
                    Buffer.BlockCopy(packet, Proto.HeaderSize, prevPayload, 0, nframes * 4);
                    prevValid = true;
                    prevRate = rate;
                    seq = unchecked(seq + 1);
                    filled = 0;
                }
                int o = Proto.HeaderSize + filled * 4;
                packet[o] = (byte)(sl & 0xFF); packet[o + 1] = (byte)((sl >> 8) & 0xFF);
                packet[o + 2] = (byte)(sr & 0xFF); packet[o + 3] = (byte)((sr >> 8) & 0xFF);
                filled++;
            }
            lock (gate)
            {
                if (pl > peakL) peakL = pl;
                if (pr > peakR) peakR = pr;
                state.Packets += sent;
                state.Rate = rate;
                if (sent > 0) state.LastAudioNs = Proto.NowNs();
            }
        }

        private void Loop()
        {
            byte[] rx = new byte[2048];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            long lastKeep = 0;
            while (running)
            {
                Socket s = sock;
                if (s == null) break;
                long now = Proto.NowNs();
                if (now - lastKeep > 500000000L)
                {
                    lastKeep = now;
                    IPEndPoint dst;
                    lock (gate) dst = target;
                    if (dst != null)
                    {
                        int len;
                        byte[] k = Proto.Keepalive((uint)rate, seq, out len);
                        try { s.SendTo(k, 0, len, SocketFlags.None, dst); } catch { }
                    }
                }
                int n = 0;
                try
                {
                    n = s.ReceiveFrom(rx, ref from);
                }
                catch (SocketException ex)
                {
                    if (!running) break;
                    if (ex.SocketErrorCode != SocketError.TimedOut) Thread.Sleep(20);
                    n = 0;
                }
                catch (ObjectDisposedException) { break; }
                if (n <= 0 || !Proto.HasMagic(rx, n)) continue;

                if (rx[4] == Proto.TypeReport && n >= 33)
                {
                    SenderReport r = new SenderReport();
                    r.AtNs = Proto.NowNs();
                    byte flags = rx[5];
                    r.Playing = (flags & Proto.FlagPlaying) != 0;
                    r.Muted = (flags & Proto.FlagMuted) != 0;
                    r.Busy = (flags & Proto.FlagBusy) != 0;
                    r.BufferMs = Proto.U16(rx, 6);
                    r.Packets = Proto.U32(rx, 8);
                    r.Lost = Proto.U32(rx, 12);
                    r.JitterMs = Proto.U32(rx, 16) / 1000.0;
                    r.TargetMs = Proto.U16(rx, 20);
                    r.PeerId = Proto.U64(rx, 24);
                    r.Name = Proto.GetName(rx, 32, n);
                    lock (gate) state.Report = r;
                }
                else if (rx[4] == Proto.TypeBye)
                {
                    lock (gate) state.Report = new SenderReport();
                }
            }
        }
    }

    internal sealed class Peer
    {
        public ulong Id;
        public string Name = "";
        public string Ip = "";
        public int Port;
        public byte Role, Platform, State;
        public byte Caps;
        /// <summary>对方信标里报的协议版本；不同版本不能互连（音频包结构对不上）。</summary>
        public byte Version;
        public long LastSeenNs;

        public bool IsReceiver { get { return Role == Proto.RoleReceiver; } }
        public bool IsSender { get { return Role == Proto.RoleSender; } }
        public bool AllowsPull { get { return (Caps & Proto.CapPull) != 0; } }
        public bool Mixing { get { return (Caps & Proto.CapMix) != 0; } }
        public bool VersionMismatch { get { return Version != 0 && Version != Proto.Version; } }
        public string PlatformLabel
        {
            get { return Platform == Proto.PlatformMac ? "Mac" : (Platform == Proto.PlatformWindows ? "Windows" : "设备"); }
        }

        public string Signature()
        {
            return Id + "|" + Name + "|" + Ip + "|" + Port + "|" + Role + "|" + State + "|" + Caps + "|" + Version;
        }
    }

    /// 设备发现：每秒往各网卡广播地址发信标，同时听别人的。
    internal sealed class Discovery
    {
        private Socket sock;
        private Thread thread;
        private volatile bool running;
        private readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        private readonly object gate = new object();
        private string lastSignature = "";
        private volatile bool announce;

        public volatile byte Role = Proto.RoleIdle;
        public volatile byte State = 0;
        public volatile byte Caps = 0;
        public int AudioPort = Proto.AudioPort;

        /// 绑哪个发现端口。默认 47832；自动化测试可以改，避免和正在跑的实例抢端口。
        public int ListenPort = Proto.DiscoveryPort;

        /// 设备表有变化（在发现线程上回调，界面要自己切回 UI 线程）。
        public Action Changed;

        /// 收到发现端口上的控制包（拉流请求/应答/再见）：type, data, fromIp。
        public Action<byte, byte[], string> Message;

        /// 往对方的发现端口发一个控制包（拉流/应答/再见）。
        public void SendControl(byte[] data, int length, string ip, int port)
        {
            try
            {
                using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.SendTo(data, 0, length, SocketFlags.None, new IPEndPoint(IPAddress.Parse(ip), port));
                }
            }
            catch { }
        }

        public List<Peer> Peers
        {
            get
            {
                lock (gate)
                {
                    List<Peer> list = new List<Peer>(peers.Values);
                    list.Sort(delegate(Peer a, Peer b)
                    {
                        int c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                        return c != 0 ? c : a.Id.CompareTo(b.Id);
                    });
                    return list;
                }
            }
        }

        public void Start()
        {
            Stop();
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                s.EnableBroadcast = true;
                Proto.DisableConnReset(s);
                s.Bind(new IPEndPoint(IPAddress.Any, ListenPort));
                s.ReceiveTimeout = 250;
            }
            catch
            {
                // 发现端口绑不上不致命：手动地址仍然可用。
                try { s.Close(); } catch { }
                return;
            }
            sock = s;
            running = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "ab-discovery";
            thread.Start();
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            Thread t = thread;
            if (t != null && t != Thread.CurrentThread) t.Join(1000);
            thread = null;
            Socket s = sock;
            sock = null;
            if (s != null) { try { s.Close(); } catch { } }
        }

        public void AnnounceNow() { announce = true; }

        private void Loop()
        {
            byte[] rx = new byte[2048];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            long lastBeacon = 0;
            long lastIfScan = 0;
            List<IPAddress> bcast = new List<IPAddress>();
            while (running)
            {
                Socket s = sock;
                if (s == null) break;
                long now = Proto.NowNs();
                if (announce || now - lastBeacon > 1000000000L)
                {
                    announce = false;
                    lastBeacon = now;
                    if (bcast.Count == 0 || now - lastIfScan > 5000000000L)
                    {
                        lastIfScan = now;
                        bcast = LocalNet.BroadcastAddresses();
                    }
                    byte[] b = new byte[21 + 61];
                    Proto.PutMagic(b, Proto.TypeBeacon);
                    b[5] = Proto.Version;
                    b[6] = Role;
                    b[7] = Proto.PlatformWindows;
                    b[8] = State;
                    b[9] = Caps;
                    Proto.PutU16(b, 10, AudioPort);
                    Proto.PutU64(b, 12, Proto.InstanceId);
                    int end = Proto.PutName(b, 20, Proto.DeviceName);
                    foreach (IPAddress a in bcast)
                    {
                        try { s.SendTo(b, 0, end, SocketFlags.None, new IPEndPoint(a, ListenPort)); } catch { }
                    }
                    Expire(now);
                }

                int n = 0;
                try
                {
                    n = s.ReceiveFrom(rx, ref from);
                }
                catch (SocketException ex)
                {
                    if (!running) break;
                    if (ex.SocketErrorCode != SocketError.TimedOut) Thread.Sleep(20);
                    n = 0;
                }
                catch (ObjectDisposedException) { break; }

                if (n > 0 && Proto.HasMagic(rx, n)
                    && (rx[4] == Proto.TypePull || rx[4] == Proto.TypePullReply
                        || rx[4] == Proto.TypePullChallenge || rx[4] == Proto.TypePullAuth
                        || rx[4] == Proto.TypePullNoKey
                        || rx[4] == Proto.TypeBye))
                {
                    byte[] copy = new byte[n];
                    Buffer.BlockCopy(rx, 0, copy, 0, n);
                    Action<byte, byte[], string> msg = Message;
                    if (msg != null)
                    {
                        try { msg(rx[4], copy, ((IPEndPoint)from).Address.ToString()); } catch { }
                    }
                    continue;
                }

                if (n >= 21 && Proto.HasMagic(rx, n) && rx[4] == Proto.TypeBeacon)
                {
                    ulong id = Proto.U64(rx, 12);
                    if (id == 0 || id == Proto.InstanceId) continue;
                    Peer p = new Peer();
                    p.Id = id;
                    p.Ip = ((IPEndPoint)from).Address.ToString();
                    p.Name = Proto.GetName(rx, 20, n);
                    if (p.Name.Length == 0) p.Name = p.Ip;
                    p.Port = Proto.U16(rx, 10);
                    p.Role = rx[6];
                    p.Platform = rx[7];
                    p.State = rx[8];
                    p.Caps = rx[9];
                    p.Version = rx[5];
                    p.LastSeenNs = Proto.NowNs();
                    lock (gate) peers[id] = p;
                    Publish();
                }
            }
        }

        private void Expire(long now)
        {
            List<ulong> dead = new List<ulong>();
            lock (gate)
            {
                foreach (KeyValuePair<ulong, Peer> kv in peers)
                {
                    if ((now - kv.Value.LastSeenNs) / 1e9 > 4.5) dead.Add(kv.Key);
                }
                foreach (ulong id in dead) peers.Remove(id);
            }
            Publish();
        }

        private void Publish()
        {
            string sig = "";
            foreach (Peer p in Peers) sig += p.Signature() + ";";
            if (sig == lastSignature) return;
            lastSignature = sig;
            Action cb = Changed;
            if (cb != null) { try { cb(); } catch { } }
        }
    }
}
