#!/usr/bin/env bash
# Exercise AppKit log updates without loading settings or accessing credentials.
set -Eeuo pipefail
cd "$(dirname "$0")/.."
TEST_DIR="$(mktemp -d)"
trap 'rm -rf "$TEST_DIR"' EXIT
cat macos/ChatToComputerMCPApp.swift tests/SwiftUILogChecks.swift > "$TEST_DIR/ChatToComputerMCPApp.swift"
cat > "$TEST_DIR/main.swift" <<'SWIFT'
import AppKit
let app = NSApplication.shared
runLogRegressionChecks()
SWIFT
swiftc -module-cache-path "${SWIFT_MODULECACHE_PATH:-$TEST_DIR/cache}" \
    -framework AppKit -framework Network -framework Security -framework SystemConfiguration \
    -o "$TEST_DIR/check" macos/ProcessRunner.swift macos/Ripgrep.swift macos/LocalMCPServer.swift \
    macos/CodexHistory.swift macos/CommandReceiptLedger.swift macos/CUA.swift macos/CommandHistoryStore.swift macos/ActivityAuditStore.swift macos/ActivityAuditView.swift macos/CommandHistoryView.swift macos/LocalMCPRuntime.swift \
    macos/LaunchAgentService.swift \
    macos/AppUpdates.swift \
    "$TEST_DIR/ChatToComputerMCPApp.swift" "$TEST_DIR/main.swift"
"$TEST_DIR/check"
