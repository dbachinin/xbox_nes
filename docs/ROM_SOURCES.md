# NES ROM sources

Scope: **authorized NES homebrew**, open-source demos and emulator test ROMs. This inventory drives the
`Browse Homebrew` screen; it is **not** permission to mirror anything. The app never offers commercial ROMs.

## What the app does today

| Source | Integration | Download offered when |
|---|---|---|
| **Homebrew Hub** (https://hh.gbdev.io) | Listing via the documented API (`/api/search?platform=NES&page=N[&title=]`); files and screenshots from the `nesdev-org/homebrew-db` dataset | entry is `platform: NES`, `typetag` game/demo, has a safe `.nes` file **and declares a redistributable license** |
| **GitHub Releases, curated** | `GET api.github.com/repos/{owner}/{repo}/releases/latest`, exact asset name, host allowlist | always for the allowlist below (licenses checked by hand) |
| RetroBrews, itch.io, NESdev | Not integrated as download sources | — (see below) |

### Live probe results (Ubuntu, 2026-10-20)

* `GET https://hh3.gbdev.io/api/search?platform=NES&page=1` → 200 JSON, 24 results over 3 pages
  (pages overlap, so the client de-duplicates by slug). 21 entries are NES game/demo; `240p-test-suite`
  is typetag `tool` and is filtered out.
* The documented file route `GET /api/entry/{slug}/{file}` returned **404 (HTML)** for every entry tried,
  including screenshots. The same files are served from the dataset:
  `https://raw.githubusercontent.com/nesdev-org/homebrew-db/master/entries/{slug}/{file}` (200, valid iNES).
  The client therefore lists from the API and fetches from the dataset.
* File names are relative paths (e.g. `files/helmet.nes`) and can contain a literal `%`
  (`screenshots/p%2F%2F8kD.png`); the client validates each segment and percent-escapes it.
* `developer` is sometimes a string, sometimes an array/object (schema d5), so a tolerant JSON reader is used.
* Only **1 of 21** entries declares a license (`cybercoaster`, CC-BY-4.0). All others are shown as
  reference-only ("No license stated in the catalog: open the official page and import via USB/LAN"),
  as required by AGENTS.md §13.3 scenario 1. Downloading Cybercoaster (MMC3) with its cover worked.
* Mappers in the catalog: mostly 0, plus 1 (Nova), 2 (240p), 3 (Guild Quest), 4 (Cybercoaster), 28 (Tactus,
  unsupported → rejected with "Unsupported mapper 28").
* Xbox network access to these hosts: **not verified**.

### Curated GitHub allowlist (`GitHubReleaseCatalogClient.Allowlist`)

| Repo | Asset | License (GitHub API + README) |
|---|---|---|
| pinobatch/thwaite-nes | thwaite.nes | GPL-3.0; title screen states GPL v3 or later |
| pinobatch/croom-nes | croom.nes | GPL-3.0-or-later, with an explicit exception allowing exact iNES copies without source |
| pinobatch/240p-test-mini | 240pee.nes | GPL-2.0-or-later |

Live: all three resolved and `thwaite.nes` downloaded through the github.com → release-assets CDN redirect.
Excluded on purpose: NovaSquirrel/NovaTheSquirrel (game code GPL, graphics under a more restrictive license).

### License rule

Download is offered for licenses starting with CC0, CC-BY (all variants), GPL/LGPL/AGPL, MIT, BSD, 0BSD,
Apache, Zlib, Unlicense, MPL, ISC, WTFPL, Artistic, BSL-1.0, "Public domain". Anything else, or no license:
reference only. Covers/screenshots are cached only for entries that pass the same rule.

## Other sources (reference only)

| Source | URL | Why not automated |
|---|---|---|
| RetroBrews NES | https://github.com/retrobrews/nes-games | README: ROMs authorized for distribution on that project only; no mirroring without author consent |
| itch.io free NES ROMs | https://itch.io/games/free/tag-nes-rom | No anonymous public download API; download pages are interactive/pay-what-you-want |
| NESdev emulator tests | https://www.nesdev.org/wiki/Emulator_tests | Diagnostics, not games; used as downloaded test fixtures (tests/fixtures/README.md) |

Specific creator-published games worth getting manually (then USB/LAN import): Böbl and Spacegulls
(morphcatgames.itch.io), FROM BELOW (mhughson.itch.io), Nova the Squirrel (novasquirrel.itch.io).
