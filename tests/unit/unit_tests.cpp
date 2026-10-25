#include "../test_util.hpp"

#include "nes/emulator.hpp"
#include "nes/nes_c_api.h"

#include <algorithm>
#include <array>
#include <memory>

using namespace nes;

namespace {

struct FlatBus : CpuBus {
    std::array<std::uint8_t, 65536> mem{};
    std::uint8_t read(std::uint16_t a) override { return mem[a]; }
    void write(std::uint16_t a, std::uint8_t v) override { mem[a] = v; }
};

// CPU with `code` at $8000 and reset vector pointing there; reset already executed.
struct CpuFixture {
    FlatBus bus;
    Cpu6502 cpu{bus};
    explicit CpuFixture(Asm& a) {
        auto& c = a.resolve();
        std::copy(c.begin(), c.end(), bus.mem.begin() + a.org);
        bus.mem[0xFFFC] = std::uint8_t(a.org);
        bus.mem[0xFFFD] = std::uint8_t(a.org >> 8);
        cpu.reset();
    }
    std::uint64_t step() { auto c0 = cpu.cycles; cpu.step(); return cpu.cycles - c0; }
};

std::vector<std::uint8_t> nrom(std::size_t prg16k, std::size_t chr8k, std::uint8_t flags6 = 0, std::uint8_t flags7 = 0) {
    auto r = inesHeader(std::uint8_t(prg16k), std::uint8_t(chr8k), flags6, flags7);
    r.resize(16 + prg16k * 16384 + chr8k * 8192, 0);
    return r;
}

// ---------------------------------------------------------------- cartridge
void cartValidNrom() {
    auto rom = nrom(1, 1, 0x01);
    rom[16] = 0x42;
    auto r = parseInes(rom);
    CHECK(r.ok());
    CHECK_EQ(r.cart.prg.size(), 16384u);
    CHECK_EQ(r.cart.chr.size(), 8192u);
    CHECK(!r.cart.chrIsRam);
    CHECK(r.cart.mirroring == Mirroring::Vertical);
    CHECK_EQ(r.cart.prg[0], 0x42);
}

void cartChrRamAndTrainer() {
    auto rom = nrom(2, 0, 0x04 | 0x02);  // trainer + battery
    rom.insert(rom.begin() + 16, 512, 0xAA);
    rom[16 + 512] = 0x99;
    auto r = parseInes(rom);
    CHECK(r.ok());
    CHECK(r.cart.chrIsRam);
    CHECK_EQ(r.cart.chr.size(), 8192u);
    CHECK_EQ(r.cart.prg[0], 0x99);  // trainer skipped
    CHECK(r.cart.battery);
}

void cartErrors() {
    CHECK(parseInes(std::vector<std::uint8_t>(10, 0)).error == RomError::TooSmall);
    auto bad = nrom(1, 1); bad[0] = 'X';
    CHECK(parseInes(bad).error == RomError::BadMagic);
    auto zero = nrom(1, 1); zero[4] = 0;
    CHECK(parseInes(zero).error == RomError::ZeroPrg);
    auto trunc = nrom(2, 1); trunc.resize(trunc.size() - 1);
    CHECK(parseInes(trunc).error == RomError::Truncated);
    auto onlyHeader = inesHeader(1, 1);
    CHECK(parseInes(onlyHeader).error == RomError::Truncated);
    auto unsup = nrom(1, 1, 0x50);  // mapper 5
    auto u = parseInes(unsup);
    CHECK(u.error == RomError::UnsupportedMapper);
    CHECK_EQ(u.cart.mapper, 5);
    std::vector<std::uint8_t> huge(kMaxRomBytes + 1, 0);
    huge[0] = 'N'; huge[1] = 'E'; huge[2] = 'S'; huge[3] = 0x1A; huge[4] = 1;
    CHECK(parseInes(huge).error == RomError::TooLarge);
}

void cartNes2AndDiskDude() {
    auto n2 = nrom(1, 1, 0x40, 0x08);  // NES 2.0, mapper 4 low nibble
    n2[8] = 0x01;                      // mapper bits 8-11 -> 0x104
    auto r = parseInes(n2);
    CHECK(r.cart.nes2);
    CHECK_EQ(r.cart.mapper, 0x104);
    CHECK(r.error == RomError::UnsupportedMapper);

    auto dd = nrom(1, 1, 0x10, 0x40);  // iNES with junk byte 7
    std::copy_n("DiskDude!", 9, dd.begin() + 7);
    dd[6] = 0x10; dd[7] = 'D';
    auto d = parseInes(dd);
    CHECK_EQ(d.cart.mapper, 1);  // upper nibble ignored
}

// ---------------------------------------------------------------- CPU
void cpuLoadStoreFlags() {
    Asm a(0x8000);
    a.imm(0xA9, 0x00).imm(0xA9, 0x80).abs(0x8D, 0x0200).imm(0xA2, 0x05).abs(0xBC, 0x01FB);
    CpuFixture f(a);
    CHECK_EQ(f.cpu.cycles, 7u);
    f.step(); CHECK(f.cpu.regs.p & Cpu6502::Z);
    f.step(); CHECK(f.cpu.regs.p & Cpu6502::N); CHECK(!(f.cpu.regs.p & Cpu6502::Z));
    CHECK_EQ(f.step(), 4u); CHECK_EQ(f.bus.mem[0x200], 0x80);
    f.step();
    f.bus.mem[0x0200] = 0x33;
    CHECK_EQ(f.step(), 5u);  // LDY abs,X crossing $01FB+5 -> $0200: +1 cycle
    CHECK_EQ(f.cpu.regs.y, 0x33);
}

void cpuAdcSbcNoDecimal() {
    Asm a(0x8000);
    a.op(0xF8).imm(0xA9, 0x09).op(0x18).imm(0x69, 0x01)   // SED: 2A03 still binary -> $0A
     .imm(0xA9, 0x7F).op(0x18).imm(0x69, 0x01)            // overflow
     .imm(0xA9, 0x00).op(0x38).imm(0xE9, 0x01);           // borrow
    CpuFixture f(a);
    for (int i = 0; i < 4; ++i) f.step();
    CHECK_EQ(f.cpu.regs.a, 0x0A);
    for (int i = 0; i < 3; ++i) f.step();
    CHECK_EQ(f.cpu.regs.a, 0x80);
    CHECK(f.cpu.regs.p & Cpu6502::V);
    for (int i = 0; i < 3; ++i) f.step();
    CHECK_EQ(f.cpu.regs.a, 0xFF);
    CHECK(!(f.cpu.regs.p & Cpu6502::C));
}

void cpuCycleCounts() {
    Asm a(0x8000);
    a.imm(0xA2, 0x01)            // 2
     .abs(0x9D, 0x0300)          // STA abs,X: 5 (always)
     .abs(0xFE, 0x0300)          // INC abs,X: 7
     .absL(0x20, "sub")          // JSR: 6
     .imm(0xA9, 0x00)            // 2
     .br(0xF0, "far")            // taken, same page: 3
     .op(0xEA)
     .label("far").op(0xEA)
     .label("sub").op(0x60);     // RTS: 6
    CpuFixture f(a);
    CHECK_EQ(f.step(), 2u);
    CHECK_EQ(f.step(), 5u);
    CHECK_EQ(f.step(), 7u);
    CHECK_EQ(f.step(), 6u);
    CHECK_EQ(f.step(), 6u);  // RTS
    CHECK_EQ(f.step(), 2u);
    CHECK_EQ(f.step(), 3u);
}

void cpuJmpIndirectBug() {
    Asm a(0x8000);
    a.abs(0x6C, 0x02FF);
    CpuFixture f(a);
    f.bus.mem[0x02FF] = 0x34;
    f.bus.mem[0x0200] = 0x12;  // high byte wraps within the page
    f.bus.mem[0x0300] = 0x99;
    CHECK_EQ(f.step(), 5u);
    CHECK_EQ(f.cpu.regs.pc, 0x1234);
}

void cpuInterrupts() {
    Asm a(0x8000);
    a.op(0x58).op(0xEA).op(0xEA).op(0x00).op(0xEA);  // CLI NOP NOP BRK
    CpuFixture f(a);
    f.bus.mem[0xFFFA] = 0x00; f.bus.mem[0xFFFB] = 0x90;
    f.bus.mem[0xFFFE] = 0x00; f.bus.mem[0xFFFF] = 0xA0;

    f.cpu.setIrq(true);
    f.step();                                  // CLI: IRQ is delayed one instruction
    CHECK_EQ(f.cpu.regs.pc, 0x8001);
    f.step();                                  // NOP runs
    CHECK_EQ(f.cpu.regs.pc, 0x8002);
    CHECK_EQ(f.step(), 7u);                    // IRQ entry
    CHECK_EQ(f.cpu.regs.pc, 0xA000);
    CHECK(f.cpu.regs.p & Cpu6502::I);
    CHECK_EQ(f.bus.mem[0x01FB] & Cpu6502::B, 0);  // B clear for hardware IRQ
    f.cpu.setIrq(false);

    f.cpu.nmi();
    f.step();
    CHECK_EQ(f.cpu.regs.pc, 0x9000);

    Asm b(0x8000);
    b.op(0x00);
    CpuFixture g(b);
    g.step();
    CHECK_EQ(g.cpu.regs.pc, 0x0000);              // vector $FFFE is zero in flat RAM
    CHECK(g.bus.mem[0x01FB] & Cpu6502::B);         // BRK pushes B set
    CHECK_EQ(g.bus.mem[0x01FC], 0x02);             // return address skips padding byte
}

void cpuUnofficialAndJam() {
    Asm a(0x8000);
    a.imm(0xA7, 0x10)            // LAX $10
     .imm(0xC7, 0x11)            // DCP $11
     .op(0x02);                  // JAM
    CpuFixture f(a);
    f.bus.mem[0x10] = 0x5A;
    f.bus.mem[0x11] = 0x5B;
    f.step();
    CHECK_EQ(f.cpu.regs.a, 0x5A); CHECK_EQ(f.cpu.regs.x, 0x5A);
    f.step();
    CHECK_EQ(f.bus.mem[0x11], 0x5A);
    CHECK(f.cpu.regs.p & Cpu6502::Z);
    f.step();
    CHECK(f.cpu.jammed);
}

// ---------------------------------------------------------------- mappers
std::vector<std::uint8_t> bankedRom(std::size_t prg16k, std::size_t chr8k, std::uint16_t mapper) {
    auto r = nrom(prg16k, chr8k, std::uint8_t((mapper & 0x0F) << 4), std::uint8_t(mapper & 0xF0));
    for (std::size_t b = 0; b < prg16k * 2; ++b) r[16 + b * 8192] = std::uint8_t(b);  // tag each 8 KiB PRG bank
    for (std::size_t b = 0; b < chr8k * 8; ++b) r[16 + prg16k * 16384 + b * 1024] = std::uint8_t(0x80 + b);
    return r;
}

std::unique_ptr<Mapper> mapperFor(const std::vector<std::uint8_t>& rom) {
    auto p = parseInes(rom);
    CHECK(p.ok());
    return createMapper(std::move(p.cart));
}

void mapperNromMirror() {
    auto m = mapperFor(bankedRom(1, 1, 0));
    CHECK_EQ(m->cpuRead(0x8000, 0), 0);
    CHECK_EQ(m->cpuRead(0xC000, 0), 0);    // 16 KiB mirrored
    CHECK_EQ(m->cpuRead(0xA000, 0), 1);
    m->cpuWrite(0x6000, 0x77, 0);
    CHECK_EQ(m->cpuRead(0x6000, 0), 0x77);
    CHECK_EQ(m->cpuRead(0x5000, 0x5A), 0x5A);  // open bus
}

void mapperUxRomCnRom() {
    auto ux = mapperFor(bankedRom(8, 0, 2));
    CHECK_EQ(ux->cpuRead(0xC000, 0), 14);  // last 16 KiB fixed
    ux->cpuWrite(0x8000, 3, 0);
    CHECK_EQ(ux->cpuRead(0x8000, 0), 6);
    ux->ppuWrite(0x0010, 0x55);
    CHECK_EQ(ux->ppuRead(0x0010), 0x55);   // CHR RAM

    auto cn = mapperFor(bankedRom(1, 4, 3));
    cn->cpuWrite(0x8000, 2, 0);
    CHECK_EQ(cn->ppuRead(0x0000), 0x80 + 16);
}

void mapperMmc1() {
    auto m = mapperFor(bankedRom(8, 2, 1));
    auto serial = [&](std::uint16_t addr, std::uint8_t v, std::uint64_t& cyc) {
        for (int i = 0; i < 5; ++i) { m->cpuWrite(addr, std::uint8_t((v >> i) & 1), cyc); cyc += 4; }
    };
    std::uint64_t cyc = 100;
    CHECK_EQ(m->cpuRead(0xC000, 0), 14);    // power-on: mode 3, last bank fixed at $C000
    serial(0xE000, 2, cyc);                 // PRG bank 2 at $8000
    CHECK_EQ(m->cpuRead(0x8000, 0), 4);
    serial(0x8000, 0x02, cyc);              // control: vertical mirroring, 32 KiB PRG mode
    CHECK(m->mirroring() == Mirroring::Vertical);
    CHECK_EQ(m->cpuRead(0x8000, 0), 4);     // bank 2>>1 = 32 KiB bank 1
    // RMW-style back-to-back writes: only the first one counts.
    m->cpuWrite(0x8000, 0x80, cyc);
    m->cpuWrite(0x8000, 0x01, cyc + 1);
    serial(0xE000, 0, cyc += 10);
    serial(0x8000, 0x0F, cyc);              // mode 3 again
    CHECK_EQ(m->cpuRead(0x8000, 0), 0);
}

void mapperMmc3Irq() {
    auto m = mapperFor(bankedRom(4, 4, 4));
    CHECK_EQ(m->cpuRead(0xE000, 0), 7);     // last 8 KiB
    m->cpuWrite(0x8000, 6, 0); m->cpuWrite(0x8001, 3, 0);
    CHECK_EQ(m->cpuRead(0x8000, 0), 3);
    m->cpuWrite(0xC000, 2, 0);              // latch 2
    m->cpuWrite(0xC001, 0, 0);              // reload
    m->cpuWrite(0xE001, 0, 0);              // enable
    m->scanline(); CHECK(!m->irq());        // counter = 2
    m->scanline(); CHECK(!m->irq());        // 1
    m->scanline(); CHECK(m->irq());         // 0 -> IRQ
    m->cpuWrite(0xE000, 0, 0);
    CHECK(!m->irq());
}

// ---------------------------------------------------------------- PPU / machine
void machineRendersTestRom() {
    NesMachine m;
    CHECK(m.loadRom(makeTestRom()) == RomError::None);
    for (int i = 0; i < 5; ++i) m.runFrame();
    const auto& px = m.frame().pixels;
    CHECK_EQ(px[10 * 256 + 10], 0xFF4240FFu);         // palette $12: top half background
    CHECK_EQ(px[200 * 256 + 10], 0xFF000000u);        // palette $0F: backdrop
    CHECK_EQ(px[66 * 256 + 0x82], 0xFF5CE430u);       // palette $2A: sprite
    CHECK_EQ(px[63 * 256 + 0x82], 0xFF4240FFu);       // just above the sprite
    // Determinism: a second machine produces the same frame.
    NesMachine m2;
    m2.loadRom(makeTestRom());
    for (int i = 0; i < 5; ++i) m2.runFrame();
    CHECK_EQ(m.frame().hash(), m2.frame().hash());
    CHECK_EQ(m.frameCount(), 5u);
}

void machineInputMovesSprite() {
    NesMachine m;
    m.loadRom(makeTestRom());
    for (int i = 0; i < 3; ++i) m.runFrame();
    std::uint64_t before = m.frame().hash();
    m.setButtons(0, kRight);
    for (int i = 0; i < 10; ++i) m.runFrame();
    CHECK(m.frame().hash() != before);
    CHECK_EQ(m.frame().pixels[66 * 256 + 0x80], 0xFF4240FFu);     // old position uncovered
    CHECK_EQ(m.frame().pixels[66 * 256 + 0x80 + 12], 0xFF5CE430u); // sprite moved right
}

void machineNmiPerFrame() {
    NesMachine m;
    m.loadRom(makeTestRom());
    for (int i = 0; i < 4; ++i) m.runFrame();  // NMI is enabled during the third frame
    std::uint8_t c0 = m.peek(0x0010);
    for (int i = 0; i < 30; ++i) m.runFrame();
    CHECK_EQ(std::uint8_t(m.peek(0x0010) - c0), 30);   // exactly one NMI per frame
}

void machineFrameCycles() {
    NesMachine m;
    m.loadRom(makeTestRom());
    m.runFrame();
    auto c0 = m.cycles();
    for (int i = 0; i < 60; ++i) m.runFrame();
    double perFrame = double(m.cycles() - c0) / 60.0;
    CHECK(perFrame > 29770 && perFrame < 29790);   // 341*262/3 minus odd-frame dot
}

void machineRandomPrgNoCrash() {
    // Garbage code must never crash the host: it may jam, loop or scribble, nothing more.
    std::uint32_t seed = 12345;
    auto rnd = [&] { seed = seed * 1664525u + 1013904223u; return std::uint8_t(seed >> 24); };
    for (std::uint16_t mapper : {0, 1, 2, 3, 4, 7}) {
        for (int trial = 0; trial < 3; ++trial) {
            auto rom = bankedRom(2, trial == 0 ? 0 : 1, mapper);
            for (std::size_t i = 16; i < rom.size(); ++i) rom[i] = rnd();
            NesMachine m;
            CHECK(m.loadRom(rom) == RomError::None);
            for (int f = 0; f < 3; ++f) m.runFrame();
            CHECK_EQ(m.frameCount(), 3u);
        }
    }
    NesMachine empty;
    empty.runFrame();  // no cartridge: harmless no-op
    CHECK(!empty.hasCartridge());
}

// ---------------------------------------------------------------- APU
void apuLengthAndSamples() {
    Asm a(0xC000);
    a.imm(0xA9, 0x01).abs(0x8D, 0x4015)       // enable pulse 1
     .imm(0xA9, 0xBF).abs(0x8D, 0x4000)       // duty 2, constant volume 15
     .imm(0xA9, 0xFD).abs(0x8D, 0x4002)
     .imm(0xA9, 0x08).abs(0x8D, 0x4003)       // length index 1 (254), period $0FD
     .label("l").absL(0x4C, "l");
    auto& code = a.resolve();
    auto rom = nrom(1, 1);
    std::copy(code.begin(), code.end(), rom.begin() + 16);
    rom[16 + 0x3FFC] = 0x00; rom[16 + 0x3FFD] = 0xC0;
    NesMachine m;
    CHECK(m.loadRom(rom) == RomError::None);
    m.runFrame();  // the power-on frame is short (starts at scanline 0)
    m.runFrame();
    std::size_t n = m.audio().size();
    CHECK(n >= 790 && n <= 810);                // 48000 / 60.1
    int peak = 0;
    for (auto s : m.audio()) peak = std::max(peak, std::abs(int(s)));
    CHECK(peak > 1000);                         // a tone is playing
    CHECK(m.peek(0x4015) & 0x01);               // length counter active
}

// ---------------------------------------------------------------- C ABI
void cApiRoundTrip() {
    auto rom = makeTestRom();
    nes_machine* m = nes_create();
    CHECK(m != nullptr);
    std::vector<std::uint8_t> junk(100, 0);
    CHECK_EQ(nes_load_rom(m, junk.data(), junk.size()), int(RomError::BadMagic));
    CHECK_EQ(nes_load_rom(m, rom.data(), rom.size()), 0);
    for (int i = 0; i < 5; ++i) nes_run_frame(m);
    std::vector<std::uint32_t> buf(256 * 240 * 4);
    CHECK_EQ(nes_copy_frame(m, reinterpret_cast<std::uint8_t*>(buf.data()), 100, 2), -1);
    CHECK_EQ(nes_copy_frame(m, reinterpret_cast<std::uint8_t*>(buf.data()), buf.size() * 4, 2), 256 * 240 * 16);
    CHECK_EQ(buf[(20 * 512) + 20], 0xFF4240FFu);
    CHECK_EQ(buf[(132 * 512) + 0x100 + 4], 0xFF5CE430u);   // sprite at 2x
    CHECK_EQ(buf[(133 * 512) + 0x100 + 5], 0xFF5CE430u);
    std::int16_t audio[2048];
    CHECK(nes_read_audio(m, audio, 2048) > 700);
    CHECK_EQ(nes_battery_size(m), 0u);
    nes_destroy(m);
}

} // namespace

int main(int argc, char** argv) {
    return runTests(argc, argv, {
        {"cart_valid_nrom", cartValidNrom},
        {"cart_chr_ram_trainer", cartChrRamAndTrainer},
        {"cart_errors", cartErrors},
        {"cart_nes2_diskdude", cartNes2AndDiskDude},
        {"cpu_load_store_flags", cpuLoadStoreFlags},
        {"cpu_adc_sbc_no_decimal", cpuAdcSbcNoDecimal},
        {"cpu_cycle_counts", cpuCycleCounts},
        {"cpu_jmp_indirect_bug", cpuJmpIndirectBug},
        {"cpu_interrupts", cpuInterrupts},
        {"cpu_unofficial_jam", cpuUnofficialAndJam},
        {"mapper_nrom", mapperNromMirror},
        {"mapper_uxrom_cnrom", mapperUxRomCnRom},
        {"mapper_mmc1", mapperMmc1},
        {"mapper_mmc3_irq", mapperMmc3Irq},
        {"machine_renders_test_rom", machineRendersTestRom},
        {"machine_input_moves_sprite", machineInputMovesSprite},
        {"machine_nmi_per_frame", machineNmiPerFrame},
        {"machine_frame_cycles", machineFrameCycles},
        {"machine_random_prg_no_crash", machineRandomPrgNoCrash},
        {"apu_length_samples", apuLengthAndSamples},
        {"c_api_round_trip", cApiRoundTrip},
    });
}
