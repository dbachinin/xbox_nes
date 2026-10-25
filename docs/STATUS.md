# Status — 2026-10-20

Verification levels: **Ubuntu verified** (built and tested here) · **Type-checked** (C# compiled on Linux
against Windows SDK 10.0.22621 projections with XAML stubs; not a UWP build) · **UNVERIFIED** (written, never
built/run on that platform).

## Baseline

The repository was empty: no `nes-xbox-starter` skeleton existed (only AGENTS.md and ROM_SOURCES.md in
`~/Downloads`). Everything was written from scratch; there was no earlier test baseline.

## Summary

| Area | Ubuntu | Windows/CI | Physical Xbox |
|---|---|---|---|
| Core emulator (CPU/PPU/APU, mappers 0/1/2/3/4/7) | ✅ verified (tests below) | UNVERIFIED (MSVC build) | NO |
| C ABI + P/Invoke wrapper | ✅ verified (C# → libnescore.so) | UNVERIFIED | NO |
| Library, import pipeline, USB scanner logic, catalog, downloads, LAN receiver | ✅ verified (27 managed tests + live probe) | UNVERIFIED | NO |
| UWP app (XAML, USB via RemovableDevices, gamepad, audio, game loop) | Type-checked only | UNVERIFIED | NO |
| Package/manifest/signing | n/a | UNVERIFIED (workflow written, never run) | NO |

## Ubuntu results (GCC 15.2, CMake 4.2, .NET 9.0.121)

* `ctest` Debug: **27/27 passed** (20+1 unit, nestest, 3 blargg, 1 homebrew smoke, managed.logic).
* `ctest` Release: 26/26 passed (managed excluded). ASan+UBSan build: 26/26 passed, no reports.
* nestest: 8991/8991 instructions match (registers + cycle counts, official and unofficial opcodes).
* blargg: instr_test-v5 `official_only` and `all_instrs` pass; `apu_test` 8/8 pass.
* Not passing (not gated, documented): `cpu_interrupts` passes test 1 (CLI latency), fails test 2
  (NMI/BRK timing); `ppu_vbl_nmi` fails at test 2 (`vbl_set_time`). Both need sub-cycle PPU/CPU phase
  accuracy beyond what NROM homebrew requires.
* Visual check: title screens and gameplay of Helmet, DVD Logo (0), Concentration Room, Thwaite, RHDE,
  Zap Ruder (0), Nova the Squirrel (1), 240p Test Suite (2), Guild Quest (3), Cybercoaster (4, raster split)
  render correctly; screenshots were inspected during development (not committed).
* Performance: Release core ≈ 2.9 ms per frame on the dev machine (Cybercoaster gameplay). Xbox figures
  must be measured with the in-game overlay; none are claimed.
* Live (opt-in) test from Ubuntu: Homebrew Hub API reachable; 21 NES game/demo entries, 1 with a
  redistributable license (Cybercoaster) downloaded with cover; curated GitHub releases (3) resolved and
  `thwaite.nes` downloaded. LAN receiver + `scripts/lan-upload.py` verified end-to-end over loopback.
* Not verified locally: clang build (clang not installed here; the CI matrix includes it).

## Milestones

### M0 — foundation
- [x] Existing skeleton inspected (none found); baseline documented above.
- [x] Linux CMake tree, CLI, unit tests, CI workflow (`linux-core.yml`; not yet run on GitHub).
- [x] Complete `.sln` / `.csproj` / `.vcxproj`, manifest, assets and XAML sources written on Linux.
- [x] README separates Linux build from Windows packaging.

### M1 — Xbox shell and USB
- [ ] TV-friendly UI and controller input — written + type-checked, **UNVERIFIED** on device.
- [x] My Games gallery logic (persistence, filters, sort, search, favorites, delete, recovery) — Ubuntu verified;
      the XAML presentation is UNVERIFIED.
- [ ] Import from USB via `KnownFolders.RemovableDevices` — code + manifest written, scanner/import logic
      Ubuntu verified with fakes; **enumeration on Xbox UNVERIFIED**.
- [x] Tests with mocked `IRomSource`: empty drive, one ROM, duplicates, corrupt ROM, permission failure, unplug mid-copy.
- [x] Gallery tests: empty state, 150 cards, duplicates, persistence, sort/search, missing cover, corrupted metadata, missing ROM.
- [ ] Hardware sign-off — **BLOCKED**: no Windows build environment or Xbox available to the agent. LAN fallback
      implemented and tested on Ubuntu (Xbox listening UNVERIFIED).

### M1.5 — homebrew catalog
- [x] Browse Homebrew queries the Homebrew Hub API (`platform=NES`, game/demo, paging, errors) — live from Ubuntu: works.
- [x] Listings with attribution/license; reference-only state for entries without a redistributable license.
- [x] Download with progress/cancel/retry through the shared pipeline, offline play afterwards (mock + live on Ubuntu).
- [x] GitHub Releases provider limited to a curated allowlist (3 repos, licenses checked).
- [x] itch.io/RetroBrews: reference only (no scraping).
- [x] Error cases with local HTTP fixtures (offline cache, 404, timeout/5xx retries, malformed JSON, redirects, length mismatch, HTML, unsupported mapper).
- [ ] Catalog on a real Xbox — UNVERIFIED.

### M2 — real NES execution
- [x] iNES parser, NROM (+ MMC1, UxROM, CNROM, MMC3, AxROM), CPU, bus, PPU; deterministic CLI regression tests.
- [x] Real frames from freely usable ROMs (own generated test cartridge; CC-BY-4.0 Cybercoaster).
- [ ] Xbox app launches an imported ROM and shows frames with controller input — UNVERIFIED.
- [ ] Measured 60 Hz on Xbox — not measured.

### M3 — first release
- [x] APU (all five channels) in the core; audio output via AudioGraph written — sound on Xbox UNVERIFIED.
- [x] Pause/resume/reset/quit, battery RAM save (on pause, quit, suspend, every 60 s) — logic written, UI UNVERIFIED.
- [x] Cleanup/hot-plug/partial-file handling — Ubuntu verified at the logic level.
- [ ] Signed sideload package + reproducible Windows build log — workflow/script written, **never run**.

## Windows build attempt — 2026-10-25: BLOCKED

* Commit `5fe44ce` pushed to `github.com/dbachinin/xbox_nes`; runs 40188435546 (`windows-xbox-package`) and
  40188435535 (`linux-core`) both "failed" in 2 s with 0 steps. Annotation: *"The job was not started because
  your account is locked due to a billing issue."* No code was compiled; this says nothing about the project.
* No local alternative: no Windows VM, 2.7 GB free on /home (a Windows + VS 2022 VM needs ~60 GB); Wine cannot
  run the UWP/MSBuild/.NET Native toolchain.
* Next action: unlock GitHub billing and re-run the workflow, or run `scripts/build-xbox-package.ps1 -SelfSign`
  on any Windows 10/11 machine with VS 2022 + UWP workload.

## Known limitations

* PAL timing not emulated (PAL ROMs run at NTSC speed). Mappers beyond 0/1/2/3/4/7 are rejected at import.
* MMC3 IRQ uses a fixed-dot approximation; DMC DMA stalls not charged; sprite overflow bug not emulated.
* No save states, no zip import, no QR codes (official page URLs are shown as text).
* Video path is `WriteableBitmap` with integer upscaling in the core; a SwapChainPanel path may be needed
  if Xbox measurements show frame pacing problems.
* Storage-full handling is mapped (`ERROR_DISK_FULL` → "Not enough storage") but not tested.

## Next actions for the owner

1. Run the `windows-xbox-package` workflow (push to GitHub) or `scripts/build-xbox-package.ps1 -SelfSign`
   on a Windows machine; fix any MSBuild/XAML compile errors it reports (the UWP project has never been built).
2. Install via Device Portal (DEPLOY_XBOX.md) and walk through TEST_PLAN.md H-1…H-14, recording results here.
