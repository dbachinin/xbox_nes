#pragma once
// Minimal test helpers: CHECK macros, a tiny 6502 assembler, and a generated
// NROM test cartridge (our own code/graphics, so it is freely redistributable).

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <functional>
#include <map>
#include <string>
#include <vector>

inline int g_failures = 0;

#define CHECK(cond)                                                                    \
    do {                                                                               \
        if (!(cond)) {                                                                 \
            std::fprintf(stderr, "%s:%d: CHECK failed: %s\n", __FILE__, __LINE__, #cond); \
            ++g_failures;                                                              \
        }                                                                              \
    } while (0)

#define CHECK_EQ(a, b)                                                                 \
    do {                                                                               \
        auto va_ = (a);                                                                \
        auto vb_ = (b);                                                                \
        if (!(va_ == vb_)) {                                                           \
            std::fprintf(stderr, "%s:%d: CHECK_EQ failed: %s (%lld) != %s (%lld)\n", __FILE__, __LINE__, #a, \
                         (long long)va_, #b, (long long)vb_);                         \
            ++g_failures;                                                              \
        }                                                                              \
    } while (0)

// Runs the test named on the command line (one ctest entry per test).
inline int runTests(int argc, char** argv, const std::map<std::string, std::function<void()>>& tests) {
    if (argc < 2) {
        for (auto& [name, fn] : tests) { std::printf("[ RUN ] %s\n", name.c_str()); fn(); }
    } else {
        auto it = tests.find(argv[1]);
        if (it == tests.end()) { std::fprintf(stderr, "unknown test %s\n", argv[1]); return 2; }
        it->second();
    }
    if (g_failures) std::fprintf(stderr, "%d check(s) failed\n", g_failures);
    return g_failures ? 1 : 0;
}

struct Asm {
    explicit Asm(std::uint16_t origin) : org(origin) {}

    std::uint16_t pc() const { return std::uint16_t(org + code.size()); }
    Asm& label(const std::string& n) { labels[n] = pc(); return *this; }
    Asm& op(std::uint8_t o) { code.push_back(o); return *this; }
    Asm& imm(std::uint8_t o, std::uint8_t v) { code.push_back(o); code.push_back(v); return *this; }
    Asm& abs(std::uint8_t o, std::uint16_t a) {
        code.push_back(o); code.push_back(std::uint8_t(a)); code.push_back(std::uint8_t(a >> 8)); return *this;
    }
    Asm& absL(std::uint8_t o, const std::string& l) {
        code.push_back(o); fix.push_back({code.size(), l, false}); code.push_back(0); code.push_back(0); return *this;
    }
    Asm& br(std::uint8_t o, const std::string& l) {
        code.push_back(o); fix.push_back({code.size(), l, true}); code.push_back(0); return *this;
    }
    Asm& bytes(std::initializer_list<std::uint8_t> b) { code.insert(code.end(), b); return *this; }

    std::vector<std::uint8_t>& resolve() {
        for (auto& f : fix) {
            std::uint16_t target = labels.at(f.label);
            if (f.relative) {
                int off = int(target) - int(org + f.at + 1);
                if (off < -128 || off > 127) { std::fprintf(stderr, "branch out of range: %s\n", f.label.c_str()); std::abort(); }
                code[f.at] = std::uint8_t(std::int8_t(off));
            } else {
                code[f.at] = std::uint8_t(target);
                code[f.at + 1] = std::uint8_t(target >> 8);
            }
        }
        return code;
    }

    struct Fixup { std::size_t at; std::string label; bool relative; };
    std::uint16_t org;
    std::vector<std::uint8_t> code;
    std::map<std::string, std::uint16_t> labels;
    std::vector<Fixup> fix;
};

inline std::vector<std::uint8_t> inesHeader(std::uint8_t prg16k, std::uint8_t chr8k, std::uint8_t flags6 = 0,
                                            std::uint8_t flags7 = 0) {
    return {'N', 'E', 'S', 0x1A, prg16k, chr8k, flags6, flags7, 0, 0, 0, 0, 0, 0, 0, 0};
}

// Builds a 24 KiB NROM-128 cartridge:
//  * top half of the screen tile 1 (palette color $12), bottom half backdrop ($0F)
//  * one 8x8 sprite (color $2A) at x=$80, scanlines 64..71
//  * NMI does OAM DMA, reads pad 1 and moves the sprite right while Right is held,
//    and increments a frame counter at $0010.
inline std::vector<std::uint8_t> makeTestRom() {
    Asm a(0xC000);
    a.op(0x78).op(0xD8).imm(0xA2, 0xFF).op(0x9A)                // SEI CLD LDX #$FF TXS
     .imm(0xA9, 0x00).abs(0x8D, 0x2000).abs(0x8D, 0x2001)      // NMI + rendering off
     .label("vb1").abs(0x2C, 0x2002).br(0x10, "vb1")
     .label("vb2").abs(0x2C, 0x2002).br(0x10, "vb2")
     .imm(0xA9, 0x3F).abs(0x8D, 0x2006).imm(0xA9, 0x00).abs(0x8D, 0x2006)
     .imm(0xA2, 0x00)
     .label("pal").absL(0xBD, "palette").abs(0x8D, 0x2007).op(0xE8).imm(0xE0, 0x20).br(0xD0, "pal")
     .imm(0xA9, 0x20).abs(0x8D, 0x2006).imm(0xA9, 0x00).abs(0x8D, 0x2006)
     .imm(0xA9, 0x01).imm(0xA2, 0x02).imm(0xA0, 0x00)
     .label("fill1").abs(0x8D, 0x2007).op(0xC8).br(0xD0, "fill1").op(0xCA).br(0xD0, "fill1")
     .imm(0xA9, 0x00).imm(0xA2, 0x02)
     .label("fill0").abs(0x8D, 0x2007).op(0xC8).br(0xD0, "fill0").op(0xCA).br(0xD0, "fill0")
     .imm(0xA2, 0x00).imm(0xA9, 0xFF)
     .label("clr").abs(0x9D, 0x0200).op(0xE8).br(0xD0, "clr")
     .imm(0xA9, 0x3F).abs(0x8D, 0x0200).imm(0xA9, 0x02).abs(0x8D, 0x0201)
     .imm(0xA9, 0x00).abs(0x8D, 0x0202).imm(0xA9, 0x80).abs(0x8D, 0x0203)
     .imm(0xA9, 0x00).abs(0x8D, 0x2005).abs(0x8D, 0x2005)
     .imm(0xA9, 0x80).abs(0x8D, 0x2000)                        // NMI on
     .imm(0xA9, 0x1E).abs(0x8D, 0x2001)                        // BG + sprites, no left clipping
     .label("loop").absL(0x4C, "loop")
     .label("nmi").op(0x48)
     .imm(0xA9, 0x02).abs(0x8D, 0x4014)
     .imm(0xA9, 0x01).abs(0x8D, 0x4016).imm(0xA9, 0x00).abs(0x8D, 0x4016);
    for (int i = 0; i < 8; ++i) a.abs(0xAD, 0x4016);            // last read = Right
    a.imm(0x29, 0x01).br(0xF0, "noright").abs(0xEE, 0x0203)
     .label("noright").imm(0xE6, 0x10)
     .imm(0xA9, 0x00).abs(0x8D, 0x2005).abs(0x8D, 0x2005)
     .op(0x68).op(0x40)
     .label("irq").op(0x40)
     .label("palette")
     .bytes({0x0F, 0x12, 0x16, 0x30, 0x0F, 0x12, 0x16, 0x30, 0x0F, 0x12, 0x16, 0x30, 0x0F, 0x12, 0x16, 0x30,
             0x0F, 0x00, 0x00, 0x2A, 0x0F, 0x00, 0x00, 0x2A, 0x0F, 0x00, 0x00, 0x2A, 0x0F, 0x00, 0x00, 0x2A});
    auto& code = a.resolve();

    std::vector<std::uint8_t> rom = inesHeader(1, 1);
    std::vector<std::uint8_t> prg(16384, 0xEA);
    std::copy(code.begin(), code.end(), prg.begin());
    auto vec = [&](std::size_t at, std::uint16_t v) { prg[at] = std::uint8_t(v); prg[at + 1] = std::uint8_t(v >> 8); };
    vec(0x3FFA, a.labels["nmi"]);
    vec(0x3FFC, 0xC000);
    vec(0x3FFE, a.labels["irq"]);
    rom.insert(rom.end(), prg.begin(), prg.end());

    std::vector<std::uint8_t> chr(8192, 0);
    for (int i = 0; i < 8; ++i) { chr[16 + i] = 0xFF; chr[32 + i] = 0xFF; chr[40 + i] = 0xFF; } // tile1 = color1, tile2 = color3
    rom.insert(rom.end(), chr.begin(), chr.end());
    return rom;
}
