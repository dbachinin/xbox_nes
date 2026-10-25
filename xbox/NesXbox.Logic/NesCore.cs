using System;
using System.Runtime.InteropServices;

namespace NesXbox.Logic
{
    /// <summary>
    /// Managed handle over the native core's C ABI (core/include/nes/nes_c_api.h).
    /// Resolves "nescore" -> nescore.dll on Windows/Xbox, libnescore.so on Linux.
    /// Not thread-safe: one emulation thread owns an instance.
    /// </summary>
    public sealed class NesCore : IDisposable
    {
        const string Lib = "nescore";
        public const int Width = 256, Height = 240;

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr nes_create();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void nes_destroy(IntPtr m);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int nes_load_rom(IntPtr m, byte[] data, UIntPtr size);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void nes_reset(IntPtr m);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void nes_set_buttons(IntPtr m, int port, byte bits);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void nes_run_frame(IntPtr m);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern long nes_copy_frame(IntPtr m, byte[] dst, UIntPtr size, int scale);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern ulong nes_frame_hash(IntPtr m);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern UIntPtr nes_read_audio(IntPtr m, short[] dst, UIntPtr max);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern UIntPtr nes_battery_size(IntPtr m);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern UIntPtr nes_battery_read(IntPtr m, byte[] dst, UIntPtr size);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern UIntPtr nes_battery_write(IntPtr m, byte[] src, UIntPtr size);

        IntPtr handle;

        public NesCore()
        {
            handle = nes_create();
            if (handle == IntPtr.Zero) throw new OutOfMemoryException("nes_create failed");
        }

        /// <summary>Returns null on success, otherwise a user-facing error.</summary>
        public string LoadRom(byte[] rom)
        {
            int rc = nes_load_rom(Handle, rom, (UIntPtr)rom.Length);
            switch (rc)
            {
                case 0: return null;
                case 6: return "Unsupported mapper";
                case 5: return "ROM too large";
                default: return "Invalid ROM (code " + rc + ")";
            }
        }

        public void Reset() => nes_reset(Handle);
        public void SetButtons(int port, byte bits) => nes_set_buttons(Handle, port, bits);
        public void RunFrame() => nes_run_frame(Handle);
        public ulong FrameHash() => nes_frame_hash(Handle);

        public static int FrameBytes(int scale) => Width * scale * Height * scale * 4;

        /// <summary>Copies the frame (BGRA8, integer-scaled) into <paramref name="dst"/> in one call.</summary>
        public bool CopyFrame(byte[] dst, int scale) => nes_copy_frame(Handle, dst, (UIntPtr)dst.Length, scale) > 0;

        public int ReadAudio(short[] dst) => (int)nes_read_audio(Handle, dst, (UIntPtr)dst.Length);

        public byte[] ReadBattery()
        {
            int n = (int)nes_battery_size(Handle);
            if (n == 0) return null;
            var b = new byte[n];
            nes_battery_read(Handle, b, (UIntPtr)n);
            return b;
        }

        public void WriteBattery(byte[] data)
        {
            if (data != null) nes_battery_write(Handle, data, (UIntPtr)data.Length);
        }

        IntPtr Handle => handle != IntPtr.Zero ? handle : throw new ObjectDisposedException(nameof(NesCore));

        public void Dispose()
        {
            if (handle != IntPtr.Zero) { nes_destroy(handle); handle = IntPtr.Zero; }
            GC.SuppressFinalize(this);
        }

        ~NesCore() { if (handle != IntPtr.Zero) nes_destroy(handle); }
    }

    /// <summary>Controller bit layout shared with the native core.</summary>
    [Flags]
    public enum NesButtons : byte
    {
        None = 0, A = 0x01, B = 0x02, Select = 0x04, Start = 0x08, Up = 0x10, Down = 0x20, Left = 0x40, Right = 0x80,
    }
}
