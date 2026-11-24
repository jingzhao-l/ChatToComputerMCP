#!/bin/bash
set -Eeuo pipefail
cd "$(dirname "$0")/.."
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cat macos/LocalMCPServer.swift tests/SwiftWorkspaceScopeChecks.swift > "$tmp/LocalMCPServer.swift"
cat > "$tmp/main.swift" <<'SWIFT'
import Foundation
try LocalMCPServer.verifyApprovedWorkspaces()
try LocalMCPServer.verifyDurableCommandReceipts()
try LocalMCPServer.verifyAuthenticatedClientIsolation()
try LocalMCPServer.verifyOAuthRevocationCancelsOwnedJobs()
SWIFT
swiftc -j 1 -module-cache-path "$tmp/cache" -framework Network \
  -o "$tmp/check" macos/ProcessRunner.swift macos/Ripgrep.swift \
  macos/CodexHistory.swift macos/CommandReceiptLedger.swift macos/CUA.swift "$tmp/LocalMCPServer.swift" "$tmp/main.swift"
"$tmp/check"
