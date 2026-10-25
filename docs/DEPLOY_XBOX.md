# Deploy to Xbox Series X|S (Developer Mode)

References: https://learn.microsoft.com/en-us/windows/uwp/apps-for-xbox/development-options and
https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/device-portal

1. Put the console in Developer Mode (Xbox Dev Mode Companion app + Partner Center developer account, per
   Microsoft's current procedure) and switch to **Dev Home**.
2. In Dev Home enable **Remote Access** (Device Portal) and set a user name/password. Note the address,
   e.g. `https://192.168.1.50:11443`.
3. On Ubuntu (or any PC) open that address in a browser and accept the self-signed certificate warning.
4. **Home → My games & apps → Add**. Upload `NesXbox_0.1.0.0_x64.msix` (or `.appx`), then on the next
   step add every file from `Dependencies\x64\` (Microsoft.VCLibs.x64.14.00, and for Release builds the
   Microsoft.NET.Native.Framework/Runtime packages). Finish and wait for installation.
5. Launch **NES / Xbox** from Dev Home (or pin it). The app works offline.

Updating: upload a newer version (bump `Version` in `Package.appxmanifest`) signed by the **same**
certificate; otherwise uninstall the old one first (save data in LocalState is then lost — the app's
library lives only on the console).

Getting files onto the console:

* **USB**: FAT32/exFAT/NTFS flash drive with `.nes` files in the root or `NES\` → Import from USB.
* **LAN**: open LAN Import, then `scripts/lan-upload.py <ip> <code> *.nes` from Ubuntu or a browser.
* **Homebrew**: Browse Homebrew → Download (only for licensed entries).

Debug data: Device Portal → File explorer → `LocalAppData\NesXbox.DevMode_…\LocalState` holds
`library.json`, `Games\`, `Saves\`. The in-game performance overlay is in Settings.
