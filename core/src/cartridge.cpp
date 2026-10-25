#include "nes/cartridge.hpp"

#include <algorithm>

namespace nes {

std::string_view toString(RomError e) {
    switch (e) {
    case RomError::None: return "OK";
    case RomError::TooSmall: return "Invalid ROM: file too small";
    case RomError::BadMagic: return "Invalid ROM: not an iNES file";
    case RomError::ZeroPrg: return "Invalid ROM: no PRG ROM";
    case RomError::Truncated: return "Invalid ROM: truncated";
    case RomError::TooLarge: return "Invalid ROM: too large";
    case RomError::UnsupportedMapper: return "Unsupported mapper";
    }
    return "Unknown error";
}

bool isMapperSupported(std::uint16_t mapper) {
    switch (mapper) {
    case 0: case 1: case 2: case 3: case 4: case 7: return true;
    default: return false;
    }
}

ParseResult parseInes(std::span<const std::uint8_t> d) {
    ParseResult r;
    if (d.size() < 16) { r.error = RomError::TooSmall; return r; }
    if (d[0] != 'N' || d[1] != 'E' || d[2] != 'S' || d[3] != 0x1A) { r.error = RomError::BadMagic; return r; }
    if (d.size() > kMaxRomBytes) { r.error = RomError::TooLarge; return r; }

    Cartridge& c = r.cart;
    c.nes2 = (d[7] & 0x0C) == 0x08;
    std::size_t prgUnits = d[4];
    std::size_t chrUnits = d[5];
    if (c.nes2) {
        // Exponent-multiplier notation (nibble 0xF) is only used by odd dumps.
        if ((d[9] & 0x0F) == 0x0F || (d[9] >> 4) == 0x0F) { r.error = RomError::TooLarge; return r; }
        prgUnits |= std::size_t(d[9] & 0x0F) << 8;
        chrUnits |= std::size_t(d[9] >> 4) << 8;
        c.mapper = std::uint16_t((d[6] >> 4) | (d[7] & 0xF0) | ((d[8] & 0x0F) << 8));
        c.submapper = d[8] >> 4;
        unsigned ramShift = std::max(d[10] & 0x0F, d[10] >> 4); // volatile vs battery RAM
        c.prgRamSize = ramShift ? (64u << ramShift) : 0;
        c.pal = (d[12] & 0x03) == 1;
    } else {
        // Junk in bytes 12-15 ("DiskDude!") means byte 7 is garbage too.
        bool junk = d[12] || d[13] || d[14] || d[15];
        c.mapper = std::uint16_t((d[6] >> 4) | (junk ? 0 : (d[7] & 0xF0)));
        c.prgRamSize = 8192;
    }
    if (prgUnits == 0) { r.error = RomError::ZeroPrg; return r; }

    c.battery = d[6] & 0x02;
    if (d[6] & 0x08) c.mirroring = Mirroring::FourScreen;
    else c.mirroring = (d[6] & 0x01) ? Mirroring::Vertical : Mirroring::Horizontal;
    if (c.battery && c.prgRamSize == 0) c.prgRamSize = 8192;
    // Family Basic and MMC1/MMC3 boards commonly expose 8 KiB even if the header omits it.
    if (c.prgRamSize < 8192) c.prgRamSize = 8192;

    std::size_t offset = 16 + ((d[6] & 0x04) ? 512 : 0);
    std::size_t prgBytes = prgUnits * 16384;
    std::size_t chrBytes = chrUnits * 8192;
    if (offset + prgBytes + chrBytes > d.size()) { r.error = RomError::Truncated; return r; }

    c.prg.assign(d.begin() + offset, d.begin() + offset + prgBytes);
    offset += prgBytes;
    if (chrBytes) {
        c.chr.assign(d.begin() + offset, d.begin() + offset + chrBytes);
    } else {
        std::size_t chrRam = 8192;
        if (c.nes2) {
            unsigned shift = std::max(d[11] & 0x0F, d[11] >> 4);
            if (shift) chrRam = std::max<std::size_t>(64u << shift, 8192);
        }
        c.chr.assign(chrRam, 0);
        c.chrIsRam = true;
    }

    if (!isMapperSupported(c.mapper)) r.error = RomError::UnsupportedMapper;
    return r;
}

} // namespace nes
