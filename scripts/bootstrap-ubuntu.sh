#!/usr/bin/env bash
# One-shot Ubuntu setup: toolchain, build (Debug), fixtures, all tests.
#   scripts/bootstrap-ubuntu.sh            # needs sudo for apt
#   SKIP_APT=1 scripts/bootstrap-ubuntu.sh # tools already installed
set -euo pipefail
cd "$(dirname "$0")/.."

if [[ -z "${SKIP_APT:-}" ]]; then
  sudo apt-get update
  sudo apt-get install -y build-essential clang cmake ninja-build git python3 curl
  # Optional: managed shell-logic tests need the .NET SDK (8 or 9).
  sudo apt-get install -y dotnet-sdk-9.0 || echo "dotnet-sdk-9.0 not available; managed tests will be skipped"
fi

# Use the system cmake/ctest explicitly: a pip-installed shim in ~/.local/bin can shadow them.
CMAKE=$(command -v /usr/bin/cmake || command -v cmake)
CTEST=$(command -v /usr/bin/ctest || command -v ctest)

scripts/fetch-test-roms.sh || echo "fixture download failed: ROM-based tests will be SKIPPED"
"$CMAKE" -S . -B build/linux -G Ninja -DCMAKE_BUILD_TYPE=Debug
"$CMAKE" --build build/linux --parallel
"$CTEST" --test-dir build/linux --output-on-failure
