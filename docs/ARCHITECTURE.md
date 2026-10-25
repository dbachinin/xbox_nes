# Architecture

```text
core/ (portable C++20, no Windows headers)            xbox/NesXbox.Logic (netstandard2.0, no WinRT)
  Cartridge::parseInes ─ Mapper (0,1,2,3,4,7)           LocalRomLibrary  ← single import pipeline
  Cpu6502 ── CpuBus ── NesMachine ── Ppu / Apu           RomScanner (IScanFolder) / BatchImporter
  nes_c_api.h (flat C ABI)  ───────── P/Invoke ───────►  NesCore (DllImport "nescore")
                                                         HomebrewHub / GitHub catalog, SafeHttpClient
                                                         RomDownloadService, LanUploadServer, GalleryQuery
                                                                  ▲
xbox/NesCoreNative → nescore.dll (AppContainer DLL)      xbox/NesXbox (UWP, C#, XAML)
                                                         Views: Gallery, Catalog, UsbImport, LanImport,
                                                                Settings, Game;  ViewModels: Gallery, Catalog
                                                         Services: UsbRomSource (KnownFolders.RemovableDevices),
                                                                   GamepadService, AudioOutput (AudioGraph),
                                                                   EmulationSession, LanRomImport, Artwork
```

## Decisions

**Bridge: flat C ABI + P/Invoke instead of a C++/WinRT component.** AGENTS.md allows "an equivalently
supported, demonstrably buildable bridge". A plain DLL with `extern "C"` functions is supported in UWP
(including .NET Native), has no WinRT metadata step, and — decisive — the *same* managed wrapper
(`NesCore.cs`) is exercised on Linux against `libnescore.so`, so the marshalling is tested, not assumed.
Ownership rule: the caller owns every buffer; the core copies ROM bytes on load, and frames/audio are
copied out in one call each (`nes_copy_frame` writes a whole, already upscaled BGRA frame). No pointer
outlives a call. One handle is used by one thread.

**Shell logic in netstandard2.0.** Library, import, scanning, catalog, HTTP policy and the LAN receiver
use only System.IO / System.Net, which UWP permits inside `ApplicationData.Current.LocalFolder` and for
networking. That keeps ~all non-UI behaviour testable on Ubuntu. UWP-only code (StorageFolder, XAML,
Gamepad, AudioGraph, DeviceWatcher) is thin and lives in `xbox/NesXbox/Services`.
Mapping to the file names suggested in AGENTS.md §3:
`LocalRomLibrary.cs`, `HomebrewHubCatalogClient`/`GitHubReleaseCatalogClient`/`RomDownloadService`
(in `Catalog.cs`) and `LanUploadServer.cs` are in NesXbox.Logic; `UsbRomSource.cs`, `LanRomImport.cs`,
`GameArtworkService.cs` are in `xbox/NesXbox/Services`.

**CPU timing.** Every CPU bus access is one cycle, including dummy reads/writes; the machine advances
the PPU by 3 dots and the APU by one cycle inside each access. Interrupts are sampled every cycle and
the decision uses the penultimate-cycle sample (gives CLI/SEI/PLP latency). OAM DMA costs 513/514 cycles.

**PPU.** Dot-based with the standard loopy-register/shifter model; sprite evaluation at dot 257 for the
next line; sprite 0 hit; odd-frame dot skip. Simplifications (marked `ponytail:` in code): sprite
overflow without the hardware bug; MMC3 IRQ clocked at dot 260 instead of tracking PPU A12.

**APU.** Two pulses (sweep, envelope), triangle (linear counter), noise, DMC (memory reads, IRQ),
frame counter with the 3/4-cycle `$4017` delay. Non-linear mixer, box-filter downsampling to 48 kHz,
DC-blocking high-pass. DMC DMA stall cycles are not charged to the CPU.

**Game loop.** `EmulationSession` owns a dedicated thread. The UI thread signals it once per display
refresh (`CompositionTarget.Rendering`); the thread then runs however many NTSC frames (60.0988 Hz)
are due by wall-clock — no busy loop, no frame skipping to hide slowness. Falling > 3 frames behind
resynchronises and increments a visible "dropped" counter. Frames pass through two fixed buffers;
audio through a bounded ring (200 ms hard cap, trimmed to ~50 ms latency).

**Video.** The core upscales by an integer factor (largest that fits the screen height, max 4×) and the
`Image` is sized to the exact physical pixel count, so XAML does not resample (no smoothing). Optional
8:7 pixel aspect stretches horizontally (and therefore filters). `WriteableBitmap` is used as allowed for
a first proof; a `SwapChainPanel` path is future work if measurements on Xbox show it is needed.

**Persistence.** `library.json` (DataContractJsonSerializer) is written atomically (`library.tmp` →
`File.Replace` with `library.bak`). Load repairs: malformed → `library.corrupt.json` + fall back to
backup; entries with bad hashes dropped; paths always re-derived from the hash; missing files marked
unavailable; orphan ROM files re-registered. Mutations are serialized with a semaphore.

**Catalog safety.** HTTPS-only host allowlist; redirects are followed manually and every hop is
checked; private/loopback addresses are impossible because only named hosts are allowed. Catalog JSON
is parsed by a small tolerant reader with depth limits (field types vary between entries). Relative paths
from the catalog are validated segment by segment and percent-escaped.
