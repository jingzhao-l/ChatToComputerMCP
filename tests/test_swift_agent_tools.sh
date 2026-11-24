#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "$0")/.."
TEST_DIR="$(mktemp -d)"
trap 'rm -rf "$TEST_DIR"' EXIT
cat macos/LocalMCPServer.swift tests/SwiftAgentToolChecks.swift > "$TEST_DIR/LocalMCPServer.swift"
cat > "$TEST_DIR/main.swift" <<'SWIFT'
import Foundation
try runAgentToolChecks(rootPath: CommandLine.arguments[1])
SWIFT
swiftc -module-cache-path "${SWIFT_MODULECACHE_PATH:-$TEST_DIR/cache}" \
    -framework Network -o "$TEST_DIR/check" macos/ProcessRunner.swift macos/Ripgrep.swift macos/CodexHistory.swift macos/CommandReceiptLedger.swift macos/CUA.swift macos/CommandHistoryStore.swift macos/ActivityAuditStore.swift macos/CommandSupervisor.swift \
    "$TEST_DIR/LocalMCPServer.swift" "$TEST_DIR/main.swift"
case "$(uname -m)" in
    arm64|aarch64) TARGET=darwin-arm64 ;;
    x86_64|amd64) TARGET=darwin-amd64 ;;
    *) echo "unsupported macOS architecture" >&2; exit 2 ;;
esac
FILEMCP_RG="$PWD/vendor/ripgrep/$TARGET/rg" "$TEST_DIR/check" "$TEST_DIR/workspace"
