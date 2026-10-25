# NES / Xbox

An independent NES emulator for **Xbox Series X|S in Developer Mode** (private sideloading, no Store).
A TV-friendly **My Games** gallery of imported `.nes` games, **Import from USB**, a **LAN import**
fallback, and a **Browse Homebrew** catalog with direct download where the license allows it.

> Current verification level: the emulator core and all shell logic are built and tested on
> **Ubuntu**. The UWP app has **not** been compiled with the Windows toolchain or run on an Xbox
> yet. See [docs/STATUS.md](docs/STATUS.md) for exactly what is verified where.

## Repository layout

| Path | What |
|---|---|
| `core/` | Portable C++20 emulator: iNES/NES 2.0, mappers 0/1/2/3/4/7, 2A03 CPU, 2C02 PPU, APU, flat C ABI |
| `tools/nes_cli/` | Headless runner: info, run N frames → PPM/WAV/hash, blargg test protocol |
| `tests/` | C++ unit + ROM integration tests (CTest) |
| `xbox/NesXbox.Logic/` | Platform-neutral C# (netstandard2.0): library, import pipeline, USB scanner, catalog clients, downloads, LAN receiver, P/Invoke wrapper |
| `xbox/NesXbox.Logic.Tests/` | Console test runner for the above (runs on Linux) |
| `xbox/NesXbox/` | UWP XAML app for `Windows.Xbox` (C#) |
| `xbox/NesCoreNative/` | `nescore.dll`: the core built as an AppContainer DLL |
| `xbox/NesXbox.sln` | Visual Studio solution (x64) |
| `scripts/` | Ubuntu bootstrap, fixture download, ROM validator, LAN upload client, Windows package build |
| `docs/` | Architecture, build/deploy, USB, library, sources, test plan, status |

## Part 1 — Linux (no Microsoft SDK needed)

```bash
sudo apt install -y build-essential cmake ninja-build python3 curl   # + dotnet-sdk-9.0 for managed tests
scripts/fetch-test-roms.sh                                          # licensed diagnostic ROMs, checksummed
cmake -S . -B build/linux -G Ninja -DCMAKE_BUILD_TYPE=Debug
cmake --build build/linux --parallel
ctest --test-dir build/linux --output-on-failure
```

Or simply `scripts/bootstrap-ubuntu.sh`. Presets: `cmake --preset debug|release|asan`.
If `cmake` is a broken pip shim in `~/.local/bin`, call `/usr/bin/cmake` and `/usr/bin/ctest`.

Run a ROM headlessly:

```bash
build/linux/nes_cli info  game.nes
build/linux/nes_cli run   game.nes --frames 600 --input 120=S,130= --ppm shot.ppm --wav audio.wav
build/linux/nes_cli blargg tests/fixtures/roms/official_only.nes
scripts/validate-rom.py game.nes
```

Details: [docs/BUILD_UBUNTU.md](docs/BUILD_UBUNTU.md).

## Part 2 — Windows: build the UWP package

UWP packaging needs Visual Studio 2022 (UWP workload + C++ v143) and Windows SDK 10.0.22621 on a
Windows machine, VM or CI runner:

```powershell
pwsh scripts/build-xbox-package.ps1 -Configuration Release -SelfSign
```

The `windows-xbox-package` GitHub workflow does the same on `windows-2022`.
Details: [docs/BUILD_WINDOWS.md](docs/BUILD_WINDOWS.md).

## Part 3 — Install on the Xbox

Upload the `.msix` plus its `Dependencies\x64\*` packages through **Xbox Device Portal**
(`https://<xbox-ip>:11443`) from any browser, including Ubuntu. Details: [docs/DEPLOY_XBOX.md](docs/DEPLOY_XBOX.md).

## Using the app

* **My Games** (start page): card grid; A opens details (Play, Favorite, Rename, Delete, info). Filter, sort, search.
* **Import from USB**: put `.nes` files on a flash drive (root or `NES/`), Rescan, Import. The drive is only read.
* **LAN Import**: open the page, then on a PC/phone browse to the shown address, or run
  `scripts/lan-upload.py <address> <code> *.nes` from Ubuntu.
* **Browse Homebrew**: Homebrew Hub NES catalog and curated GitHub releases. Download appears only for
  entries whose license permits redistribution; others show the official page.
* In game (NES layout, B left of A): Xbox A = NES B, Xbox B = NES A, X = turbo B, Y = turbo A, Menu = Start, View = Select, D-pad/left stick. **RT** = pause menu, **LT** = save-state menu (one quick-save slot per game); the same trigger or B resumes.

No commercial ROMs are included or offered. You may import games you lawfully own.
