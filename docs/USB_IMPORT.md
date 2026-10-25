# USB import

## How it works

* API: `Windows.Storage.KnownFolders.RemovableDevices` (not `broadFileSystemAccess`, which Xbox does not support).
* Manifest prerequisites (both present in `xbox/NesXbox/Package.appxmanifest`):
  * `<uap:Capability Name="removableStorage" />` — note it lives in the **uap** namespace;
  * a `windows.fileTypeAssociation` for `.nes`. The app can only see file types it declares.
* `UsbRomSource` wraps each volume as an `IScanFolder`; the shared `RomScanner` (NesXbox.Logic) then:
  1. lists `.nes` (case-insensitive) in the volume root, then `NES/`, then other folders up to depth 3
     (Settings → "Scan USB subfolders" turns recursion off: root + `NES/` only);
  2. skips `System Volume Information` and dot-folders, stops at 2000 files (shown as truncated);
  3. reads only the 16-byte header + size of each file and classifies it:
     Ready / Unsupported mapper N / Invalid ROM / File too large;
  4. reports `No USB drive found`, `Cannot access USB (access denied)`, `Drive disconnected` per case.
* Import (`BatchImporter` → `LocalRomLibrary.ImportAsync`) copies into `LocalFolder/Temp/*.part` with a
  32 MiB cap and SHA-256, validates, then moves to `LocalFolder/Games/<sha256>.nes` and updates
  `library.json` atomically. Duplicates (same SHA-256 from USB, LAN or catalog) are stored once.
* Files are opened **read-only**; nothing writes to the drive. Pulling the drive mid-copy yields
  `Drive disconnected` for that file, removes the partial temp file and leaves the library unchanged.
* Hot-plug: a `DeviceWatcher` on `DeviceClass.PortableStorageDevice` triggers a rescan when a drive is
  inserted or removed while the page is open; **Rescan** is always available.
* Opening a `.nes` file with the app (file activation) imports it through the same pipeline.

## Verified so far

* Scanner/import logic: unit-tested on Ubuntu with fake volumes (empty drive, one ROM, duplicates,
  corrupt ROM, permission failure, unplug mid-copy, depth/count limits) and with a real directory.
* **Not verified on Xbox.** Whether `RemovableDevices` enumerates a flash drive in Dev Mode on the
  owner's console must be proved on hardware (see TEST_PLAN.md, H-3..H-6).

## If USB does not work on your console

Use **LAN Import** (in-app receiver, see below). If the receiver cannot listen either, a Device Portal
procedure is the next fallback, but it has **not been tested**:

1. On Ubuntu run `scripts/validate-rom.py game.nes`; it prints the file's `sha256`.
2. Rename the file to `<sha256>.nes` (64 lowercase hex digits).
3. In Device Portal → File explorer, open this app's `LocalState\Games\` and upload the file
   (only works if Device Portal exposes the app's local folder on your OS build).
4. Restart the app: unknown ROM files in `Games\` are re-registered as "Recovered game …" (rename in the gallery).

## LAN Import

* Opening the page starts a tiny HTTP receiver on the console's private IPv4 address, port 8765,
  with a fresh 6-digit code; leaving the page stops it. Only private/link-local peers are served.
* From a browser: open `http://<xbox-ip>:8765/`, type the code, choose files.
* From Ubuntu: `scripts/lan-upload.py <xbox-ip> <code> ~/roms/*.nes` (validates locally first).
* Requires `privateNetworkClientServer`. Whether inbound connections reach a Dev Mode app on Xbox is
  **UNVERIFIED**; the page shows a clear error if listening fails.
