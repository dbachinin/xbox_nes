#pragma once

#include <array>
#include <cstdint>

namespace nes {

struct Frame {
    static constexpr int kWidth = 256;
    static constexpr int kHeight = 240;
    // 0xAARRGGBB, i.e. BGRA byte order in memory on little-endian hosts.
    std::array<std::uint32_t, kWidth * kHeight> pixels{};

    // FNV-1a over the pixel values: stable regression fingerprint.
    std::uint64_t hash() const {
        std::uint64_t h = 1469598103934665603ull;
        for (std::uint32_t p : pixels) {
            for (int i = 0; i < 4; ++i) { h ^= (p >> (i * 8)) & 0xFF; h *= 1099511628211ull; }
        }
        return h;
    }
};

} // namespace nes
