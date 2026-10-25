#pragma once

#include <cstddef>
#include <cstdint>
#include <span>
#include <string_view>
#include <vector>

namespace nes {

enum class Mirroring : std::uint8_t { Horizontal, Vertical, SingleLow, SingleHigh, FourScreen };

enum class RomError : std::uint8_t {
    None,
    TooSmall,          // shorter than the 16-byte header
    BadMagic,          // missing "NES\x1A"
    ZeroPrg,           // header declares no PRG ROM
    Truncated,         // file shorter than header-declared sizes
    TooLarge,          // exceeds kMaxRomBytes or uses exponent size notation
    UnsupportedMapper, // header parsed fine, mapper not implemented
};

std::string_view toString(RomError e);

// Untrusted input is capped well above the largest real iNES image.
inline constexpr std::size_t kMaxRomBytes = 32u * 1024u * 1024u;

struct Cartridge {
    std::vector<std::uint8_t> prg;
    std::vector<std::uint8_t> chr;   // CHR ROM, or zero-filled CHR RAM when chrIsRam
    bool chrIsRam = false;
    std::size_t prgRamSize = 0;      // battery or work RAM at $6000-$7FFF
    std::uint16_t mapper = 0;
    std::uint8_t submapper = 0;
    Mirroring mirroring = Mirroring::Horizontal;
    bool battery = false;
    bool nes2 = false;
    bool pal = false;                // emulated with NTSC timing regardless
};

struct ParseResult {
    RomError error = RomError::None;
    Cartridge cart;                  // filled for None and UnsupportedMapper
    bool ok() const { return error == RomError::None; }
};

bool isMapperSupported(std::uint16_t mapper);

// Parses iNES 1.0 and NES 2.0 images. Never reads past `data`.
ParseResult parseInes(std::span<const std::uint8_t> data);

} // namespace nes
