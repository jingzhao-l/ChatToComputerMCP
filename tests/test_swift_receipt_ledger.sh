#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "$0")/.."
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
cat >"$scratch/main.swift" <<'SWIFT'
import Foundation
try runReceiptLedgerChecks()
SWIFT
swiftc -j 1 -module-cache-path "$scratch/cache" \
    macos/CommandReceiptLedger.swift tests/SwiftReceiptLedgerChecks.swift \
    "$scratch/main.swift" -o "$scratch/check"
"$scratch/check"
