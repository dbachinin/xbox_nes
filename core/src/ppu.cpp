#include "nes/ppu.hpp"

namespace nes {

namespace {

// 2C02 NTSC palette (0xAARRGGBB).
constexpr std::uint32_t kPalette[64] = {
    0xFF666666, 0xFF002A88, 0xFF1412A7, 0xFF3B00A4, 0xFF5C007E, 0xFF6E0040, 0xFF6C0600, 0xFF561D00,
    0xFF333500, 0xFF0B4800, 0xFF005200, 0xFF004F08, 0xFF00404D, 0xFF000000, 0xFF000000, 0xFF000000,
    0xFFADADAD, 0xFF155FD9, 0xFF4240FF, 0xFF7527FE, 0xFFA01ACC, 0xFFB71E7B, 0xFFB53120, 0xFF994E00,
    0xFF6B6D00, 0xFF388700, 0xFF0C9300, 0xFF008F32, 0xFF007C8D, 0xFF000000, 0xFF000000, 0xFF000000,
    0xFFFFFEFF, 0xFF64B0FF, 0xFF9290FF, 0xFFC676FF, 0xFFF36AFF, 0xFFFE6ECC, 0xFFFE8170, 0xFFEA9E22,
    0xFFBCBE00, 0xFF88D800, 0xFF5CE430, 0xFF45E082, 0xFF48CDDE, 0xFF4F4F4F, 0xFF000000, 0xFF000000,
    0xFFFFFEFF, 0xFFC0DFFF, 0xFFD3D2FF, 0xFFE8C8FF, 0xFFFBC2FF, 0xFFFEC4EA, 0xFFFECCC5, 0xFFF7D8A5,
    0xFFE4E594, 0xFFCFEF96, 0xFFBDF4AB, 0xFFB3F3CC, 0xFFB5EBF2, 0xFFB8B8B8, 0xFF000000, 0xFF000000,
};

std::uint8_t reverseBits(std::uint8_t b) {
    b = std::uint8_t((b & 0xF0) >> 4 | (b & 0x0F) << 4);
    b = std::uint8_t((b & 0xCC) >> 2 | (b & 0x33) << 2);
    return std::uint8_t((b & 0xAA) >> 1 | (b & 0x55) << 1);
}

} // namespace

void Ppu::reset() {
    ctrl_ = mask_ = status_ = oamAddr_ = 0;
    readBuffer_ = openBus_ = 0;
    v_ = t_ = 0;
    fineX_ = 0;
    w_ = false;
    scanline_ = dot_ = 0;
    oddFrame_ = frameComplete_ = false;
    spriteCount_ = 0;
}

std::uint16_t Ppu::ntIndex(std::uint16_t addr) const {
    std::uint16_t a = (addr - 0x2000) & 0x0FFF;
    std::uint16_t table = a >> 10, off = a & 0x03FF;
    switch (mapper_->mirroring()) {
    case Mirroring::Horizontal: table >>= 1; break;
    case Mirroring::Vertical: table &= 1; break;
    case Mirroring::SingleLow: table = 0; break;
    case Mirroring::SingleHigh: table = 1; break;
    case Mirroring::FourScreen: break;
    }
    return std::uint16_t(table * 0x400 + off);
}

std::uint8_t& Ppu::paletteRef(std::uint16_t addr) {
    std::uint16_t i = addr & 0x1F;
    if ((i & 0x13) == 0x10) i &= 0x0F; // $3F10/14/18/1C mirror $3F00/04/08/0C
    return palette_[i];
}

std::uint8_t Ppu::busRead(std::uint16_t addr) {
    addr &= 0x3FFF;
    if (addr < 0x2000) return mapper_->ppuRead(addr);
    if (addr < 0x3F00) return vram_[ntIndex(addr)];
    return paletteRef(addr);
}

void Ppu::busWrite(std::uint16_t addr, std::uint8_t v) {
    addr &= 0x3FFF;
    if (addr < 0x2000) mapper_->ppuWrite(addr, v);
    else if (addr < 0x3F00) vram_[ntIndex(addr)] = v;
    else paletteRef(addr) = v & 0x3F;
}

std::uint8_t Ppu::readRegister(std::uint16_t addr) {
    switch (addr & 7) {
    case 2: {
        std::uint8_t r = std::uint8_t((status_ & 0xE0) | (openBus_ & 0x1F));
        status_ &= 0x7F;
        w_ = false;
        openBus_ = r;
        return r;
    }
    case 4: openBus_ = oam_[oamAddr_]; return openBus_;
    case 7: {
        std::uint16_t a = v_ & 0x3FFF;
        std::uint8_t r;
        if (a >= 0x3F00) {
            r = std::uint8_t((paletteRef(a) & 0x3F) | (openBus_ & 0xC0));
            readBuffer_ = busRead(a - 0x1000);
        } else {
            r = readBuffer_;
            readBuffer_ = busRead(a);
        }
        v_ = (v_ + ((ctrl_ & 0x04) ? 32 : 1)) & 0x7FFF;
        openBus_ = r;
        return r;
    }
    default: return openBus_;
    }
}

void Ppu::writeRegister(std::uint16_t addr, std::uint8_t v) {
    openBus_ = v;
    switch (addr & 7) {
    case 0: ctrl_ = v; t_ = std::uint16_t((t_ & 0x73FF) | ((v & 0x03) << 10)); break;
    case 1: mask_ = v; break;
    case 3: oamAddr_ = v; break;
    case 4: oam_[oamAddr_++] = v; break;
    case 5:
        if (!w_) { t_ = std::uint16_t((t_ & 0x7FE0) | (v >> 3)); fineX_ = v & 7; }
        else t_ = std::uint16_t((t_ & 0x0C1F) | ((v & 0x07) << 12) | ((v & 0xF8) << 2));
        w_ = !w_;
        break;
    case 6:
        if (!w_) t_ = std::uint16_t((t_ & 0x00FF) | ((v & 0x3F) << 8));
        else { t_ = std::uint16_t((t_ & 0x7F00) | v); v_ = t_; }
        w_ = !w_;
        break;
    case 7:
        busWrite(v_ & 0x3FFF, v);
        v_ = (v_ + ((ctrl_ & 0x04) ? 32 : 1)) & 0x7FFF;
        break;
    default: break;
    }
}

void Ppu::incX() {
    if ((v_ & 0x001F) == 31) { v_ &= ~0x001F; v_ ^= 0x0400; }
    else ++v_;
}

void Ppu::incY() {
    if ((v_ & 0x7000) != 0x7000) { v_ += 0x1000; return; }
    v_ &= ~0x7000;
    int y = (v_ & 0x03E0) >> 5;
    if (y == 29) { y = 0; v_ ^= 0x0800; }
    else if (y == 31) y = 0;
    else ++y;
    v_ = std::uint16_t((v_ & ~0x03E0) | (y << 5));
}

void Ppu::loadShifters() {
    shiftPatLo_ = std::uint16_t((shiftPatLo_ & 0xFF00) | patLo_);
    shiftPatHi_ = std::uint16_t((shiftPatHi_ & 0xFF00) | patHi_);
    shiftAtLo_ = std::uint16_t((shiftAtLo_ & 0xFF00) | ((atByte_ & 1) ? 0xFF : 0));
    shiftAtHi_ = std::uint16_t((shiftAtHi_ & 0xFF00) | ((atByte_ & 2) ? 0xFF : 0));
}

void Ppu::evaluateSprites() {
    spriteCount_ = 0;
    int height = (ctrl_ & 0x20) ? 16 : 8;
    for (int i = 0; i < 64; ++i) {
        int row = scanline_ - oam_[i * 4];
        if (row < 0 || row >= height) continue;
        if (spriteCount_ == 8) { status_ |= 0x20; break; } // ponytail: no hardware overflow bug emulation
        std::uint8_t tile = oam_[i * 4 + 1], attr = oam_[i * 4 + 2];
        if (attr & 0x80) row = height - 1 - row;
        std::uint16_t a;
        if (height == 8) a = std::uint16_t(((ctrl_ & 0x08) ? 0x1000 : 0) + tile * 16 + row);
        else a = std::uint16_t(((tile & 1) ? 0x1000 : 0) + (tile & 0xFE) * 16 + (row >= 8 ? 16 : 0) + (row & 7));
        std::uint8_t lo = busRead(a), hi = busRead(a + 8);
        if (attr & 0x40) { lo = reverseBits(lo); hi = reverseBits(hi); }
        sprites_[spriteCount_++] = {oam_[i * 4 + 3], attr, lo, hi, i == 0};
    }
}

void Ppu::renderPixel() {
    int x = dot_ - 1, y = scanline_;
    std::uint8_t color;
    if (!rendering()) {
        // With rendering off the PPU shows the backdrop, or the palette entry v points at.
        color = (v_ & 0x3F00) == 0x3F00 ? paletteRef(v_) : palette_[0];
    } else {
        std::uint8_t bgPix = 0, bgPal = 0;
        if ((mask_ & 0x08) && (x >= 8 || (mask_ & 0x02))) {
            std::uint16_t bit = std::uint16_t(0x8000 >> fineX_);
            bgPix = std::uint8_t(((shiftPatLo_ & bit) ? 1 : 0) | ((shiftPatHi_ & bit) ? 2 : 0));
            bgPal = std::uint8_t(((shiftAtLo_ & bit) ? 1 : 0) | ((shiftAtHi_ & bit) ? 2 : 0));
        }
        std::uint8_t spPix = 0, spPal = 0;
        bool spBehind = false, spZero = false;
        if ((mask_ & 0x10) && (x >= 8 || (mask_ & 0x04))) {
            for (int i = 0; i < spriteCount_; ++i) {
                const Sprite& s = sprites_[i];
                int off = x - s.x;
                if (off < 0 || off > 7) continue;
                int b = 7 - off;
                std::uint8_t p = std::uint8_t(((s.lo >> b) & 1) | (((s.hi >> b) & 1) << 1));
                if (!p) continue;
                spPix = p;
                spPal = std::uint8_t((s.attr & 3) + 4);
                spBehind = s.attr & 0x20;
                spZero = s.zero;
                break;
            }
        }
        std::uint8_t pix = 0, pal = 0;
        if (bgPix && spPix) {
            if (spZero && x != 255) status_ |= 0x40;
            if (spBehind) { pix = bgPix; pal = bgPal; } else { pix = spPix; pal = spPal; }
        } else if (spPix) { pix = spPix; pal = spPal; }
        else if (bgPix) { pix = bgPix; pal = bgPal; }
        color = pix ? paletteRef(std::uint16_t(0x3F00 + pal * 4 + pix)) : palette_[0];
    }
    if (mask_ & 0x01) color &= 0x30;
    frame_.pixels[std::size_t(y) * Frame::kWidth + std::size_t(x)] = kPalette[color & 0x3F];
}

void Ppu::tick() {
    bool visible = scanline_ < 240, pre = scanline_ == 261;
    if (visible || pre) {
        if (pre && dot_ == 1) status_ &= 0x1F;
        if (rendering()) {
            if ((dot_ >= 2 && dot_ <= 257) || (dot_ >= 321 && dot_ <= 337)) {
                shiftPatLo_ <<= 1; shiftPatHi_ <<= 1; shiftAtLo_ <<= 1; shiftAtHi_ <<= 1;
                std::uint16_t bgTable = (ctrl_ & 0x10) ? 0x1000 : 0;
                switch ((dot_ - 1) & 7) {
                case 0: loadShifters(); ntByte_ = busRead(0x2000 | (v_ & 0x0FFF)); break;
                case 2: {
                    std::uint8_t at = busRead(std::uint16_t(0x23C0 | (v_ & 0x0C00) | ((v_ >> 4) & 0x38) | ((v_ >> 2) & 0x07)));
                    atByte_ = (at >> (((v_ >> 4) & 4) | (v_ & 2))) & 3;
                    break;
                }
                case 4: patLo_ = busRead(std::uint16_t(bgTable + ntByte_ * 16 + ((v_ >> 12) & 7))); break;
                case 6: patHi_ = busRead(std::uint16_t(bgTable + ntByte_ * 16 + ((v_ >> 12) & 7) + 8)); break;
                case 7: incX(); break;
                }
            }
            if (dot_ == 256) incY();
            if (dot_ == 257) {
                loadShifters();
                v_ = std::uint16_t((v_ & ~0x041F) | (t_ & 0x041F));
                if (visible) evaluateSprites(); else spriteCount_ = 0;
            }
            if (pre && dot_ >= 280 && dot_ <= 304) v_ = std::uint16_t((v_ & ~0x7BE0) | (t_ & 0x7BE0));
            // ponytail: MMC3 IRQ clocked at a fixed dot instead of tracking PPU A12; fine for the common BG=$0000/SPR=$1000 layout.
            if (dot_ == 260) mapper_->scanline();
        }
        if (visible && dot_ >= 1 && dot_ <= 256) renderPixel();
    }
    if (scanline_ == 241 && dot_ == 1) { status_ |= 0x80; frameComplete_ = true; }

    ++dot_;
    if (pre && dot_ == 340 && oddFrame_ && rendering()) dot_ = 341; // odd frames are one dot shorter
    if (dot_ > 340) {
        dot_ = 0;
        if (++scanline_ > 261) { scanline_ = 0; oddFrame_ = !oddFrame_; }
    }
}

} // namespace nes
