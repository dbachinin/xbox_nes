# Test fixtures

No ROM images are committed. `scripts/fetch-test-roms.sh` downloads them into `tests/fixtures/roms/`
(git-ignored) and verifies SHA-256. Tests that need a missing fixture report **SKIPPED**, never passed.

| File | Source | License / terms | SHA-256 |
|---|---|---|---|
| nestest.nes, nestest.log | kevtris, via https://github.com/christopherpow/nes-test-roms (`other/`) | Freely distributed CPU test by its author; used here only as a downloaded fixture | f67d55fd…0004, 442c4dd5…f6f8f |
| official_only.nes, all_instrs.nes | blargg `instr_test-v5`, same repository | Distributed by the author for emulator testing; downloaded, not redistributed | 589b8835…c3cb, 353870c1…c785 |
| cpu_interrupts.nes | blargg `cpu_interrupts_v2` | as above | ccbac4e8…9c4f |
| ppu_vbl_nmi.nes | blargg `ppu_vbl_nmi` | as above | 8dbab1be…fb7d |
| apu_test.nes | blargg `apu_test` | as above | 00d4722b…ef0a |
| cybercoaster.nes | Fizzer & noby, https://github.com/nesdev-org/homebrew-db (`entries/cybercoaster`) | **CC-BY-4.0** (declared in the Homebrew Hub entry) | 40a7631e…34b1 |

Full hashes are in `scripts/fetch-test-roms.sh`.

The unit tests also use a cartridge generated in code (`tests/test_util.hpp`, `makeTestRom()`):
own 6502 code and graphics, so it carries this repository's terms and needs no download.
