// Compares CPU state against nestest.log line by line (official + unofficial opcodes).
// usage: nestest_log <nestest.nes> <nestest.log>; exits 77 (ctest SKIP) if fixtures are absent.
#include "nes/emulator.hpp"

#include <cstdio>
#include <fstream>
#include <iterator>
#include <string>
#include <vector>

int main(int argc, char** argv) {
    if (argc < 3) return 2;
    std::ifstream romFile(argv[1], std::ios::binary), log(argv[2]);
    if (!romFile || !log) { std::printf("fixtures missing; run scripts/fetch-test-roms.sh\n"); return 77; }
    std::vector<std::uint8_t> rom((std::istreambuf_iterator<char>(romFile)), {});

    nes::NesMachine m;
    if (m.loadRom(rom) != nes::RomError::None) { std::printf("load failed\n"); return 1; }
    auto& cpu = m.cpu();
    cpu.regs.pc = 0xC000;
    cpu.regs.p = 0x24;
    cpu.regs.sp = 0xFD;
    cpu.cycles = 7;

    std::string line;
    int n = 0;
    while (std::getline(log, line)) {
        ++n;
        unsigned pc, a, x, y, p, sp;
        unsigned long long cyc;
        auto regs = line.find("A:");
        auto cycPos = line.find("CYC:");
        if (regs == std::string::npos || cycPos == std::string::npos ||
            std::sscanf(line.c_str(), "%4x", &pc) != 1 ||
            std::sscanf(line.c_str() + regs, "A:%2x X:%2x Y:%2x P:%2x SP:%2x", &a, &x, &y, &p, &sp) != 5 ||
            std::sscanf(line.c_str() + cycPos, "CYC:%llu", &cyc) != 1) {
            std::printf("unparseable log line %d\n", n);
            return 1;
        }
        const auto& r = cpu.regs;
        if (r.pc != pc || r.a != a || r.x != x || r.y != y || r.p != p || r.sp != sp || cpu.cycles != cyc) {
            std::printf("mismatch at line %d\nexpected: %s\n     got: %04X A:%02X X:%02X Y:%02X P:%02X SP:%02X CYC:%llu\n", n,
                        line.c_str(), r.pc, r.a, r.x, r.y, r.p, r.sp, static_cast<unsigned long long>(cpu.cycles));
            return 1;
        }
        cpu.step();
    }
    std::printf("nestest: %d instructions match (result bytes $02=%02X $03=%02X)\n", n, m.peek(0x02), m.peek(0x03));
    return 0;
}
