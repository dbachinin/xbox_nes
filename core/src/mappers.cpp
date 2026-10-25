#include "nes/mapper.hpp"

#include <algorithm>

namespace nes {

Mapper::Mapper(Cartridge cart) : cart_(std::move(cart)), prgRam_(cart_.prgRamSize, 0) {}

std::uint8_t Mapper::prgByte(std::size_t bank, std::size_t bankSize, std::uint16_t offset) const {
    return cart_.prg[(bank * bankSize + offset) % cart_.prg.size()];
}

std::uint8_t& Mapper::chrByte(std::size_t bank, std::size_t bankSize, std::uint16_t offset) {
    return cart_.chr[(bank * bankSize + offset) % cart_.chr.size()];
}

// Base behaviour is NROM (mapper 0): fixed 16/32 KiB PRG, fixed 8 KiB CHR.
std::uint8_t Mapper::cpuRead(std::uint16_t addr, std::uint8_t openBus) {
    if (addr >= 0x8000) return cart_.prg[(addr - 0x8000) % cart_.prg.size()];
    if (addr >= 0x6000 && !prgRam_.empty()) return prgRam_[(addr - 0x6000) % prgRam_.size()];
    return openBus;
}

void Mapper::cpuWrite(std::uint16_t addr, std::uint8_t value, std::uint64_t) {
    if (addr >= 0x6000 && addr < 0x8000 && !prgRam_.empty()) prgRam_[(addr - 0x6000) % prgRam_.size()] = value;
}

std::uint8_t Mapper::ppuRead(std::uint16_t addr) { return cart_.chr[addr % cart_.chr.size()]; }

void Mapper::ppuWrite(std::uint16_t addr, std::uint8_t value) {
    if (cart_.chrIsRam) cart_.chr[addr % cart_.chr.size()] = value;
}

namespace {

class Mmc1 final : public Mapper {
public:
    using Mapper::Mapper;

    std::uint8_t cpuRead(std::uint16_t addr, std::uint8_t openBus) override {
        if (addr < 0x8000) return Mapper::cpuRead(addr, openBus);
        std::size_t outer = cart_.prg.size() > 256 * 1024 ? (chr0_ & 0x10) : 0; // SUROM 512 KiB
        std::size_t bank = (prg_ & 0x0F) | outer;
        std::size_t lastBank = (std::min<std::size_t>(cart_.prg.size(), 256 * 1024) / 16384 - 1) | outer;
        std::uint16_t off = addr & 0x3FFF;
        switch ((control_ >> 2) & 3) {
        case 0: case 1: return prgByte(bank >> 1, 32768, addr & 0x7FFF);
        case 2: return addr < 0xC000 ? prgByte(outer, 16384, off) : prgByte(bank, 16384, off);
        default: return addr < 0xC000 ? prgByte(bank, 16384, off) : prgByte(lastBank, 16384, off);
        }
    }

    void cpuWrite(std::uint16_t addr, std::uint8_t v, std::uint64_t cycle) override {
        if (addr < 0x8000) { Mapper::cpuWrite(addr, v, cycle); return; }
        bool consecutive = cycle == lastWrite_ + 1; // RMW dummy+real write: second is ignored
        lastWrite_ = cycle;
        if (consecutive) return;
        if (v & 0x80) { shift_ = 0x10; control_ |= 0x0C; return; }
        bool full = shift_ & 1;
        shift_ = std::uint8_t((shift_ >> 1) | ((v & 1) << 4));
        if (!full) return;
        switch ((addr >> 13) & 3) {
        case 0: control_ = shift_; break;
        case 1: chr0_ = shift_; break;
        case 2: chr1_ = shift_; break;
        case 3: prg_ = shift_; break;
        }
        shift_ = 0x10;
    }

    std::uint8_t ppuRead(std::uint16_t addr) override { return chrRef(addr); }
    void ppuWrite(std::uint16_t addr, std::uint8_t v) override { if (cart_.chrIsRam) chrRef(addr) = v; }

    Mirroring mirroring() const override {
        switch (control_ & 3) {
        case 0: return Mirroring::SingleLow;
        case 1: return Mirroring::SingleHigh;
        case 2: return Mirroring::Vertical;
        default: return Mirroring::Horizontal;
        }
    }

private:
    std::uint8_t& chrRef(std::uint16_t addr) {
        if (control_ & 0x10) return chrByte(addr < 0x1000 ? chr0_ : chr1_, 4096, addr & 0x0FFF);
        return chrByte(chr0_ >> 1, 8192, addr & 0x1FFF);
    }

    void state(StateIO& io) override { Mapper::state(io); io(shift_); io(control_); io(chr0_); io(chr1_); io(prg_); io(lastWrite_); }

    std::uint8_t shift_ = 0x10, control_ = 0x0C, chr0_ = 0, chr1_ = 0, prg_ = 0;
    std::uint64_t lastWrite_ = ~0ull - 1;
};

class UxRom final : public Mapper {
public:
    using Mapper::Mapper;
    std::uint8_t cpuRead(std::uint16_t addr, std::uint8_t openBus) override {
        if (addr < 0x8000) return Mapper::cpuRead(addr, openBus);
        std::size_t last = cart_.prg.size() / 16384 - 1;
        return prgByte(addr < 0xC000 ? bank_ : last, 16384, addr & 0x3FFF);
    }
    void cpuWrite(std::uint16_t addr, std::uint8_t v, std::uint64_t c) override {
        if (addr >= 0x8000) bank_ = v; else Mapper::cpuWrite(addr, v, c);
    }
public:
    void state(StateIO& io) override { Mapper::state(io); io(bank_); }
private:
    std::uint8_t bank_ = 0;
};

class CnRom final : public Mapper {
public:
    using Mapper::Mapper;
    void cpuWrite(std::uint16_t addr, std::uint8_t v, std::uint64_t c) override {
        if (addr >= 0x8000) bank_ = v; else Mapper::cpuWrite(addr, v, c);
    }
    std::uint8_t ppuRead(std::uint16_t addr) override { return chrByte(bank_, 8192, addr & 0x1FFF); }
    void ppuWrite(std::uint16_t addr, std::uint8_t v) override { if (cart_.chrIsRam) chrByte(bank_, 8192, addr & 0x1FFF) = v; }
public:
    void state(StateIO& io) override { Mapper::state(io); io(bank_); }
private:
    std::uint8_t bank_ = 0;
};

class AxRom final : public Mapper {
public:
    using Mapper::Mapper;
    std::uint8_t cpuRead(std::uint16_t addr, std::uint8_t openBus) override {
        if (addr < 0x8000) return openBus;
        return prgByte(reg_ & 0x07, 32768, addr & 0x7FFF);
    }
    void cpuWrite(std::uint16_t addr, std::uint8_t v, std::uint64_t) override { if (addr >= 0x8000) reg_ = v; }
    Mirroring mirroring() const override { return (reg_ & 0x10) ? Mirroring::SingleHigh : Mirroring::SingleLow; }
public:
    void state(StateIO& io) override { Mapper::state(io); io(reg_); }
private:
    std::uint8_t reg_ = 0;
};

class Mmc3 final : public Mapper {
public:
    using Mapper::Mapper;

    std::uint8_t cpuRead(std::uint16_t addr, std::uint8_t openBus) override {
        if (addr < 0x8000) return Mapper::cpuRead(addr, openBus);
        std::size_t count = cart_.prg.size() / 8192;
        std::size_t secondLast = count - 2, last = count - 1;
        bool mode = select_ & 0x40;
        std::size_t bank;
        switch ((addr >> 13) & 3) {
        case 0: bank = mode ? secondLast : regs_[6]; break;
        case 1: bank = regs_[7]; break;
        case 2: bank = mode ? regs_[6] : secondLast; break;
        default: bank = last; break;
        }
        return prgByte(bank, 8192, addr & 0x1FFF);
    }

    void cpuWrite(std::uint16_t addr, std::uint8_t v, std::uint64_t c) override {
        if (addr < 0x8000) { Mapper::cpuWrite(addr, v, c); return; }
        bool odd = addr & 1;
        switch ((addr >> 13) & 3) {
        case 0: if (odd) regs_[select_ & 7] = v; else select_ = v; break;
        case 1: if (!odd) mirrorH_ = v & 1; break; // odd: PRG RAM protect, ignored
        case 2: if (odd) { counter_ = 0; reload_ = true; } else latch_ = v; break;
        case 3: if (odd) enabled_ = true; else { enabled_ = false; pending_ = false; } break;
        }
    }

    std::uint8_t ppuRead(std::uint16_t addr) override { return chrRef(addr); }
    void ppuWrite(std::uint16_t addr, std::uint8_t v) override { if (cart_.chrIsRam) chrRef(addr) = v; }

    Mirroring mirroring() const override {
        if (cart_.mirroring == Mirroring::FourScreen) return Mirroring::FourScreen;
        return mirrorH_ ? Mirroring::Horizontal : Mirroring::Vertical;
    }
    bool irq() const override { return pending_; }

    void scanline() override {
        if (counter_ == 0 || reload_) { counter_ = latch_; reload_ = false; }
        else --counter_;
        if (counter_ == 0 && enabled_) pending_ = true;
    }

private:
    std::uint8_t& chrRef(std::uint16_t addr) {
        if (select_ & 0x80) addr ^= 0x1000;
        std::uint16_t a = addr & 0x1FFF;
        if (a < 0x0800) return chrByte(regs_[0] & 0xFE, 1024, a);
        if (a < 0x1000) return chrByte(regs_[1] & 0xFE, 1024, a - 0x0800);
        return chrByte(regs_[2 + ((a - 0x1000) >> 10)], 1024, a & 0x03FF);
    }

public:
    void state(StateIO& io) override {
        Mapper::state(io);
        io(regs_); io(select_); io(latch_); io(counter_); io(mirrorH_); io(reload_); io(enabled_); io(pending_);
    }

private:
    std::uint8_t regs_[8] = {0, 2, 4, 5, 6, 7, 0, 1};
    std::uint8_t select_ = 0, latch_ = 0, counter_ = 0;
    bool mirrorH_ = false, reload_ = false, enabled_ = false, pending_ = false;
};

} // namespace

std::unique_ptr<Mapper> createMapper(Cartridge cart) {
    switch (cart.mapper) {
    case 0: return std::make_unique<Mapper>(std::move(cart));
    case 1: return std::make_unique<Mmc1>(std::move(cart));
    case 2: return std::make_unique<UxRom>(std::move(cart));
    case 3: return std::make_unique<CnRom>(std::move(cart));
    case 4: return std::make_unique<Mmc3>(std::move(cart));
    case 7: return std::make_unique<AxRom>(std::move(cart));
    default: return nullptr;
    }
}

} // namespace nes
