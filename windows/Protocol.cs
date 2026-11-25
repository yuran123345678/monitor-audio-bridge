// 音频桥 · Windows 端 · 协议与设置
//
// 线上协议必须和 Mac 端 Sources/Protocol.swift 逐字节一致（小端）。包格式说明见那个文件 / README。
// 整个 Windows 端只用 C# 5 语法 + .NET Framework 4 自带的库，系统自带的 csc.exe 就能编。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace AudioBridge
{
    internal static class Proto
    {
        public const int AudioPort = 47831;
        public const int DiscoveryPort = 47832;
        public const byte Version = 5;
        public const int HeaderSize = 24;
        public const int FramesPerPacket = 240;
        /// 带冗余副本时的每包帧数：24 + 120*4*2 = 984 字节，留在 MTU 内。
        public const int RedundantFramesPerPacket = 120;

        public const byte TypeAudio = 1;
        public const byte TypeKeepalive = 2;
        public const byte TypeReport = 3;
        public const byte TypeBye = 4;
        public const byte TypeBeacon = 16;
        public const byte TypePull = 5;         // 接收端主动拉流（走发现端口）
        public const byte TypePullReply = 6;    // 发送端应答
        public const byte TypePullChallenge = 7; // 发送端回一次性随机数
        public const byte TypePullAuth = 8;      // 请求方回 HMAC
        public const byte TypePullNoKey = 9;     // 请求方：本机没有你的密钥（请重新配对）

        public const byte RoleIdle = 0;
        public const byte RoleSender = 1;
        public const byte RoleReceiver = 2;

        public const byte PlatformMac = 1;
        public const byte PlatformWindows = 2;

        public const byte FlagPlaying = 1;
        public const byte FlagMuted = 2;
        public const byte FlagBusy = 8;
        public const byte AudioFlagRedundant = 1;   // 音频头第 23 字节：包尾还带了一份上一包的 PCM

        // 信标第 9 字节的能力位
        public const byte CapPull = 1;          // 允许别人拉我的声音
        public const byte CapMix = 2;           // 接收端支持多路混音

        // 拉流应答结果码
        public const byte PullOk = 0;           // 接受，密钥没变
        public const byte PullOkNew = 1;        // 接受，新密钥（这次才建立信任）
        public const byte PullDenied = 2;       // 用户拒绝
        public const byte PullBusy = 3;         // 正在给别的设备发
        public const byte PullDisabled = 4;     // 没开「允许拉取」或没在运行
        public const byte PullWait = 6;         // 正在等本机用户确认

        public static readonly string DeviceName = MakeName();
        public static ulong InstanceId;

        private static string MakeName()
        {
            // 截图/演示时可以用 MAB_DEVICE_NAME 覆盖，避免把真实机器名带进画面
            string env = Environment.GetEnvironmentVariable("MAB_DEVICE_NAME");
            if (!string.IsNullOrEmpty(env)) return env;
            string n = Environment.MachineName;
            return string.IsNullOrEmpty(n) ? "Windows" : n;
        }

        /// 设备号：首次运行随机生成后存盘，对端凭它在我们换 IP / 重启之后认出是同一台。
        public static void InitIdentity()
        {
            ulong id;
            if (!ulong.TryParse(Settings.Get("deviceID", ""), out id) || id == 0)
            {
                byte[] b = Guid.NewGuid().ToByteArray();
                id = BitConverter.ToUInt64(b, 0) ^ BitConverter.ToUInt64(b, 8);
                if (id == 0) id = 1;
                Settings.Set("deviceID", id.ToString());
            }
            InstanceId = id;
        }

        public static long NowNs()
        {
            return (long)(Stopwatch.GetTimestamp() * (1000000000.0 / Stopwatch.Frequency));
        }

        public static bool HasMagic(byte[] b, int n)
        {
            return n >= 5 && b[0] == 0x4D && b[1] == 0x41 && b[2] == 0x42 && b[3] == 0x31;
        }

        public static void PutMagic(byte[] b, byte type)
        {
            b[0] = 0x4D; b[1] = 0x41; b[2] = 0x42; b[3] = 0x31;
            b[4] = type;
        }

        public static void PutU16(byte[] b, int o, int v)
        {
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
        }

        public static void PutU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
            b[o + 2] = (byte)((v >> 16) & 0xFF);
            b[o + 3] = (byte)((v >> 24) & 0xFF);
        }

        public static void PutU64(byte[] b, int o, ulong v)
        {
            for (int i = 0; i < 8; i++) b[o + i] = (byte)((v >> (8 * i)) & 0xFF);
        }

        public static int U16(byte[] b, int o)
        {
            return b[o] | (b[o + 1] << 8);
        }

        public static uint U32(byte[] b, int o)
        {
            return (uint)b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24);
        }

        public static ulong U64(byte[] b, int o)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)b[o + i] << (8 * i);
            return v;
        }

        /// 在 o 处写 nameLen(u8) + utf8，返回写完后的偏移。最长 60 字节，按字符截断。
        public static int PutName(byte[] b, int o, string name)
        {
            string s = name ?? "";
            byte[] raw = Encoding.UTF8.GetBytes(s);
            while (raw.Length > 60 && s.Length > 0)
            {
                s = s.Substring(0, s.Length - 1);
                raw = Encoding.UTF8.GetBytes(s);
            }
            b[o] = (byte)raw.Length;
            Buffer.BlockCopy(raw, 0, b, o + 1, raw.Length);
            return o + 1 + raw.Length;
        }

        public static string GetName(byte[] b, int o, int count)
        {
            if (count <= o) return "";
            int n = b[o];
            if (n <= 0 || count < o + 1 + n) return "";
            try { return Encoding.UTF8.GetString(b, o + 1, n); }
            catch { return ""; }
        }

        public static byte[] Keepalive(uint rate, uint seq, out int length)
        {
            byte[] b = new byte[33 + 61];
            PutMagic(b, TypeKeepalive);
            PutU32(b, 6, rate);
            PutU32(b, 10, seq);
            PutU64(b, 16, (ulong)NowNs());
            PutU64(b, 24, InstanceId);
            length = PutName(b, 32, DeviceName);
            return b;
        }

        /// 拉流请求：告诉对方「把声音发到我的 audioPort」，key 是我上次拿到的密钥（第一次为 0）。
        public static byte[] Pull(ulong target, int audioPort, out int length)
        {
            byte[] b = new byte[25 + 61];
            PutMagic(b, TypePull);
            b[5] = Version;
            PutU16(b, 6, (ushort)audioPort);
            PutU64(b, 8, target);
            PutU64(b, 16, InstanceId);
            length = PutName(b, 24, DeviceName);
            return b;
        }

        public static byte[] PullChallenge(ulong requester, ulong nonce, ulong senderId, out int length)
        {
            byte[] b = new byte[33 + 61];
            PutMagic(b, TypePullChallenge);
            b[5] = Version;
            PutU64(b, 8, requester);
            PutU64(b, 16, senderId);
            PutU64(b, 24, nonce);
            length = PutName(b, 32, DeviceName);
            return b;
        }

        public static byte[] PullAuth(ulong senderId, ulong nonce, byte[] mac, int audioPort, out int length)
        {
            byte[] b = new byte[49 + 61];
            PutMagic(b, TypePullAuth);
            b[5] = Version;
            PutU16(b, 6, (ushort)audioPort);
            PutU64(b, 8, nonce);
            PutU64(b, 16, InstanceId);
            PutU64(b, 24, senderId);
            int n = mac == null ? 0 : Math.Min(16, mac.Length);
            for (int i = 0; i < n; i++) b[32 + i] = mac[i];
            length = PutName(b, 48, DeviceName);
            return b;
        }

        /// result 见 PullOk / PullOkNew / PullDenied / PullBusy / PullDisabled / PullWait。
        public static byte[] PullReply(ulong requester, byte result, ulong token, ulong key, out int length)
        {
            byte[] b = new byte[41 + 61];
            PutMagic(b, TypePullReply);
            b[5] = Version;
            b[6] = result;
            b[7] = (byte)((key != 0 ? 1 : 0) | (token != 0 ? 2 : 0));
            PutU64(b, 8, requester);
            PutU64(b, 16, InstanceId);
            PutU64(b, 24, token);
            PutU64(b, 32, key);
            length = PutName(b, 40, DeviceName);
            return b;
        }

        /// <summary>「我没有你的密钥」：布局同拉流请求（ver@5、audioPort@6、requester@8、senderId@16、名字@24）。</summary>
        public static byte[] PullNoKey(ulong senderId, int audioPort, out int length)
        {
            byte[] b = new byte[25 + 61];
            PutMagic(b, TypePullNoKey);
            b[5] = Version;
            PutU16(b, 6, audioPort);
            PutU64(b, 8, InstanceId);
            PutU64(b, 16, senderId);
            length = PutName(b, 24, DeviceName);
            return b;
        }

        public static byte[] Bye(ulong token)
        {
            byte[] b = new byte[16];
            PutMagic(b, TypeBye);
            PutU64(b, 8, token);
            return b;
        }

        /// Windows 的坑：UDP 发到一个没人听的端口，对方回 ICMP「端口不可达」，之后本 socket 的
        /// 收/发会抛 WSAECONNRESET（10054）。关掉这个行为，否则对方没开时这边会不停出异常。
        public static void DisableConnReset(Socket s)
        {
            try
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                s.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch { }
        }
    }

    /// 本机 IPv4 地址与各网卡的广播地址。
    internal static class LocalNet
    {
        public static string PrimaryIPv4()
        {
            string best = "";
            int bestScore = -1;
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties props = ni.GetIPProperties();
                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = ua.Address.ToString();
                        if (ip.StartsWith("169.254.")) continue;
                        // 有默认网关的真实网卡优先；虚拟网卡（VPN、Hyper-V、虚拟机）靠后。
                        int score = 0;
                        if (props.GatewayAddresses.Count > 0) score += 4;
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                            || ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 2;
                        string d = (ni.Description ?? "").ToLowerInvariant();
                        if (d.Contains("virtual") || d.Contains("vmware") || d.Contains("hyper-v")
                            || d.Contains("tap") || d.Contains("vpn")) score -= 3;
                        if (score > bestScore) { bestScore = score; best = ip; }
                    }
                }
            }
            catch { }
            return best;
        }

        public static List<IPAddress> BroadcastAddresses()
        {
            List<IPAddress> list = new List<IPAddress>();
            list.Add(IPAddress.Broadcast);
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        IPAddress mask = ua.IPv4Mask;
                        if (mask == null) continue;
                        byte[] a = ua.Address.GetAddressBytes();
                        byte[] m = mask.GetAddressBytes();
                        if (a.Length != 4 || m.Length != 4) continue;
                        if (m[0] == 0 && m[1] == 0 && m[2] == 0 && m[3] == 0) continue;
                        byte[] bc = new byte[4];
                        for (int i = 0; i < 4; i++) bc[i] = (byte)(a[i] | (~m[i] & 0xFF));
                        IPAddress addr = new IPAddress(bc);
                        if (!list.Contains(addr)) list.Add(addr);
                    }
                }
            }
            catch { }
            return list;
        }
    }

    /// 极简设置存储：%APPDATA%\AudioBridge\settings.ini，一行一个 key=value。
    internal static class Settings
    {
        private static readonly Dictionary<string, string> map = new Dictionary<string, string>();
        private static readonly object gate = new object();
        private static string path;

        public static void Load()
        {
            lock (gate)
            {
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioBridge");
                    Directory.CreateDirectory(dir);
                    path = Path.Combine(dir, "settings.ini");
                    if (!File.Exists(path)) return;
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        map[line.Substring(0, eq)] = line.Substring(eq + 1);
                    }
                }
                catch { }
            }
        }

        public static string Get(string key, string fallback)
        {
            lock (gate)
            {
                string v;
                return map.TryGetValue(key, out v) ? v : fallback;
            }
        }

        public static bool GetBool(string key, bool fallback)
        {
            string v = Get(key, fallback ? "1" : "0");
            return v == "1";
        }

        public static double GetDouble(string key, double fallback)
        {
            double d;
            return double.TryParse(Get(key, ""), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out d) ? d : fallback;
        }

        public static void Set(string key, string value)
        {
            lock (gate)
            {
                string v = (value ?? "").Replace("\r", " ").Replace("\n", " ");
                string old;
                if (map.TryGetValue(key, out old) && old == v) return;
                map[key] = v;
                Save();
            }
        }

        public static void Set(string key, bool value) { Set(key, value ? "1" : "0"); }

        public static void Set(string key, double value)
        {
            Set(key, value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void Save()
        {
            if (path == null) return;
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in map) lines.Add(kv.Key + "=" + kv.Value);
                string tmp = path + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray(), Encoding.UTF8);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch { }
        }
    }
}
