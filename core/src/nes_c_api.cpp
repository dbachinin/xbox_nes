#include "nes/nes_c_api.h"

#include "nes/emulator.hpp"

#include <algorithm>
#include <cstring>
#include <new>

struct nes_machine {
    nes::NesMachine machine;
};

nes_machine* nes_create(void) { return new (std::nothrow) nes_machine(); }

void nes_destroy(nes_machine* m) { delete m; }

int nes_load_rom(nes_machine* m, const uint8_t* data, size_t size) {
    if (!m || (!data && size)) return int(nes::RomError::TooSmall);
    try {
        return int(m->machine.loadRom({data, size}));
    } catch (const std::bad_alloc&) {
        return int(nes::RomError::TooLarge);
    }
}

void nes_reset(nes_machine* m) { if (m) m->machine.reset(); }

void nes_set_buttons(nes_machine* m, int port, uint8_t bits) { if (m) m->machine.setButtons(port, bits); }

void nes_run_frame(nes_machine* m) { if (m) m->machine.runFrame(); }

int64_t nes_copy_frame(nes_machine* m, uint8_t* dst, size_t dst_size, int scale) {
    if (!m || !dst || scale < 1 || scale > 8) return -1;
    constexpr int W = nes::Frame::kWidth, H = nes::Frame::kHeight;
    size_t rowPixels = size_t(W) * scale;
    size_t needed = rowPixels * H * scale * 4;
    if (dst_size < needed) return -1;
    const auto& px = m->machine.frame().pixels;
    auto* out = reinterpret_cast<uint32_t*>(dst); // ponytail: assumes a 4-byte aligned buffer (managed arrays are)
    for (int y = 0; y < H; ++y) {
        uint32_t* row = out + size_t(y) * scale * rowPixels;
        for (int x = 0; x < W; ++x) std::fill_n(row + size_t(x) * scale, scale, px[size_t(y) * W + x]);
        for (int r = 1; r < scale; ++r) std::memcpy(row + r * rowPixels, row, rowPixels * 4);
    }
    return int64_t(needed);
}

uint64_t nes_frame_hash(nes_machine* m) { return m ? m->machine.frame().hash() : 0; }

size_t nes_read_audio(nes_machine* m, int16_t* dst, size_t max_samples) {
    if (!m || !dst) return 0;
    auto s = m->machine.audio();
    size_t n = std::min(max_samples, s.size());
    std::memcpy(dst, s.data(), n * sizeof(int16_t));
    return n;
}

size_t nes_save_state(nes_machine* m, uint8_t* dst, size_t dst_size) {
    if (!m) return 0;
    auto s = m->machine.saveState();
    if (dst && dst_size >= s.size()) std::memcpy(dst, s.data(), s.size());
    return s.size();
}

int nes_load_state(nes_machine* m, const uint8_t* src, size_t size) {
    if (!m || !src) return 1;
    return m->machine.loadState({src, size}) ? 0 : 1;
}

size_t nes_battery_size(nes_machine* m) {
    return (m && m->machine.hasBattery()) ? m->machine.prgRam().size() : 0;
}

size_t nes_battery_read(nes_machine* m, uint8_t* dst, size_t size) {
    size_t n = std::min(size, nes_battery_size(m));
    if (n) std::memcpy(dst, m->machine.prgRam().data(), n);
    return n;
}

size_t nes_battery_write(nes_machine* m, const uint8_t* src, size_t size) {
    size_t n = std::min(size, nes_battery_size(m));
    if (n) std::memcpy(m->machine.prgRam().data(), src, n);
    return n;
}
