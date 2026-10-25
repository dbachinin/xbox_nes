// nes_cli: headless runner for regression tests and diagnostics.
//   nes_cli info   <rom>
//   nes_cli run    <rom> [--frames N] [--ppm out.ppm] [--hold BUTTONS] [--input F=BTN,F=BTN] [--wav out.wav]
//   nes_cli blargg <rom> [--max-frames N]   (exit code = test result, 0 = pass)
#include "nes/emulator.hpp"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>
#include <vector>

namespace {

bool readFile(const char* path, std::vector<std::uint8_t>& out) {
    std::ifstream f(path, std::ios::binary);
    if (!f) return false;
    f.seekg(0, std::ios::end);
    auto size = f.tellg();
    if (size < 0 || std::size_t(size) > nes::kMaxRomBytes + 1) return false;
    f.seekg(0);
    out.resize(std::size_t(size));
    return bool(f.read(reinterpret_cast<char*>(out.data()), size));
}

void writePpm(const char* path, const nes::Frame& fr) {
    std::ofstream f(path, std::ios::binary);
    f << "P6\n256 240\n255\n";
    for (std::uint32_t p : fr.pixels) {
        char rgb[3] = {char((p >> 16) & 0xFF), char((p >> 8) & 0xFF), char(p & 0xFF)};
        f.write(rgb, 3);
    }
}

void writeWav(const char* path, const std::vector<std::int16_t>& s) {
    std::ofstream f(path, std::ios::binary);
    auto u32 = [&](std::uint32_t v) { f.write(reinterpret_cast<const char*>(&v), 4); };
    auto u16 = [&](std::uint16_t v) { f.write(reinterpret_cast<const char*>(&v), 2); };
    std::uint32_t bytes = std::uint32_t(s.size() * 2);
    f << "RIFF"; u32(36 + bytes); f << "WAVEfmt "; u32(16); u16(1); u16(1);
    u32(nes::Apu::kSampleRate); u32(nes::Apu::kSampleRate * 2); u16(2); u16(16);
    f << "data"; u32(bytes);
    f.write(reinterpret_cast<const char*>(s.data()), bytes);
}

std::uint8_t parseButtons(const std::string& s) {
    std::uint8_t b = 0;
    for (char c : s) {
        switch (c) {
        case 'A': b |= nes::kA; break;      case 'B': b |= nes::kB; break;
        case 's': b |= nes::kSelect; break; case 'S': b |= nes::kStart; break;
        case 'U': b |= nes::kUp; break;     case 'D': b |= nes::kDown; break;
        case 'L': b |= nes::kLeft; break;   case 'R': b |= nes::kRight; break;
        default: break;
        }
    }
    return b;
}

int usage() {
    std::cerr << "usage: nes_cli info|run|blargg <rom> [--frames N] [--ppm file] [--wav file] [--hold ABsSUDLR]\n"
                 "       [--input F=BTN,F=BTN...] [--max-frames N]\n";
    return 2;
}

} // namespace

int main(int argc, char** argv) {
    if (argc < 3) return usage();
    std::string cmd = argv[1];
    std::vector<std::uint8_t> rom;
    if (!readFile(argv[2], rom)) { std::cerr << "cannot read " << argv[2] << "\n"; return 2; }

    long frames = 60, maxFrames = 3600;
    const char* ppm = nullptr;
    const char* wav = nullptr;
    std::uint8_t hold = 0;
    std::vector<std::pair<long, std::uint8_t>> script; // from frame F on, hold buttons
    for (int i = 3; i + 1 < argc; i += 2) {
        std::string k = argv[i];
        if (k == "--frames") frames = std::strtol(argv[i + 1], nullptr, 10);
        else if (k == "--max-frames") maxFrames = std::strtol(argv[i + 1], nullptr, 10);
        else if (k == "--ppm") ppm = argv[i + 1];
        else if (k == "--wav") wav = argv[i + 1];
        else if (k == "--hold") hold = parseButtons(argv[i + 1]);
        else if (k == "--input") {
            std::string spec = argv[i + 1];
            for (std::size_t pos = 0; pos < spec.size();) {
                std::size_t end = spec.find(',', pos);
                std::string item = spec.substr(pos, end == std::string::npos ? std::string::npos : end - pos);
                std::size_t eq = item.find('=');
                if (eq == std::string::npos) return usage();
                script.emplace_back(std::strtol(item.c_str(), nullptr, 10), parseButtons(item.substr(eq + 1)));
                if (end == std::string::npos) break;
                pos = end + 1;
            }
        }
        else return usage();
    }

    nes::ParseResult info = nes::parseInes(rom);
    if (cmd == "info") {
        std::cout << "status: " << nes::toString(info.error) << "\n";
        if (info.error == nes::RomError::None || info.error == nes::RomError::UnsupportedMapper) {
            const auto& c = info.cart;
            std::cout << "format: " << (c.nes2 ? "NES 2.0" : "iNES") << "\nmapper: " << c.mapper
                      << "\nprg: " << c.prg.size() << "\nchr: " << c.chr.size() << (c.chrIsRam ? " (RAM)" : "")
                      << "\nbattery: " << c.battery << "\n";
        }
        return info.ok() ? 0 : 1;
    }

    nes::NesMachine m;
    if (auto e = m.loadRom(rom); e != nes::RomError::None) { std::cerr << nes::toString(e) << "\n"; return 1; }
    m.setButtons(0, hold);

    if (cmd == "run") {
        std::vector<std::int16_t> audio;
        auto t0 = std::chrono::steady_clock::now();
        for (long i = 0; i < frames; ++i) {
            for (auto& [f, b] : script) if (f == i) m.setButtons(0, b);
            m.runFrame();
            auto a = m.audio();
            audio.insert(audio.end(), a.begin(), a.end());
        }
        double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
        if (ppm) writePpm(ppm, m.frame());
        if (wav) writeWav(wav, audio);
        std::printf("frames=%ld hash=%016llx audio_samples=%zu ms_per_frame=%.3f\n", frames,
                    static_cast<unsigned long long>(m.frame().hash()), audio.size(), frames ? ms / frames : 0.0);
        return 0;
    }

    if (cmd == "blargg") {
        // blargg protocol: $6001-$6003 = DE B0 61, $6000 = 0x80 while running, else result; text at $6004.
        for (long f = 0; f < maxFrames; ++f) {
            m.runFrame();
            auto ram = m.prgRam();
            if (ram.size() < 8 || ram[1] != 0xDE || ram[2] != 0xB0 || ram[3] != 0x61) continue;
            if (ram[0] == 0x81) { m.reset(); continue; } // test asks for a reset
            if (ram[0] >= 0x80) continue;
            std::string text;
            for (std::size_t i = 4; i < ram.size() && ram[i]; ++i) text += char(ram[i]);
            std::cout << text << (text.empty() || text.back() != '\n' ? "\n" : "");
            std::cout << "result=" << int(ram[0]) << " frames=" << f + 1 << "\n";
            return ram[0];
        }
        std::cerr << "timeout after " << maxFrames << " frames\n";
        return 255;
    }
    return usage();
}
