#pragma once

#include <cstdint>

namespace nes {

// One call == one CPU cycle. The machine advances PPU/APU inside these calls.
class CpuBus {
public:
    virtual ~CpuBus() = default;
    virtual std::uint8_t read(std::uint16_t addr) = 0;
    virtual void write(std::uint16_t addr, std::uint8_t value) = 0;
};

} // namespace nes
