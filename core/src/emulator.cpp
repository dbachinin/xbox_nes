#include "nes/emulator.hpp"

namespace nes {

NesMachine::NesMachine() {
    apu_.dmcRead = [this](std::uint16_t a) { return readNoTick(a); };
}

RomError NesMachine::loadRom(std::span<const std::uint8_t> rom) {
    ParseResult parsed = parseInes(rom);
    if (!parsed.ok()) { mapper_.reset(); return parsed.error; }
    mapper_ = createMapper(std::move(parsed.cart));
    ppu_.setMapper(mapper_.get());
    ram_.fill(0);
    reset();
    return RomError::None;
}

void NesMachine::reset() {
    if (!mapper_) return;
    ppu_.reset();
    apu_.reset();
    strobe_ = nmiLine_ = false;
    cpu_.regs = {};
    cpu_.reset();
}

void NesMachine::tick() {
    ++cycle_;
    ppu_.tick(); ppu_.tick(); ppu_.tick();
    apu_.tick();
    bool nmi = ppu_.nmiLine();
    if (nmi && !nmiLine_) cpu_.nmi();
    nmiLine_ = nmi;
    cpu_.setIrq(apu_.irq() || mapper_->irq());
}

std::uint8_t NesMachine::readNoTick(std::uint16_t addr) {
    if (addr < 0x2000) return ram_[addr & 0x07FF];
    if (addr >= 0x4020) return mapper_->cpuRead(addr, openBus_);
    return openBus_;
}

std::uint8_t NesMachine::read(std::uint16_t addr) {
    tick();
    std::uint8_t v;
    if (addr < 0x2000) v = ram_[addr & 0x07FF];
    else if (addr < 0x4000) v = ppu_.readRegister(addr);
    else if (addr == 0x4015) v = std::uint8_t((apu_.readStatus() & ~0x20) | (openBus_ & 0x20)); // bit 5 is open bus
    else if (addr == 0x4016 || addr == 0x4017) {
        int port = addr & 1;
        if (strobe_) shift_[port] = buttons_[port];
        v = std::uint8_t((openBus_ & 0xE0) | (shift_[port] & 1));
        if (!strobe_) shift_[port] = std::uint8_t((shift_[port] >> 1) | 0x80); // official pads return 1 after 8 reads
    } else if (addr >= 0x4020) v = mapper_->cpuRead(addr, openBus_);
    else v = openBus_;
    openBus_ = v;
    return v;
}

void NesMachine::write(std::uint16_t addr, std::uint8_t value) {
    tick();
    openBus_ = value;
    if (addr < 0x2000) ram_[addr & 0x07FF] = value;
    else if (addr < 0x4000) ppu_.writeRegister(addr, value);
    else if (addr == 0x4014) oamDma(value);
    else if (addr == 0x4016) {
        strobe_ = value & 1;
        if (strobe_) { shift_[0] = buttons_[0]; shift_[1] = buttons_[1]; }
    } else if (addr < 0x4018) apu_.writeRegister(addr, value);
    else if (addr >= 0x4020) mapper_->cpuWrite(addr, value, cycle_);
}

void NesMachine::oamDma(std::uint8_t page) {
    // 1 halt cycle (+1 alignment on odd cycles), then 256 read/write pairs.
    tick();
    if (cycle_ & 1) tick();
    for (int i = 0; i < 256; ++i) {
        tick();
        std::uint8_t v = readNoTick(std::uint16_t((page << 8) | i));
        if (((page << 8) | i) >= 0x2000 && ((page << 8) | i) < 0x4000) v = ppu_.readRegister(std::uint16_t(0x2000 | (i & 7)));
        tick();
        ppu_.oamWrite(v);
    }
}

const Frame& NesMachine::runFrame() {
    apu_.samples().clear();
    if (!mapper_) return ppu_.frame();
    ppu_.clearFrameComplete();
    // Bounded: a jammed CPU still burns cycles, so the PPU always reaches vblank.
    while (!ppu_.frameComplete()) {
        if (cpu_.jammed) { tick(); continue; }
        cpu_.step();
    }
    ++frames_;
    return ppu_.frame();
}

} // namespace nes
