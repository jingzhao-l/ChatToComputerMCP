#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "$0")/.."
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cp tests/cua_driver_fixture.py "$tmp/driver-fixture"
chmod 700 "$tmp/driver-fixture"
swiftc -module-cache-path "${SWIFT_MODULECACHE_PATH:-$tmp/cache}" -o "$tmp/check" macos/CommandReceiptLedger.swift macos/CUA.swift tests/SwiftCUAChecks.swift
"$tmp/check" "$tmp" "$tmp/driver-fixture"
