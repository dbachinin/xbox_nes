// C ABI test. Links only libnescore (not nes_core) so the core exists once per process,
// exactly like the Xbox app loading nescore.dll.
#include "../test_util.hpp"

#include "nes/nes_c_api.h"

namespace {

// ---------------------------------------------------------------- C ABI
void cApiRoundTrip() {
    auto rom = makeTestRom();
    nes_machine* m = nes_create();
    CHECK(m != nullptr);
    std::vector<std::uint8_t> junk(100, 0);
    CHECK_EQ(nes_load_rom(m, junk.data(), junk.size()), int(2 /* RomError::BadMagic */));
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

int main(int argc, char** argv) { return runTests(argc, argv, {{"c_api_round_trip", cApiRoundTrip}}); }
