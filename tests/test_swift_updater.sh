#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "$0")/.."
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
cat > "$scratch/main.swift" <<'SWIFT'
import Foundation
try runNativeUpdateChecks()
SWIFT
swiftc -j 1 -framework AppKit -module-cache-path "$scratch/cache" \
  macos/AppUpdates.swift tests/SwiftUpdateChecks.swift "$scratch/main.swift" \
  -o "$scratch/check"
"$scratch/check"
