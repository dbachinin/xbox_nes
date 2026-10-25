#!/usr/bin/env bash
# Downloads diagnostic/homebrew ROM fixtures into tests/fixtures/roms (git-ignored)
# and verifies SHA-256. Sources and licenses: tests/fixtures/README.md
set -euo pipefail
dest="$(cd "$(dirname "$0")/.." && pwd)/tests/fixtures/roms"
mkdir -p "$dest"
T=https://raw.githubusercontent.com/christopherpow/nes-test-roms/master
H=https://raw.githubusercontent.com/nesdev-org/homebrew-db/master/entries

fetch() { # name url sha256
  local f="$dest/$1"
  if [[ -f "$f" ]] && echo "$3  $f" | sha256sum -c --quiet - 2>/dev/null; then return; fi
  curl -fsSL --max-time 60 -o "$f.part" "$2"
  echo "$3  $f.part" | sha256sum -c --quiet - || { rm -f "$f.part"; echo "checksum mismatch: $1" >&2; exit 1; }
  mv "$f.part" "$f"
  echo "fetched $1"
}

fetch nestest.nes        $T/other/nestest.nes                    f67d55fd6b3cf0bad1cc85f1df0d739c65b53e79cecb7fea8f77ec0eadab0004
fetch nestest.log        $T/other/nestest.log                    442c4dd5539c7e88b3fd73c7b732a7eadbd22b47c2cd9e58397ef147f64f6f8f
fetch official_only.nes  $T/instr_test-v5/official_only.nes      589b8835deb5cbc69618dac193a3dbd675540f7f2794e2d2a92e97beb8abc3cb
fetch all_instrs.nes     $T/instr_test-v5/all_instrs.nes         353870c157242e3d428ef7387109deaee0d2e158bdb432ab9aae4e657072c785
fetch cpu_interrupts.nes $T/cpu_interrupts_v2/cpu_interrupts.nes ccbac4e824eb96ecfe8b82d331a083be186eb6776aa57e25c52251eaf7df9c4f
fetch ppu_vbl_nmi.nes    $T/ppu_vbl_nmi/ppu_vbl_nmi.nes          8dbab1be785585c399cf055ef02147b788ab75fd80e81cf9568a2feafc03fb7d
fetch apu_test.nes       $T/apu_test/apu_test.nes                00d4722bae1c82a14528dd3220462d3fb9ce4b14b8cec996619dea23e07fef0a
fetch cybercoaster.nes   $H/cybercoaster/cybercoaster.nes        40a7631e1ae15b2120437f080b623ea23c11d475429d568f11d33b0f80e234b1
echo "fixtures ready in $dest"
