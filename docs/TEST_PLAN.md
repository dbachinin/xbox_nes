# Test plan

## Automated (Ubuntu, every commit)

`ctest` — see BUILD_UBUNTU.md for the list. Acceptance scenarios from AGENTS.md mapped to tests:

| AGENTS.md scenario | Test |
|---|---|
| §12: import 10 via mocked source, restart, 10 cards offline, favorite, sort, open, delete, restart | `library_ten_roms_restart_favorite_sort_delete` |
| §12: duplicate from a different source stored once | `library_duplicate_across_sources`, `catalog_cancel_retry_dedupe` |
| §12: unsupported mapper explained, no crash | `library_invalid_and_unsupported`, `usb_scan_layout_and_limits` |
| M1: empty drive / one ROM / duplicates / corrupt / permission failure / unplug mid-copy | `usb_*`, `library_*` |
| M1: gallery empty, 100+ cards, persistence, sort/search, missing thumbnail, corrupted metadata, missing ROM | `library_150_cards_and_query`, `library_corrupt_metadata_recovery`, `library_missing_rom_file`, `library_cover_and_saves` |
| §13.3/1: 2 legal NES + 1 GB + 1 unknown-rights → only NES, unknown is reference-only | `catalog_filters_platform_and_rights` |
| §13.3/2: download over mock HTTPS with progress, offline restart, launchable | `catalog_download_progress_offline_restart` (+ `native_core_pinvoke` for loading) |
| §13.3/3: cancel mid-download leaves nothing; retry works; USB+catalog collapse | `catalog_cancel_retry_dedupe` |
| §13.3/4: API down, malformed JSON, HTML instead of ROM, offline, unsupported mapper, screenshot missing, length mismatch, redirects | `catalog_error_cases`, `catalog_offline_uses_cache`, `catalog_redirect_policy`, `catalog_path_safety` |
| M2: real frames from a freely usable ROM | `unit.machine_renders_test_rom`, `rom.homebrew.cybercoaster` |
| CPU correctness | `rom.nestest`, `rom.blargg.official_only`, `rom.blargg.all_instrs` |
| APU | `rom.blargg.apu_test`, `unit.apu_length_samples` |

Not covered automatically: storage-full (`ERROR_DISK_FULL` mapping exists but is not simulated).

## Windows / CI (not yet run)

W-1 `windows-xbox-package` workflow green, package + binlog uploaded.
W-2 Package installs on a Windows PC with the device family temporarily set to `Windows.Universal`;
    gallery, USB import from a flash drive, catalog download and gameplay work with a controller.

## Physical Xbox (owner, record screenshots/logs in STATUS.md)

| # | Step | Expected |
|---|---|---|
| H-1 | Install via Device Portal, launch from Dev Home | My Games with empty-state text |
| H-2 | Navigate all tabs with the controller only (LB/RB, D-pad, A, B) | Visible focus everywhere, no mouse cursor |
| H-3 | Insert flash drive with `NES\*.nes`, open Import from USB | Drive and files listed ("Ready (mapper 0)") |
| H-4 | Import 1 ROM, remove the drive, restart the app | Card still present and playable |
| H-5 | Pull the drive during a multi-file import | "Drive disconnected" for the file, library intact |
| H-6 | Insert a drive while the page is open | Automatic rescan |
| H-7 | Play a mapper-0 game: A/B/D-pad/Start/Select | Correct input, ~60 FPS in overlay, no dropped frames |
| H-8 | Audio | Clean sound, no persistent crackle (watch "audio underruns") |
| H-9 | View+Menu → Pause → Quit to library | Returns to the same selected card |
| H-10 | Guide button / suspend during play, resume | Paused on return, battery save written |
| H-11 | Browse Homebrew online | Listing appears; Cybercoaster downloadable, others "Official page only" |
| H-12 | Download Cybercoaster, go offline (unplug network), restart, play | Plays offline |
| H-13 | LAN Import page, upload from Ubuntu with `scripts/lan-upload.py` | "Imported"; if listening fails, record the exact error |
| H-14 | Offline launch of Browse Homebrew | "Offline: showing catalog cached …" or offline message; gallery unaffected |
