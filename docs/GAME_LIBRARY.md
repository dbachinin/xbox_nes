# Game library (My Games)

Everything lives in `ApplicationData.Current.LocalFolder`:

```text
LocalState/
  library.json          metadata (source of truth for titles, favorites, timestamps)
  library.bak           previous good library.json (written by File.Replace)
  library.corrupt.json  kept for diagnosis if library.json was unreadable
  Games/<sha256>.nes    ROM bytes (source of truth for existence)
  Covers/<sha256>.png   cover/screenshot, only when the source's license allows it
  Saves/<sha256>.sav    battery-backed PRG RAM (kept when a game is deleted)
  Temp/                 in-flight imports; cleared on start
  CatalogCache/         last catalog pages (offline display, time-stamped)
```

## library.json

```json
{
  "schema_version": 1,
  "games": [
    {
      "id": "sha256:40a7631e1ae15b2120437f080b623ea23c11d475429d568f11d33b0f80e234b1",
      "rom_path": "Games/40a7631e1ae15b2120437f080b623ea23c11d475429d568f11d33b0f80e234b1.nes",
      "title": "Cybercoaster",
      "sha256": "40a7631e1ae15b2120437f080b623ea23c11d475429d568f11d33b0f80e234b1",
      "mapper": 4, "size": 49168, "battery": false,
      "source": "homebrewhub",
      "source_url": "https://hh.gbdev.io/game/cybercoaster",
      "license": "CC-BY-4.0", "developer": "Fizzer & noby",
      "cover_path": "Covers/40a7631e1ae15b2120437f080b623ea23c11d475429d568f11d33b0f80e234b1.png",
      "favorite": false,
      "imported_at": "2026-10-20T21:05:13Z",
      "last_played_at": null
    }
  ]
}
```

`source` is one of `usb`, `lan`, `homebrewhub`, `github`, `recovered`.

## Single ingestion pipeline

`LocalRomLibrary.ImportAsync(stream, fileName, origin, expectedLength, progress, ct)` is used by USB,
LAN, catalog downloads and file activation:

stream → 32 MiB cap → `Temp/*.part` + SHA-256 → length check (if announced) → iNES/NES 2.0 + mapper
check → (lock) duplicate check by hash → move to `Games/<sha>.nes` → `library.json` atomic write →
`Changed` event → gallery refresh. Cover art is fetched afterwards and can never fail the import.

Results: `Imported`, `Already in library`, `Invalid ROM`, `Unsupported mapper N`, `File too large`,
`Transfer incomplete`, `Cancelled`, `Drive disconnected`, `Access denied`, `Not enough storage`.
Unsupported mappers are rejected at import with an explanation (the USB list shows them beforehand).

## Robustness rules

* Never trust stored paths or ROM-embedded names: paths are derived from the hash, titles are sanitized
  (no control characters, ≤ 80 chars), cover paths must be `Covers/<sha>.png|jpg`.
* Corrupted/partial `library.json` → fall back to `library.bak`, then re-register orphan ROM files.
* Missing ROM file → card shows "File missing"; re-importing the same bytes repairs it.
* Delete: metadata first, then files. A crash in between leaves an orphan that is re-registered on next
  start (never a card pointing at nothing).

## Gallery UX

GridView (virtualised) of cards: cover or generated placeholder (hash-coloured cartridge + initials),
title, status (`Ready` / `Unsupported mapper N` / `File missing`), favorite star, last played.
Filters: All, Favorites, Recently played, Recently added. Sort: title, date added, last played.
Search box (opens the Xbox on-screen keyboard). A → details dialog: Play, Favorite, Rename, Delete
(with confirmation), info (source, license, mapper, hash). The selected card is restored when returning
from a game. LB/RB switch top-level sections, B returns focus to the tab bar.
