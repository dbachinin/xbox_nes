# Build and test on Ubuntu

Needs no Microsoft SDK. Tested on Ubuntu with GCC 15.2, CMake 4.2, Ninja, .NET SDK 9.0.121.

```bash
sudo apt install -y build-essential cmake ninja-build python3 curl
sudo apt install -y dotnet-sdk-9.0          # optional: managed shell-logic tests
scripts/fetch-test-roms.sh                  # downloads + verifies fixtures into tests/fixtures/roms/

cmake --preset debug   && cmake --build --preset debug   && ctest --preset debug
cmake --preset release && cmake --build --preset release && ctest --preset release
cmake --preset asan    && cmake --build --preset asan    && ctest --preset asan -E managed
```

Without presets: `cmake -S . -B build/linux -G Ninja -DCMAKE_BUILD_TYPE=Debug`.

> If `cmake`/`ctest` print `ModuleNotFoundError: No module named 'cmake'`, a pip shim in
> `~/.local/bin` shadows the real binaries; use `/usr/bin/cmake` and `/usr/bin/ctest`.

## What the tests cover

| CTest name | What |
|---|---|
| `unit.cart_*` | iNES/NES 2.0 parsing, trainer, CHR RAM, truncated/too small/bad magic/zero PRG/too large, unsupported mapper, DiskDude header |
| `unit.cpu_*` | flags, no decimal mode, cycle counts incl. page-cross and dummy cycles, JMP indirect bug, IRQ/NMI/BRK, CLI latency, unofficial opcodes, JAM |
| `unit.mapper_*` | NROM mirroring + PRG RAM, UxROM, CNROM, MMC1 serial + back-to-back write rule, MMC3 banking + IRQ counter |
| `unit.machine_*` | generated test cartridge renders expected pixels, input moves a sprite, one NMI per frame, 29780.5 cycles/frame, random PRG never crashes |
| `unit.apu_length_samples` | tone produces ~800 samples/frame, `$4015` length status |
| `unit.c_api_round_trip` | C ABI: load errors, upscaled frame copy, audio, battery |
| `rom.nestest` | CPU state vs nestest.log, all 8991 lines (official + unofficial) |
| `rom.blargg.*` | instr_test-v5 official_only + all_instrs, apu_test (8/8) |
| `rom.homebrew.cybercoaster` | CC-BY-4.0 MMC3 homebrew boots, deterministic, non-blank |
| `managed.logic` | 27 C# tests: library, USB scanner/import, catalog, downloads, HTTP policy, LAN receiver, P/Invoke into libnescore.so |

Opt-in live test against the real Homebrew Hub and GitHub:

```bash
NES_LIVE=1 dotnet run -c Release --project xbox/NesXbox.Logic.Tests live
```

## CLI

```bash
build/linux/nes_cli info game.nes
build/linux/nes_cli run game.nes --frames 900 --input 120=S,130=,400=A --ppm out.ppm --wav out.wav
build/linux/nes_cli blargg rom.nes --max-frames 6000     # exit code = blargg result
```

`--input F=BUTTONS` holds buttons from frame F on (`A B s(select) S(start) U D L R`).
