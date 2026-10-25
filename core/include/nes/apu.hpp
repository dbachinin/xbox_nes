#pragma once

#include "nes/state.hpp"

#include <cstdint>
#include <functional>
#include <vector>

namespace nes {

// 2A03 APU, NTSC. Produces mono signed 16-bit PCM at kSampleRate.
class Apu {
public:
    static constexpr int kSampleRate = 48000;
    static constexpr double kCpuHz = 1789773.0;

    std::function<std::uint8_t(std::uint16_t)> dmcRead; // set by the machine

    void reset();
    void tick();                                       // one CPU cycle
    void writeRegister(std::uint16_t addr, std::uint8_t v);
    std::uint8_t readStatus();                         // $4015
    bool irq() const { return frameIrq_ || dmc_.irq; }
    void state(StateIO& io);

    std::vector<std::int16_t>& samples() { return samples_; }
    const std::vector<std::int16_t>& samples() const { return samples_; }

private:
    struct Envelope {
        bool start = false, loop = false, constant = false;
        std::uint8_t volume = 0, divider = 0, decay = 0;
        void clock();
        std::uint8_t output() const { return constant ? volume : decay; }
    };
    struct Pulse {
        bool ones = false;            // pulse 1 negates with ones' complement
        bool enabled = false;
        std::uint8_t duty = 0, pos = 0, length = 0;
        std::uint16_t timer = 0, period = 0;
        bool sweepEnabled = false, sweepNegate = false, sweepReload = false;
        std::uint8_t sweepPeriod = 0, sweepShift = 0, sweepDivider = 0;
        Envelope env;
        std::uint16_t target() const;
        bool muted() const { return period < 8 || target() > 0x7FF; }
        void clockSweep();
        std::uint8_t output() const;
    };
    struct Triangle {
        bool enabled = false, control = false, reload = false;
        std::uint8_t linear = 0, linearReload = 0, length = 0, pos = 0;
        std::uint16_t timer = 0, period = 0;
    };
    struct Noise {
        bool enabled = false, mode = false;
        std::uint8_t length = 0;
        std::uint16_t timer = 0, period = 4, shift = 1;
        Envelope env;
    };
    struct Dmc {
        bool irqEnabled = false, loop = false, irq = false, silence = true, bufferEmpty = true;
        std::uint16_t rate = 428, timer = 0, sampleAddr = 0xC000, sampleLen = 1, addr = 0xC000, remaining = 0;
        std::uint8_t level = 0, shift = 0, bits = 8, buffer = 0;
    };

    void quarterFrame();
    void halfFrame();
    void tickDmc();
    float mix() const;

    Pulse p1_, p2_;                  // p1_.ones is set in reset()
    Triangle tri_;
    Noise noise_;
    Dmc dmc_;

    std::uint32_t frameCycle_ = 0;
    bool fiveStep_ = false, irqInhibit_ = false, frameIrq_ = false;
    bool oddCycle_ = false;
    int resetDelay_ = 0;             // $4017 write takes effect 3-4 cycles later

    double sampleAcc_ = 0.0, sampleSum_ = 0.0;
    int sampleCount_ = 0;
    float hpPrevIn_ = 0.0f, hpPrevOut_ = 0.0f;
    std::vector<std::int16_t> samples_;
};

} // namespace nes
