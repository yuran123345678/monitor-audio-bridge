// 音频桥 · Windows 端 · 环形缓冲 + 异步重采样
//
// Sources/RingBridge.swift 的逐行移植，两边行为要保持一致：
// 网络线程写、播放线程读；用「填充量闭环 + Catmull-Rom 插值」抵消两台机器的时钟偏差。
using System;

namespace AudioBridge
{
    internal sealed class RingBridge
    {
        private readonly int capacity;
        private readonly float[] buf;
        private long writeCount;
        private double readPos;
        private readonly object gate = new object();

        private bool primed;
        private double fillErr;
        private const double MaxTrim = 0.0015;   // ±0.15% ≈ ±2.6 音分

        public int Underruns;
        public int Overruns;
        private double lastFillFrames;

        public RingBridge(double seconds, double sampleRate)
        {
            capacity = Math.Max(1024, (int)(seconds * sampleRate));
            buf = new float[capacity * 2];
        }

        public bool IsPrimed { get { lock (gate) return primed; } }
        public double LastFillFrames { get { lock (gate) return lastFillFrames; } }

        /// 当前可读帧数（不消费）。混音器用它判断这一拍够不够取。
        public double AvailableFrames { get { lock (gate) return writeCount - readPos; } }

        public void Reset()
        {
            lock (gate)
            {
                writeCount = 0;
                readPos = 0;
                primed = false;
                fillErr = 0;
                Underruns = 0;
                Overruns = 0;
                lastFillFrames = 0;
                Array.Clear(buf, 0, buf.Length);
            }
        }

        private float Sample(long absIndex, int ch)
        {
            long idx = absIndex % capacity;
            if (idx < 0) idx += capacity;
            return buf[idx * 2 + ch];
        }

        /// src：交织双声道，从 offsetFrames 帧开始的 frames 帧。
        public void Push(float[] src, int offsetFrames, int frames)
        {
            if (frames <= 0) return;
            lock (gate)
            {
                long cap = capacity;
                // 快写满时把读指针往前挪，丢掉最旧的一小段，避免覆盖未读数据。
                if (writeCount - (long)readPos + frames > cap - 16)
                {
                    double newRead = writeCount + frames - (cap - 16);
                    if (newRead > readPos) { readPos = newRead; Overruns++; }
                }
                long b = writeCount;
                for (int i = 0; i < frames; i++)
                {
                    long idx = (b + i) % cap;
                    buf[idx * 2] = src[(offsetFrames + i) * 2];
                    buf[idx * 2 + 1] = src[(offsetFrames + i) * 2 + 1];
                }
                writeCount = b + frames;
            }
        }

        /// 从缓冲里按 outRate 取 frames 帧（交织双声道写进 dst），自动做 inRate→outRate 重采样。
        /// targetFrames 以输入采样率下的帧数计。
        public void Pull(float[] dst, int frames, double outRate, double inRate, double targetFrames)
        {
            if (frames <= 0) return;
            lock (gate)
            {
                double avail = writeCount - readPos;
                lastFillFrames = avail;

                // 起播/断流之后先攒够目标深度再出声。
                if (!primed)
                {
                    if (targetFrames > 0 && avail >= targetFrames)
                    {
                        primed = true;
                    }
                    else
                    {
                        Array.Clear(dst, 0, frames * 2);
                        // 只允许往前对齐到实时位置，绝不把读指针往回扳（否则断流时会复读最后几十毫秒）。
                        if (targetFrames > 0)
                        {
                            double pinned = Math.Max(0, writeCount - targetFrames);
                            if (pinned > readPos) readPos = pinned;
                        }
                        return;
                    }
                }

                // 缓冲涨过目标 80 ms：直接对齐，不靠微调慢慢收。
                if (inRate > 0 && outRate > 0 && targetFrames > 0 && avail > targetFrames + inRate * 0.08)
                {
                    readPos = writeCount - targetFrames;
                    avail = writeCount - readPos;
                    Overruns++;
                }
                lastFillFrames = avail;

                if (inRate <= 0 || outRate <= 0 || avail < 8)
                {
                    Array.Clear(dst, 0, frames * 2);
                    if (inRate > 0)
                    {
                        Underruns++;
                        primed = false;
                    }
                    return;
                }

                // 时钟伺服：先做约 2.6 s 的平滑，再乘 0.05，上限 ±0.15%。关键在「慢」，否则持续音会有颤音。
                double err = (avail - targetFrames) / Math.Max(targetFrames, 1);
                fillErr += (err - fillErr) * 0.008;
                double trim = Math.Max(-MaxTrim, Math.Min(MaxTrim, fillErr * 0.05));
                double inc = inRate / outRate * (1 + trim);

                // 快饿空时把最后 20 ms 淡出，硬切会「啪」一声。
                double fadeSpan = inRate * 0.02;
                bool fading = avail < fadeSpan;

                double pos = readPos;
                for (int n = 0; n < frames; n++)
                {
                    long b = (long)Math.Floor(pos);
                    float t = (float)(pos - b);
                    float t2 = t * t;
                    float t3 = t2 * t;
                    float a0 = -0.5f * t3 + t2 - 0.5f * t;
                    float a1 = 1.5f * t3 - 2.5f * t2 + 1.0f;
                    float a2 = -1.5f * t3 + 2.0f * t2 + 0.5f * t;
                    float a3 = 0.5f * t3 - 0.5f * t2;

                    float l = Sample(b - 1, 0) * a0 + Sample(b, 0) * a1 + Sample(b + 1, 0) * a2 + Sample(b + 2, 0) * a3;
                    float r = Sample(b - 1, 1) * a0 + Sample(b, 1) * a1 + Sample(b + 1, 1) * a2 + Sample(b + 2, 1) * a3;
                    float g = 1f;
                    if (fading)
                    {
                        double rem = avail - n * inc;
                        g = rem >= fadeSpan ? 1f : (float)Math.Max(0, Math.Min(1, rem / fadeSpan));
                    }
                    dst[n * 2] = l * g;
                    dst[n * 2 + 1] = r * g;
                    pos += inc;
                }
                readPos = pos;
                if (readPos > writeCount)
                {
                    readPos = writeCount;
                    Underruns++;
                }
            }
        }
    }
}
