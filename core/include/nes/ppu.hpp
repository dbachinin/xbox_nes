#pragma once

#include "nes/frame.hpp"
#include "nes/mapper.hpp"

#include <array>
#include <cstdint>

namespace nes {

// 2C02 PPU, NTSC timing: 341 dots x 262 scanlines, 3 dots per CPU cycle.
class Ppu {
public:
    void setMapper(Mapper* m) { mapper_ = m; }
    void reset();

    void tick();                                // advance one dot
    std::uint8_t readRegister(std::uint16_t addr);
    void writeRegister(std::uint16_t addr, std::uint8_t v);
    void oamWrite(std::uint8_t v) { oam_[oamAddr_++] = v; }

    bool nmiLine() const { return (ctrl_ & 0x80) && (status_ & 0x80); }
    bool frameComplete() const { return frameComplete_; }
    void clearFrameComplete() { frameComplete_ = false; }
    const Frame& frame() const { return frame_; }

    int scanline() const { return scanline_; }
    int dot() const { return dot_; }

private:
    struct Sprite { std::uint8_t x, attr, lo, hi; bool zero; };

    std::uint8_t busRead(std::uint16_t addr);
    void busWrite(std::uint16_t addr, std::uint8_t v);
    std::uint16_t ntIndex(std::uint16_t addr) const;
    std::uint8_t& paletteRef(std::uint16_t addr);
    bool rendering() const { return mask_ & 0x18; }

    void incX();
    void incY();
    void loadShifters();
    void evaluateSprites();
    void renderPixel();

    Mapper* mapper_ = nullptr;
    std::array<std::uint8_t, 4096> vram_{};      // 2 KiB used unless four-screen
    std::array<std::uint8_t, 32> palette_{};
    std::array<std::uint8_t, 256> oam_{};
    std::uint8_t ctrl_ = 0, mask_ = 0, status_ = 0, oamAddr_ = 0;
    std::uint8_t readBuffer_ = 0, openBus_ = 0;
    std::uint16_t v_ = 0, t_ = 0;
    std::uint8_t fineX_ = 0;
    bool w_ = false;

    int scanline_ = 0, dot_ = 0;
    bool oddFrame_ = false, frameComplete_ = false;

    std::uint8_t ntByte_ = 0, atByte_ = 0, patLo_ = 0, patHi_ = 0;
    std::uint16_t shiftPatLo_ = 0, shiftPatHi_ = 0, shiftAtLo_ = 0, shiftAtHi_ = 0;

    std::array<Sprite, 8> sprites_{};
    int spriteCount_ = 0;

    Frame frame_;
};

} // namespace nes
