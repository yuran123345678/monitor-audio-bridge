// 音频桥 · Windows 端 · 入口
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AudioBridge
{
    internal static class Program
    {
        public const string VersionText = "3.0.2";

        public static bool SnapshotMode;

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static int Main(string[] args)
        {
            bool background = Array.IndexOf(args, "--background") >= 0;

            // 界面预览：AudioBridge.exe --snapshot 输出目录（明/暗各一套 PNG，不显示窗口、不碰音频和网络）
            int si = Array.IndexOf(args, "--snapshot");
            if (si >= 0 && si + 1 < args.Length)
            {
                SnapshotMode = true;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Directory.CreateDirectory(args[si + 1]);
                foreach (string kind in new string[] { "receiver-live", "receiver-wait", "sender-live", "sender-warn", "off" })
                {
                    for (int dark = 0; dark <= 1; dark++)
                    {
                        Theme.Force = dark;
                        Theme.Load();
                        AppModel m = new AppModel();
                        m.LoadDemo(kind);
                        using (MainForm f = new MainForm(m, true))
                            f.Snapshot(Path.Combine(args[si + 1], kind + (dark == 1 ? "-dark" : "-light") + ".png"));
                    }
                }
                return 0;
            }

            // 对测模式（不碰声卡）：和另一端的实现互相收发，验证协议一致。结果写进文件（本程序没有控制台）。
            //   --selfcheck send HOST PORT 秒 结果文件     发 440 Hz
            //   --selfcheck recv PORT 秒 结果文件          收包并模拟播放端拉取
            int ci = Array.IndexOf(args, "--selfcheck");
            if (ci >= 0)
            {
                try { return SelfCheck(args, ci + 1); }
                catch (Exception ex) { Log(ex); return 3; }
            }

            // 单实例：第二次启动只负责把已有窗口叫出来。
            bool created;
            Mutex mutex = new Mutex(true, "AudioBridge.SingleInstance", out created);
            EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "AudioBridge.ShowWindow");
            if (!created)
            {
                showEvent.Set();
                return 0;
            }

            Application.ThreadException += delegate(object o, ThreadExceptionEventArgs e) { Log(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object o, UnhandledExceptionEventArgs e) { Log(e.ExceptionObject as Exception); };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Settings.Load();
            Proto.InitIdentity();
            Theme.Load();

            AppModel model = new AppModel();
            MainForm form = new MainForm(model, background);
            model.Start();

            Thread waiter = new Thread(delegate()
            {
                while (true)
                {
                    showEvent.WaitOne();
                    try { form.BeginInvoke((Action)delegate { form.ShowMain(); }); } catch { }
                }
            });
            waiter.IsBackground = true;
            waiter.Start();

            Application.Run(form);
            GC.KeepAlive(mutex);
            return 0;
        }

        private sealed class RedundancyResult
        {
            public int Lost, Recovered, Late;
            public double Frames;
            public bool LateStable;
        }

        /// <summary>合成一个音频包；trailing 为真时尾部再带一份上一包（序号 seq-1）的副本。</summary>
        private static byte[] RedundancyPacket(uint seq, float v, bool trailing)
        {
            const int frames = 240;
            byte[] b = new byte[24 + frames * 4 + (trailing ? frames * 4 : 0)];
            Proto.PutMagic(b, Proto.TypeAudio);
            b[5] = 2;
            Proto.PutU32(b, 6, 48000);
            Proto.PutU32(b, 10, seq);
            Proto.PutU16(b, 14, frames);
            short sv = (short)(v * 32000);
            for (int k = 0; k < frames; k++)
            {
                int o = 24 + k * 4;
                b[o] = (byte)(sv & 0xFF); b[o + 1] = (byte)((sv >> 8) & 0xFF);
                b[o + 2] = b[o]; b[o + 3] = b[o + 1];
            }
            if (trailing)
            {
                b[23] = Proto.AudioFlagRedundant;
                for (int k = 0; k < frames; k++)
                {
                    int o = 24 + frames * 4 + k * 4;
                    b[o] = (byte)(sv & 0xFF); b[o + 1] = (byte)((sv >> 8) & 0xFF);
                    b[o + 2] = b[o]; b[o + 3] = b[o + 1];
                }
            }
            return b;
        }

        private static RedundancyResult ProbeRedundancy(int port, uint gap)
        {
            RingBridge b = new RingBridge(2.0, 48000);
            NetReceiver rx = new NetReceiver(b, port);
            rx.Start();
            RedundancyResult r = new RedundancyResult();
            try
            {
                using (System.Net.Sockets.Socket s = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp))
                {
                    System.Net.IPEndPoint to = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.1"), port);
                    s.SendTo(RedundancyPacket(0, 0.1f, false), to);
                    s.SendTo(RedundancyPacket(1, 0.1f, false), to);
                    Thread.Sleep(80);
                    s.SendTo(RedundancyPacket(gap, 0.1f, true), to);
                    Thread.Sleep(150);
                    r.Lost = rx.Stats.Lost;
                    r.Recovered = rx.Stats.Recovered;
                    r.Frames = b.AvailableFrames;
                    // 再补一个迟到的原包（gap-1）：时间轴上已经补过，应计为迟到并丢弃。
                    s.SendTo(RedundancyPacket(gap - 1, 0.1f, false), to);
                    Thread.Sleep(100);
                    r.Late = rx.Stats.Late;
                    r.LateStable = Math.Abs(b.AvailableFrames - r.Frames) < 1;
                }
            }
            finally { rx.Stop(); }
            return r;
        }

        private static double MixToneMag(float[] buf, int frames, double hz)
        {
            double re = 0, im = 0;
            for (int i = 0; i < frames; i++)
            {
                double w = 2 * Math.PI * hz * i / 48000.0;
                re += buf[i * 2] * Math.Cos(w);
                im += buf[i * 2] * Math.Sin(w);
            }
            double n = Math.Max(frames, 1);
            return 2 * Math.Sqrt(re * re + im * im) / n;
        }

        private static int SelfCheckMix(string outPath)
        {
            StringBuilder sb = new StringBuilder();
            int fails = 0;
            Action<bool, string> ok = delegate(bool pass, string what)
            {
                if (!pass) fails++;
                sb.AppendLine((pass ? "  OK  " : "  FAIL ") + what);
            };

            // 8a. 单路 3 s 1 kHz（幅度 0.49）：稳态不许有一拍偷停，RMS 应接近 0.346。
            MixBus bus = new MixBus();
            RingBridge chA = bus.Add("a", 48000);
            float[] bufA = new float[240 * 2];
            float[] outBuf = new float[240 * 2];
            double phase = 0;
            float rmsAcc = 0;
            int counted = 0, clicks = 0;
            bool havePrev = false;
            float prev = 0;
            for (int round = 0; round < 600; round++)
            {
                for (int i = 0; i < 240; i++)
                {
                    float v = (float)(Math.Sin(phase) * 0.49);
                    phase += 2 * Math.PI * 1000 / 48000;
                    bufA[i * 2] = v; bufA[i * 2 + 1] = v;
                }
                chA.Push(bufA, 0, 240);
                bus.Pull(outBuf, 240, 48000);
                if (round >= 100)
                {
                    for (int i = 0; i < 240; i++)
                    {
                        float v = outBuf[i * 2];
                        if (havePrev && Math.Abs(v - prev) > 0.12f) clicks++;
                        prev = v; havePrev = true;
                        rmsAcc += v * v; counted++;
                    }
                }
            }
            double rms = Math.Sqrt(rmsAcc / Math.Max(counted, 1));
            ok(bus.ChannelCount == 1, "混音器接住一路");
            ok(clicks == 0, "稳态 2 s 内没有波形断点（" + clicks + " 处）");
            ok(rms > 0.33 && rms < 0.36, "单路混音电平正确（RMS " + rms.ToString("0.000") + "）");

            // 8b. 只停音频、保活照旧：继续取数 0.5 s，之后必须静音，不能循环重放最后一块。
            float tailAcc = 0;
            int tailCount = 0;
            for (int round = 0; round < 100; round++)
            {
                bus.Pull(outBuf, 240, 48000);
                if (round >= 60)
                {
                    for (int i = 0; i < 240 * 2; i++) tailAcc += outBuf[i] * outBuf[i];
                    tailCount += 240 * 2;
                }
            }
            double tailRms = Math.Sqrt(tailAcc / Math.Max(tailCount, 1));
            ok(tailRms < 0.001, "断流 0.5 s 后输出静音（RMS " + tailRms.ToString("0.00000") + "）");
            bus.Stop();

            // 8c. 44.1 kHz 与 48 kHz 两路同时发：两路的频率成分都要在。
            MixBus bus2 = new MixBus();
            RingBridge chC = bus2.Add("c", 44100);
            RingBridge chD = bus2.Add("d", 48000);
            float[] bufC = new float[240 * 2];
            float[] bufD = new float[240 * 2];
            List<float> collected = new List<float>();
            double phaseC = 0, phaseD = 0, carryC = 0;
            for (int round = 0; round < 500; round++)
            {
                carryC += 0.005 * 44100;
                int nC = (int)Math.Floor(carryC);
                carryC -= nC;
                for (int i = 0; i < nC; i++)
                {
                    float v = (float)(Math.Sin(phaseC) * 0.3);
                    phaseC += 2 * Math.PI * 440 / 44100;
                    bufC[i * 2] = v; bufC[i * 2 + 1] = v;
                }
                chC.Push(bufC, 0, nC);
                for (int i = 0; i < 240; i++)
                {
                    float v = (float)(Math.Sin(phaseD) * 0.3);
                    phaseD += 2 * Math.PI * 660 / 48000;
                    bufD[i * 2] = v; bufD[i * 2 + 1] = v;
                }
                chD.Push(bufD, 0, 240);
                bus2.Pull(outBuf, 240, 48000);
                if (collected.Count > 48000 * 2) collected.RemoveRange(0, 24000 * 2);
                for (int i = 0; i < 240 * 2; i++) collected.Add(outBuf[i]);
            }
            float[] coll = collected.ToArray();
            double mag440 = MixToneMag(coll, coll.Length / 2, 440);
            double mag660 = MixToneMag(coll, coll.Length / 2, 660);
            ok(mag440 > 0.05 && mag660 > 0.05,
               "44.1k 与 48k 两路成分都在（440 Hz " + mag440.ToString("0.00") + " / 660 Hz " + mag660.ToString("0.00") + "）");
            bus2.Stop();

            sb.AppendLine(fails == 0 ? "PASS 全部通过" : "FAIL " + fails + " 项未通过");
            File.WriteAllText(outPath, sb.ToString());
            return fails == 0 ? 0 : 1;
        }

        private static int SelfCheck(string[] a, int i)
        {
            Settings.Load();
            Proto.InitIdentity();
            string mode = a[i];
            if (mode == "send")
            {
                string host = a[i + 1];
                int port = int.Parse(a[i + 2]);
                double secs = double.Parse(a[i + 3], System.Globalization.CultureInfo.InvariantCulture);
                string outPath = a[i + 4];
                NetSender sender = new NetSender();
                sender.Start(new System.Net.IPEndPoint(System.Net.IPAddress.Parse(host), port));
                float[] buf = new float[480 * 2];
                double phase = 0;
                long start = Proto.NowNs(), next = start;
                while ((next - start) / 1e9 < secs)
                {
                    for (int k = 0; k < 480; k++)
                    {
                        float v = (float)(Math.Sin(phase) * 0.2);
                        phase += 2 * Math.PI * 440 / 48000;
                        buf[k * 2] = v; buf[k * 2 + 1] = v;
                    }
                    sender.Push(buf, 480, 48000);
                    next += 10000000L;
                    long wait = (next - Proto.NowNs()) / 1000000L;
                    if (wait > 0) Thread.Sleep((int)wait);
                }
                SenderStats st = sender.Stats;
                SenderReport rep = st.Report;
                bool fresh = rep.AtNs != 0 && (Proto.NowNs() - rep.AtNs) / 1e9 < 2;
                File.WriteAllText(outPath, "已发 " + st.Packets + " 包；" + (fresh
                    ? "对方「" + rep.Name + "」回执：收 " + rep.Packets + " 丢 " + rep.Lost + " 缓冲 " + (int)rep.BufferMs + " ms 目标 " + (int)rep.TargetMs + " ms"
                    : "对方无回执") + Environment.NewLine);
                sender.Stop();
                return fresh ? 0 : 1;
            }
            if (mode == "recv")
            {
                int port = int.Parse(a[i + 1]);
                double secs = double.Parse(a[i + 2], System.Globalization.CultureInfo.InvariantCulture);
                string outPath = a[i + 3];
                RingBridge bridge = new RingBridge(2.0, 48000);
                NetReceiver receiver = new NetReceiver(bridge, port);
                receiver.BufferMs = delegate { return bridge.LastFillFrames / 48.0; };
                receiver.TargetMs = delegate { return 40; };
                receiver.Muted = delegate { return false; };
                receiver.Start();
                Discovery disc = new Discovery();
                disc.Role = Proto.RoleReceiver;
                disc.AudioPort = port;
                disc.Start();
                float[] outBuf = new float[480 * 2];
                // 发送端停 2.5 s 后这一路会被撤掉、stats 清零；留一份最热闹时的快照给结果文件。
                ReceiverStats best = receiver.Stats.Clone();
                float peak = 0, prev = 0;
                int crossings = 0, loud = 0;
                long start = Proto.NowNs(), next = start;
                while ((next - start) / 1e9 < secs)
                {
                    ReceiverStats now = receiver.Stats;
                    if (now.Packets > best.Packets) best = now.Clone();
                    double rate = now.Rate;
                    bridge.Pull(outBuf, 480, 48000, rate, 0.04 * rate);
                    for (int k = 0; k < 480; k++)
                    {
                        float v = outBuf[k * 2];
                        if (Math.Abs(v) > peak) peak = Math.Abs(v);
                        if (Math.Abs(v) > 0.01f) loud++;
                        if (prev <= 0 && v > 0) crossings++;
                        prev = v;
                    }
                    next += 10000000L;
                    long wait = (next - Proto.NowNs()) / 1000000L;
                    if (wait > 0) Thread.Sleep((int)wait);
                }
                ReceiverStats rs = best;
                double hz = loud > 4800 ? crossings / (loud / 48000.0) : 0;
                string text = "收 " + rs.Packets + " 包 丢 " + rs.Lost + " 迟 " + rs.Late + " · 来自「" + rs.SenderName + "」" + rs.Sender
                    + " · " + (int)rs.Rate + " Hz · 峰值 " + peak.ToString("0.000") + " · 音高约 " + hz.ToString("0") + " Hz" + Environment.NewLine + "发现的设备：";
                foreach (Peer p in disc.Peers) text += p.Name + "[" + p.PlatformLabel + " 角色" + p.Role + " " + p.Ip + ":" + p.Port + "] ";
                File.WriteAllText(outPath, text + Environment.NewLine);
                receiver.Stop();
                disc.Stop();
                return rs.Packets > 0 ? 0 : 1;
            }
            if (mode == "mix")
            {
                // 不碰声卡：只验混音器的取数、电平与断流静音。--selfcheck mix 结果文件
                return SelfCheckMix(a[i + 1]);
            }
            if (mode == "capture")
            {
                // 不碰 WASAPI：只验「请求 pid / 生效 pid / 是否回退」的决策。--selfcheck capture 结果文件
                return SelfCheckCapture(a[i + 1]);
            }
            if (mode == "stage1")
            {
                // 不碰声卡/网络：只验本轮 A-1..A-5 的纯逻辑与真实调用路径上的判断。
                return SelfCheckStage1(a[i + 1]);
            }
            if (mode == "stage2")
            {
                // 不碰声卡：只验第二轮审查修复的纯逻辑与真实调用路径。--selfcheck stage2 结果文件
                return SelfCheckStage2(a[i + 1]);
            }
            if (mode == "stress")
            {
                // 不碰声卡：收包线程与界面线程并发查/释放来源，跑 2 s 不抛异常。
                return SelfCheckStress(a[i + 1]);
            }
            if (mode == "redundancy")
            {
                // 不碰声卡：只验包尾副本的补包计数。--selfcheck redundancy 结果文件
                string outPath = a[i + 1];
                StringBuilder sb = new StringBuilder();
                int fails = 0;
                Action<bool, string> ok = delegate(bool pass, string what)
                {
                    if (!pass) fails++;
                    sb.AppendLine((pass ? "  OK  " : "  FAIL ") + what);
                };
                RedundancyResult g1 = ProbeRedundancy(47860, 3);
                ok(g1.Frames == 960 && g1.Lost == 0 && g1.Recovered == 1 && g1.LateStable,
                   "缺 1 包：副本补回、不记丢包（" + (int)g1.Frames + " 帧 / lost " + g1.Lost + "）");
                RedundancyResult g2 = ProbeRedundancy(47861, 4);
                ok(g2.Frames == 1200 && g2.Lost == 1 && g2.Recovered == 1 && g2.LateStable,
                   "缺 2 包：副本补 1 包、隐藏 1 包（" + (int)g2.Frames + " 帧 / lost " + g2.Lost + "）");
                RedundancyResult g3 = ProbeRedundancy(47862, 5);
                ok(g3.Frames == 1440 && g3.Lost == 2 && g3.Recovered == 1 && g3.LateStable,
                   "缺 3 包：副本补 1 包、隐藏 2 包（" + (int)g3.Frames + " 帧 / lost " + g3.Lost + "）");
                sb.AppendLine(fails == 0 ? "PASS 全部通过" : "FAIL " + fails + " 项未通过");
                File.WriteAllText(outPath, sb.ToString());
                return fails == 0 ? 0 : 1;
            }
            if (mode == "control")
            {
                // 不碰声卡：只验控制面（拉流请求/应答）与受信任设备表。--selfcheck control 结果文件
                string outPath = a[i + 1];
                StringBuilder sb = new StringBuilder();
                int fails = 0;
                Action<bool, string> ok = delegate(bool pass, string what)
                {
                    if (!pass) fails++;
                    sb.AppendLine((pass ? "  OK  " : "  FAIL ") + what);
                };

                Discovery da = new Discovery();
                Discovery db = new Discovery();
                da.ListenPort = Proto.DiscoveryPort + 5;
                db.ListenPort = Proto.DiscoveryPort + 6;
                da.Role = Proto.RoleReceiver;
                db.Role = Proto.RoleSender;
                byte[] gotPull = null, gotReply = null, gotChallenge = null, gotAuth = null;
                db.Message = delegate(byte type, byte[] data, string ip)
                {
                    if (type == Proto.TypePull) gotPull = data;
                    if (type == Proto.TypePullAuth) gotAuth = data;
                };
                da.Message = delegate(byte type, byte[] data, string ip)
                {
                    if (type == Proto.TypePullReply) gotReply = data;
                    if (type == Proto.TypePullChallenge) gotChallenge = data;
                };
                da.Start();
                db.Start();
                System.Threading.Thread.Sleep(300);

                int len;
                byte[] pull = Proto.Pull(0, Proto.AudioPort, out len);
                da.SendControl(pull, len, "127.0.0.1", db.ListenPort);
                System.Threading.Thread.Sleep(400);
                if (gotPull != null && gotPull.Length >= 25)
                {
                    int port = Proto.U16(gotPull, 6);
                    ulong requester = Proto.U64(gotPull, 16);
                    string requesterName = Proto.GetName(gotPull, 24, gotPull.Length);
                    ok(port == Proto.AudioPort && requester == Proto.InstanceId && requesterName.Length > 0,
                       "对方收到拉流请求（音频端口/设备号/名字）：" + port + " / " + requesterName);
                    ok(gotPull.Length < 25 + 61 && requesterName == Proto.DeviceName,
                       "v4 的拉流请求里不再带长期密钥");
                }
                else ok(false, "没有收到拉流请求");

                ulong cnonce = 0x0102030405060708UL;
                byte[] challenge = Proto.PullChallenge(Proto.InstanceId, cnonce, 0xAAAABBBBCCCCDDDDUL, out len);
                db.SendControl(challenge, len, "127.0.0.1", da.ListenPort);
                System.Threading.Thread.Sleep(400);
                ok(gotChallenge != null && gotChallenge.Length >= 33
                   && Proto.U64(gotChallenge, 24) == cnonce && Proto.U64(gotChallenge, 16) == 0xAAAABBBBCCCCDDDDUL,
                   "收到质询（随机数/发送端设备号）");

                ulong macKey = 0x1122334455667788UL;
                byte[] mac = PullAuth.Mac(macKey, cnonce, Proto.InstanceId, 0xAAAABBBBCCCCDDDDUL);
                byte[] auth = Proto.PullAuth(0xAAAABBBBCCCCDDDDUL, cnonce, mac, Proto.AudioPort, out len);
                da.SendControl(auth, len, "127.0.0.1", db.ListenPort);
                System.Threading.Thread.Sleep(400);
                ok(gotAuth != null && gotAuth.Length >= 49 && Proto.U64(gotAuth, 8) == cnonce
                   && Proto.U64(gotAuth, 24) == 0xAAAABBBBCCCCDDDDUL
                   && PullAuth.Verify(macKey, cnonce, Proto.InstanceId, 0xAAAABBBBCCCCDDDDUL, gotAuth, 32),
                   "收到鉴权包（随机数/设备号/MAC/音频端口）");

                byte[] reply = Proto.PullReply(Proto.InstanceId, Proto.PullOk, 0xDEAD000000000001UL, 0, out len);
                db.SendControl(reply, len, "127.0.0.1", da.ListenPort);
                System.Threading.Thread.Sleep(400);
                ok(gotReply != null && gotReply.Length >= 41 && gotReply[6] == Proto.PullOk
                   && Proto.U64(gotReply, 24) == 0xDEAD000000000001UL && (gotReply[7] & 2) != 0,
                   "收到应答（结果/会话令牌）");
                da.Stop();
                db.Stop();

                // —— 拉流鉴权（与 Mac 端 --nettest §11 对应）——
                ulong rq = 0x123456789ABCDEF0UL, sd = 0x0FEDCBA987654321UL;
                ulong nonce = 0xAAAA1111BBBB2222UL;
                byte[] gm = PullAuth.Mac(macKey, nonce, rq, sd);
                ok(PullAuth.Verify(macKey, nonce, rq, sd, gm, 0), "正确密钥算出的 MAC 通过验证");
                ok(!PullAuth.Verify(macKey + 1, nonce, rq, sd, gm, 0), "错误密钥算出的 MAC 被拒绝");
                ok(!PullAuth.Verify(macKey, nonce + 1, rq, sd, gm, 0), "换一个随机数后旧 MAC 失效（防重放）");
                ok(!PullAuth.Verify(0, nonce, rq, sd, gm, 0), "没有存过密钥时不接受任何 MAC");
                byte[] bad = (byte[])gm.Clone(); bad[0] ^= 0xFF;
                ok(!PullAuth.Verify(macKey, nonce, rq, sd, bad, 0), "篡改一个字节的 MAC 被拒绝");

                ulong st = 0x5555666677778888UL;
                byte[] goodBye = Proto.Bye(st);
                ok(PullAuth.ByeAccepted(goodBye, "192.168.1.20", "192.168.1.20", true, st), "合法再见被接受");
                byte[] shortBye = new byte[5];
                ok(!PullAuth.ByeAccepted(shortBye, "192.168.1.20", "192.168.1.20", true, st), "5 字节的再见包被拒绝而不是抛异常");
                ok(!PullAuth.ByeAccepted(goodBye, "192.168.1.99", "192.168.1.20", true, st), "来源 IP 不对的再见被拒绝");
                ok(!PullAuth.ByeAccepted(Proto.Bye(st + 1), "192.168.1.20", "192.168.1.20", true, st), "令牌不对的再见被拒绝");
                ok(!PullAuth.ByeAccepted(goodBye, "192.168.1.20", "192.168.1.20", false, st), "没在发送时再见不生效");
                ok(!PullAuth.ByeAccepted(Proto.Bye(0), "192.168.1.20", "192.168.1.20", true, st), "没有令牌的再见被拒绝");

                byte[] okReply = Proto.PullReply(rq, Proto.PullOk, st, 0, out len);
                ok(PullAuth.ReplyAccepted(okReply, "192.168.1.20", "192.168.1.20", rq, true), "来自请求目标的应答被接受");
                ok(!PullAuth.ReplyAccepted(okReply, "192.168.1.30", "192.168.1.20", rq, true), "别的 IP 伪造的应答被忽略");
                ok(!PullAuth.ReplyAccepted(okReply, "192.168.1.20", "192.168.1.20", rq, false), "已经连上/取消后，重放的旧应答被忽略");
                byte[] otherReply = Proto.PullReply(rq + 1, Proto.PullOk, st, 0, out len);
                ok(!PullAuth.ReplyAccepted(otherReply, "192.168.1.20", "192.168.1.20", rq, true), "不是发给本机的应答被忽略");

                ChallengeBook book = new ChallengeBook();
                ulong issued = book.Issue(rq, "10.0.0.5");
                ok(book.Consume(rq, issued, "10.0.0.5"), "质询用来源 IP 和随机数换到一次放行");
                ok(!book.Consume(rq, issued, "10.0.0.5"), "同一个质询不能二次使用（防重放）");
                ulong i2 = book.Issue(rq, "10.0.0.5");
                ok(!book.Consume(rq, i2, "10.0.0.6"), "换了来源 IP 的质询不成立");

                byte[] waitReply = Proto.PullReply(rq, Proto.PullWait, 0, 0, out len);
                ok(waitReply[6] == Proto.PullWait && waitReply.Length >= 41
                   && PullAuth.ReplyAccepted(waitReply, "192.168.1.20", "192.168.1.20", rq, true),
                   "「等待对方确认」的应答被认作仍在请求中（不按 20 秒判失败）");

                PullDenyList failList = new PullDenyList();
                for (int k = 0; k < 4; k++) failList.NoteFailure("10.0.0.31");
                ok(!failList.IsCooling("10.0.0.31"), "错 4 次还不冷却（不能让一次手误就封掉）");
                failList.NoteFailure("10.0.0.31");
                ok(failList.IsCooling("10.0.0.31"), "连续错 5 次才冷却 30 秒");

                PullDenyList dl = new PullDenyList();
                dl.Deny("10.0.0.9");
                ok(dl.IsCooling("10.0.0.9"), "被拒绝的来源 30 秒内直接回拒绝");
                ok(!dl.IsCooling("10.0.0.10"), "别的来源不受影响");

                // 冗余包缩到 120 帧：24 + 480 + 480 = 984 字节，接收端照收、副本照补。
                {
                    RingBridge rb = new RingBridge(2.0, 48000);
                    NetReceiver rx = new NetReceiver(rb, Proto.DiscoveryPort + 11);
                    rx.Start();
                    System.Net.Sockets.Socket sk = new System.Net.Sockets.Socket(
                        System.Net.Sockets.AddressFamily.InterNetwork,
                        System.Net.Sockets.SocketType.Dgram,
                        System.Net.Sockets.ProtocolType.Udp);
                    System.Net.IPEndPoint ep = new System.Net.IPEndPoint(
                        System.Net.IPAddress.Parse("127.0.0.1"), Proto.DiscoveryPort + 11);
                    byte[] p0 = RedundantPacket(Proto.RedundantFramesPerPacket, 0, false);
                    byte[] p2 = RedundantPacket(Proto.RedundantFramesPerPacket, 2, true);
                    ok(p0.Length == 504 && p2.Length == 984 && Proto.RedundantFramesPerPacket == 120,
                       "120 帧包 504 字节、带副本 984 字节（" + p0.Length + " / " + p2.Length + "）");
                    sk.SendTo(p0, ep);
                    sk.SendTo(p2, ep);
                    System.Threading.Thread.Sleep(200);
                    ok(Math.Abs(rb.AvailableFrames - 360) < 1, "接收端按包头帧数收下 3 包共 360 帧（" + rb.AvailableFrames + "）");
                    rx.Stop();
                    sk.Close();
                }

                // 受信任设备表：允许、验证、再来一次沿用密钥、落盘重读、移除。
                // 放在结果文件旁边：这个目录一定可写（结果文件自己就写在这里）。
                string tmp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath)),
                    "ab-trust-" + Guid.NewGuid().ToString("N") + ".ini");
                TrustStore ts = new TrustStore(tmp);
                ulong sid = 0x0BADF00D12345678UL;
                ulong k1 = ts.Grant(sid, "客厅 Windows");
                ok(k1 != 0 && ts.Verify(sid, k1), "允许后凭密钥可验证");
                ok(!ts.Verify(sid, k1 + 1), "错误密钥被拒绝");
                ulong k2 = ts.Grant(sid, "客厅 Windows 11");
                ok(k2 == k1 && ts.Snapshot().Count == 1, "再次允许沿用同一把密钥并更新名字");
                ts.SetKey(sid, 0xDEADBEEF00000001UL);
                TrustStore reload = new TrustStore(tmp);
                ok(reload.Snapshot().Count == 1 && reload.Snapshot()[0].Name == "客厅 Windows 11" && reload.Verify(sid, k1),
                   "写盘后重新读取，信任关系还在");
                ok(reload.KeyForSender(sid) == 0xDEADBEEF00000001UL, "接收端保存的发送端密钥也持久化");
                reload.Revoke(sid);
                ok(!reload.Verify(sid, k1) && reload.Snapshot().Count == 0, "移除后不再信任");
                try { System.IO.File.Delete(tmp); } catch { }

                sb.AppendLine(fails == 0 ? "PASS 全部通过" : "FAIL " + fails + " 项未通过");
                File.WriteAllText(outPath, sb.ToString());
                return fails == 0 ? 0 : 1;
            }

            return 2;
        }

        /// 造一个「每包 N 帧」的音频包；trailing 为 true 时尾部再带一份上一包的 PCM。
        private static byte[] RedundantPacket(int frames, uint seq, bool trailing)
        {
            int body = frames * 4 * (trailing ? 2 : 1);
            byte[] p = new byte[Proto.HeaderSize + body];
            Proto.PutMagic(p, Proto.TypeAudio);
            p[5] = 2;
            Proto.PutU32(p, 6, 48000);
            Proto.PutU32(p, 10, seq);
            Proto.PutU16(p, 14, (ushort)frames);
            Proto.PutU64(p, 16, (ulong)Proto.NowNs());
            p[23] = trailing ? Proto.AudioFlagRedundant : (byte)0;
            for (int i = 0; i < frames; i++)
            {
                short v = (short)(Math.Sin(i * 0.05) * 6000);
                int o = Proto.HeaderSize + i * 4;
                p[o] = (byte)(v & 0xFF); p[o + 1] = (byte)((v >> 8) & 0xFF);
                p[o + 2] = p[o]; p[o + 3] = p[o + 1];
                if (trailing)
                {
                    int o2 = Proto.HeaderSize + frames * 4 + i * 4;
                    p[o2] = p[o]; p[o2 + 1] = p[o + 1]; p[o2 + 2] = p[o]; p[o2 + 3] = p[o + 1];
                }
            }
            return p;
        }

        /// 按程序采集的决策：激活失败要回退到整机、回退后不反复重开、换选择才重开、名字找不到回整机。

        /// <summary>A-1..A-5 的自测：弹窗异常隔离、会话令牌一致、无密钥信号、信标跟随、唤醒保留拉流。</summary>
        private static int SelfCheckStage1(string outPath)
        {
            StringBuilder sb = new StringBuilder();
            int fails = 0;
            Action<bool, string> ok = delegate(bool pass, string what)
            {
                if (!pass) fails++;
                sb.AppendLine((pass ? "  OK  " : "  FAIL ") + what);
            };

            // A-1：弹窗委托抛异常 -> 按拒绝返回，且置顶状态一定被复原（true 之后必须回到 false）。
            StringBuilder trace = new StringBuilder();
            bool threw = false;
            try
            {
                bool allowed = PullDialogGuard.Run(
                    delegate(string t, string x) { throw new InvalidOperationException("boom"); },
                    "拉取请求", "某设备请求接收声音",
                    delegate(bool on) { trace.Append(on ? "1" : "0"); },
                    delegate(string w, Exception e) { });
                ok(!allowed && trace.ToString() == "10" && !threw,
                   "弹窗抛异常按拒绝处理，且置顶状态被复原（trace=" + trace + "）");
            }
            catch (Exception) { threw = true; }
            ok(!threw, "弹窗抛异常不会逃出调用方");
            StringBuilder trace2 = new StringBuilder();
            bool yes = PullDialogGuard.Run(delegate(string t, string x) { return true; },
                "拉取请求", "x", delegate(bool on) { trace2.Append(on ? "1" : "0"); }, null);
            ok(yes && trace2.ToString() == "10", "弹窗正常返回时置顶也复原");

            // A-2：重新拉流时令牌必须同步，否则取消的再见对不上。
            PullSessionState cur = new PullSessionState();
            cur.Token = 0x1111; cur.Ip = "10.0.0.7"; cur.Port = Proto.AudioPort;
            bool restart;
            PullSessionState same = PullSessionState.Adopt(cur, "10.0.0.7", Proto.AudioPort, 0x2222, true, out restart);
            ok(!restart && same.Token == 0x2222, "同一目标重新拉流：不重启但令牌更新");
            PullSessionState other = PullSessionState.Adopt(cur, "10.0.0.8", Proto.AudioPort, 0x3333, false, out restart);
            ok(restart && other.Token == 0x3333, "换目标：重启发送并采用新令牌");
            PullSessionState s3 = cur;
            for (int i = 0; i < 3; i++)
            {
                PullSessionState n = PullSessionState.Adopt(s3, "10.0.0.7", Proto.AudioPort, (ulong)(0x4000 + i), true, out restart);
                s3 = n;
            }
            byte[] bye3 = Proto.Bye(s3.Token);
            ok(PullAuth.ByeAccepted(bye3, "10.0.0.7", "10.0.0.7", true, s3.Token),
               "连续重拉 3 次后取消的再见仍然有效");

            // A-4：信标不能把会话目标带走。
            ok(!BeaconPolicy.ShouldFollow("10.0.0.7", "10.0.0.99", true, 30), "拉流会话期间不跟随陌生信标");
            ok(!BeaconPolicy.ShouldFollow("10.0.0.7", "10.0.0.99", false, 0.4), "推送模式旧地址有回执时不跟随");
            ok(BeaconPolicy.ShouldFollow("10.0.0.7", "10.0.0.99", false, 3.0), "推送模式旧地址静默 2.5 s 后跟随");
            ok(!BeaconPolicy.ShouldFollow("10.0.0.7", "10.0.0.7", true, 0), "拉流会话期间连端口变化也不跟");

            // A-3：没有密钥的信号只认刚质询过的那台机器 + 同一来源 IP。
            ChallengeBook book = new ChallengeBook();
            ulong rid = 0xA0A0A0A0A0A0A0A0UL;
            book.Issue(rid, "10.0.0.5");
            ok(book.HasPending(rid, "10.0.0.5"), "刚质询过的来源可以声明「没有密钥」");
            ok(!book.HasPending(rid, "10.0.0.6"), "第三方换 IP 冒用设备号不被采纳");
            ok(!book.HasPending(rid + 1, "10.0.0.5"), "没质询过的设备号不被采纳");
            int nkLen;
            byte[] nk = Proto.PullNoKey(0xAABB, Proto.AudioPort, out nkLen);
            ok(nk[4] == Proto.TypePullNoKey && Proto.U64(nk, 16) == 0xAABB
               && Proto.U64(nk, 8) == Proto.InstanceId && Proto.U16(nk, 6) == Proto.AudioPort,
               "「没有密钥」包布局正确（type/端口/请求方/发送端）");

            // A-5：睡眠唤醒的内部重启不发再见、起来后重新请求。
            ok(ResumePolicy.ShouldResend(true, PullState.Accepted, "10.0.0.7"), "唤醒后拉流会话保留并重新请求");
            ok(!ResumePolicy.ShouldResend(false, PullState.Accepted, "10.0.0.7"), "外部停止不重新请求");
            ok(!ResumePolicy.ShouldResend(true, PullState.None, ""), "本来没在拉就不请求");

            // 版本号：协议 v5，两端一致。
            ok(Proto.Version == 5, "协议版本为 5（与上一版不互通）");

            sb.AppendLine(fails == 0 ? "PASS 全部通过" : "FAIL " + fails + " 项未通过");
            File.WriteAllText(outPath, sb.ToString());
            return fails == 0 ? 0 : 1;
        }

        private static int SelfCheckStress(string outPath)
        {
            StringBuilder sb = new StringBuilder();
            int fails = 0;
            Action<bool, string> ok = delegate(bool pass, string what)
            {
                if (!pass) fails++;
                sb.AppendLine((pass ? "  OK  " : "  FAIL ") + what);
            };

            RingBridge rb = new RingBridge(2.0, 48000);
            NetReceiver rx = new NetReceiver(rb, 47873);
            Exception caught = null;
            int packets = 0;
            try
            {
                rx.Start();
                NetSender tx = new NetSender();
                tx.Start(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.1"), 47873));
                float[] src = new float[480 * 2];
                for (int k = 0; k < 480; k++) { src[k * 2] = 0.1f; src[k * 2 + 1] = 0.1f; }

                Thread pusher = new Thread(delegate()
                {
                    try
                    {
                        // 400 次推送 ≈ 2 s：期间来源表不断被网络线程插入
                        for (int k = 0; k < 400; k++) { tx.Push(src, 480, 48000); Thread.Sleep(2); }
                    }
                    catch (Exception e) { caught = e; }
                });
                Thread poker = new Thread(delegate()
                {
                    try
                    {
                        // 界面线程的读法：查在不在、然后要求释放（释放被投递给网络线程执行）
                        for (int k = 0; k < 400; k++)
                        {
                            rx.HasSource("127.0.0.1");
                            if ((k % 5) == 0) rx.ReleaseSource("127.0.0.1");
                            Thread.Sleep(2);
                        }
                    }
                    catch (Exception e) { caught = e; }
                });

                pusher.Start();
                poker.Start();
                pusher.Join();
                poker.Join();

                ReceiverStats st = rx.Stats;
                packets = st.Packets;
                ok(caught == null, caught == null
                    ? "并发查询/释放来源与收包同时进行 2 s 未抛异常"
                    : "并发压力下抛异常：" + caught.GetType().Name + " " + caught.Message);
                ok(st.SourceCount >= 0, "并发结束后仍可读统计（来源 " + st.SourceCount + " 路）");
                tx.Stop();
            }
            catch (Exception ex)
            {
                ok(false, "并发压力测试无法运行：" + ex.GetType().Name + " " + ex.Message);
            }
            try { rx.Stop(); } catch { }

            sb.AppendLine("（其间收到 " + packets + " 包）");
            sb.AppendLine(fails == 0 ? "PASS 全部通过" : "FAIL " + fails + " 项未通过");
            File.WriteAllText(outPath, sb.ToString());
            return fails == 0 ? 0 : 1;
        }

        private static int SelfCheckStage2(string outPath)
        {
            StringBuilder sb = new StringBuilder();
            int fails = 0;
            Action<bool, string> ok = delegate(bool pass, string what)
            {
                if (!pass) fails++;
                sb.AppendLine((pass ? "  OK  " : "  FAIL ") + what);
            };

            // 1）鉴权包里的设备名在 48（nameLen 偏移），不是 49。
            int authLen;
            byte[] auth = Proto.PullAuth(0x1234, 0x5678, new byte[16], Proto.AudioPort, out authLen);
            ok(auth[48] == (byte)System.Text.Encoding.UTF8.GetByteCount(Proto.DeviceName),
               "48 处是名字长度（" + auth[48] + "）");
            ok(Proto.GetName(auth, 48, auth.Length) == Proto.DeviceName,
               "真实解析路径读得出设备名（" + Proto.GetName(auth, 48, auth.Length) + "）");
            ok(Proto.GetName(auth, 49, auth.Length) != Proto.DeviceName, "按 49 读不出来（说明偏移是 48）");
            ok(Proto.GetName(new byte[20], 48, 20).Length == 0, "短包读不出名字");

            // 7）质询按「设备号 + IP」存，冒用设备号顶不掉正主。
            ulong rid = 0x5151515151515151UL;
            ChallengeBook book = new ChallengeBook();
            book.Issue(rid, "10.0.0.5");
            book.Issue(rid, "10.0.0.66");   // 冒用者
            ok(book.HasPending(rid, "10.0.0.5"), "冒用设备号从别的 IP 发请求，正主的质询仍在");
            ok(!book.HasPending(rid, "10.0.0.77"), "没被质询过的 IP 查不到质询");
            ChallengeBook book2 = new ChallengeBook();
            ulong na = book2.Issue(rid, "10.0.0.5");
            ok(book2.Issue(rid, "10.0.0.5") == na, "重复质询给同一个 nonce（否则在途鉴权被判失效）");
            ok(!book2.Consume(rid, na, "10.0.0.9"), "换 IP 消费失败");
            ok(book2.Consume(rid, na, "10.0.0.5"), "换 IP 的失败尝试没有把正主的质询消耗掉");
            ChallengeBook book3 = new ChallengeBook();
            for (int i = 0; i < 100; i++) book3.Issue((ulong)(1000 + i), "10.0.0.5");
            ok(book3.Count <= 64, "质询表有条目上限（" + book3.Count + "）");

            // 8）单路锁让位：主动拉流的目标优先。
            ok(AdmitPolicy.ShouldYield("10.0.0.7", "10.0.0.9", "10.0.0.9", false),
               "正在被 X 推送时，拉 S 会让位给 S");
            ok(!AdmitPolicy.ShouldYield("10.0.0.7", "10.0.0.9", "", false),
               "没有主动拉流时不让位（保持锁定语义）");
            ok(!AdmitPolicy.ShouldYield("10.0.0.7", "10.0.0.7", "10.0.0.7", false), "同一 IP 谈不上让位");

            // 15）版本门禁与「版本不同」标记。
            ok(Proto.Version == 5, "协议版本为 5（与上一版不互通）");
            Peer same = new Peer(); same.Version = Proto.Version;
            Peer oldp = new Peer(); oldp.Version = (byte)(Proto.Version - 1);
            ok(!same.VersionMismatch, "同版本设备可以互连");
            ok(oldp.VersionMismatch, "版本不同的设备被标记出来");

            // 16）拒绝表有上限。
            PullDenyList dl = new PullDenyList();
            for (int i = 0; i < 200; i++) dl.Deny("10.0.1." + (i % 250));
            ok(dl.Count <= 128, "拒绝表有条目上限（" + dl.Count + "）");

            // 6）冗余开关反复切换：不丢包、不重同步、不误判补包。
            RingBridge rb = new RingBridge(2.0, 48000);
            NetReceiver rx = new NetReceiver(rb, 47872);
            try
            {
                rx.Start();
                NetSender tx = new NetSender();
                tx.Start(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.1"), 47872));
                float[] src = new float[480 * 2];
                for (int k = 0; k < 480; k++) { src[k * 2] = 0.2f; src[k * 2 + 1] = 0.2f; }
                for (int round = 0; round < 30; round++)
                {
                    tx.Redundancy = (round / 3) % 2 == 0;
                    for (int j = 0; j < 2; j++)
                    {
                        tx.Push(src, 480, 48000);
                        Thread.Sleep(2);
                    }
                }
                Thread.Sleep(150);
                ReceiverStats st = rx.Stats;
                ok(st.Lost == 0 && st.Resyncs == 0 && st.Recovered == 0,
                   "反复切换冗余开关：不丢包/不重同步/不误判补包（lost " + st.Lost + " / resync " + st.Resyncs
                   + " / rec " + st.Recovered + "）");
                ok(st.Packets > 0 && Math.Abs(st.Rate - 48000) < 1,
                   "反复切换后仍在正常收（" + st.Packets + " 包 / " + (int)st.Rate + " Hz）");
                tx.Stop();
            }
            catch (Exception ex)
            {
                ok(false, "冗余切换测试无法运行：" + ex.Message);
            }
            rx.Stop();

            sb.AppendLine(fails == 0 ? "PASS 全部通过" : "FAIL " + fails + " 项未通过");
            File.WriteAllText(outPath, sb.ToString());
            return fails == 0 ? 0 : 1;
        }

        private static int SelfCheckCapture(string outPath)
        {
            StringBuilder sb = new StringBuilder();
            int fails = 0;
            Action<bool, string> ok = delegate(bool pass, string what)
            {
                if (!pass) fails++;
                sb.AppendLine((pass ? "  OK  " : "  FAIL ") + what);
            };

            CapturePlan p1 = new CapturePlan();
            p1.RequestedPid = 4242;
            p1.Decide(false, "这个系统版本不支持按程序采集");
            ok(p1.EffectivePid == 0 && p1.FellBack && p1.FallbackReason.Length > 0,
               "激活失败：生效改为整机、状态为已回退（" + p1.FallbackReason + "）");
            ok(!CapturePlan.ShouldReopen(4242, 4242), "回退后请求没变，不触发重开（不会一秒一断）");
            ok(CapturePlan.ShouldReopen(4242, 5555), "用户改选程序，触发重开");

            CapturePlan p2 = new CapturePlan();
            p2.RequestedPid = 777;
            p2.Decide(true, null);
            ok(p2.EffectivePid == 777 && !p2.FellBack, "激活成功：按请求的进程采集");

            CapturePlan p3 = new CapturePlan();
            p3.RequestedPid = 0;
            p3.Decide(false, null);
            ok(p3.EffectivePid == 0 && !p3.FellBack, "本来选的就是整机：不算回退");

            ok(AppModel.ResolveProcessByName("no-such-program-xyz-1234") == 0, "程序名解析不到时回整机采集");

            sb.AppendLine(fails == 0 ? "PASS 全部通过" : "FAIL " + fails + " 项未通过");
            File.WriteAllText(outPath, sb.ToString());
            return fails == 0 ? 0 : 1;
        }

        /// 出了没料到的异常：记到 %APPDATA%\AudioBridge\error.log，不弹崩溃框、不退出。
        private static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioBridge");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "error.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
