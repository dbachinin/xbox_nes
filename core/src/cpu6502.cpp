#include "nes/cpu6502.hpp"

namespace nes {

void Cpu6502::reset() {
    // Reset is an interrupt sequence with the stack writes turned into reads.
    rd(regs.pc); rd(regs.pc);
    rd(0x0100 | regs.sp--); rd(0x0100 | regs.sp--); rd(0x0100 | regs.sp--);
    regs.p |= I;
    std::uint8_t lo = rd(0xFFFC);
    regs.pc = std::uint16_t(lo | (rd(0xFFFD) << 8));
    nmiPending_ = false;
    pollNow_ = pollPrev_ = false;
    jammed = false;
}

std::uint16_t Cpu6502::addr(Mode m, bool write) {
    switch (m) {
    case Mode::Imm: return regs.pc++;
    case Mode::Zp: return fetch();
    case Mode::ZpX: case Mode::ZpY: {
        std::uint8_t b = fetch();
        rd(b);
        return std::uint8_t(b + (m == Mode::ZpX ? regs.x : regs.y));
    }
    case Mode::Abs: {
        std::uint8_t lo = fetch();
        return std::uint16_t(lo | (fetch() << 8));
    }
    case Mode::IndX: {
        std::uint8_t b = fetch();
        rd(b);
        std::uint8_t p = std::uint8_t(b + regs.x);
        std::uint8_t lo = rd(p);
        return std::uint16_t(lo | (rd(std::uint8_t(p + 1)) << 8));
    }
    case Mode::AbsX: case Mode::AbsY: case Mode::IndY: {
        std::uint16_t base;
        if (m == Mode::IndY) {
            std::uint8_t b = fetch();
            std::uint8_t lo = rd(b);
            base = std::uint16_t(lo | (rd(std::uint8_t(b + 1)) << 8));
        } else {
            std::uint8_t lo = fetch();
            base = std::uint16_t(lo | (fetch() << 8));
        }
        std::uint16_t a = std::uint16_t(base + (m == Mode::AbsX ? regs.x : regs.y));
        if (write || (a & 0xFF00) != (base & 0xFF00)) rd(std::uint16_t((base & 0xFF00) | (a & 0x00FF)));
        return a;
    }
    }
    return 0;
}

void Cpu6502::interrupt(std::uint16_t vector, bool brk) {
    if (brk) fetch(); else { rd(regs.pc); rd(regs.pc); }
    push(std::uint8_t(regs.pc >> 8));
    push(std::uint8_t(regs.pc));
    // An NMI arriving during BRK/IRQ entry hijacks the vector.
    if (nmiPending_ && vector == 0xFFFE) vector = 0xFFFA;
    if (vector == 0xFFFA) nmiPending_ = false;
    push(std::uint8_t(regs.p | U | (brk ? B : 0)));
    regs.p |= I;
    std::uint8_t lo = rd(vector);
    regs.pc = std::uint16_t(lo | (rd(vector + 1) << 8));
}

void Cpu6502::branch(bool take) {
    std::int8_t off = std::int8_t(fetch());
    if (!take) return;
    rd(regs.pc);
    std::uint16_t target = std::uint16_t(regs.pc + off);
    if ((target & 0xFF00) != (regs.pc & 0xFF00)) rd(std::uint16_t((regs.pc & 0xFF00) | (target & 0xFF)));
    regs.pc = target;
}

void Cpu6502::adc(std::uint8_t v) {
    unsigned sum = regs.a + v + (regs.p & C);
    setFlag(V, (~(regs.a ^ v) & (regs.a ^ sum) & 0x80) != 0);
    setFlag(C, sum > 0xFF);
    regs.a = std::uint8_t(sum);
    setZN(regs.a);
}

void Cpu6502::cmp(std::uint8_t reg, std::uint8_t v) {
    setFlag(C, reg >= v);
    setZN(std::uint8_t(reg - v));
}

std::uint8_t Cpu6502::asl(std::uint8_t v) { setFlag(C, v & 0x80); v <<= 1; setZN(v); return v; }
std::uint8_t Cpu6502::lsr(std::uint8_t v) { setFlag(C, v & 1); v >>= 1; setZN(v); return v; }
std::uint8_t Cpu6502::rol(std::uint8_t v) {
    std::uint8_t r = std::uint8_t((v << 1) | (regs.p & C));
    setFlag(C, v & 0x80); setZN(r); return r;
}
std::uint8_t Cpu6502::ror(std::uint8_t v) {
    std::uint8_t r = std::uint8_t((v >> 1) | ((regs.p & C) << 7));
    setFlag(C, v & 1); setZN(r); return r;
}

template <typename F> void Cpu6502::readOp(Mode m, F f) { f(rd(addr(m, false))); }

template <typename F> void Cpu6502::rmwOp(Mode m, F f) {
    std::uint16_t a = addr(m, true);
    std::uint8_t v = rd(a);
    wr(a, v);
    wr(a, f(v));
}

void Cpu6502::store(Mode m, std::uint8_t v) { wr(addr(m, true), v); }

void Cpu6502::storeHigh(Mode m, std::uint8_t v) {
    std::uint16_t base;
    if (m == Mode::IndY) {
        std::uint8_t b = fetch();
        std::uint8_t lo = rd(b);
        base = std::uint16_t(lo | (rd(std::uint8_t(b + 1)) << 8));
    } else {
        std::uint8_t lo = fetch();
        base = std::uint16_t(lo | (fetch() << 8));
    }
    std::uint16_t a = std::uint16_t(base + (m == Mode::AbsX ? regs.x : regs.y));
    rd(std::uint16_t((base & 0xFF00) | (a & 0xFF)));
    std::uint8_t value = std::uint8_t(v & ((base >> 8) + 1));
    if ((a & 0xFF00) != (base & 0xFF00)) a = std::uint16_t((value << 8) | (a & 0xFF));
    wr(a, value);
}

void Cpu6502::step() {
    if (jammed) { ++cycles; return; }
    if (pollPrev_) {
        bool nmi = nmiPending_;
        nmiPending_ = false;
        interrupt(nmi ? 0xFFFA : 0xFFFE, false);
        pollNow_ = pollPrev_ = false;
        return;
    }

    std::uint8_t op = fetch();
    auto& r = regs;
    auto implied = [&] { rd(r.pc); };
    auto lda = [&](std::uint8_t v) { r.a = v; setZN(v); };
    auto ldx = [&](std::uint8_t v) { r.x = v; setZN(v); };
    auto ldy = [&](std::uint8_t v) { r.y = v; setZN(v); };
    auto lax = [&](std::uint8_t v) { r.a = r.x = v; setZN(v); };
    auto ora = [&](std::uint8_t v) { r.a |= v; setZN(r.a); };
    auto and_ = [&](std::uint8_t v) { r.a &= v; setZN(r.a); };
    auto eor = [&](std::uint8_t v) { r.a ^= v; setZN(r.a); };
    auto adcF = [&](std::uint8_t v) { adc(v); };
    auto sbcF = [&](std::uint8_t v) { adc(std::uint8_t(~v)); };
    auto cmpA = [&](std::uint8_t v) { cmp(r.a, v); };
    auto cmpX = [&](std::uint8_t v) { cmp(r.x, v); };
    auto cmpY = [&](std::uint8_t v) { cmp(r.y, v); };
    auto nop = [](std::uint8_t) {};
    auto bit = [&](std::uint8_t v) {
        setFlag(Z, (r.a & v) == 0);
        r.p = std::uint8_t((r.p & ~(N | V)) | (v & (N | V)));
    };
    auto aslF = [&](std::uint8_t v) { return asl(v); };
    auto lsrF = [&](std::uint8_t v) { return lsr(v); };
    auto rolF = [&](std::uint8_t v) { return rol(v); };
    auto rorF = [&](std::uint8_t v) { return ror(v); };
    auto inc = [&](std::uint8_t v) { v++; setZN(v); return v; };
    auto dec = [&](std::uint8_t v) { v--; setZN(v); return v; };
    auto slo = [&](std::uint8_t v) { v = asl(v); ora(v); return v; };
    auto rla = [&](std::uint8_t v) { v = rol(v); and_(v); return v; };
    auto sre = [&](std::uint8_t v) { v = lsr(v); eor(v); return v; };
    auto rra = [&](std::uint8_t v) { v = ror(v); adc(v); return v; };
    auto dcp = [&](std::uint8_t v) { v--; cmp(r.a, v); return v; };
    auto isb = [&](std::uint8_t v) { v++; adc(std::uint8_t(~v)); return v; };
    auto acc = [&](auto f) { implied(); r.a = f(r.a); };

    using M = Mode;
    switch (op) {
    // Loads
    case 0xA9: readOp(M::Imm, lda); break;  case 0xA5: readOp(M::Zp, lda); break;
    case 0xB5: readOp(M::ZpX, lda); break;  case 0xAD: readOp(M::Abs, lda); break;
    case 0xBD: readOp(M::AbsX, lda); break; case 0xB9: readOp(M::AbsY, lda); break;
    case 0xA1: readOp(M::IndX, lda); break; case 0xB1: readOp(M::IndY, lda); break;
    case 0xA2: readOp(M::Imm, ldx); break;  case 0xA6: readOp(M::Zp, ldx); break;
    case 0xB6: readOp(M::ZpY, ldx); break;  case 0xAE: readOp(M::Abs, ldx); break;
    case 0xBE: readOp(M::AbsY, ldx); break;
    case 0xA0: readOp(M::Imm, ldy); break;  case 0xA4: readOp(M::Zp, ldy); break;
    case 0xB4: readOp(M::ZpX, ldy); break;  case 0xAC: readOp(M::Abs, ldy); break;
    case 0xBC: readOp(M::AbsX, ldy); break;
    // Stores
    case 0x85: store(M::Zp, r.a); break;    case 0x95: store(M::ZpX, r.a); break;
    case 0x8D: store(M::Abs, r.a); break;   case 0x9D: store(M::AbsX, r.a); break;
    case 0x99: store(M::AbsY, r.a); break;  case 0x81: store(M::IndX, r.a); break;
    case 0x91: store(M::IndY, r.a); break;
    case 0x86: store(M::Zp, r.x); break;    case 0x96: store(M::ZpY, r.x); break;
    case 0x8E: store(M::Abs, r.x); break;
    case 0x84: store(M::Zp, r.y); break;    case 0x94: store(M::ZpX, r.y); break;
    case 0x8C: store(M::Abs, r.y); break;
    // ALU
    case 0x09: readOp(M::Imm, ora); break;  case 0x05: readOp(M::Zp, ora); break;
    case 0x15: readOp(M::ZpX, ora); break;  case 0x0D: readOp(M::Abs, ora); break;
    case 0x1D: readOp(M::AbsX, ora); break; case 0x19: readOp(M::AbsY, ora); break;
    case 0x01: readOp(M::IndX, ora); break; case 0x11: readOp(M::IndY, ora); break;
    case 0x29: readOp(M::Imm, and_); break; case 0x25: readOp(M::Zp, and_); break;
    case 0x35: readOp(M::ZpX, and_); break; case 0x2D: readOp(M::Abs, and_); break;
    case 0x3D: readOp(M::AbsX, and_); break; case 0x39: readOp(M::AbsY, and_); break;
    case 0x21: readOp(M::IndX, and_); break; case 0x31: readOp(M::IndY, and_); break;
    case 0x49: readOp(M::Imm, eor); break;  case 0x45: readOp(M::Zp, eor); break;
    case 0x55: readOp(M::ZpX, eor); break;  case 0x4D: readOp(M::Abs, eor); break;
    case 0x5D: readOp(M::AbsX, eor); break; case 0x59: readOp(M::AbsY, eor); break;
    case 0x41: readOp(M::IndX, eor); break; case 0x51: readOp(M::IndY, eor); break;
    case 0x69: readOp(M::Imm, adcF); break; case 0x65: readOp(M::Zp, adcF); break;
    case 0x75: readOp(M::ZpX, adcF); break; case 0x6D: readOp(M::Abs, adcF); break;
    case 0x7D: readOp(M::AbsX, adcF); break; case 0x79: readOp(M::AbsY, adcF); break;
    case 0x61: readOp(M::IndX, adcF); break; case 0x71: readOp(M::IndY, adcF); break;
    case 0xE9: case 0xEB: readOp(M::Imm, sbcF); break;
    case 0xE5: readOp(M::Zp, sbcF); break;  case 0xF5: readOp(M::ZpX, sbcF); break;
    case 0xED: readOp(M::Abs, sbcF); break; case 0xFD: readOp(M::AbsX, sbcF); break;
    case 0xF9: readOp(M::AbsY, sbcF); break; case 0xE1: readOp(M::IndX, sbcF); break;
    case 0xF1: readOp(M::IndY, sbcF); break;
    case 0xC9: readOp(M::Imm, cmpA); break; case 0xC5: readOp(M::Zp, cmpA); break;
    case 0xD5: readOp(M::ZpX, cmpA); break; case 0xCD: readOp(M::Abs, cmpA); break;
    case 0xDD: readOp(M::AbsX, cmpA); break; case 0xD9: readOp(M::AbsY, cmpA); break;
    case 0xC1: readOp(M::IndX, cmpA); break; case 0xD1: readOp(M::IndY, cmpA); break;
    case 0xE0: readOp(M::Imm, cmpX); break; case 0xE4: readOp(M::Zp, cmpX); break;
    case 0xEC: readOp(M::Abs, cmpX); break;
    case 0xC0: readOp(M::Imm, cmpY); break; case 0xC4: readOp(M::Zp, cmpY); break;
    case 0xCC: readOp(M::Abs, cmpY); break;
    case 0x24: readOp(M::Zp, bit); break;   case 0x2C: readOp(M::Abs, bit); break;
    // Read-modify-write
    case 0x0A: acc(aslF); break; case 0x4A: acc(lsrF); break;
    case 0x2A: acc(rolF); break; case 0x6A: acc(rorF); break;
    case 0x06: rmwOp(M::Zp, aslF); break;  case 0x16: rmwOp(M::ZpX, aslF); break;
    case 0x0E: rmwOp(M::Abs, aslF); break; case 0x1E: rmwOp(M::AbsX, aslF); break;
    case 0x46: rmwOp(M::Zp, lsrF); break;  case 0x56: rmwOp(M::ZpX, lsrF); break;
    case 0x4E: rmwOp(M::Abs, lsrF); break; case 0x5E: rmwOp(M::AbsX, lsrF); break;
    case 0x26: rmwOp(M::Zp, rolF); break;  case 0x36: rmwOp(M::ZpX, rolF); break;
    case 0x2E: rmwOp(M::Abs, rolF); break; case 0x3E: rmwOp(M::AbsX, rolF); break;
    case 0x66: rmwOp(M::Zp, rorF); break;  case 0x76: rmwOp(M::ZpX, rorF); break;
    case 0x6E: rmwOp(M::Abs, rorF); break; case 0x7E: rmwOp(M::AbsX, rorF); break;
    case 0xE6: rmwOp(M::Zp, inc); break;   case 0xF6: rmwOp(M::ZpX, inc); break;
    case 0xEE: rmwOp(M::Abs, inc); break;  case 0xFE: rmwOp(M::AbsX, inc); break;
    case 0xC6: rmwOp(M::Zp, dec); break;   case 0xD6: rmwOp(M::ZpX, dec); break;
    case 0xCE: rmwOp(M::Abs, dec); break;  case 0xDE: rmwOp(M::AbsX, dec); break;
    // Register ops
    case 0xAA: implied(); r.x = r.a; setZN(r.x); break;
    case 0xA8: implied(); r.y = r.a; setZN(r.y); break;
    case 0x8A: implied(); r.a = r.x; setZN(r.a); break;
    case 0x98: implied(); r.a = r.y; setZN(r.a); break;
    case 0xBA: implied(); r.x = r.sp; setZN(r.x); break;
    case 0x9A: implied(); r.sp = r.x; break;
    case 0xE8: implied(); r.x++; setZN(r.x); break;
    case 0xC8: implied(); r.y++; setZN(r.y); break;
    case 0xCA: implied(); r.x--; setZN(r.x); break;
    case 0x88: implied(); r.y--; setZN(r.y); break;
    // Flags
    case 0x18: implied(); setFlag(C, false); break;
    case 0x38: implied(); setFlag(C, true); break;
    case 0x58: implied(); setFlag(I, false); break;
    case 0x78: implied(); setFlag(I, true); break;
    case 0xB8: implied(); setFlag(V, false); break;
    case 0xD8: implied(); setFlag(D, false); break;
    case 0xF8: implied(); setFlag(D, true); break;
    // Stack
    case 0x48: implied(); push(r.a); break;
    case 0x08: implied(); push(r.p | B | U); break;
    case 0x68: implied(); rd(0x0100 | r.sp); r.a = pull(); setZN(r.a); break;
    case 0x28: implied(); rd(0x0100 | r.sp); r.p = std::uint8_t((pull() & ~B) | U); break;
    // Control flow
    case 0x4C: r.pc = addr(M::Abs, false); break;
    case 0x6C: {
        std::uint16_t p = addr(M::Abs, false);
        std::uint8_t lo = rd(p);
        r.pc = std::uint16_t(lo | (rd(std::uint16_t((p & 0xFF00) | ((p + 1) & 0xFF))) << 8));
        break;
    }
    case 0x20: {
        std::uint8_t lo = fetch();
        rd(0x0100 | r.sp);
        push(std::uint8_t(r.pc >> 8));
        push(std::uint8_t(r.pc));
        r.pc = std::uint16_t(lo | (fetch() << 8));
        break;
    }
    case 0x60: {
        implied(); rd(0x0100 | r.sp);
        std::uint8_t lo = pull();
        r.pc = std::uint16_t(lo | (pull() << 8));
        rd(r.pc++);
        break;
    }
    case 0x40: {
        implied(); rd(0x0100 | r.sp);
        r.p = std::uint8_t((pull() & ~B) | U);
        std::uint8_t lo = pull();
        r.pc = std::uint16_t(lo | (pull() << 8));
        break;
    }
    case 0x00: interrupt(0xFFFE, true); break;
    case 0x10: branch(!(r.p & N)); break; case 0x30: branch(r.p & N); break;
    case 0x50: branch(!(r.p & V)); break; case 0x70: branch(r.p & V); break;
    case 0x90: branch(!(r.p & C)); break; case 0xB0: branch(r.p & C); break;
    case 0xD0: branch(!(r.p & Z)); break; case 0xF0: branch(r.p & Z); break;
    case 0xEA: implied(); break;

    // ---- Unofficial opcodes (stable ones exact, unstable ones approximated) ----
    case 0x1A: case 0x3A: case 0x5A: case 0x7A: case 0xDA: case 0xFA: implied(); break;
    case 0x80: case 0x82: case 0x89: case 0xC2: case 0xE2: readOp(M::Imm, nop); break;
    case 0x04: case 0x44: case 0x64: readOp(M::Zp, nop); break;
    case 0x14: case 0x34: case 0x54: case 0x74: case 0xD4: case 0xF4: readOp(M::ZpX, nop); break;
    case 0x0C: readOp(M::Abs, nop); break;
    case 0x1C: case 0x3C: case 0x5C: case 0x7C: case 0xDC: case 0xFC: readOp(M::AbsX, nop); break;
    case 0xA7: readOp(M::Zp, lax); break;   case 0xB7: readOp(M::ZpY, lax); break;
    case 0xAF: readOp(M::Abs, lax); break;  case 0xBF: readOp(M::AbsY, lax); break;
    case 0xA3: readOp(M::IndX, lax); break; case 0xB3: readOp(M::IndY, lax); break;
    case 0xAB: readOp(M::Imm, lax); break;
    case 0x87: store(M::Zp, r.a & r.x); break;   case 0x97: store(M::ZpY, r.a & r.x); break;
    case 0x8F: store(M::Abs, r.a & r.x); break;  case 0x83: store(M::IndX, r.a & r.x); break;
#define RMW_GROUP(f, zp, zpx, abs, absx, absy, indx, indy) \
    case zp: rmwOp(M::Zp, f); break; case zpx: rmwOp(M::ZpX, f); break; \
    case abs: rmwOp(M::Abs, f); break; case absx: rmwOp(M::AbsX, f); break; \
    case absy: rmwOp(M::AbsY, f); break; case indx: rmwOp(M::IndX, f); break; \
    case indy: rmwOp(M::IndY, f); break;
    RMW_GROUP(slo, 0x07, 0x17, 0x0F, 0x1F, 0x1B, 0x03, 0x13)
    RMW_GROUP(rla, 0x27, 0x37, 0x2F, 0x3F, 0x3B, 0x23, 0x33)
    RMW_GROUP(sre, 0x47, 0x57, 0x4F, 0x5F, 0x5B, 0x43, 0x53)
    RMW_GROUP(rra, 0x67, 0x77, 0x6F, 0x7F, 0x7B, 0x63, 0x73)
    RMW_GROUP(dcp, 0xC7, 0xD7, 0xCF, 0xDF, 0xDB, 0xC3, 0xD3)
    RMW_GROUP(isb, 0xE7, 0xF7, 0xEF, 0xFF, 0xFB, 0xE3, 0xF3)
#undef RMW_GROUP
    case 0x0B: case 0x2B: readOp(M::Imm, [&](std::uint8_t v) { and_(v); setFlag(C, r.a & 0x80); }); break;
    case 0x4B: readOp(M::Imm, [&](std::uint8_t v) { r.a = lsr(r.a & v); }); break;
    case 0x6B: readOp(M::Imm, [&](std::uint8_t v) {
        r.a = std::uint8_t(((r.a & v) >> 1) | ((r.p & C) << 7));
        setZN(r.a);
        setFlag(C, r.a & 0x40);
        setFlag(V, ((r.a >> 6) ^ (r.a >> 5)) & 1);
    }); break;
    case 0xCB: readOp(M::Imm, [&](std::uint8_t v) {
        std::uint8_t ax = r.a & r.x;
        setFlag(C, ax >= v);
        r.x = std::uint8_t(ax - v);
        setZN(r.x);
    }); break;
    case 0x8B: readOp(M::Imm, [&](std::uint8_t v) { r.a = r.x & v; setZN(r.a); }); break;
    case 0xBB: readOp(M::AbsY, [&](std::uint8_t v) { r.a = r.x = r.sp = v & r.sp; setZN(r.a); }); break;
    case 0x9C: storeHigh(M::AbsX, r.y); break;
    case 0x9E: storeHigh(M::AbsY, r.x); break;
    case 0x9F: storeHigh(M::AbsY, r.a & r.x); break;
    case 0x93: storeHigh(M::IndY, r.a & r.x); break;
    case 0x9B: r.sp = r.a & r.x; storeHigh(M::AbsY, r.a & r.x); break;
    default: // 0x02, 0x12, ... KIL/JAM
        jammed = true;
        break;
    }
}

} // namespace nes
