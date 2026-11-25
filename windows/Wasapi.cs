// 音频桥 · Windows 端 · WASAPI（系统声音采集 / 播放 / 设备枚举）
//
// 两条音频线程都是「自愈」的：设备被拔掉、默认设备切换、格式变化、驱动报错，
// 统统走同一条路——收拾干净，等半秒，重新打开。上层不用管。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace AudioBridge
{
    internal sealed class AudioDevice
    {
        public string Id = "";
        public string Name = "";
    }

    // 公开是因为 ActivateAudioInterfaceAsync 要把托管对象当 COM 回调传出去：
    // 只有公开类型才能生成 CCW，否则运行时会抛 NotSupportedException。
    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-031B93D2A1B3"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    public sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly System.Threading.ManualResetEvent Done = new System.Threading.ManualResetEvent(false);
        public object Result;
        public int Error = unchecked((int)0x80004005);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                int activate;
                object obj;
                int hr = operation.GetActivateResult(out activate, out obj);
                if (hr >= 0 && activate >= 0 && obj != null) { Result = obj; Error = 0; }
                else Error = hr < 0 ? hr : activate;
            }
            catch
            {
                Error = -1;
            }
            finally
            {
                try { Done.Set(); } catch { }
            }
        }
    }

    internal static class Wasapi
    {
        public const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        public const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        public const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        public const int CLSCTX_ALL = 23;
        public const int DEVICE_STATE_ACTIVE = 1;

        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
        [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr AvSetMmThreadCharacteristics(string task, ref uint index);
        [DllImport("avrt.dll")] public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
        [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PROPVARIANT pv);
        [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr p);

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what + " 失败 (0x" + hr.ToString("X8") + ")", hr);
        }

        public static void Release(object o)
        {
            if (o == null) return;
            try { Marshal.ReleaseComObject(o); } catch { }
        }

        /// 按程序采集（Windows 10 2004+）：把某个进程（含它的子进程）的声音单独捞出来。
        /// 激活走 ActivateAudioInterfaceAsync + 虚拟设备 "VAD\Process_Loopback"。
        public const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
        public const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
        public const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
        {
            public uint TargetProcessId;
            public int ProcessLoopbackMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AUDIOCLIENT_ACTIVATION_PARAMS
        {
            public int ActivationType;
            public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
        }

        /// 装 AUDIOCLIENT_ACTIVATION_PARAMS 的 PROPVARIANT（VT_BLOB）。
        [StructLayout(LayoutKind.Sequential)]
        internal struct PROPVARIANT_BLOB
        {
            public ushort vt;
            public ushort r1, r2, r3;
            public uint blobSize;
            public IntPtr blobData;
        }

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
        internal static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            ref Guid riid,
            IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation operation);

        /// 拿一个「只装某个进程声音」的 IAudioClient。失败就抛异常，调用方回退到整机采集。
        public static IAudioClient ActivateProcessLoopback(uint pid)
        {
            AUDIOCLIENT_ACTIVATION_PARAMS ap = new AUDIOCLIENT_ACTIVATION_PARAMS();
            ap.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
            ap.ProcessLoopbackParams.TargetProcessId = pid;
            ap.ProcessLoopbackParams.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;

            int size = Marshal.SizeOf(typeof(AUDIOCLIENT_ACTIVATION_PARAMS));
            IntPtr block = Marshal.AllocHGlobal(size);
            PROPVARIANT_BLOB pv = new PROPVARIANT_BLOB();
            pv.vt = 65;   // VT_BLOB
            pv.blobSize = (uint)size;
            pv.blobData = block;
            IntPtr pvPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PROPVARIANT_BLOB)));
            ActivationHandler handler = new ActivationHandler();
            IActivateAudioInterfaceAsyncOperation op = null;
            try
            {
                Marshal.StructureToPtr(ap, block, false);
                Marshal.StructureToPtr(pv, pvPtr, false);

                Guid iid = typeof(IAudioClient).GUID;
                int hr = ActivateAudioInterfaceAsync(ProcessLoopbackDevice, ref iid, pvPtr, handler, out op);
                Check(hr, "打开按程序采集");
                if (!handler.Done.WaitOne(5000)) throw new Exception("系统没有回应按程序采集的请求");
                if (handler.Error < 0 || !(handler.Result is IAudioClient))
                    throw new Exception("这个系统版本不支持按程序采集（需要 Windows 10 2004 或更新）");
                return (IAudioClient)handler.Result;
            }
            finally
            {
                Marshal.FreeHGlobal(block);
                Marshal.FreeHGlobal(pvPtr);
                Release(op);
                try { handler.Done.Close(); } catch { }   // 事件句柄用完释放
            }
        }

        /// 按程序采集只接受 32 位浮点立体声，这个格式自己拼。
        public static IntPtr AllocFloatStereoFormat(int rate)
        {
            WAVEFORMATEX wf = new WAVEFORMATEX();
            wf.wFormatTag = 3;                 // WAVE_FORMAT_IEEE_FLOAT
            wf.nChannels = 2;
            wf.nSamplesPerSec = (uint)rate;
            wf.wBitsPerSample = 32;
            wf.nBlockAlign = 8;
            wf.nAvgBytesPerSec = (uint)(rate * 8);
            wf.cbSize = 0;
            // 这个块由 CoTaskMemFree 释放，所以必须用 CoTaskMemAlloc 分配（配对，不能混用 AllocHGlobal）。
            IntPtr fmt = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WAVEFORMATEX)));
            Marshal.StructureToPtr(wf, fmt, false);
            return fmt;
        }

        public static IMMDeviceEnumerator NewEnumerator()
        {
            return (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        }

        /// 打开指定播放设备；id 为空或设备不在时用系统默认。usedId 返回实际打开的设备 ID。
        public static IMMDevice OpenRender(IMMDeviceEnumerator en, string id, out string usedId)
        {
            IMMDevice dev = null;
            if (!string.IsNullOrEmpty(id))
            {
                int state = 0;
                if (en.GetDevice(id, out dev) < 0 || dev == null || dev.GetState(out state) < 0 || state != DEVICE_STATE_ACTIVE)
                {
                    Release(dev);
                    dev = null;
                }
            }
            if (dev == null)
            {
                Check(en.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out dev), "取默认播放设备");
            }
            dev.GetId(out usedId);
            return dev;
        }

        public static string DefaultRenderId(IMMDeviceEnumerator en)
        {
            IMMDevice dev = null;
            try
            {
                if (en.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out dev) < 0 || dev == null) return "";
                string id;
                dev.GetId(out id);
                return id ?? "";
            }
            catch { return ""; }
            finally { Release(dev); }
        }

        /// 列出所有可用的播放设备（在线程池线程上调用，别在 UI 线程直接调）。
        public static List<AudioDevice> ListRenderDevices()
        {
            List<AudioDevice> list = new List<AudioDevice>();
            IMMDeviceEnumerator en = null;
            IMMDeviceCollection coll = null;
            try
            {
                en = NewEnumerator();
                if (en.EnumAudioEndpoints(EDataFlow.Render, DEVICE_STATE_ACTIVE, out coll) < 0) return list;
                int count;
                coll.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IMMDevice dev = null;
                    IPropertyStore store = null;
                    try
                    {
                        if (coll.Item(i, out dev) < 0 || dev == null) continue;
                        AudioDevice d = new AudioDevice();
                        dev.GetId(out d.Id);
                        d.Name = d.Id;
                        if (dev.OpenPropertyStore(0, out store) >= 0 && store != null)
                        {
                            PROPERTYKEY key = new PROPERTYKEY();
                            key.fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");
                            key.pid = 14;   // PKEY_Device_FriendlyName
                            PROPVARIANT pv = new PROPVARIANT();
                            if (store.GetValue(ref key, out pv) >= 0)
                            {
                                if (pv.vt == 31 && pv.pointer != IntPtr.Zero)   // VT_LPWSTR
                                {
                                    string s = Marshal.PtrToStringUni(pv.pointer);
                                    if (!string.IsNullOrEmpty(s)) d.Name = s;
                                }
                                PropVariantClear(ref pv);
                            }
                        }
                        list.Add(d);
                    }
                    catch { }
                    finally { Release(store); Release(dev); }
                }
            }
            catch { }
            finally { Release(coll); Release(en); }
            return list;
        }

        /// 解析混音格式：返回是否 float；不认识的格式抛异常。
        public static bool ParseFormat(IntPtr fmtPtr, out int channels, out int rate, out int bits)
        {
            WAVEFORMATEX fmt = (WAVEFORMATEX)Marshal.PtrToStructure(fmtPtr, typeof(WAVEFORMATEX));
            channels = fmt.nChannels;
            rate = (int)fmt.nSamplesPerSec;
            bits = fmt.wBitsPerSample;
            bool isFloat = false;
            if (fmt.wFormatTag == 3)
            {
                isFloat = true;
            }
            else if (fmt.wFormatTag == 0xFFFE && fmt.cbSize >= 22)
            {
                // WAVEFORMATEXTENSIBLE：SubFormat GUID 的 Data1 在偏移 24（1 = PCM，3 = IEEE float）
                isFloat = Marshal.ReadInt32(fmtPtr, 24) == 3;
            }
            if (channels < 1 || rate < 8000) throw new InvalidOperationException("设备格式异常");
            if (isFloat && bits != 32) throw new InvalidOperationException("不支持的格式：" + bits + " bit float");
            if (!isFloat && bits != 16 && bits != 24 && bits != 32) throw new InvalidOperationException("不支持的格式：" + bits + " bit");
            return isFloat;
        }
    }

    /// 系统声音采集（WASAPI loopback）。抓的是「某个播放设备正在放的所有声音」。
    /// 「要按程序采集 / 实际按什么采集 / 有没有回退」的决策，不碰 COM，便于自测。
    internal sealed class CapturePlan
    {
        public int RequestedPid;      // 用户请求的进程（0 = 整机）
        public string RequestedName = "";
        public int EffectivePid;      // 本次会话实际生效（0 = 整机）
        public string FallbackReason = "";

        public bool WantsProcess { get { return RequestedPid > 0; } }
        public bool FellBack { get { return RequestedPid > 0 && EffectivePid == 0; } }

        /// processOk：激活/初始化/取接口/启动这条链是否全成功。失败就把生效 pid 定为整机。
        public void Decide(bool processOk, string reason)
        {
            if (RequestedPid > 0 && processOk)
            {
                EffectivePid = RequestedPid;
                FallbackReason = "";
            }
            else if (RequestedPid > 0)
            {
                EffectivePid = 0;
                FallbackReason = reason == null ? "按程序采集不可用" : reason;
            }
            else
            {
                EffectivePid = 0;
                FallbackReason = "";
            }
        }

        /// 用户的选择变了才需要重开会话。
        public static bool ShouldReopen(int lastRequested, int requested)
        {
            return lastRequested != requested;
        }
    }

    internal sealed class LoopbackCapture
    {
        private Thread thread;
        private volatile bool running;
        private volatile string deviceId = "";

        /// 交织双声道、帧数、采样率。在采集线程上回调。
        public Action<float[], int, int> Audio;
        /// 最近一次错误（null = 正常工作中）。
        public volatile string Error;
        public volatile bool Capturing;
        public volatile string ActiveDeviceName = "";

        public bool IsRunning { get { return running; } }

        /// 采集线程是否真的还活着（running 为 true 但线程已退出时，看门狗靠它发现）。
        public bool Alive { get { Thread t = thread; return t != null && t.IsAlive; } }

        /// 本次会话的决策（请求 pid / 生效 pid / 是否回退）。界面据此显示状态。
        public volatile CapturePlan Plan = new CapturePlan();

        /// id 为空 = 跟随系统默认播放设备。运行中调用会在下一轮自动切过去。
        public void SetDevice(string id) { deviceId = id ?? ""; }

        /// 大于 0 时按这个进程（含子进程）采集；0 = 按播放设备整机采集。
        /// 注意这是「用户请求的 pid」，实际生效的看 Plan.EffectivePid。
        private volatile int requestedPid;
        private volatile string requestedName = "";
        public void SetProcess(int pid, string name)
        {
            requestedPid = pid;
            requestedName = name == null ? "" : name;
        }

        /// 采集线程的代际：Stop 后老线程即使还卡在激活里，也不会再往播放端推数据。
        private volatile int generation;
        private int failStreak;

        public void Start()
        {
            if (running) return;
            running = true;
            generation++;
            Error = null;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "ab-capture";
            thread.Priority = ThreadPriority.Highest;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            generation++;          // 让可能还卡在激活里的老会话作废，避免两个采集线程同时推数据
            Thread t = thread;
            if (t != null && t != Thread.CurrentThread) t.Join(1500);
            thread = null;
            Capturing = false;
        }

        /// 指定 pid 的进程还在不在（采集循环里用「本会话记下的 pid」，不是可能已被改掉的请求）。
        internal static bool ProcessGone(int pid)
        {
            if (pid <= 0) return false;
            try { System.Diagnostics.Process.GetProcessById(pid); return false; }
            catch { return true; }
        }

        private void Loop()
        {
            // 默认定时器精度 15.6 ms：不改的话 Sleep(1) 实际睡 15 ms，包会一坨一坨地发出去，对方只能加大缓冲。
            Wasapi.timeBeginPeriod(1);
            uint taskIndex = 0;
            IntPtr mm = IntPtr.Zero;
            try { mm = Wasapi.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex); } catch { }
            try
            {
                while (running)
                {
                    bool failed = false;
                    try
                    {
                        Session();
                        failed = Error != null;
                    }
                    catch (Exception ex)
                    {
                        Error = ex.Message;
                        failed = true;
                    }
                    Capturing = false;
                    // 设备被拔/禁用时别每 0.5 s 重建一次会话：失败就逐步退避，成功一次即复位。
                    if (failed) failStreak = Math.Min(failStreak + 1, 4); else failStreak = 0;
                    int waitMs = 500 << Math.Min(failStreak, 3);
                    for (int i = 0; i < waitMs / 10 && running; i++) Thread.Sleep(10);
                }
            }
            finally
            {
                if (mm != IntPtr.Zero) { try { Wasapi.AvRevertMmThreadCharacteristics(mm); } catch { } }
                Wasapi.timeEndPeriod(1);
            }
        }

        /// 一次采集会话：打开设备 → 一直抓，直到出错、设备变化或被叫停。
        private void Session()
        {
            IMMDeviceEnumerator en = null;
            IMMDevice device = null;
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            IntPtr fmtPtr = IntPtr.Zero;
            int wantPid = requestedPid;
            string wantName = requestedName;
            int gen = generation;
            CapturePlan plan = new CapturePlan();
            plan.RequestedPid = wantPid;
            plan.RequestedName = wantName;
            try
            {
                int channels = 2, rate = 48000, bits = 32;
                bool isFloat = true;
                bool followingDefault = false;
                string wantId = deviceId;
                string usedId = "";
                object obj;
                bool opened = false;

                if (wantPid > 0)
                {
                    // 按程序采集：没有设备，虚拟设备直接给一个只有这个进程声音的流。
                    // 激活、Initialize、GetService、Start 任一步失败都回退，且回退后不再反复重开。
                    try
                    {
                        client = Wasapi.ActivateProcessLoopback((uint)wantPid);
                        fmtPtr = Wasapi.AllocFloatStereoFormat(rate);
                        Wasapi.Check(client.Initialize(0, Wasapi.AUDCLNT_STREAMFLAGS_LOOPBACK, 2000000, 0, fmtPtr, IntPtr.Zero),
                                     "初始化按程序采集");
                        Guid iidCapP = typeof(IAudioCaptureClient).GUID;
                        Wasapi.Check(client.GetService(ref iidCapP, out obj), "取按程序采集接口");
                        capture = (IAudioCaptureClient)obj;
                        Wasapi.Check(client.Start(), "启动按程序采集");
                        opened = true;
                    }
                    catch (Exception ex)
                    {
                        Error = ex.Message;
                        LogCaptureError("按程序采集失败，改为整机采集", ex);
                        Wasapi.Release(capture); capture = null;
                        Wasapi.Release(client); client = null;
                        if (fmtPtr != IntPtr.Zero) { Wasapi.CoTaskMemFree(fmtPtr); fmtPtr = IntPtr.Zero; }
                    }
                }

                if (!opened)
                {
                    if (wantPid > 0)
                    {
                        plan.Decide(false, Error);
                        Error = "按程序采集不可用，已改为整机采集" + (string.IsNullOrEmpty(Error) ? "" : "：" + Error);
                    }
                    else
                    {
                        plan.Decide(false, null);
                    }
                    en = Wasapi.NewEnumerator();
                    device = Wasapi.OpenRender(en, wantId, out usedId);
                    followingDefault = wantId.Length == 0 || usedId != wantId;

                    Guid iidClient = typeof(IAudioClient).GUID;
                    Wasapi.Check(device.Activate(ref iidClient, Wasapi.CLSCTX_ALL, IntPtr.Zero, out obj), "打开音频设备");
                    client = (IAudioClient)obj;

                    Wasapi.Check(client.GetMixFormat(out fmtPtr), "读取混音格式");
                    isFloat = Wasapi.ParseFormat(fmtPtr, out channels, out rate, out bits);

                    // 缓冲给 200 ms：只是上限，我们每 1–2 ms 就取一次，不增加延迟。
                    Wasapi.Check(client.Initialize(0, Wasapi.AUDCLNT_STREAMFLAGS_LOOPBACK, 2000000, 0, fmtPtr, IntPtr.Zero),
                                 "初始化系统声音采集");
                    Guid iidCap = typeof(IAudioCaptureClient).GUID;
                    Wasapi.Check(client.GetService(ref iidCap, out obj), "取采集接口");
                    capture = (IAudioCaptureClient)obj;
                    Wasapi.Check(client.Start(), "启动采集");
                }
                else
                {
                    plan.Decide(true, null);
                }

                Plan = plan;
                Capturing = true;

                float[] stereo = new float[4096 * 2];
                float[] fbuf = new float[4096 * 8];
                byte[] bbuf = new byte[0];
                int bytesPerSample = bits / 8;
                long lastCheck = Proto.NowNs();

                while (running && gen == generation)
                {
                    uint available;
                    Wasapi.Check(capture.GetNextPacketSize(out available), "查询可用数据");
                    if (available == 0)
                    {
                        Thread.Sleep(1);
                        long now = Proto.NowNs();
                        if (now - lastCheck > 1000000000L)
                        {
                            lastCheck = now;
                            // 用户改了选择（按请求的 pid 判断，不是生效的 pid），或者系统默认播放设备换了：重开。
                            if (gen != generation || requestedPid != wantPid) return;
                            // 按程序采集：目标程序退出了就回整机（并清掉请求，免得看门狗反复重开失败）。
                            if (wantPid > 0 && ProcessGone(wantPid))
                            {
                                // 不写 Error：这会让界面临时显示「无法发送」，而回退要等看门狗（≤3 s）。
                                // 只把原因交给 CapturePlan，界面用 CaptureFallback 展示。
                                requestedPid = 0;
                                running = false;
                                Error = null;
                                Plan.Decide(false, "程序已退出");
                                return;
                            }
                            if (en != null)
                            {
                                if (deviceId != wantId) return;
                                if (followingDefault && Wasapi.DefaultRenderId(en) != usedId) return;
                            }
                        }
                        continue;
                    }

                    IntPtr data;
                    uint frames, flags;
                    ulong devPos, qpcPos;
                    Wasapi.Check(capture.GetBuffer(out data, out frames, out flags, out devPos, out qpcPos), "取数据块");
                    try
                    {
                        int total = (int)frames;
                        bool silent = (flags & Wasapi.AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == IntPtr.Zero;
                        int done = 0;
                        while (done < total)
                        {
                            int n = Math.Min(total - done, 4096);
                            if (silent)
                            {
                                Array.Clear(stereo, 0, n * 2);
                            }
                            else if (isFloat)
                            {
                                int count = n * channels;
                                if (fbuf.Length < count) fbuf = new float[count];
                                Marshal.Copy(new IntPtr(data.ToInt64() + (long)done * channels * 4), fbuf, 0, count);
                                // 不管几声道，取前两个声道发出去（5.1/7.1 的前左/前右）。
                                for (int f = 0; f < n; f++)
                                {
                                    float l = fbuf[f * channels];
                                    stereo[f * 2] = l;
                                    stereo[f * 2 + 1] = channels >= 2 ? fbuf[f * channels + 1] : l;
                                }
                            }
                            else
                            {
                                int count = n * channels * bytesPerSample;
                                if (bbuf.Length < count) bbuf = new byte[count];
                                Marshal.Copy(new IntPtr(data.ToInt64() + (long)done * channels * bytesPerSample), bbuf, 0, count);
                                for (int f = 0; f < n; f++)
                                {
                                    float l = PcmSample(bbuf, (f * channels) * bytesPerSample, bytesPerSample);
                                    float r = channels >= 2 ? PcmSample(bbuf, (f * channels + 1) * bytesPerSample, bytesPerSample) : l;
                                    stereo[f * 2] = l;
                                    stereo[f * 2 + 1] = r;
                                }
                            }
                            Action<float[], int, int> cb = Audio;
                            if (cb != null && gen == generation) cb(stereo, n, rate);
                            done += n;
                        }
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }
                }
            }
            finally
            {
                Capturing = false;
                if (client != null) { try { client.Stop(); } catch { } }
                if (fmtPtr != IntPtr.Zero) Wasapi.CoTaskMemFree(fmtPtr);
                Wasapi.Release(capture);
                Wasapi.Release(client);
                Wasapi.Release(device);
                Wasapi.Release(en);
            }
        }

        /// 采集相关的错误写进 %APPDATA%\AudioBridge\error.log（与 Program 的崩溃日志同一份）。
        internal static void LogCaptureError(string what, Exception ex)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioBridge");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "error.log");
                System.IO.File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + what + "  " + ex + Environment.NewLine);
            }
            catch { }
        }

        private static float PcmSample(byte[] b, int o, int bytes)
        {
            if (bytes == 2) return (short)(b[o] | (b[o + 1] << 8)) / 32768f;
            if (bytes == 3) return ((b[o] << 8) | (b[o + 1] << 16) | (b[o + 2] << 24)) / 2147483648f;
            return (b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)) / 2147483648f;
        }
    }

    /// 播放：从 RingBridge 拉数据写进 WASAPI 共享模式的播放设备。
    internal sealed class RenderPlayer
    {
        private readonly RingBridge bridge;
        private Thread thread;
        private volatile bool running;
        private volatile string deviceId = "";

        public volatile float Volume = 0.8f;
        public volatile bool Muted;
        /// double 的读写在 32 位进程里不是原子的，这两个量用 Interlocked 包一下。
        private long inputRateBits = BitConverter.DoubleToInt64Bits(48000);
        private long targetMsBits = BitConverter.DoubleToInt64Bits(40);

        public volatile string Error;
        public volatile bool Playing;
        public volatile string ActiveDeviceName = "";
        /// 设备自身缓冲带来的延迟（毫秒，估算）。
        public volatile int DeviceLatencyMs;

        private readonly object peakGate = new object();
        private float peakL, peakR;

        /// <summary>混音模式：非 null 时直接从混音器取数（主缓冲不再使用）。</summary>
        public MixBus Mixer;

        public RenderPlayer(RingBridge bridge) { this.bridge = bridge; }

        public double InputRate
        {
            get { return BitConverter.Int64BitsToDouble(Interlocked.Read(ref inputRateBits)); }
            set { Interlocked.Exchange(ref inputRateBits, BitConverter.DoubleToInt64Bits(value)); }
        }

        public double TargetMs
        {
            get { return BitConverter.Int64BitsToDouble(Interlocked.Read(ref targetMsBits)); }
            set { Interlocked.Exchange(ref targetMsBits, BitConverter.DoubleToInt64Bits(value)); }
        }

        public bool IsRunning { get { return running; } }

        public void SetDevice(string id) { deviceId = id ?? ""; }

        public void TakePeaks(out float l, out float r)
        {
            lock (peakGate) { l = peakL; r = peakR; peakL = 0; peakR = 0; }
        }

        public void Start()
        {
            if (running) return;
            running = true;
            Error = null;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "ab-render";
            thread.Priority = ThreadPriority.Highest;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            Thread t = thread;
            if (t != null && t != Thread.CurrentThread) t.Join(1500);
            thread = null;
            Playing = false;
        }

        private void Loop()
        {
            Wasapi.timeBeginPeriod(1);
            uint taskIndex = 0;
            IntPtr mm = IntPtr.Zero;
            try { mm = Wasapi.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex); } catch { }
            try
            {
                while (running)
                {
                    try
                    {
                        Session();
                    }
                    catch (Exception ex)
                    {
                        Error = ex.Message;
                    }
                    Playing = false;
                    for (int i = 0; i < 50 && running; i++) Thread.Sleep(10);
                }
            }
            finally
            {
                if (mm != IntPtr.Zero) { try { Wasapi.AvRevertMmThreadCharacteristics(mm); } catch { } }
                Wasapi.timeEndPeriod(1);
            }
        }

        private void Session()
        {
            IMMDeviceEnumerator en = null;
            IMMDevice device = null;
            IAudioClient client = null;
            IAudioRenderClient render = null;
            IntPtr fmtPtr = IntPtr.Zero;
            AutoResetEvent evt = new AutoResetEvent(false);
            try
            {
                en = Wasapi.NewEnumerator();
                string wantId = deviceId;
                string usedId;
                device = Wasapi.OpenRender(en, wantId, out usedId);
                // 钉住的设备不在线时会落到系统默认上；之后每秒看一眼它回来没有。
                bool onFallback = wantId.Length > 0 && usedId != wantId;
                bool followingDefault = wantId.Length == 0 || onFallback;

                Guid iidClient = typeof(IAudioClient).GUID;
                object obj;
                Wasapi.Check(device.Activate(ref iidClient, Wasapi.CLSCTX_ALL, IntPtr.Zero, out obj), "打开播放设备");
                client = (IAudioClient)obj;

                Wasapi.Check(client.GetMixFormat(out fmtPtr), "读取混音格式");
                int channels, rate, bits;
                bool isFloat = Wasapi.ParseFormat(fmtPtr, out channels, out rate, out bits);
                if (!isFloat && bits != 16) throw new InvalidOperationException("播放设备格式不支持：" + bits + " bit PCM");

                long defPeriod, minPeriod;
                Wasapi.Check(client.GetDevicePeriod(out defPeriod, out minPeriod), "读取设备周期");
                Wasapi.Check(client.Initialize(0, Wasapi.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 0, 0, fmtPtr, IntPtr.Zero),
                             "初始化播放");
                Wasapi.Check(client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()), "设置播放事件");
                uint bufferFrames;
                Wasapi.Check(client.GetBufferSize(out bufferFrames), "读取缓冲大小");
                Guid iidRender = typeof(IAudioRenderClient).GUID;
                Wasapi.Check(client.GetService(ref iidRender, out obj), "取播放接口");
                render = (IAudioRenderClient)obj;
                Wasapi.Check(client.Start(), "启动播放");

                // 设备缓冲里最多保持两个周期（约 20 ms）的数据：再多就是白白增加延迟。
                int periodFrames = Math.Max(64, (int)(defPeriod * (long)rate / 10000000L));
                int keepFrames = Math.Min((int)bufferFrames, periodFrames * 2);
                DeviceLatencyMs = (int)(keepFrames * 1000L / rate);

                Error = null;
                Playing = true;

                float[] stereo = new float[(int)bufferFrames * 2 + 16];
                float[] outF = new float[(int)bufferFrames * channels + 16];
                short[] outS = isFloat ? null : new short[(int)bufferFrames * channels + 16];
                long lastCheck = Proto.NowNs();
                int timeouts = 0;

                while (running)
                {
                    if (!evt.WaitOne(200))
                    {
                        // 事件一直不来说明设备流已经死了（常见于设备被禁用/拔掉但没报错）。
                        if (++timeouts >= 5) throw new InvalidOperationException("播放设备无响应");
                    }
                    else
                    {
                        timeouts = 0;
                    }

                    long now = Proto.NowNs();
                    if (now - lastCheck > 1000000000L)
                    {
                        lastCheck = now;
                        string curWant = deviceId;
                        if (curWant != wantId) return;
                        if (followingDefault && Wasapi.DefaultRenderId(en) != usedId) return;
                        if (onFallback)
                        {
                            IMMDevice probe = null;
                            int st = 0;
                            bool back = en.GetDevice(wantId, out probe) >= 0 && probe != null
                                        && probe.GetState(out st) >= 0 && st == Wasapi.DEVICE_STATE_ACTIVE;
                            Wasapi.Release(probe);
                            if (back) return;
                        }
                    }

                    uint padding;
                    Wasapi.Check(client.GetCurrentPadding(out padding), "查询播放缓冲");
                    int want = keepFrames - (int)padding;
                    if (want <= 0) continue;

                    double inRate = InputRate;
                    MixBus mixer = Mixer;
                    if (mixer != null) mixer.Pull(stereo, want, rate);
                    else bridge.Pull(stereo, want, rate, inRate, TargetMs / 1000.0 * inRate);

                    float pl = 0, pr = 0;
                    float gain = Muted ? 0f : Volume;
                    Array.Clear(outF, 0, want * channels);
                    for (int f = 0; f < want; f++)
                    {
                        float l = stereo[f * 2], r = stereo[f * 2 + 1];
                        float al = l < 0 ? -l : l, ar = r < 0 ? -r : r;
                        if (al > pl) pl = al;
                        if (ar > pr) pr = ar;
                        if (channels >= 2)
                        {
                            outF[f * channels] = l * gain;
                            outF[f * channels + 1] = r * gain;
                        }
                        else
                        {
                            outF[f] = (l + r) * 0.5f * gain;
                        }
                    }
                    lock (peakGate)
                    {
                        if (pl > peakL) peakL = pl;
                        if (pr > peakR) peakR = pr;
                    }

                    IntPtr dst;
                    Wasapi.Check(render.GetBuffer((uint)want, out dst), "取播放缓冲");
                    if (isFloat)
                    {
                        Marshal.Copy(outF, 0, dst, want * channels);
                    }
                    else
                    {
                        int count = want * channels;
                        for (int i = 0; i < count; i++)
                        {
                            float v = outF[i];
                            if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                            outS[i] = (short)(v * 32767f);
                        }
                        Marshal.Copy(outS, 0, dst, count);
                    }
                    Wasapi.Check(render.ReleaseBuffer((uint)want, 0), "提交播放缓冲");
                }
            }
            finally
            {
                Playing = false;
                if (client != null) { try { client.Stop(); } catch { } }
                if (fmtPtr != IntPtr.Zero) Wasapi.CoTaskMemFree(fmtPtr);
                Wasapi.Release(render);
                Wasapi.Release(client);
                Wasapi.Release(device);
                Wasapi.Release(en);
                evt.Close();
            }
        }
    }

    // ---- COM 定义（方法顺序 = vtable 顺序，不能动）----

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    internal enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
                                     IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags,
                                    out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }
}
