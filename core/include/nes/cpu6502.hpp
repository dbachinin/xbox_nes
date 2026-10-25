#pragma once

#include "nes/bus.hpp"
#include "nes/state.hpp"

#include <cstdint>

namespace nes {

// Ricoh 2A03 CPU core: a 6502 without decimal mode. Every bus access is one
// cycle, including the dummy reads/writes real hardware performs.
class Cpu6502 {
public:
    enum Flag : std::uint8_t { C = 0x01, Z = 0x02, I = 0x04, D = 0x08, B = 0x10, U = 0x20, V = 0x40, N = 0x80 };

    struct Registers {
        std::uint16_t pc = 0;
        std::uint8_t a = 0, x = 0, y = 0, sp = 0x00, p = I | U; // reset pulls SP down to $FD
    };

    explicit Cpu6502(CpuBus& bus) : bus_(bus) {}

    void reset();                 // power-on/reset sequence: 7 cycles, loads $FFFC
    void step();                  // one instruction or one interrupt entry
    void nmi() { nmiPending_ = true; }  // edge already detected by the caller
    void setIrq(bool level) { irqLine_ = level; }
    void state(StateIO& io) { io(regs); io(cycles); io(jammed); io(nmiPending_); io(irqLine_); io(pollNow_); io(pollPrev_); }

    Registers regs;
    std::uint64_t cycles = 0;
    bool jammed = false;          // KIL/JAM opcode executed

private:
    std::uint8_t rd(std::uint16_t a) { ++cycles; std::uint8_t v = bus_.read(a); poll(); return v; }
    void wr(std::uint16_t a, std::uint8_t v) { ++cycles; bus_.write(a, v); poll(); }
    // Interrupts are sampled every cycle; the decision uses the sample from the
    // penultimate cycle of an instruction, which yields the CLI/SEI/PLP delay.
    void poll() { pollPrev_ = pollNow_; pollNow_ = nmiPending_ || (irqLine_ && !(regs.p & I)); }
    std::uint8_t fetch() { return rd(regs.pc++); }
    void push(std::uint8_t v) { wr(0x0100 | regs.sp--, v); }
    std::uint8_t pull() { return rd(0x0100 | ++regs.sp); }
    void setZN(std::uint8_t v) { regs.p = std::uint8_t((regs.p & ~(Z | N)) | (v ? 0 : Z) | (v & N)); }
    void setFlag(Flag f, bool on) { regs.p = on ? (regs.p | f) : (regs.p & ~f); }

    enum class Mode { Imm, Zp, ZpX, ZpY, Abs, AbsX, AbsY, IndX, IndY };
    std::uint16_t addr(Mode m, bool write);
    void interrupt(std::uint16_t vector, bool brk);
    void branch(bool take);

    void adc(std::uint8_t v);
    void cmp(std::uint8_t reg, std::uint8_t v);
    std::uint8_t asl(std::uint8_t v);
    std::uint8_t lsr(std::uint8_t v);
    std::uint8_t rol(std::uint8_t v);
    std::uint8_t ror(std::uint8_t v);

    template <typename F> void readOp(Mode m, F f);
    template <typename F> void rmwOp(Mode m, F f);
    void store(Mode m, std::uint8_t v);
    void storeHigh(Mode m, std::uint8_t v); // SHX/SHY/AHX/TAS quirk

    CpuBus& bus_;
    bool nmiPending_ = false;
    bool irqLine_ = false;
    bool pollNow_ = false, pollPrev_ = false;
};

} // namespace nes
