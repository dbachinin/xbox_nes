#pragma once

#include "nes/cartridge.hpp"
#include "nes/state.hpp"

#include <cstdint>
#include <memory>
#include <span>

namespace nes {

// Cartridge board logic. CPU side covers $4020-$FFFF, PPU side $0000-$1FFF.
class Mapper {
public:
    explicit Mapper(Cartridge cart);
    virtual ~Mapper() = default;

    // `openBus` is returned for unmapped regions.
    virtual std::uint8_t cpuRead(std::uint16_t addr, std::uint8_t openBus);
    // `cycle` is the CPU cycle of the write; MMC1 ignores back-to-back writes.
    virtual void cpuWrite(std::uint16_t addr, std::uint8_t value, std::uint64_t cycle);
    virtual std::uint8_t ppuRead(std::uint16_t addr);
    virtual void ppuWrite(std::uint16_t addr, std::uint8_t value);

    virtual Mirroring mirroring() const { return cart_.mirroring; }
    virtual bool irq() const { return false; }
    // Called once per rendered scanline (dot 260) while rendering is enabled.
    virtual void scanline() {}
    // Bank registers + writable memory. Overrides call the base first.
    virtual void state(StateIO& io) { io(prgRam_); if (cart_.chrIsRam) io(cart_.chr); }

    std::span<std::uint8_t> prgRam() { return prgRam_; }
    const Cartridge& cart() const { return cart_; }

protected:
    std::uint8_t prgByte(std::size_t bank, std::size_t bankSize, std::uint16_t offset) const;
    std::uint8_t& chrByte(std::size_t bank, std::size_t bankSize, std::uint16_t offset);

    Cartridge cart_;
    std::vector<std::uint8_t> prgRam_;
};

// Returns nullptr for unsupported mappers.
std::unique_ptr<Mapper> createMapper(Cartridge cart);

} // namespace nes
