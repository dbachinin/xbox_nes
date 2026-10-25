using System;

namespace NesXbox.Logic
{
    public enum RomCheck
    {
        Ok,
        InvalidRom,
        UnsupportedMapper,
        TooLarge,
    }

    /// <summary>
    /// Managed mirror of core/src/cartridge.cpp validation, used before a ROM is
    /// committed to the library (the native core re-validates on load).
    /// </summary>
    public sealed class InesHeader
    {
        public const long MaxRomBytes = 32L * 1024 * 1024;
        static readonly int[] SupportedMappers = { 0, 1, 2, 3, 4, 7 };

        public int Mapper { get; private set; }
        public bool Nes2 { get; private set; }
        public bool Battery { get; private set; }
        public long PrgBytes { get; private set; }
        public long ChrBytes { get; private set; }
        public long RequiredBytes { get; private set; }

        public static bool IsMapperSupported(int mapper) => Array.IndexOf(SupportedMappers, mapper) >= 0;

        /// <summary>Parses the 16-byte header. Returns null if it is not an iNES header.</summary>
        public static InesHeader TryParse(byte[] h)
        {
            if (h == null || h.Length < 16 || h[0] != 'N' || h[1] != 'E' || h[2] != 'S' || h[3] != 0x1A) return null;
            var r = new InesHeader { Nes2 = (h[7] & 0x0C) == 0x08, Battery = (h[6] & 0x02) != 0 };
            long prg = h[4], chr = h[5];
            if (r.Nes2)
            {
                if ((h[9] & 0x0F) == 0x0F || (h[9] >> 4) == 0x0F) return null; // exponent notation unsupported
                prg |= (long)(h[9] & 0x0F) << 8;
                chr |= (long)(h[9] >> 4) << 8;
                r.Mapper = (h[6] >> 4) | (h[7] & 0xF0) | ((h[8] & 0x0F) << 8);
            }
            else
            {
                bool junk = h[12] != 0 || h[13] != 0 || h[14] != 0 || h[15] != 0;
                r.Mapper = (h[6] >> 4) | (junk ? 0 : (h[7] & 0xF0));
            }
            if (prg == 0) return null;
            r.PrgBytes = prg * 16384;
            r.ChrBytes = chr * 8192;
            r.RequiredBytes = 16 + ((h[6] & 0x04) != 0 ? 512 : 0) + r.PrgBytes + r.ChrBytes;
            return r;
        }

        /// <summary>Full check given the header bytes and the total file length.</summary>
        public static RomCheck Check(byte[] header, long fileLength, out InesHeader parsed)
        {
            parsed = null;
            if (fileLength > MaxRomBytes) return RomCheck.TooLarge;
            parsed = TryParse(header);
            if (parsed == null || fileLength < parsed.RequiredBytes) return RomCheck.InvalidRom;
            return IsMapperSupported(parsed.Mapper) ? RomCheck.Ok : RomCheck.UnsupportedMapper;
        }
    }
}
