#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "$0")/.."
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cat > "$tmp/main.swift" <<'SWIFT'
import Foundation
let args=CommandLine.arguments
let server=try LocalMCPServer(port:UInt16(args[2])!,allowedDirectory:URL(fileURLWithPath:args[3]).deletingLastPathComponent().path,gitUserName:"",gitUserEmail:"",enableCommands:false,localAuthToken:args[4],cuaGuest:args[5]=="guest",cuaPolicyURL:URL(fileURLWithPath:args[3]),log:{_ in})
try server.start()
print("READY");fflush(stdout)
_ = readLine()
server.stop()
SWIFT
swiftc -module-cache-path "${SWIFT_MODULECACHE_PATH:-$tmp/cache}" -o "$tmp/cua-test-server" \
  macos/ProcessRunner.swift macos/Ripgrep.swift macos/CodexHistory.swift macos/CommandReceiptLedger.swift macos/CommandHistoryStore.swift macos/ActivityAuditStore.swift macos/CUA.swift macos/LocalMCPServer.swift "$tmp/main.swift"
CTCMCP_CUA_TEST_SERVER="$tmp/cua-test-server" node tests/test_cua_p4.mjs
