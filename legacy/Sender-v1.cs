// 显示器音频桥 · Windows 发送端
//
// 作用：把 Windows 正在播放的声音（WASAPI loopback）实时打包成 UDP，发给 Mac。
// 只用 .NET Framework 自带的 csc.exe 就能编译，不需要装任何东西、不需要管理员权限。
//
// 用法：Sender.exe 192.168.1.23 [47831]
//
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace MonitorAudioSend
{
    internal static class Program
    {
        private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const int AUDCLNT_BUFFERFLAGS_SILENT = 0x00000002;
        private const int CLSCTX_ALL = 23;
        private const int FRAMES_PER_PACKET = 240;

        private static volatile bool running = true;
        private static readonly float[] Scratch = new float[4096 * 8];
        private static readonly short[] PcmScratch = new short[4096 * 8];

        private static int Main(string[] args)
        {
            string host = args.Length > 0 ? args[0] : "";
            int port = 47831;
            if (args.Length > 1 && !int.TryParse(args[1], out port)) port = 47831;
            if (port <= 0 || port > 65535) port = 47831;
            if (host.Length == 0)
            {
                Console.WriteLine("用法: Sender.exe <Mac 的 IP> [端口，默认 47831]");
                Console.WriteLine("（Mac 上菜单栏 App 里有「复制本机地址」）");
                return 2;
            }

            Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                running = false;
            };

            try
            {
                return Run(host, port);
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("出错: " + ex.Message);
                Console.WriteLine("常见原因：Windows 没有声音输出设备；或者默认输出被切走了。");
                return 1;
            }
        }

        private static int Run(string host, int port)
        {
            IPAddress ip = IPAddress.Parse(host);
            EndPoint ep = new IPEndPoint(ip, port);

            Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.SendBufferSize = 1 << 20;
            sock.Connect(ep);

            MMDeviceEnumeratorComObject en = new MMDeviceEnumeratorComObject();
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)en;

            IMMDevice device;
            Check(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device),
                  "取默认播放设备");

            Guid iidClient = typeof(IAudioClient).GUID;
            object clientObj;
            Check(device.Activate(ref iidClient, CLSCTX_ALL, IntPtr.Zero, out clientObj), "打开音频引擎");
            IAudioClient client = (IAudioClient)clientObj;

            IntPtr fmtPtr;
            Check(client.GetMixFormat(out fmtPtr), "读取混音格式");
            WAVEFORMATEX fmt = (WAVEFORMATEX)Marshal.PtrToStructure(fmtPtr, typeof(WAVEFORMATEX));

            bool isFloat = false;
            if (fmt.wFormatTag == 3)
            {
                isFloat = true;
            }
            else if (fmt.wFormatTag == 0xFFFE && fmt.cbSize >= 22)
            {
                // WAVEFORMATEXTENSIBLE：SubFormat GUID 的 data1 在偏移 24
                isFloat = Marshal.ReadInt32(fmtPtr, 24) == 3;
            }

            int channels = fmt.nChannels;
            int rate = (int)fmt.nSamplesPerSec;
            Console.WriteLine(string.Format("播放格式: {0} Hz · {1} 声道 · {2} bit · {3}",
                                            rate, channels, fmt.wBitsPerSample, isFloat ? "float" : "pcm"));

            Check(client.Initialize(0, AUDCLNT_STREAMFLAGS_LOOPBACK, 0, 0, fmtPtr, IntPtr.Zero),
                  "初始化 loopback（错误 0x88890008 表示格式不被接受）");

            Guid iidCap = typeof(IAudioCaptureClient).GUID;
            object capObj;
            Check(client.GetService(ref iidCap, out capObj), "取采集接口");
            IAudioCaptureClient capture = (IAudioCaptureClient)capObj;

            Check(client.Start(), "启动采集");

            Console.WriteLine(string.Format("发送到 {0}:{1}，Ctrl-C 结束", host, port));

            byte[] packet = new byte[24 + FRAMES_PER_PACKET * 2 * 2];
            int seq = 0;
            long packets = 0;
            long bytes = 0;
            long windowStart = Stopwatch.GetTimestamp();
            long windowPackets = 0;

            Stopwatch clock = Stopwatch.StartNew();

            while (running)
            {
                uint available;
                Check(capture.GetNextPacketSize(out available), "查询可用数据");

                if (available == 0)
                {
                    Thread.Sleep(2);
                    continue;
                }

                IntPtr data;
                uint frames;
                uint flags;
                ulong devPos;
                ulong qpcPos;
                Check(capture.GetBuffer(out data, out frames, out flags, out devPos, out qpcPos), "取数据块");

                bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                int consumed = 0;

                while (consumed < (int)frames)
                {
                    int n = (int)frames - consumed;
                    if (n > FRAMES_PER_PACKET) n = FRAMES_PER_PACKET;

                    int len = 24 + n * 2 * 2;
                    WriteHeader(packet, 2, rate, (uint)seq, (ushort)n,
                                (ulong)(clock.Elapsed.Ticks * 100L));

                    if (silent || data == IntPtr.Zero)
                    {
                        Array.Clear(packet, 24, n * 2 * 2);
                    }
                    else
                    {
                        int sampleOffset = consumed * channels;
                        int count = n * channels;
                        if (count > Scratch.Length) count = Scratch.Length;

                        if (isFloat)
                        {
                            Marshal.Copy(new IntPtr(data.ToInt64() + (long)sampleOffset * 4), Scratch, 0, count);
                        }
                        else
                        {
                            if (count > PcmScratch.Length) count = PcmScratch.Length;
                            Marshal.Copy(new IntPtr(data.ToInt64() + (long)sampleOffset * 2), PcmScratch, 0, count);
                            for (int i = 0; i < count; i++) Scratch[i] = PcmScratch[i] / 32768f;
                        }

                        // 不管 Windows 那边是几声道，统一降混成双声道再发（Mac 端只认 1/2 声道）。
                        int ready = count / channels;
                        int o = 24;
                        for (int f = 0; f < ready; f++)
                        {
                            float l = Scratch[f * channels];
                            float r = channels >= 2 ? Scratch[f * channels + 1] : l;
                            o = PutSample(packet, o, l);
                            o = PutSample(packet, o, r);
                        }
                    }

                    sock.Send(packet, 0, len, SocketFlags.None);
                    seq++;
                    packets++;
                    bytes += len;
                    windowPackets++;
                    consumed += n;
                }

                capture.ReleaseBuffer(frames);

                long now = Stopwatch.GetTimestamp();
                if (now - windowStart > Stopwatch.Frequency)
                {
                    double secs = (double)(now - windowStart) / Stopwatch.Frequency;
                    double kbps = bytes * 8.0 / 1000.0 / secs;
                    Console.Write(string.Format("\r已发 {0} 包 · {1:F1} kbps · 队列 {2} 帧   ",
                                                packets, kbps, available));
                    windowStart = now;
                    windowPackets = 0;
                    bytes = 0;
                }
            }

            Console.WriteLine();
            try { client.Stop(); } catch { }
            sock.Close();
            Console.WriteLine("已停止。");
            return 0;
        }

        private static int PutSample(byte[] b, int o, float v)
        {
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            short s = (short)(v * 32000f);
            b[o] = (byte)(s & 0xFF);
            b[o + 1] = (byte)((s >> 8) & 0xFF);
            return o + 2;
        }

        private static void WriteHeader(byte[] b, int channels, int rate, uint seq, ushort frames, ulong tsc)
        {
            b[0] = 0x4D; b[1] = 0x41; b[2] = 0x42; b[3] = 0x31;
            b[4] = 1;
            b[5] = (byte)channels;
            PutU32(b, 6, (uint)rate);
            PutU32(b, 10, seq);
            b[14] = (byte)(frames & 0xFF);
            b[15] = (byte)(frames >> 8);
            for (int i = 0; i < 8; i++) b[16 + i] = (byte)((tsc >> (8 * i)) & 0xFF);
        }

        private static void PutU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
            b[o + 2] = (byte)((v >> 16) & 0xFF);
            b[o + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0)
            {
                throw new COMException(string.Format("{0} 失败 (0x{1:X8})", what, hr), hr);
            }
        }

        // ---- WASAPI COM 定义 ----

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        private enum EDataFlow { Render = 0, Capture = 1, All = 2 }
        private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            int RegisterEndpointNotificationCallback(IntPtr client);
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            int GetCount(out int count);
            int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                         [MarshalAs(UnmanagedType.IUnknown)] out object obj);
            int OpenPropertyStore(int access, out IntPtr store);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            int GetState(out int state);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
                           IntPtr format, IntPtr sessionGuid);
            int GetBufferSize(out uint frames);
            int GetStreamLatency(out long latency);
            int GetCurrentPadding(out uint frames);
            int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
            int GetMixFormat(out IntPtr format);
            int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            int Start();
            int Stop();
            int Reset();
            int SetEventHandle(IntPtr handle);
            int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            int GetBuffer(out IntPtr data, out uint frames, out uint flags,
                          out ulong devicePosition, out ulong qpcPosition);
            int ReleaseBuffer(uint frames);
            int GetNextPacketSize(out uint frames);
        }
    }
}
