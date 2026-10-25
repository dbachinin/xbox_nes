# AGENTS.md — NES emulator for Xbox Series X|S (Ubuntu-first)

> **Role:** You are an autonomous coding agent. Implement the application described below, not just a design document. Work primarily on **Ubuntu Linux**. Target hardware: **Xbox Series X and Series S in Developer Mode**, with private sideloading and **no Microsoft Store publication**.
>
> **Highest-priority user features:** (1) a persistent, controller-friendly **gallery of imported `.nes` games** (not merely a USB file list), (2) import `.nes` games from USB, (3) a built-in **legal NES homebrew catalog** with direct download where a documented endpoint and license allow it, and (4) launch playable games from the gallery. Direct USB access and online download must be verified on real Xbox hardware; do not claim they work based solely on desktop testing.

## 0. Existing project and operating rules

If the repository contains the previous `nes-xbox-starter` skeleton, **extend it rather than replacing it blindly**. It already has a CMake-based C++ test-pattern core and partial `xbox/NesXbox/MainPage.xaml[.cs]` files, but **does not include a complete Visual Studio UWP solution** and **does not emulate NES yet**.

1. Begin by inspecting the repository, running the existing Linux tests, and recording their status.
2. Implement working source code and tests in small increments; do not replace requirements with TODO-only placeholders.
3. Do not fabricate a successful UWP/Xbox build or USB-device test. Distinguish precisely between **Ubuntu verified**, **Windows CI/VM verified**, and **physical Xbox verified**.
4. The user should be able to run the Linux part without any Microsoft SDK. Windows-specific project files may be authored on Linux, but UWP packaging requires a **Windows build environment** (Windows VM, remote Windows builder, or a Windows CI runner with supported Visual Studio + Windows SDK).
5. Keep the source repository free of copyrighted commercial ROM images, signing keys, passwords, personal information, and proprietary Microsoft SDK binaries.
6. Do not implement Retail Mode exploits, console jailbreaks, Store distribution, or Xbox GDK features requiring special partner access.
7. Make reasonable implementation choices without blocking for clarification. Document assumptions and unsupported functions openly.

## 1. Deliverable and target UX

Build an independently developed Nintendo Entertainment System emulator whose **shipping application targets Xbox Series X|S only**, runs in Developer Mode, works offline, and is entirely navigable with an Xbox controller.

### Expected end-user flow

1. Launch `NES / Xbox` from Xbox Developer Home.
2. See a TV-friendly **My Games gallery** of ROMs already imported to the app: artwork/thumbnail, title, platform, mapper support indicator, latest-played marker, gamepad navigation, and an empty-state prompt if none exist. The top-level navigation is `My Games`, `Browse Homebrew`, `Import from USB`, `Settings`.
3. Insert a normal USB flash drive containing `/NES/*.nes` (also handle files at root and optional nested folders).
4. Choose **Import from USB**. Display any available USB volumes and `.nes` games, then import one or several games into the app's **local storage**. Never modify the original files on the flash drive.
5. Select a gallery card with D-pad/left stick and press A to launch. Use card details for `Play`, `Delete`, `Favorite`, `Rename`, `Game info`. Play via Xbox controller; open a pause menu via Menu button and return to the gallery.
6. Imported ROMs should still work when the USB flash drive is removed. If the drive disappears while importing, fail gracefully without corrupting previously imported games.
7. Open `Browse Homebrew` and discover authorized NES homebrew titles from the network catalog (Homebrew Hub first). Download a compatible `.nes` directly into the local game library, show progress and source/license, and then launch it **offline**. If a source lacks an approved direct-download API, show its official page and explain how to import its ROM via USB/LAN; do not scrape or bypass its download controls.
8. A secondary **LAN import** path must exist if Xbox UWP removable-storage APIs are restricted on that console/OS build: see section 5.

### Core MVP — must work before polishing

- Accept and parse uncompressed iNES-format `.nes` files (validate header and lengths).
- **Mapper 0 / NROM** games and test ROMs: PRG ROM, CHR ROM or CHR RAM, mirroring, PRG RAM as appropriate.
- CPU Ricoh 2A03: 6502-compatible instructions, interrupts, correct handling of 2A03 decimal-mode omission, memory-mapped I/O.
- PPU: correct-enough output for basic NROM homebrew at **256×240**; sprite/background rendering and timing, NMI/vblank, palette handling.
- Input: map Xbox gamepad A/B, D-pad, Start/Menu and Select/View; keep navigation controls and emulated controller buttons distinct.
- Present frames at approximately **60 Hz for NTSC**, avoiding uncontrolled busy loops; audio may be implemented after the first playable proof, but must have its own clear milestone.
- Produce visual/audio output on the Xbox using **UWP-supported** APIs. A Windows desktop build or test-pattern screen alone does not count as a completed emulator.
- Persist imported/downloaded library metadata, card thumbnails, favorites, last-played timestamps, and any supported battery-backed RAM inside `ApplicationData.Current.LocalFolder`. Ensure gallery works offline and survives restarts and USB removal.
- The **My Games gallery** and **Browse Homebrew catalog** are distinct screens: local games are not automatically deleted when remote listings change; downloaded games join the local gallery.
- Never require a network connection after installation and import.

### Subsequent features, not grounds to block MVP

APU audio (pulse/triangle/noise/DMC), save states with versioning, 2-player support, other mappers (MMC1, UxROM, MMC3), CRT filter, sophisticated cover-art matching and screenshot capture. **Basic gallery cards, placeholder artwork and optional source-provided screenshots are part of MVP**, not a later feature. Do **not** market features as implemented before working tests exist.

## 2. Technology and architecture

Preferred:

- **Core:** portable C++20, STL, CMake, no WinRT/Windows headers and no Xbox-specific code.
- **Linux test harness:** CLI app + CTest + downloadable/reproducible *licensed* diagnostic ROM fixtures.
- **Xbox shell:** UWP XAML targeting `Windows.Xbox`. For continuity with the starter, prefer **C# UWP** shell; integrate C++ core via a **Windows Runtime Component in C++/WinRT** (or document and implement an equivalently supported, demonstrably buildable bridge).
- **Build:** Ubuntu GCC/Clang + Ninja for core; Visual Studio/MSBuild + Windows SDK for x64 UWP binaries, optionally automated with a Windows CI runner.
- **Video:** deliver native core's BGRA/RGBA 256×240 frame to a TV-friendly UWP rendering surface; integer/nearly integer scale, correct aspect ratio, no smoothing by default. Use a supported, measured rendering path (e.g. `SwapChainPanel`/Direct3D via a native rendering component; `WriteableBitmap` acceptable for initial proof if frame rate is stable).
- **Audio:** XAudio2 or supported UWP AudioGraph/Media APIs, with a bounded PCM queue once APU is implemented.
- **Gamepad:** `Windows.Gaming.Input.Gamepad` with connected/disconnected handling.
- **ROM storage:** UWP `StorageFile` / `StorageFolder` APIs; do not assume arbitrary Win32 paths are available on Xbox.

Keep clear module boundaries:

```text
core (portable C++)
  Cartridge / iNES / Mapper0
  Cpu6502 / MemoryBus / Ppu2C02 / Apu (later)
  NesMachine::loadRom(bytes)
  NesMachine::setButtons(port, bits)
  NesMachine::runFrame() -> Frame(256x240) [+ audio]
      ^
      | thin, explicit native ABI / C++/WinRT bridge
      v
xbox UWP (C# XAML UI)
  GalleryViewModel / CatalogViewModel / GameView / PauseMenu
  GamepadService / FramePresenter / ThumbnailService
  RomLibraryService / UsbRomSource / LanRomSource
  CatalogClient / HomebrewHubProvider / GitHubReleaseProvider
  RomDownloadService / ImportPipeline / MetadataStore
  ApplicationData local storage
```

Do not add an HTTP server to the portable emulation core; file import and networking are shell/adapter responsibilities.

## 3. Required repository structure

The agent may refine names, but the final repo should look broadly like this, with no missing project files needed for a Windows build:

```text
nes-xbox/
├── AGENTS.md                         # this specification
├── README.md                         # human setup and run instructions
├── CMakeLists.txt
├── CMakePresets.json
├── .gitignore
├── .github/workflows/
│   ├── linux-core.yml
│   └── windows-xbox-package.yml       # build job + documented signing path
├── core/
│   ├── include/nes/
│   │   ├── cartridge.hpp
│   │   ├── cpu6502.hpp
│   │   ├── bus.hpp
│   │   ├── ppu.hpp
│   │   ├── mapper.hpp
│   │   ├── emulator.hpp
│   │   └── frame.hpp
│   └── src/...
├── tools/nes_cli/...
├── tests/
│   ├── unit/...
│   ├── integration/...
│   └── fixtures/README.md            # links, licenses, checksum info
├── xbox/
│   ├── NesXbox.sln                    # actual VS solution, not pseudocode
│   ├── NesXbox/                      # installable UWP app project
│   │   ├── NesXbox.csproj
│   │   ├── App.xaml[.cs]
│   │   ├── MainPage.xaml[.cs]
│   │   ├── Package.appxmanifest
│   │   ├── Assets/...
│   │   ├── Views/...
│   │   ├── ViewModels/
│   │   │   ├── GalleryViewModel.cs
│   │   │   └── CatalogViewModel.cs
│   │   └── Services/
│   │       ├── UsbRomSource.cs
│   │       ├── LocalRomLibrary.cs
│   │       ├── LanRomImport.cs
│   │       ├── HomebrewHubCatalogClient.cs
│   │       ├── GitHubReleaseCatalogClient.cs
│   │       ├── RomDownloadService.cs
│   │       └── GameArtworkService.cs
│   └── NesCoreWinRT/                 # native C++ WinRT bridge project
├── scripts/
│   ├── bootstrap-ubuntu.sh
│   └── validate-rom.py
└── docs/
    ├── ARCHITECTURE.md
    ├── USB_IMPORT.md
    ├── ROM_SOURCES.md
    ├── GAME_LIBRARY.md
    ├── BUILD_UBUNTU.md
    ├── BUILD_WINDOWS.md
    ├── DEPLOY_XBOX.md
    ├── TEST_PLAN.md
    └── STATUS.md
```

**Important:** Generate a genuinely complete `.sln`, `.csproj`, WinRT project, application assets, manifest, and package build scripts. The earlier skeleton only had snippets. `README.md` must not instruct the user to manually create an entire template when the agent can include it in the repo.

## 4. Linux setup, build and tests

Install prerequisites on Ubuntu:

```bash
sudo apt update
sudo apt install -y build-essential clang cmake ninja-build git python3
cmake -S . -B build/linux -G Ninja -DCMAKE_BUILD_TYPE=Debug
cmake --build build/linux --parallel
ctest --test-dir build/linux --output-on-failure
```

Acceptance:

- `core` compiles and the tests run on Linux with **no Windows SDK**.
- Build in `Debug` and `Release`; run sanitizers (ASan/UBSan) in CI if possible.
- Deterministic CPU test cycles, NROM memory mapping, ROM-header checks and PPU frame test hashes; test invalid and truncated ROMs.
- The CLI can load a valid **licensed open/homebrew test ROM**, execute a bounded number of frames and export a diagnostic PNG/PPM or framebuffer checksum without Xbox hardware.
- Keep tests stable and deterministic; don't check in copyrighted commercial ROMs.

## 5. USB flash drive support — REQUIRED, verify on Xbox

### 5.1 API contract and permissions

Use **`Windows.Storage.KnownFolders.RemovableDevices`**, not `broadFileSystemAccess`: the latter is **unsupported on Xbox**. According to Microsoft UWP API requirements, removable devices access needs BOTH:

1. Manifest `removableStorage` capability.
2. A `windows.fileTypeAssociation` declaration identifying the `.nes` file extension.

Schematic manifest excerpt (merge into a complete manifest with correct `xmlns:uap` declaration, `Application`, visual elements and dependencies):

```xml
<Applications>
  <Application Id="App" ...>
    <Extensions>
      <uap:Extension Category="windows.fileTypeAssociation">
        <uap:FileTypeAssociation Name="nesrom">
          <uap:SupportedFileTypes>
            <uap:FileType>.nes</uap:FileType>
          </uap:SupportedFileTypes>
        </uap:FileTypeAssociation>
      </uap:Extension>
    </Extensions>
  </Application>
</Applications>
<Capabilities>
  <Capability Name="removableStorage" />
</Capabilities>
```

Do not copy literal `...` into final XML. Validate the final manifest and packaging on Windows. When a native Xbox build is not available, mark XML/platform behavior **UNVERIFIED** in `docs/STATUS.md`.

### 5.2 USB browsing and import implementation

Implement `IRomSource`, `UsbRomSource`, `LocalRomLibrary` with async APIs; inject `IRomSource` to enable desktop/mock tests.

High-level sketch (not a replacement for robust production code):

```csharp
using Windows.Storage;

StorageFolder root = KnownFolders.RemovableDevices;
var volumes = await root.GetFoldersAsync();
foreach (StorageFolder volume in volumes)
{
    // Enumerate relevant .nes files (root and /NES, optional recursive).
    // Access only declared extensions; handle permission/IO exceptions.
    // Display files on UI and import selected StorageFile asynchronously.
}
```

Detailed requirements:

- Probe flash drives when `Import from USB` is activated; provide **Rescan** button and support hot-unplug. Do not rely on fixed paths such as `D:\\`.
- Start with root and `/NES` folder; optionally recursive scan with depth and file-count limits, cancellation and backpressure. Don't freeze the UI thread.
- Filter `.nes` case-insensitively, parse **iNES header**, validate PRG/CHR sizes and supported mapper BEFORE import, and classify errors as `Unsupported mapper`, `Invalid ROM`, `Drive disconnected`, `No USB drive found`, `Access denied`.
- Protect against oversized/untrusted files; set reasonable size caps and avoid unbounded allocation. Prefer streaming/copy with progress and cancellation where feasible.
- Copy imported ROMs into `ApplicationData.Current.LocalFolder/Games/` by normalized SHA-256-based name; metadata into `library.json` or a small reliable database. Prevent duplicates by content hash **across USB, LAN, and internet downloads**; all sources feed one shared import pipeline. No write operation should ever target the USB drive.
- Import atomically: copy to a temporary local file, validate, then move/rename into the library; clean partial temp files after errors. Preserve existing library on import failure.
- Track display name, size, sha256, iNES mapper, imported timestamp, optional PRG-RAM path **plus** optional developer, source URL, license, screenshot, favorite flag, last-played timestamp, and import channel (`usb`, `lan`, `catalog`). Sanitize names and paths; never trust embedded ROM metadata or directory names.
- File access from an Xbox UWP app is sandboxed: never require raw Win32 handle, direct kernel access, unsupported device driver, or unrestricted filesystem capabilities.
- A `FileOpenPicker` can be explored as an **alternative**, not assumed to behave identically on Xbox. Do not make the main USB workflow depend on an unverified picker.

**Critical risk:** `KnownFolders.RemovableDevices` is a documented UWP API, but the expected USB-volume enumeration/import behavior on the user's particular Xbox in Developer Mode **must be proved on physical hardware**. Linux/Windows desktop tests cannot establish Xbox USB support. Keep this integration isolated behind `IRomSource` and use the fallback below.

### 5.3 Fallback: import over LAN

Build a usable fallback if native Xbox USB browsing is unavailable or empty. Preferred approach:

- Let the user copy `.nes` files from the flash drive onto Ubuntu and transfer them to the Xbox app over the local network; the source flash drive is still the user's ROM source.
- Prefer a simple, explicit **app-managed local LAN upload endpoint** only if ordinary UWP networking and listening sockets are verified to function on Xbox Dev Mode, with `privateNetworkClientServer` and other necessary manifest capabilities as verified. Bind only to the selected LAN interface, generate a short one-time pairing token, enforce a size limit, and disable server when not importing.
- If an in-app receiver cannot be made to work, document a concrete **Xbox Device Portal app-local-storage upload** procedure only after testing that it can place files inside the app's accessible `LocalFolder/Games/`. Do **not** claim Device Portal supports arbitrary transfers to any Xbox directory.
- Provide a ready-to-use Linux-side client or instructions for uploading validated ROMs once a functioning transfer target is established.
- Never require opening a port to the public internet or weakening console security. Show an understandable error and fallback help inside the app.

Native USB import remains a major acceptance criterion. Fallback is a resilience feature, **not an excuse to silently omit USB code**.

## 6. Xbox UWP packaging, UI and native integration

- Specify **`TargetDeviceFamily Name="Windows.Xbox"`** in the final Xbox package, with SDK/version values consistent with the actual project. Use **x64** on Series X/S.
- Build a 10-foot UI readable from a couch: **cover/card grid for imported games**, large focus targets, visible focus, high contrast, gamepad-only navigation and no required mouse/keyboard.
- Integrate `Windows.Gaming.Input.Gamepad`; handle disconnects and avoid continuously triggering menu actions while a button is held (edge detection).
- Keep IO/import work asynchronous, game loop separate from UI dispatch, and rendering frame buffers bounded.
- Add visible state and errors: `No ROMs`, `Scanning USB`, `Importing 2/5`, `Unsupported mapper`, `Cannot access USB`, `Browse Homebrew`, `Downloading`, `Download failed`, `Offline`, `Already in library`, `Play`, `Pause`, `Resume`, `Quit to library`.
- The native/managed bridge must define ownership and lifetime of frame buffers and ROM bytes. Avoid per-pixel managed/native calls; transfer a whole framebuffer in a single operation. Include a crash-safe game-stop and app suspend/resume policy.
- For starter integration, the C# XAML files from the previous skeleton can be reused, but **must be extended into a complete buildable UWP project**.

## 7. Windows build from a Linux-driven workflow

Ubuntu is the main **editing and testing workstation**, not a full substitute for Microsoft's UWP compiler and packaging stack.

Provide both:

1. A reproducible **Windows builder** document (Visual Studio 2022 with UWP tools, Windows SDK, MSBuild, x64 components), with exact commands and required project components.
2. A `windows-xbox-package.yml` or equivalent CI pipeline capable of compiling the UWP `.sln` on an appropriate **Windows runner**. The pipeline should upload build logs/artifacts, and should **fail explicitly** if SDK/workloads are absent rather than claiming success.

Signed sideload packages require a development certificate trusted by the console (or the current Microsoft-supported signing/deployment workflow). Do not commit private `.pfx` keys, certificate passwords, or tokens. Unsigned CI compilation is a valid intermediate checkpoint, but **not a sideload-ready artifact**.

From Ubuntu, deployment to an Xbox already in Developer Mode can be completed by browsing to the **Xbox Device Portal on the local network**, uploading a correctly packaged/signed app and its required dependencies. See official links in section 11.

## 8. Engineering quality and security

- Modern C++20; RAII, `std::span` where appropriate, explicit fixed-width integer types, no UB, bounded reads, no global mutable singleton state for emulator instances.
- Unit test instruction decoding (including unofficial opcodes only if intentionally supported), state transitions, interrupts, mappers, mirroring and memory regions.
- Core data structures independent of UWP: controller bitmask, frame buffer, PCM buffer, ROM bytes.
- Graceful handling of corrupt/untrusted ROMs; never execute native code from a ROM. A ROM is **data**, not a DLL or executable.
- Do not download or bundle commercial NES games. Use homebrew and diagnostics whose licenses permit redistribution, or document how the user can supply their own.
- Keep performance and correctness balanced: NES emulation is deterministic; never use frame skipping to hide a broken timing model. Log effective FPS, CPU time, dropped frames in a debug overlay.
- Record significant architectural choices in `docs/ARCHITECTURE.md`; keep `docs/STATUS.md` truthful and current.

## 9. Implementation order — checkpoints and acceptance tests

### M0 — repository and build foundation

- [ ] Existing skeleton inspected; Ubuntu test baseline documented.
- [ ] Complete Linux CMake tree, CLI, unit tests and CI.
- [ ] Complete UWP `.sln/.csproj/.vcxproj` project files and XAML app sources written in Linux, without claiming hardware success.
- [ ] `README.md` clearly separates Linux build from Windows UWP packaging.

### M1 — Xbox shell and USB proof of concept (prioritize this)

- [ ] TV-friendly Xbox UI and controller input.
- [ ] `My Games` gallery (responsive card grid, focus navigation, filename-based fallback artwork, search/filter/sort, details, favorites, delete confirmation) backed by app-local storage; works on restart with no internet/USB.
- [ ] `Import from USB` interface, rescan, volume/files listing via `KnownFolders.RemovableDevices` with `.nes` association and `removableStorage` capability.
- [ ] Import one valid `.nes` to app-local storage, list it, unplug USB and still see it in library.
- [ ] Tests with mocked `IRomSource`: empty drive, one ROM, duplicates, corrupt ROM, permission failure, unplug mid-copy.
- [ ] Tests for gallery: empty state, 100+ cards, duplicates (SHA-256), persistence, sorting/search, missing thumbnail, corrupted metadata recovery, missing ROM file, gamepad focus state.
- [ ] Hardware-only sign-off: screenshots/logs proving drive enumeration and imported game on an actual Series X/S in Dev Mode, or a clearly documented **blocker** and working LAN fallback.

### M1.5 — online homebrew catalog and downloads (before broader game compatibility)

- [ ] `Browse Homebrew` queries the documented Homebrew Hub API; filters to `platform=NES`, permitted game/demo categories, and supports paging/error states. Mark live API availability **UNVERIFIED** until probed from Ubuntu and Xbox.
- [ ] Real listings: metadata, legal attribution/license, thumbnails/screenshots if permitted; do not confuse catalog entries with installed games.
- [ ] For entries with verifiable, permitted `.nes` download files, the `Download` action fetches over HTTPS with progress/cancel/retry, validates NES structure and mapper, uses shared import pipeline, adds entry to `My Games`, and supports offline play.
- [ ] GitHub Releases provider restricted to a curated allowlist of authorized homebrew repositories; no arbitrary GitHub search and no automatic trust in a public release.
- [ ] itch.io provider is discovery/manual-transfer only unless a documented API and download permissions for the exact game are verified. The generic itch.io account API is not an anonymous game-file download API.
- [ ] Offline catalog-cache display, 404/timeout/expired URLs, malformed JSON, redirect/host policy, mismatched content length, invalid ROM, hash collision, unknown license and unsupported mapper tested with local HTTP fixtures.

### M2 — real NES execution

- [ ] iNES parser, NROM, CPU 2A03, bus and PPU; deterministic CLI regression tests.
- [ ] Core returns real frame pixels from a freely usable test ROM (not a generated static pattern).
- [ ] Xbox app launches selected imported ROM, displays real emulated frames, and reads A/B/D-pad/Start/Select input.
- [ ] Stable real-time NTSC execution with 60 Hz target; measure actual performance on Xbox rather than invent metrics.

### M3 — usable first release

- [ ] Audio (APU minimum viable sound) with no persistent crackling/desync.
- [ ] Pause/resume, return to library, save/load supported cartridge RAM.
- [ ] Error handling, cleanup, reboot, offline catalog/library navigation, canceled download, partial-file cleanup and hot-plug scenarios.
- [ ] Signed local sideload package plus install instructions, with reproducible Windows build logs.

**Do not stop at M0** and describe the emulator as complete. If any Microsoft SDK, CI access or physical Xbox test is unavailable, finish all feasible source, unit tests and docs; leave the blocked checkbox unchecked with exact evidence and next action.

## 10. Agent final report format

On completion, report (briefly, with commands and paths):

1. **Implemented:** exact features, with references to key source files.
2. **Verified on Ubuntu:** executed commands and test output summary.
3. **Verified on Windows/CI:** whether the solution compiled and a package was actually produced.
4. **Verified on physical Xbox:** whether app launch, controller, local gallery, USB enumeration, ROM import, catalog fetch, catalog ROM download and gameplay were actually tested (YES / NO / BLOCKED separately).
5. **Next action for owner:** the smallest set of actions required from the user to run or test it.

Never write “all works on Xbox” unless it was really deployed and tested there.

## 11. Official references

- Xbox UWP development/deployment: https://learn.microsoft.com/en-us/windows/uwp/apps-for-xbox/development-options
- Windows Device Portal / sideload: https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/device-portal
- UWP removable devices (`KnownFolders.RemovableDevices`), `.nes` association prerequisite: https://learn.microsoft.com/en-us/uwp/api/windows.storage.knownfolders.removabledevices
- UWP file access permissions: https://learn.microsoft.com/en-us/windows/uwp/files/file-access-permissions
- Unsupported Xbox UWP APIs (`broadFileSystemAccess`): https://learn.microsoft.com/en-us/uwp/extension-sdks/uwp-limitations-on-xbox
- UWP gamepad API: https://learn.microsoft.com/en-us/windows/uwp/gaming/gamepad-and-vibration
- UWP Xbox gamepad/TV interactions: https://learn.microsoft.com/en-us/windows/uwp/ui-input/gamepad-and-remote-interactions
- NES development / emulation: https://www.nesdev.org/wiki/Nesdev_Wiki
- NES iNES format: https://www.nesdev.org/wiki/INES
- NES mapper 0: https://www.nesdev.org/wiki/NROM
- NES emulator test ROMs: https://www.nesdev.org/wiki/Emulator_tests
- Homebrew Hub API documentation: https://github.com/gbdev/homebrewhub/blob/main/API.md
- Homebrew Hub NES dataset: https://github.com/nesdev-org/homebrew-db
- GitHub release asset API: https://docs.github.com/en/rest/releases/assets
- itch.io NES ROM homebrew discovery: https://itch.io/games/free/tag-nes-rom
- RetroBrews NES collection (special redistribution conditions): https://github.com/retrobrews/nes-games

## 12. Persistent ROM gallery — mandatory first-class UX

Implement **My Games** as the default startup page. This is a persistent gallery of ROMs **already copied into the app's own storage**, not just a raw directory listing or remote-catalog feed.

**Card content (MVP):** title inferred from safe filename, optional source-provided screenshot/cover, generated generic NES-cartridge fallback (do not silently download copyrighted commercial box art), small `Ready`/`Unsupported mapper` state, favorite marker, last played. A newly imported ROM should appear immediately. Store covers only when permitted by their source, with local cache and no runtime network dependency.

**Navigation:** Xbox D-pad/left stick to choose card, `A` to open details/play, `B` back, Menu for context actions. Provide `All`, `Favorites`, `Recently played`, `Recently added`, sort (title/date) and an on-screen keyboard search supported on Xbox. A selected card remains selected on return from gameplay if it still exists. UI must scale to 720p/1080p and tolerate TV safe area / large font settings. Virtualize/lazy-load thumbnails for hundreds of games.

**Library persistence model (suggested shape, adapt as needed):**

```json
{
  "schema_version": 1,
  "games": [
    {
      "id": "sha256:<64 lowercase hex digits>",
      "rom_path": "Games/<sha256>.nes",
      "title": "Example Homebrew",
      "sha256": "<64 lowercase hex digits>",
      "mapper": 0,
      "source": "usb|lan|homebrewhub|github",
      "source_url": null,
      "license": null,
      "cover_path": null,
      "favorite": false,
      "imported_at": "2026-10-20T12:00:00Z",
      "last_played_at": null
    }
  ]
}
```

This JSON is illustrative: actual persisted files must use real hash values, validated timestamps and supported serialization (never the angle-bracket placeholders). Metadata is **not** the source of truth for file existence; on startup repair stale records or mark them unavailable without crashing. Write atomically (`library.tmp` + replace) and recover from malformed/partial metadata; library mutations are serialized, never concurrently overwrite updates. Consider SQLite if it improves reliability, but avoid unnecessary external dependency for MVP.

**Single ingestion architecture** for USB, LAN and online: `candidate bytes/stream -> size checks -> iNES parser -> SHA-256 -> compatibility check -> temp write -> atomic commit -> metadata + artwork update -> gallery event`. Preserve duplicate handling, cancellation, disk-full behavior and offline use regardless of origin. The library supports locally imported commercial images that a user lawfully supplies, but **the built-in browser never offers unlicensed commercial ROM download links**.

**Acceptance:** import 10 ROMs via a mocked source, restart UI, show 10 cards offline, favorite and sort, open selected ROM, delete with confirmation, restart again with consistent metadata. Import a duplicate from a different source and verify only one ROM is stored. Validate unsupported mappers are shown with an explanation rather than crashing.

## 13. Homebrew catalog and servers — approved integration policy

**Purpose:** The user wants to discover NES ROMs and download them inside the Xbox application. This must be implemented as an **opt-in `Browse Homebrew` tab**, distinct from `My Games`. Only download a work when the author/source grants access; 'free to download' is not automatically equivalent to 'freely redistributable'. Never bypass login, payments, DRM, site restrictions or copyrighted commercial-game catalogs.

### 13.1 Recommended online sources, verified documentation as of October 2026

| Priority | Source | Reference | Technical strategy | Caveats |
|---|---|---|---|---|
| 1 | **Homebrew Hub (NES)** | `https://hh.gbdev.io/`; API docs `https://github.com/gbdev/homebrewhub/blob/main/API.md`; dataset `https://github.com/nesdev-org/homebrew-db` | **Preferred automated catalog** with metadata, screenshots and downloadable attached NES ROM files; check `license` | Live host responsiveness and individual game rights **must be verified** before enabling Download |
| 2 | **GitHub Releases from selected homebrew authors** | `https://docs.github.com/en/rest/releases/releases` and `https://docs.github.com/en/rest/releases/assets` | Curated repository allowlist; GitHub REST API provides `browser_download_url` for release assets | Open-source repository does not automatically grant ROM/asset redistribution permission. Respect per-game license and GitHub API rate limits |
| 3 | **RetroBrews NES games** | `https://github.com/retrobrews/nes-games` | **Browse/reference** or user-initiated fetch **directly from the original repository**, only after confirming permission for this client workflow | Repository says ROMs are approved for distribution **on that site/project only**; no mirroring, repackaging or rehosting without author's consent |
| 4 | **itch.io free NES ROM-tagged games** | `https://itch.io/games/free/tag-nes-rom` | Discover authors/game pages; display a **link or QR code** to download on phone/PC, then USB/LAN import | No general anonymous public file-download API established; do not scrape or invent stable download URLs; some games are paid or impose special terms |
| 5 | **NESdev homebrew / tests** | `https://www.nesdev.org/wiki/Emulator_tests`; `https://www.nesdev.org/wiki/Category:Homebrew_games` | Developer diagnostics / links to authors; approved test ROMs for automated test workflows | Check license individually; diagnostics are not interchangeable with finished games |

**Homebrew Hub API contract (documented, not yet confirmed live on physical Xbox):**

```text
Base URL:  https://hh3.gbdev.io/api
Search:    GET /search?platform=NES&page=1
Manifest:  GET /entry/{url-encoded-slug}.json
ROM file:  GET /entry/{url-encoded-slug}/{url-encoded-filename}
Image:     GET /entry/{url-encoded-slug}/{url-encoded-screenshot-name}
```

The API docs specify `/search`, `/entry/<slug>.json`, `/entry/<slug>/<filename>`, optional query params `platform`, `title`, `tags`, and small-page pagination. Filter every response to `platform == NES`, verified content type/category and `.nes` file assets, even if the server query was filtered. Validate that `slug` and attached `filename` are safe single path segments. Inspect JSON manifest fields `title`, `slug`, `developer`, `files[]`, `license`, `screenshots[]`, `tags`. The published API documentation examples focus partly on Game Boy: **perform a live NES request and adjust for the current backend behavior**; do not claim it works until checked. Use limited pagination and a responsive UI.

**GitHub releases flow:** configured known repo -> `GET https://api.github.com/repos/{owner}/{repo}/releases` -> inspect assets (prefer `.nes`, optionally `.zip` if safe extractor is implemented) -> verify publisher/rights -> download `browser_download_url` -> import and cite actual release and license. Do not automatically pull arbitrary `.nes` files from code search, unknown repositories, or pirated repacks. For a project with copyrighted art/levels and restricted usage, do not distribute that content bundled with the emulator.

**itch.io:** this is mainly an author discovery directory. The official server-side API requires authentication and is not documented as a general public endpoint to anonymously list and download every creator's game assets. For the MVP show a clearly labelled `Open official page / Import via USB` flow, not a broken pseudo-Download button. Add direct automatic integration only for individual titles with stable, explicitly authorized direct binary URLs and tested behavior.

### 13.2 Network/download reliability and security

- Network transport: HTTPS (`Windows.Web.Http.HttpClient` or supported .NET UWP equivalent); declare `internetClient` and any other capabilities confirmed necessary on Xbox. Apply request timeout, cancellation, retry with capped exponential backoff, and visible offline state.
- Whitelist trusted catalog API domains and carefully validate redirects. Permit an explicitly verified CDN only when needed by the chosen provider; never accept arbitrary external URL strings from untrusted catalog JSON. Block non-HTTPS, localhost/private-network and file-scheme redirections to avoid SSRF or local-network scanning.
- Constrain the size of downloaded ROMs (configurable bounded limit, e.g. 32 MiB for plain `.nes`), limit concurrent downloads, stream into app-local temporary files rather than buffering huge responses, validate iNES/NES 2.0 format/size and optional server hash **before** promoting to the library. Server Content-Type and extension are not sufficient checks. Support Content-Length absent/mismatched.
- Optional `.zip` source support: must be opt-in, limit compressed/decompressed size, reject zip-slip/symlinks/duplicate file names/huge archive expansion and import only validated `.nes`. If not safely implemented, support only plain `.nes` and show clear instructions.
- No persistent third-party OAuth secrets in source, no embedded paid-game access keys or tokens. Avoid access-token leakage in logs.
- Preserve source attribution and license in local game details; use remote screenshots only if authors permit display/cache. Failed artwork fetch must never block ROM import or gallery navigation.
- Catalog cache is optional and clearly time-stamped; **installed library** remains functional without any network connection.
- User-requested downloads only; no silent downloading of hundreds of games and no background auto-install.

### 13.3 Remote catalog acceptance scenarios

1. Mock API returns 2 legal, downloadable NES games, 1 Game Boy entry and 1 NES ROM with unknown rights: show only eligible NES games, with the rights-unknown item available only for details/reference, not direct Download.
2. Download an authorized, validated Mapper 0 `.nes` over mock HTTPS; observe progress; it is in the gallery after offline restart and can launch without USB or internet.
3. Cancel mid-download; no partial file/card remains; retried download succeeds and duplicates across USB and catalog collapse to one SHA-256 entry.
4. API is down, JSON malformed, download returns HTML, user is offline, license is missing, mapper unsupported, storage full or remote screenshot unavailable: clear recoverable error, no gallery corruption.
5. Test on actual Xbox Dev Mode, recording catalog listing, downloading, library persistence, gamepad navigation and gameplay separately; if console test unavailable, report `NOT VERIFIED`.

## 14. Updated agent execution priority

M0: project foundation -> M1: persistent My Games gallery **and** USB import -> M1.5: Homebrew Hub catalog and permitted downloads -> M2: NES CPU/PPU gameplay -> M3: sound and reliability. The agent must not stop after drawing a beautiful gallery: it must implement actual persistence, validated download/import, real emulator execution and truthful test results. Update `docs/ROM_SOURCES.md`, `docs/GAME_LIBRARY.md`, `docs/STATUS.md`, code and tests in step with this specification.

---

**Instruction to the coding agent:** Start implementing M0 and M1 immediately on Ubuntu; then implement M1.5, M2 and M3 as far as the environment permits. Produce usable source, tests, actual Windows build definitions and frank verification results. Give priority to the USB ROM-import workflow on Xbox, while preserving a LAN fallback. Do not ask for routine implementation decisions that can be resolved from this specification.
