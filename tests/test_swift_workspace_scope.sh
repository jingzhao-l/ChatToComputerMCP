#!/bin/bash
set -Eeuo pipefail
cd "$(dirname "$0")/.."
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cat macos/LocalMCPServer.swift tests/SwiftWorkspaceScopeChecks.swift > "$tmp/LocalMCPServer.swift"
cat > "$tmp/main.swift" <<'SWIFT'
import Foundation
if CommandLine.arguments.count == 3, CommandLine.arguments[1] == "--command-worker" {
    do { try SupervisedCommandJob.runWorker(directory: URL(fileURLWithPath: CommandLine.arguments[2])); exit(0) } catch { exit(1) }
}
if CommandLine.arguments.count == 3, CommandLine.arguments[1] == "--supervisor-fixture" {
    try LocalMCPServer.supervisorFixture(URL(fileURLWithPath: CommandLine.arguments[2])); exit(0)
}
try LocalMCPServer.verifySupervisedRecovery()
try LocalMCPServer.verifyApprovedWorkspaces()
try LocalMCPServer.verifyDurableCommandReceipts()
try LocalMCPServer.verifyAuthenticatedClientIsolation()
try LocalMCPServer.verifyOAuthRevocationCancelsOwnedJobs()
SWIFT
swiftc -j 1 -module-cache-path "$tmp/cache" -framework Network \
  -o "$tmp/check" macos/ProcessRunner.swift macos/Ripgrep.swift \
  macos/CodexHistory.swift macos/CommandReceiptLedger.swift macos/CUA.swift macos/CommandHistoryStore.swift macos/ActivityAuditStore.swift macos/CommandSupervisor.swift "$tmp/LocalMCPServer.swift" "$tmp/main.swift"
"$tmp/check"
