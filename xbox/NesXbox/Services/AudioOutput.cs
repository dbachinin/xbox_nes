using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.MediaProperties;
using Windows.Media.Render;

namespace NesXbox.Services
{
    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }

    /// <summary>
    /// 48 kHz mono output through AudioGraph. The emulator pushes ~800 samples per frame into a bounded
    /// ring; the graph pulls on its own clock. Overflow drops the oldest audio, underrun plays silence.
    /// </summary>
    public sealed class AudioOutput : IDisposable
    {
        const int SampleRate = 48000;
        const int Capacity = SampleRate / 5;         // 200 ms hard bound
        const int TargetLatency = SampleRate / 20;   // trim back to ~50 ms when the ring runs ahead

        readonly short[] ring = new short[Capacity];
        int head, count;
        readonly object gate = new object();
        AudioGraph graph;
        AudioFrameInputNode input;

        public long Underruns { get; private set; }

        public static async Task<AudioOutput> CreateAsync()
        {
            var settings = new AudioGraphSettings(AudioRenderCategory.GameMedia) { QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency };
            var result = await AudioGraph.CreateAsync(settings);
            if (result.Status != AudioGraphCreationStatus.Success) return null;
            var output = await result.Graph.CreateDeviceOutputNodeAsync();
            if (output.Status != AudioDeviceNodeCreationStatus.Success) { result.Graph.Dispose(); return null; }

            var props = AudioEncodingProperties.CreatePcm(SampleRate, 1, 32);
            props.Subtype = MediaEncodingSubtypes.Float;
            var a = new AudioOutput { graph = result.Graph };
            a.input = a.graph.CreateFrameInputNode(props);
            a.input.AddOutgoingConnection(output.DeviceOutputNode);
            a.input.QuantumStarted += a.OnQuantumStarted;
            a.graph.Start();
            return a;
        }

        public void Push(short[] samples, int n)
        {
            lock (gate)
            {
                for (int i = 0; i < n; i++)
                {
                    ring[(head + count) % Capacity] = samples[i];
                    if (count < Capacity) count++;
                    else head = (head + 1) % Capacity;
                }
                // Keep latency bounded when the emulator runs slightly faster than the audio clock.
                if (count > TargetLatency * 2) { int drop = count - TargetLatency; head = (head + drop) % Capacity; count -= drop; }
            }
        }

        public void Clear() { lock (gate) { head = 0; count = 0; } }

        unsafe void OnQuantumStarted(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
        {
            int required = args.RequiredSamples;
            if (required <= 0) return;
            var frame = new AudioFrame((uint)(required * sizeof(float)));
            using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (var reference = buffer.CreateReference())
            {
                ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* data, out uint capacity);
                float* f = (float*)data;
                int n = Math.Min(required, (int)(capacity / sizeof(float)));
                lock (gate)
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (count > 0) { f[i] = ring[head] / 32768f; head = (head + 1) % Capacity; count--; }
                        else { f[i] = 0; if (i == 0) Underruns++; }
                    }
                }
            }
            sender.AddFrame(frame);
        }

        public void Dispose()
        {
            graph?.Stop();
            input?.Dispose();
            graph?.Dispose();
            graph = null;
        }
    }
}
