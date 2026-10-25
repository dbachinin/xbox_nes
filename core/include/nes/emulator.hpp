#pragma once

#include "nes/apu.hpp"
#include "nes/bus.hpp"
#include "nes/cartridge.hpp"
#include "nes/cpu6502.hpp"
#include "nes/frame.hpp"
#include "nes/mapper.hpp"
#include "nes/ppu.hpp"

#include <array>
#include <cstdint>
#include <memory>
#include <span>

namespace nes {

// Controller bits, in the order the NES shift register reports them.
enum Button : std::uint8_t {
    kA = 0x01, kB = 0x02, kSelect = 0x04, kStart = 0x08,
    kUp = 0x10, kDown = 0x20, kLeft = 0x40, kRight = 0x80,
};

// One emulated console. Instances share no global state.
class NesMachine final : private CpuBus {
public:
    NesMachine();

    // Parses and inserts a cartridge, then powers on. On error the machine keeps no cartridge.
    RomError loadRom(std::span<const std::uint8_t> rom);
    bool hasCartridge() const { return mapper_ != nullptr; }
    void reset();

    void setButtons(int port, std::uint8_t bits) { if (port == 0 || port == 1) buttons_[port] = bits; }

    // Runs until the PPU enters vblank (one NTSC frame, ~29780 CPU cycles).
    const Frame& runFrame();
    const Frame& frame() const { return ppu_.frame(); }
    std::span<const std::int16_t> audio() const { return apu_.samples(); }

    bool hasBattery() const { return mapper_ && mapper_->cart().battery; }
    std::span<std::uint8_t> prgRam() { return mapper_ ? mapper_->prgRam() : std::span<std::uint8_t>{}; }

    Cpu6502& cpu() { return cpu_; }
    std::uint64_t frameCount() const { return frames_; }
    std::uint64_t cycles() const { return cycle_; }  // CPU cycles including DMA stalls
    // Raw bus access for debuggers and tests (advances time like the CPU would).
    std::uint8_t peek(std::uint16_t addr) { return read(addr); }

private:
    std::uint8_t read(std::uint16_t addr) override;
    void write(std::uint16_t addr, std::uint8_t value) override;
    void tick();                                 // one CPU cycle of PPU+APU time
    std::uint8_t readNoTick(std::uint16_t addr);
    void oamDma(std::uint8_t page);

    Cpu6502 cpu_{*this};
    Ppu ppu_;
    Apu apu_;
    std::unique_ptr<Mapper> mapper_;
    std::array<std::uint8_t, 2048> ram_{};
    std::array<std::uint8_t, 2> buttons_{}, shift_{};
    bool strobe_ = false, nmiLine_ = false;
    std::uint8_t openBus_ = 0;
    std::uint64_t cycle_ = 0, frames_ = 0;
};

} // namespace nes
