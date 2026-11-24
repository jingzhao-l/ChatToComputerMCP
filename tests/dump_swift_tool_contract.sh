#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "$0")/.."
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
mkdir "$TMP/workspace"
cat macos/LocalMCPServer.swift tests/SwiftToolContractChecks.swift > "$TMP/LocalMCPServer.swift"
cat > "$TMP/main.swift" <<'SWIFT'
import Foundation
try dumpBaseToolContract(root: URL(fileURLWithPath: CommandLine.arguments[1]))
SWIFT
swiftc -module-cache-path "${SWIFT_MODULECACHE_PATH:-$TMP/cache}" \
    -framework Network -o "$TMP/check" macos/ProcessRunner.swift macos/Ripgrep.swift macos/CodexHistory.swift \
    macos/CommandReceiptLedger.swift macos/CUA.swift macos/CommandHistoryStore.swift macos/ActivityAuditStore.swift macos/CommandSupervisor.swift \
    "$TMP/LocalMCPServer.swift" "$TMP/main.swift"
"$TMP/check" "$TMP/workspace"
