using System;
using System.Diagnostics;
using System.Threading;
using NesXbox.Logic;

namespace NesXbox.Services
{
    /// <summary>
    /// Runs the native core on a dedicated thread. The UI thread pulses <see cref="VsyncTick"/> once per
    /// display refresh (CompositionTarget.Rendering); the thread then emulates however many NTSC frames
    /// (60.0988 Hz) are due by wall-clock time, so there is no busy loop and no frame skipping to hide
    /// slowness: if it falls behind by more than 3 frames it resynchronises and counts dropped frames.
    /// Frames are handed over through two fixed buffers (bounded memory, one copy per frame).
    /// </summary>
    public sealed class EmulationSession : IDisposable
    {
        const double FrameSeconds = 1.0 / 60.0988;
        const int MaxCatchUp = 3;

        readonly NesCore core;
        readonly AudioOutput audio;
        readonly GamepadService pads;
        readonly AutoResetEvent vsync = new AutoResetEvent(false);
        readonly byte[][] buffers;
        readonly object swapLock = new object();
        readonly object coreLock = new object();   // core is touched by the emulation thread and by save/load calls
        readonly short[] audioScratch = new short[2048];
        Thread thread;
        volatile bool running, paused;
        int ready = -1, writing;
        bool hasNewFrame;

        public int Scale { get; }
        public int PixelWidth => NesCore.Width * Scale;
        public int PixelHeight => NesCore.Height * Scale;

        // Debug overlay metrics (written by the emulation thread).
        public double EffectiveFps { get; private set; }
        public double CoreMsPerFrame { get; private set; }
        public long DroppedFrames { get; private set; }
        public long Underruns => audio?.Underruns ?? 0;

        /// <summary>Raised on the emulation thread when RT is pressed.</summary>
        public event Action PauseRequested;
        /// <summary>Raised on the emulation thread when LT is pressed.</summary>
        public event Action StateMenuRequested;
        /// <summary>Raised on the emulation thread if the core throws; the session stops.</summary>
        public event Action<Exception> Faulted;

        public EmulationSession(NesCore core, int scale, AudioOutput audio, GamepadService pads)
        {
            this.core = core;
            this.audio = audio;
            this.pads = pads;
            Scale = scale;
            buffers = new[] { new byte[NesCore.FrameBytes(scale)], new byte[NesCore.FrameBytes(scale)] };
        }

        public bool Paused => paused;

        public void Start()
        {
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "NES emulation" };
            thread.Start();
        }

        public void VsyncTick() => vsync.Set();

        public void Pause() { paused = true; audio?.Clear(); }
        public void Resume() { paused = false; }

        /// <summary>Copies the newest completed frame into <paramref name="target"/>; false if none is new.</summary>
        public bool TakeFrame(Action<byte[]> target)
        {
            lock (swapLock)
            {
                if (!hasNewFrame || ready < 0) return false;
                target(buffers[ready]);
                hasNewFrame = false;
                return true;
            }
        }

        void Loop()
        {
            var clock = Stopwatch.StartNew();
            double next = 0, fpsWindowStart = 0;
            int fpsFrames = 0;
            var coreTime = new Stopwatch();
            try
            {
                while (running)
                {
                    vsync.WaitOne(100);
                    if (!running) break;
                    double now = clock.Elapsed.TotalSeconds;
                    if (paused) { next = now; continue; }
                    if (now - next > MaxCatchUp * FrameSeconds)
                    {
                        DroppedFrames += (long)((now - next) / FrameSeconds) - 1;
                        next = now - FrameSeconds;
                    }
                    bool produced = false;
                    lock (coreLock)
                    while (next <= now && running && !paused)
                    {
                        var input = pads.Poll();
                        if (input.PausePressed) { paused = true; PauseRequested?.Invoke(); break; }
                        if (input.StateMenuPressed) { paused = true; StateMenuRequested?.Invoke(); break; }
                        core.SetButtons(0, (byte)input.Player1);
                        core.SetButtons(1, (byte)input.Player2);
                        coreTime.Restart();
                        core.RunFrame();
                        CoreMsPerFrame = CoreMsPerFrame * 0.95 + coreTime.Elapsed.TotalMilliseconds * 0.05;
                        int n = core.ReadAudio(audioScratch);
                        audio?.Push(audioScratch, n);
                        next += FrameSeconds;
                        fpsFrames++;
                        produced = true;
                    }
                    if (produced)
                    {
                        lock (coreLock) core.CopyFrame(buffers[writing], Scale);
                        lock (swapLock) { ready = writing; writing ^= 1; hasNewFrame = true; }
                    }
                    if (now - fpsWindowStart >= 1.0)
                    {
                        EffectiveFps = fpsFrames / (now - fpsWindowStart);
                        fpsFrames = 0;
                        fpsWindowStart = now;
                    }
                }
            }
            catch (Exception e)
            {
                running = false;
                Faulted?.Invoke(e);
            }
        }

        public byte[] ReadBattery() { lock (coreLock) return core.ReadBattery(); }
        public byte[] SaveState() { lock (coreLock) return core.SaveState(); }
        public bool LoadState(byte[] state) { lock (coreLock) return core.LoadState(state); }

        public void Dispose()
        {
            running = false;
            vsync.Set();
            thread?.Join(2000);
            core.Dispose();
            vsync.Dispose();
        }
    }
}
