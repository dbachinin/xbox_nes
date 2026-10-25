#include "nes/apu.hpp"

#include <algorithm>
#include <cmath>

namespace nes {

namespace {

constexpr std::uint8_t kLength[32] = {10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
                                      12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30};
constexpr std::uint8_t kDuty[4][8] = {{0, 1, 0, 0, 0, 0, 0, 0}, {0, 1, 1, 0, 0, 0, 0, 0},
                                      {0, 1, 1, 1, 1, 0, 0, 0}, {1, 0, 0, 1, 1, 1, 1, 1}};
constexpr std::uint8_t kTriangle[32] = {15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0,
                                        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15};
constexpr std::uint16_t kNoisePeriod[16] = {4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068};
constexpr std::uint16_t kDmcRate[16] = {428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54};

} // namespace

void Apu::Envelope::clock() {
    if (start) { start = false; decay = 15; divider = volume; return; }
    if (divider) { --divider; return; }
    divider = volume;
    if (decay) --decay;
    else if (loop) decay = 15;
}

std::uint16_t Apu::Pulse::target() const {
    std::uint16_t change = period >> sweepShift;
    if (!sweepNegate) return std::uint16_t(period + change);
    int t = int(period) - int(change) - (ones ? 1 : 0);
    return std::uint16_t(std::max(t, 0));
}

void Apu::Pulse::clockSweep() {
    if (sweepDivider == 0 && sweepEnabled && sweepShift && !muted()) period = target();
    if (sweepDivider == 0 || sweepReload) { sweepDivider = sweepPeriod; sweepReload = false; }
    else --sweepDivider;
}

std::uint8_t Apu::Pulse::output() const {
    if (muted() || length == 0 || !kDuty[duty][pos]) return 0;
    return env.output();
}

void Apu::state(StateIO& io) {
    io(p1_); io(p2_); io(tri_); io(noise_); io(dmc_);
    io(frameCycle_); io(fiveStep_); io(irqInhibit_); io(frameIrq_); io(oddCycle_); io(resetDelay_);
    io(sampleAcc_); io(sampleSum_); io(sampleCount_); io(hpPrevIn_); io(hpPrevOut_);
}

void Apu::reset() {
    p1_ = Pulse{};
    p1_.ones = true;
    p2_ = Pulse{};
    tri_ = Triangle{};
    noise_ = Noise{};
    dmc_ = Dmc{};
    frameCycle_ = 0;
    fiveStep_ = irqInhibit_ = frameIrq_ = oddCycle_ = false;
    sampleAcc_ = sampleSum_ = 0.0;
    sampleCount_ = 0;
    hpPrevIn_ = hpPrevOut_ = 0.0f;
    samples_.clear();
}

void Apu::writeRegister(std::uint16_t addr, std::uint8_t v) {
    auto pulseWrite = [&](Pulse& p, int reg) {
        switch (reg) {
        case 0: p.duty = v >> 6; p.env.loop = v & 0x20; p.env.constant = v & 0x10; p.env.volume = v & 0x0F; break;
        case 1: p.sweepEnabled = v & 0x80; p.sweepPeriod = (v >> 4) & 7; p.sweepNegate = v & 0x08;
                p.sweepShift = v & 7; p.sweepReload = true; break;
        case 2: p.period = std::uint16_t((p.period & 0x0700) | v); break;
        case 3:
            p.period = std::uint16_t((p.period & 0x00FF) | ((v & 7) << 8));
            if (p.enabled) p.length = kLength[v >> 3];
            p.pos = 0;
            p.env.start = true;
            break;
        }
    };
    switch (addr) {
    case 0x4000: case 0x4001: case 0x4002: case 0x4003: pulseWrite(p1_, addr & 3); break;
    case 0x4004: case 0x4005: case 0x4006: case 0x4007: pulseWrite(p2_, addr & 3); break;
    case 0x4008: tri_.control = v & 0x80; tri_.linearReload = v & 0x7F; break;
    case 0x400A: tri_.period = std::uint16_t((tri_.period & 0x0700) | v); break;
    case 0x400B:
        tri_.period = std::uint16_t((tri_.period & 0x00FF) | ((v & 7) << 8));
        if (tri_.enabled) tri_.length = kLength[v >> 3];
        tri_.reload = true;
        break;
    case 0x400C: noise_.env.loop = v & 0x20; noise_.env.constant = v & 0x10; noise_.env.volume = v & 0x0F; break;
    case 0x400E: noise_.mode = v & 0x80; noise_.period = kNoisePeriod[v & 0x0F]; break;
    case 0x400F:
        if (noise_.enabled) noise_.length = kLength[v >> 3];
        noise_.env.start = true;
        break;
    case 0x4010:
        dmc_.irqEnabled = v & 0x80;
        dmc_.loop = v & 0x40;
        dmc_.rate = kDmcRate[v & 0x0F];
        if (!dmc_.irqEnabled) dmc_.irq = false;
        break;
    case 0x4011: dmc_.level = v & 0x7F; break;
    case 0x4012: dmc_.sampleAddr = std::uint16_t(0xC000 + v * 64); break;
    case 0x4013: dmc_.sampleLen = std::uint16_t(v * 16 + 1); break;
    case 0x4015:
        p1_.enabled = v & 0x01; if (!p1_.enabled) p1_.length = 0;
        p2_.enabled = v & 0x02; if (!p2_.enabled) p2_.length = 0;
        tri_.enabled = v & 0x04; if (!tri_.enabled) tri_.length = 0;
        noise_.enabled = v & 0x08; if (!noise_.enabled) noise_.length = 0;
        dmc_.irq = false;
        if (!(v & 0x10)) dmc_.remaining = 0;
        else if (dmc_.remaining == 0) { dmc_.addr = dmc_.sampleAddr; dmc_.remaining = dmc_.sampleLen; }
        break;
    case 0x4017:
        fiveStep_ = v & 0x80;
        irqInhibit_ = v & 0x40;
        if (irqInhibit_) frameIrq_ = false;
        resetDelay_ = oddCycle_ ? 4 : 5; // +1 because tick() increments right after the reset
        break;
    default: break;
    }
}

std::uint8_t Apu::readStatus() {
    std::uint8_t r = std::uint8_t((p1_.length ? 0x01 : 0) | (p2_.length ? 0x02 : 0) | (tri_.length ? 0x04 : 0) |
                                  (noise_.length ? 0x08 : 0) | (dmc_.remaining ? 0x10 : 0) |
                                  (frameIrq_ ? 0x40 : 0) | (dmc_.irq ? 0x80 : 0));
    frameIrq_ = false;
    return r;
}

void Apu::quarterFrame() {
    p1_.env.clock();
    p2_.env.clock();
    noise_.env.clock();
    if (tri_.reload) tri_.linear = tri_.linearReload;
    else if (tri_.linear) --tri_.linear;
    if (!tri_.control) tri_.reload = false;
}

void Apu::halfFrame() {
    if (!p1_.env.loop && p1_.length) --p1_.length;
    if (!p2_.env.loop && p2_.length) --p2_.length;
    if (!tri_.control && tri_.length) --tri_.length;
    if (!noise_.env.loop && noise_.length) --noise_.length;
    p1_.clockSweep();
    p2_.clockSweep();
}

void Apu::tickDmc() {
    if (dmc_.bufferEmpty && dmc_.remaining) {
        // ponytail: DMA stall cycles are not charged to the CPU.
        dmc_.buffer = dmcRead ? dmcRead(dmc_.addr) : 0;
        dmc_.bufferEmpty = false;
        dmc_.addr = dmc_.addr == 0xFFFF ? 0x8000 : std::uint16_t(dmc_.addr + 1);
        if (--dmc_.remaining == 0) {
            if (dmc_.loop) { dmc_.addr = dmc_.sampleAddr; dmc_.remaining = dmc_.sampleLen; }
            else if (dmc_.irqEnabled) dmc_.irq = true;
        }
    }
    if (dmc_.timer) { --dmc_.timer; return; }
    dmc_.timer = std::uint16_t(dmc_.rate - 1);
    if (!dmc_.silence) {
        if (dmc_.shift & 1) { if (dmc_.level <= 125) dmc_.level += 2; }
        else if (dmc_.level >= 2) dmc_.level -= 2;
    }
    dmc_.shift >>= 1;
    if (--dmc_.bits == 0) {
        dmc_.bits = 8;
        if (dmc_.bufferEmpty) dmc_.silence = true;
        else { dmc_.silence = false; dmc_.shift = dmc_.buffer; dmc_.bufferEmpty = true; }
    }
}

float Apu::mix() const {
    int p = p1_.output() + p2_.output();
    float pulse = p ? 95.88f / (8128.0f / float(p) + 100.0f) : 0.0f;
    // Ultrasonic triangle periods are inaudible on hardware; hold mid-level instead of aliasing.
    int t = tri_.period < 2 ? 7 : kTriangle[tri_.pos];
    int n = (!(noise_.shift & 1) && noise_.length) ? noise_.env.output() : 0;
    float d = t / 8227.0f + n / 12241.0f + dmc_.level / 22638.0f;
    float tnd = d > 0 ? 159.79f / (1.0f / d + 100.0f) : 0.0f;
    return pulse + tnd;
}

void Apu::tick() {
    // Frame sequencer (NTSC CPU-cycle positions).
    if (resetDelay_ && --resetDelay_ == 0) {
        frameCycle_ = 0;
        if (fiveStep_) { quarterFrame(); halfFrame(); }
    }
    ++frameCycle_;
    switch (frameCycle_) {
    case 7457: quarterFrame(); break;
    case 14913: quarterFrame(); halfFrame(); break;
    case 22371: quarterFrame(); break;
    case 29828: if (!fiveStep_ && !irqInhibit_) frameIrq_ = true; break;
    case 29829: if (!fiveStep_) { quarterFrame(); halfFrame(); if (!irqInhibit_) frameIrq_ = true; } break;
    case 29830: if (!fiveStep_) { if (!irqInhibit_) frameIrq_ = true; frameCycle_ = 0; } break;
    case 37281: quarterFrame(); halfFrame(); break;
    case 37282: frameCycle_ = 0; break;
    default: break;
    }

    // Triangle and DMC run at CPU rate; pulses at APU rate (every other cycle).
    if (tri_.timer) --tri_.timer;
    else { tri_.timer = tri_.period; if (tri_.length && tri_.linear) tri_.pos = (tri_.pos + 1) & 31; }
    if (noise_.timer) --noise_.timer;
    else {
        noise_.timer = std::uint16_t(noise_.period - 1);
        std::uint16_t fb = (noise_.shift ^ (noise_.shift >> (noise_.mode ? 6 : 1))) & 1;
        noise_.shift = std::uint16_t((noise_.shift >> 1) | (fb << 14));
    }
    oddCycle_ = !oddCycle_;
    if (oddCycle_) {
        for (Pulse* p : {&p1_, &p2_}) {
            if (p->timer) --p->timer;
            else { p->timer = p->period; p->pos = (p->pos + 1) & 7; }
        }
    }
    tickDmc();

    // Box-filter downsample to kSampleRate, then a DC-blocking high-pass.
    sampleSum_ += mix();
    ++sampleCount_;
    sampleAcc_ += kSampleRate;
    if (sampleAcc_ >= kCpuHz) {
        sampleAcc_ -= kCpuHz;
        float in = float(sampleSum_ / sampleCount_);
        sampleSum_ = 0.0;
        sampleCount_ = 0;
        float out = 0.996f * (hpPrevOut_ + in - hpPrevIn_);
        hpPrevIn_ = in;
        hpPrevOut_ = out;
        samples_.push_back(std::int16_t(std::clamp(out * 30000.0f, -32768.0f, 32767.0f)));
    }
}

} // namespace nes
